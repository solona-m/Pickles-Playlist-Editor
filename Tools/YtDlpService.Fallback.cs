using Pickles_Playlist_Editor.Utils;

namespace Pickles_Playlist_Editor.Tools;

/// <summary>The yt-dlp calls the YouTube match makes, behind a seam so tests need no network.</summary>
internal interface IFallbackYtDlp
{
    Task<string> ProbeSoundCloudTrackAsync(string url);
    Task<string> SearchYouTubeAsync(string query);
    Task<string?> DownloadYouTubeAudioAsync(string youtubeUrl, string dir, Action<YtDlpProgressInfo>? onProgress);
}

public static partial class YtDlpService
{
    internal const int MaxFallbackTracks = 25;
    internal static readonly TimeSpan FallbackCallTimeout = TimeSpan.FromSeconds(90);

    // Public metadata only: no cookies, nothing downloaded.
    internal static string BuildFallbackProbeArgs(string url, string denoArg) =>
        $"-J --skip-download --ignore-no-formats-error --no-playlist --no-warnings {denoArg} -- \"{url}\"";

    internal static string BuildFallbackSearchArgs(string query, string denoArg) =>
        $"-J --flat-playlist --skip-download --no-warnings {denoArg} -- \"ytsearch5:{query}\"";

    /// <summary>
    /// For each SoundCloud track yt-dlp reported as DRM protected (not downloadable from SoundCloud),
    /// looks for a matching YouTube upload and puts it where the track sat in the set.
    /// </summary>
    internal static async Task<List<FallbackTrackResult>> ApplyDrmFallbackAsync(
        MediaService service, SoundCloudLinkKind kind, string stderr, IReadOnlyList<ProbeEntry> entries,
        string outputDirectory, bool numbered, IFallbackYtDlp ytdlp, Action<YtDlpProgressInfo>? onProgress)
    {
        var results = new List<FallbackTrackResult>();
        if (service != MediaService.SoundCloud) return results;

        var ids = YouTubeFallbackMatcher.ParseDrmTrackIds(stderr);
        if (ids.Count == 0) return results;
        if (kind == SoundCloudLinkKind.Unsupported)
        {
            Logger.LogInfo("YouTube matching is only tried for single SoundCloud tracks and sets.");
            return results;
        }

        var byId = new Dictionary<string, ProbeEntry>();
        foreach (var e in entries) byId.TryAdd(e.Id, e);
        var ordered = ids.Select((id, i) => (id, order: byId.TryGetValue(id, out var e) ? e.Index : int.MaxValue - ids.Count + i))
            .OrderBy(x => x.order).Select(x => x.id).ToList();

        int total = Math.Min(ordered.Count, MaxFallbackTracks);
        FallbackSkipReason? stop = null;

        for (int i = 0; i < ordered.Count; i++)
        {
            string id = ordered[i];
            FallbackTrackResult result;
            if (i >= MaxFallbackTracks)
                result = new(id, FallbackSkipReason.TooMany, null);
            else if (stop is { } reason)
                result = new(id, reason, null);
            else
            {
                (result, stop) = await MatchOneAsync(id, byId.GetValueOrDefault(id), i + 1, total, outputDirectory, numbered, ytdlp, onProgress).ConfigureAwait(false);
            }

            results.Add(result);
            Logger.LogInfo("{Outcome}", YouTubeFallbackMatcher.FormatOutcomeLog(id, result));
        }

        return results;
    }

