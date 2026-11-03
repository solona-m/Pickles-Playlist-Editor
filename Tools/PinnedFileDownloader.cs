using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>
    /// Downloads one pinned file: sources in order, retries with backoff, a per-read stall timeout, and size plus
    /// SHA-256 checked before the file appears at its final path. No resume; a failed attempt starts over.
    /// </summary>
    internal sealed class PinnedFileDownloader : IDisposable
    {
        private const int MaxAttempts = 3;
        private const int BufferSize = 1024 * 1024;

        private readonly HttpClient _http;
        private readonly Func<int, TimeSpan?, TimeSpan> _backoff;
        private readonly TimeSpan _readTimeout;

        public PinnedFileDownloader(HttpMessageHandler? handler = null, Func<int, TimeSpan?, TimeSpan>? backoff = null, TimeSpan? readTimeout = null)
        {
            _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = Timeout.InfiniteTimeSpan; // _readTimeout bounds the wait for headers and each read instead
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PicklesPlaylistEditor/" + Utils.AppVersion.Display.Replace(' ', '-'));
            _backoff = backoff ?? DefaultBackoff;
            _readTimeout = readTimeout ?? TimeSpan.FromSeconds(60);
        }

        public void Dispose() => _http.Dispose();

        private static readonly Random s_jitter = new();

        private static TimeSpan DefaultBackoff(int attempt, TimeSpan? retryAfter)
        {
            if (retryAfter is { } ra) return ra > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : ra;
            return TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(s_jitter.Next(0, 500));
        }

        // Throttling and transient server trouble; GitHub answers a rate limit with 403.
        private static bool IsRetryable(HttpStatusCode code) => (int)code is 403 or 408 or 429 or 500 or 502 or 503 or 504;

        /// <param name="onProgress">Bytes of this file received so far in the current attempt (absolute).</param>
        public async Task<AiAssetResult> FetchAsync(string[] baseUrls, AiAsset asset, string destPath,
            Action<long>? onProgress, CancellationToken ct)
        {
            var last = new AiAssetResult(false, AiAssetFailure.Transport, asset.Name, "no sources");
            foreach (var baseUrl in baseUrls)
            {
                string url = baseUrl + asset.Name;
                for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    string tmp = $"{destPath}.{Guid.NewGuid():N}.part";
                    TimeSpan? retryAfter = null;
                    bool nextSource = false;
                    try
                    {
                        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        headers.CancelAfter(_readTimeout);
                        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
                        long? declared = response.Content.Headers.ContentLength;
                        if (!response.IsSuccessStatusCode)
                        {
                            last = new(false, AiAssetFailure.Http, asset.Name, $"HTTP {(int)response.StatusCode} from {url}");
                            Utils.Logger.LogWarn("AI asset: {Detail} (attempt {Attempt})", last.Detail, attempt);
                            if (!IsRetryable(response.StatusCode)) { nextSource = true; }
                            else retryAfter = RetryAfter(response.Headers.RetryAfter);
                        }
                        else if (declared is long len && len != asset.Bytes)
                        {
                            // The source says it is serving a different file; don't download it to find out.
                            last = new(false, AiAssetFailure.Checksum, asset.Name, $"{asset.Name}: {url} serves {len} bytes, pinned {asset.Bytes}");
                            Utils.Logger.LogWarn("AI asset: {Detail}", last.Detail);
                            nextSource = true;
                        }
                        else
                        {
                            var (got, sha) = await ReceiveAsync(response, tmp, asset.Bytes, onProgress, ct).ConfigureAwait(false);
                            if (got > asset.Bytes)
                            {
                                last = new(false, AiAssetFailure.Checksum, asset.Name, $"{asset.Name}: {url} sent more than the pinned {asset.Bytes} bytes");
                                Utils.Logger.LogWarn("AI asset: {Detail}", last.Detail);
                                nextSource = true;
                            }
                            else if (got != asset.Bytes)
                            {
                                last = new(false, AiAssetFailure.Transport, asset.Name, $"{asset.Name}: {got} of {asset.Bytes} bytes from {url}");
                                Utils.Logger.LogWarn("AI asset: {Detail}", last.Detail);
                            }
                            else if (!string.Equals(sha, asset.Sha256, StringComparison.Ordinal))
                            {
                                // The right size with the wrong bytes is a bad source, not a bad transfer.
                                last = new(false, AiAssetFailure.Checksum, asset.Name, $"{asset.Name}: SHA-256 mismatch from {url}");
                                Utils.Logger.LogWarn("AI asset: {Detail}", last.Detail);
                                nextSource = true;
                            }
                            else
                            {
                                return Promote(tmp, destPath, asset);
                            }
                        }
                    }
                    catch (LocalDiskException ex)
                    {
                        // Our own disk, not the network: retrying or another source can't help.
                        Utils.Logger.LogWarn("AI asset: cannot write {File}: {Error}", tmp, ex.InnerException?.Message ?? ex.Message);
                        return new(false, AiAssetFailure.Disk, asset.Name, $"{asset.Name}: cannot write the download: {ex.InnerException?.Message}");
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        // An OperationCanceledException that is not the caller's is the stall timeout.
                        last = new(false, AiAssetFailure.Transport, asset.Name, $"{asset.Name}: {ex.Message} ({url})");
                        Utils.Logger.LogWarn("AI asset: {Detail} (attempt {Attempt})", last.Detail, attempt);
                    }
                    finally { TryDelete(tmp); }

                    if (nextSource) break;
                    if (attempt < MaxAttempts) await Task.Delay(_backoff(attempt, retryAfter), ct).ConfigureAwait(false);
                }
            }
            return last;
        }

        private static TimeSpan? RetryAfter(RetryConditionHeaderValue? h)
        {
            if (h?.Delta is { } d) return d;
            if (h?.Date is { } date) { var wait = date - DateTimeOffset.UtcNow; return wait > TimeSpan.Zero ? wait : TimeSpan.Zero; }
            return null;
        }

        /// <summary>A failure writing our own temp file, kept apart from network IOExceptions.</summary>
        private sealed class LocalDiskException(Exception inner) : Exception(inner.Message, inner);

        private async Task<(long Bytes, string Sha)> ReceiveAsync(HttpResponseMessage response, string tmp, long limit, Action<long>? onProgress, CancellationToken ct)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long got = 0;
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            FileStream file;
            try { file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new LocalDiskException(ex); }
            await using (file)
            {
                var buf = new byte[BufferSize];
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(_readTimeout);
                    int n = await body.ReadAsync(buf, stall.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    got += n;
                    if (got > limit) break; // a wrong file; stop before it fills the disk
                    hash.AppendData(buf, 0, n);
                    try { await file.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new LocalDiskException(ex); }
                    onProgress?.Invoke(got);
                }
            }
            return (got, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }

        private static AiAssetResult Promote(string tmp, string dest, AiAsset asset)
        {
            // A just-closed exe is often held briefly by an antivirus scan.
            for (int i = 0; i < 5; i++)
            {
                try { File.Move(tmp, dest, overwrite: true); return AiAssetResult.Success; }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }
            if (Verify(dest, asset)) return AiAssetResult.Success; // locked, but already the right file
            return new(false, AiAssetFailure.Disk, asset.Name, $"{asset.Name}: could not replace {dest}");
        }

        /// <summary>True if the file exists with the pinned size and SHA-256.</summary>
        internal static bool Verify(string path, AiAsset asset)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length != asset.Bytes) return false;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
                return string.Equals(Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant(), asset.Sha256, StringComparison.Ordinal);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
