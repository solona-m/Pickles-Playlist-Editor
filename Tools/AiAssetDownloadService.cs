using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>
    /// Makes sure the AI score's pinned files are on disk and verified, downloading what is missing or wrong.
    /// Nothing runs until <see cref="EnsureAsync"/> is called, which only the AI-score opt-in does.
    /// </summary>
    internal sealed class AiAssetDownloadService : IDisposable
    {
        private readonly string _root;
        private readonly IReadOnlyList<AiAsset> _files;
        private readonly Func<string, string[]> _baseUrls;
        private readonly PinnedFileDownloader _downloader;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<AiAssetResult>? _running;

        // Files verified in this process, keyed so a truncated, replaced or touched file is checked again.
        private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Mtime, string Sha), byte> s_verified = new();

        internal int HashCount; // test seam: how many full-file hashes this instance ran

        public AiAssetDownloadService()
            : this(AiAssets.DefaultRoot, AiAssets.Files, AiAssets.BaseUrls) { }

        internal AiAssetDownloadService(string root, IReadOnlyList<AiAsset> files, Func<string, string[]> baseUrls,
            HttpMessageHandler? handler = null, Func<int, TimeSpan?, TimeSpan>? backoff = null, TimeSpan? readTimeout = null)
        {
            _root = root;
            _files = files;
            _baseUrls = baseUrls;
            _downloader = new PinnedFileDownloader(handler, backoff, readTimeout);
        }

        public void Dispose() => _downloader.Dispose();

        public string PathFor(AiAsset asset) => Path.Combine(_root, asset.Tag, asset.Name);

        /// <summary>Returns at once; concurrent callers share one run. Cancellation throws OperationCanceledException.</summary>
        public Task<AiAssetResult> EnsureAsync(IProgress<AiAssetProgress>? progress, CancellationToken ct)
        {
            _gate.Wait(ct);
            try
            {
                if (_running is { IsCompleted: false } r) return r;
                return _running = Task.Run(() => RunAsync(progress, ct), ct);
            }
            finally { _gate.Release(); }
        }

        private async Task<AiAssetResult> RunAsync(IProgress<AiAssetProgress>? progress, CancellationToken ct)
        {
            long total = _files.Sum(f => f.Bytes), done = 0;
            for (int i = 0; i < _files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var asset = _files[i];
                string dest = PathFor(asset);
                string dir = Path.GetDirectoryName(dest)!;
                try
                {
                    Directory.CreateDirectory(dir);
                    CleanStaleParts(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new(false, AiAssetFailure.Disk, asset.Name, $"cannot use {dir}: {ex.Message}");
                }

                if (IsVerified(dest, asset))
                {
                    done += asset.Bytes;
                    progress?.Report(new(asset.Name, i, _files.Count, asset.Bytes, asset.Bytes, done, total));
                    continue;
                }
                TryDelete(dest);

                long before = done;
                int index = i;
                var result = await _downloader.FetchAsync(_baseUrls(asset.Tag), asset, dest,
                    got => progress?.Report(new(asset.Name, index, _files.Count, got, asset.Bytes, before + got, total)), ct).ConfigureAwait(false);
                if (!result.Ok)
                {
                    Utils.Logger.LogWarn("AI asset download failed: {Detail}", result.Detail);
                    return result;
                }
                Remember(dest, asset);
                done += asset.Bytes;
                progress?.Report(new(asset.Name, i, _files.Count, asset.Bytes, asset.Bytes, done, total));
            }
            return AiAssetResult.Success;
        }

        private bool IsVerified(string path, AiAsset asset)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != asset.Bytes) return false;
            if (s_verified.ContainsKey((fi.FullName, fi.Length, fi.LastWriteTimeUtc, asset.Sha256))) return true;
            Interlocked.Increment(ref HashCount);
            if (!PinnedFileDownloader.Verify(path, asset)) return false;
            Remember(path, asset);
            return true;
        }

        private static void Remember(string path, AiAsset asset)
        {
            var fi = new FileInfo(path);
            if (fi.Exists) s_verified[(fi.FullName, fi.Length, fi.LastWriteTimeUtc, asset.Sha256)] = 0;
        }

        // Leftovers of a crashed run. One still open in another instance is skipped.
        private static void CleanStaleParts(string dir)
        {
            foreach (var part in Directory.EnumerateFiles(dir, "*.part"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(part) < DateTime.UtcNow.AddMinutes(-10)) File.Delete(part);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