    private static async Task<(FallbackTrackResult, FallbackSkipReason?)> MatchOneAsync(string id, ProbeEntry? entry, int n, int total,
        string outputDirectory, bool numbered, IFallbackYtDlp ytdlp, Action<YtDlpProgressInfo>? onProgress)
    {
        string title = id;
        void Report(YtDlpProgressInfo? inner) => onProgress?.Invoke(new YtDlpProgressInfo
        {
            Stage = inner?.Stage ?? "Searching",
            Current = n,
            Total = total,
            Percent = inner?.Percent,
            FallbackTitle = title,
        });

        if (entry == null || !MediaUrlInfo.TryValidate(entry.Url, out var url) || MediaUrlInfo.Classify(url) != MediaService.SoundCloud)
            return (new(title, FallbackSkipReason.NoDetails, null), null);

        Report(null);
        string subdir = Path.Combine(outputDirectory, $"yt-fallback-{n}");
        try
        {
            string probeJson;
            try
            {
                probeJson = await ytdlp.ProbeSoundCloudTrackAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ClassifySoundCloudFailure(ex.Message) == DownloadFailureKind.SoundCloudRateLimited)
                    return (new(title, FallbackSkipReason.SoundCloudRateLimited, null), FallbackSkipReason.SoundCloudRateLimited);
                Logger.LogWarn("SoundCloud track {Id}: reading its details failed: {Error}", id, YouTubeFallbackMatcher.RedactUrls(ex.Message));
                return (new(title, FallbackSkipReason.NoDetails, null), null);
            }

            var info = YouTubeFallbackMatcher.ParseTrackInfo(probeJson);
            if (info == null)
            {
                Logger.LogWarn("Could not read yt-dlp's probe output for SoundCloud track {Id}", id);
                return (new(title, FallbackSkipReason.NoDetails, null), null);
            }
            title = info.Title;
            Report(null);

            string searchJson;
            try
            {
                searchJson = await ytdlp.SearchYouTubeAsync(YouTubeFallbackMatcher.BuildSearchQuery(info)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (LooksLikeAuthFailure(ex.Message))
                    return (new(title, FallbackSkipReason.YouTubeBlocked, null), FallbackSkipReason.YouTubeBlocked);
                Logger.LogWarn("SoundCloud track {Id}: YouTube search failed: {Error}", id, YouTubeFallbackMatcher.RedactUrls(ex.Message));
                return (new(title, FallbackSkipReason.SearchFailed, null), null);
            }

            var candidates = YouTubeFallbackMatcher.ParseSearchResults(searchJson);
            if (candidates.Count == 0 && !searchJson.TrimStart().StartsWith('{'))
                Logger.LogWarn("Could not read yt-dlp's search output for SoundCloud track {Id}", id);

            var pick = YouTubeFallbackMatcher.SelectCandidate(info, candidates);
            if (pick == null)
                return (new(title, FallbackSkipReason.NoConfidentMatch, null), null);

            string? file;
            try
            {
                file = await ytdlp.DownloadYouTubeAudioAsync($"https://www.youtube.com/watch?v={pick.Id}", subdir, Report).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (LooksLikeAuthFailure(ex.Message))
                    return (new(title, FallbackSkipReason.YouTubeBlocked, null), FallbackSkipReason.YouTubeBlocked);
                Logger.LogWarn("SoundCloud track {Id}: YouTube download failed: {Error}", id, YouTubeFallbackMatcher.RedactUrls(ex.Message));
                return (new(title, FallbackSkipReason.DownloadFailed, null), null);
            }
            if (file == null || !File.Exists(file))
                return (new(title, FallbackSkipReason.DownloadFailed, null), null);

            // The ordering prefix puts it back at the track's place in the set.
            string name = YouTubeFallbackMatcher.SafeFileName(info.Title, pick.Title);
            string baseName = numbered ? $"{entry.Index:D5} - {name}" : name;
            string target = Path.Combine(outputDirectory, baseName + ".ogg");
            for (int k = 2; File.Exists(target); k++)
                target = Path.Combine(outputDirectory, $"{baseName} ({k}).ogg");
            File.Move(file, target);
            return (new(title, FallbackSkipReason.None, pick.Title), null);
        }
        catch (Exception ex)
        {
            Logger.LogWarn("SoundCloud track {Id}: YouTube match failed: {Error}", id, YouTubeFallbackMatcher.RedactUrls(ex.Message));
            return (new(title, FallbackSkipReason.DownloadFailed, null), null);
        }
        finally
        {
            try { if (Directory.Exists(subdir)) Directory.Delete(subdir, true); } catch (Exception) { }
        }
    }

    /// <summary>The real calls. None of them carries the SoundCloud sign-in or the saved YouTube cookies.</summary>
    private sealed class LiveFallbackYtDlp(string denoArg, string ffmpegArg) : IFallbackYtDlp
    {
        public Task<string> ProbeSoundCloudTrackAsync(string url) =>
            RunProcessAsync(LocalYtDlpPath, BuildFallbackProbeArgs(url, denoArg), FallbackCallTimeout);

        public Task<string> SearchYouTubeAsync(string query) =>
            RunProcessAsync(LocalYtDlpPath, BuildFallbackSearchArgs(query, denoArg), FallbackCallTimeout);

        public async Task<string?> DownloadYouTubeAudioAsync(string youtubeUrl, string dir, Action<YtDlpProgressInfo>? onProgress)
        {
            Directory.CreateDirectory(dir);
            var result = await DownloadAudioAttemptAsync(youtubeUrl, dir, YtDownloadMode.Single, denoArg, ffmpegArg,
                cookiesArg: string.Empty, onProgress, MediaService.YouTube).ConfigureAwait(false);
            return result.DownloadedFiles.FirstOrDefault();
        }
    }
}
