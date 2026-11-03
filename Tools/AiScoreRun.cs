using Pickles_Playlist_Editor.Utils;
using System;
using System.IO;
using System.Threading;

namespace Pickles_Playlist_Editor.Tools
{
    internal enum AiSongOutcome { Cached, Scored, NoScore, Failed, Unavailable }

    /// <summary>
    /// One pass of AI scoring over some songs: cache first, then extract, decode and classify at below-normal
    /// priority, then cache the result. Failures are logged and counted, never thrown.
    /// </summary>
    internal sealed class AiScoreRun : IDisposable
    {
        private readonly AiScoreCache _cache;
        private readonly Func<string, CancellationToken, AiScoreResult>? _score; // analysis file path -> result
        private readonly SonicsModel.Lease? _lease;

        public int Scored, NoScore, Failed, Cached;

        /// <summary>Why AI is off for this run (the model or ONNX Runtime didn't load), or null.</summary>
        public string? UnavailableReason { get; }

        internal AiScoreRun(AiScoreCache cache, Func<string, CancellationToken, AiScoreResult>? score, string? unavailable, SonicsModel.Lease? lease = null)
        {
            _cache = cache;
            _score = score;
            UnavailableReason = unavailable;
            _lease = lease;
        }

        /// <summary>Loads the shared model; on failure the run reports itself unavailable instead of throwing.</summary>
        public static AiScoreRun Begin(AiScoreCache cache, Func<SonicsModel>? factory = null, string? ffmpegExe = null)
        {
            string exe = ffmpegExe ?? AiAssets.LocalPath(AiAssets.Ffmpeg);
            SonicsModel.Lease lease;
            try
            {
                using (ThreadPriorityScope.BelowNormal())
                    lease = SonicsModel.Acquire(factory ?? (() => SonicsModel.Load(AiAssets.LocalPath(AiAssets.Model))));
            }
            catch (Exception ex)
            {
                Logger.LogWarn("AI score unavailable: {Error}", ex.ToString());
                return new AiScoreRun(cache, null, ex.GetType().Name + ": " + ex.Message);
            }
            float[]? buffer = null; // one 7.7 MB window buffer per run, reused for every song
            return new AiScoreRun(cache, (path, ct) =>
                AiAudioDecoder.DecodeAndScore(exe, path, lease.Model.Logit, ct, buffer ??= new float[AiScoreMath.WindowSamples]), null, lease);
        }

        public AiSongOutcome ScoreSong(string fullScdPath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(fullScdPath)) return AiSongOutcome.Failed;
            if (_cache.TryGet(fullScdPath) is not null) { Cached++; return AiSongOutcome.Cached; }
            if (_score == null) return AiSongOutcome.Unavailable;

            long generation = _cache.Generation(fullScdPath); // the window stays usable, so the file may move meanwhile
            AiScoreResult result;
            try
            {
                using (ThreadPriorityScope.BelowNormal())
                    result = ScdOggExtractor.WithExtractedAudioForAnalysis(fullScdPath, path => _score(path, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result = AiScoreResult.Fail(ex.Message); }

            switch (result.Outcome)
            {
                case AiScoreOutcome.Scored when _cache.StoreIfUnchanged(fullScdPath, AiScoreMath.ToStored(result.Score!.Value), generation):
                    Scored++;
                    return AiSongOutcome.Scored;
                case AiScoreOutcome.NoScore when _cache.StoreIfUnchanged(fullScdPath, AiScoreMath.NoScore, generation):
                    NoScore++;
                    return AiSongOutcome.NoScore;
                case AiScoreOutcome.Scored or AiScoreOutcome.NoScore:
                    // Moved, renamed or deleted while it was scored: the result may not belong to the path any more.
                    Logger.LogInfo("AI score for '{File}' dropped: the file moved while it was scored", Path.GetFileName(fullScdPath));
                    Failed++;
                    return AiSongOutcome.Failed;
                default:
                    // Not cached: it is tried again next time.
                    Logger.LogWarn("AI score failed for '{File}': {Error}", Path.GetFileName(fullScdPath), result.Error ?? "unknown");
                    Failed++;
                    return AiSongOutcome.Failed;
            }
        }

        public void Dispose() => _lease?.Dispose();
    }
}
