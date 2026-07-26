using libZPlay;
using Pickles_Playlist_Editor.Tools;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    internal static class Player
    {
        static ZPlay player = new ZPlay();
        private static readonly object _playerLock = new object();
        private static readonly int[] EqualizerBands = [64, 250, 1000, 4000, 12000];

        enum PauseState
        {
            PAUSED,
            PLAYING,
            STOPPED
        }

        // Written on the UI thread, read by the playback monitor on a thread-pool thread
        // where it is the sole gate on auto-advance — needs a barrier to be visible.
        private static volatile PauseState currentState = PauseState.STOPPED;
        public static bool IsPlaying => currentState == PauseState.PLAYING || currentState == PauseState.PAUSED;
        public static bool IsPaused => currentState == PauseState.PAUSED;

        private static int _volume = 100;

        // Nullable: cleared by the owning monitor when it disposes its own source.
        private static CancellationTokenSource? monitorCts;
        private static string? extractedTempOggPath;

        // Tracks whether libZPlay currently holds an open stream. Calling StopPlayback /
        // Close / StartPlayback against a closed stream is a native-side crash with no
        // managed exception and no stack — nothing survives it to be logged.
        private static bool _streamOpen;

        // Added optional onEnded callback parameter
        public static void Play(string filePath, Action? onEnded = null)
        {
            Utils.Logger.LogInfo("Play: requested '{File}'", filePath);

            // Release any existing playback handle before writing a new temporary file.
            if (currentState == PauseState.PLAYING || currentState == PauseState.PAUSED)
            {
                Stop();
            }

            CleanupExtractedTempFile();

            string extractedOgg = CreateExtractedOggPath(filePath, "now_playing");
            Utils.Logger.LogInfo("Play: extracting audio to '{Target}'", extractedOgg);

            ScdOggExtractor.ExtractOgg(filePath, extractedOgg);
            extractedTempOggPath = extractedOgg;

            long extractedBytes = new FileInfo(extractedOgg).Length;
            Utils.Logger.LogInfo("Play: extracted {Bytes} bytes", extractedBytes);
            if (extractedBytes == 0)
                throw new InvalidOperationException("The extracted audio was empty — the SCD may be corrupt or in an unsupported format.");

            PlayOgg(extractedOgg, onEnded);
        }

        /// <summary>
        /// Builds the path for the temporary decoded OGG.
        /// Deliberately NOT next to the source SCD: the SCD lives in the Penumbra mod
        /// directory, which may be read-only or on a drive the user can't write to (an
        /// UnauthorizedAccessException there took the whole app down), and writing into
        /// the mod folder also triggers Penumbra re-compaction.
        /// </summary>
        public static string CreateExtractedOggPath(string scdPath, string fileTag)
        {
            if (string.IsNullOrWhiteSpace(scdPath))
                throw new ArgumentException("SCD path is required.", nameof(scdPath));

            string baseName = Path.GetFileNameWithoutExtension(scdPath);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "audio";
            baseName = SanitizeForFileName(baseName);

            string suffix = string.IsNullOrWhiteSpace(fileTag) ? "preview" : fileTag;
            return Path.Combine(PlaybackTempDir, $"{baseName}_{suffix}_{Guid.NewGuid():N}.ogg");
        }

        // %LOCALAPPDATA%\PicklesPlaylistEditor\playback — always writable for the current
        // user, falls back to %TEMP% if it somehow isn't.
        private static string PlaybackTempDir
        {
            get
            {
                string dir = Path.Combine(Utils.Logger.LogDirectory, "playback");
                try
                {
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                catch (Exception ex)
                {
                    Utils.Logger.LogWarn("Play: falling back to %TEMP% ('{Dir}' unusable): {Error}", dir, ex.Message);
                    string fallback = Path.Combine(Path.GetTempPath(), "pickles-playback");
                    Directory.CreateDirectory(fallback);
                    return fallback;
                }
            }
        }

        // Song names come from meta.json and routinely contain characters that are legal
        // in a playlist but not in a path.
        private static string SanitizeForFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        public static void PlayOgg(string oggPath, Action? onEnded = null)
        {
            if (currentState == PauseState.PLAYING || currentState == PauseState.PAUSED)
            {
                Stop();
            }

            lock (_playerLock)
            {
                Utils.Logger.LogInfo("PlayOgg: opening '{File}'", oggPath);

                // Every libZPlay call returns a success flag. Ignoring it meant a failed
                // OpenFile was followed by StartPlayback against a stream that was never
                // opened — an access violation inside the native DLL, which kills the
                // process outright and cannot be caught by any managed handler.
                if (!player.OpenFile(oggPath, TStreamFormat.sfOgg))
                {
                    string error = SafeGetError();
                    currentState = PauseState.STOPPED;
                    Utils.Logger.LogError("PlayOgg: OpenFile failed for '{File}': {Error}", oggPath, error);
                    throw new InvalidOperationException($"The audio decoder could not open this track: {error}");
                }

                _streamOpen = true;
                player.SetPlayerVolume(_volume, _volume);

                if (!player.StartPlayback())
                {
                    string error = SafeGetError();
                    Utils.Logger.LogError("PlayOgg: StartPlayback failed for '{File}': {Error}", oggPath, error);
                    CloseStreamNoThrow();
                    currentState = PauseState.STOPPED;
                    throw new InvalidOperationException($"Playback could not be started: {error}");
                }

                currentState = PauseState.PLAYING;
            }

            Utils.Logger.LogInfo("PlayOgg: playback started");
            StartPlaybackMonitor(onEnded);
        }

        private static string SafeGetError()
        {
            try { return player.GetError() ?? "unknown error"; }
            catch (Exception ex) { return $"unknown error ({ex.GetType().Name})"; }
        }

        // Caller must hold _playerLock.
        private static void CloseStreamNoThrow()
        {
            if (!_streamOpen) return;
            try { player.Close(); }
            catch (Exception ex) { Utils.Logger.LogWarn("Player.Close failed: {Error}", ex.Message); }
            finally { _streamOpen = false; }
        }

        private static void StartPlaybackMonitor(Action? onEnded)
        {
            // Each monitor owns its CancellationTokenSource and disposes it on the way out.
            // Cancel alone leaked one CTS (plus its timer/callback registrations) per
            // track, but disposing the outgoing source here instead would race the task
            // still unwinding on it — Task.Delay's ct.Register would throw.
            var cts = new CancellationTokenSource();
            var previous = monitorCts;
            monitorCts = cts;
            var ct = cts.Token;

            CancelNoThrow(previous);

            Task.Run(async () =>
            {
                bool loggedPollError = false;
                try
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            try
                            {
                                // Check playback status; break when playback is no longer active
                                var status = new TStreamStatus();
                                lock (_playerLock)
                                {
                                    if (!_streamOpen) break;
                                    player.GetStatus(ref status);
                                }
                                if (!status.fPlay)
                                {
                                    break;
                                }

                                // Optionally you can also check position vs length if needed:
                                // var pos = new TStreamTime();
                                // player.GetPosition(ref pos);
                                // var info = new TStreamInfo();
                                // player.GetStreamInfo(ref info);
                                // if (info.Length.ms > 0 && pos.ms >= info.Length.ms) break;
                            }
                            catch (Exception ex)
                            {
                                // Keep polling, but don't lose the fact that it happened —
                                // repeated status failures are a symptom worth seeing in a log.
                                if (!loggedPollError)
                                {
                                    loggedPollError = true;
                                    Utils.Logger.LogWarn("Playback monitor: status poll failed: {Error}", ex.Message);
                                }
                            }

                            await Task.Delay(500, ct);
                        }
                    }
                    catch (TaskCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Utils.Logger.LogCrash("Playback monitor", ex);
                        return;
                    }

                    // Only auto-advance if this monitor is still the current one. A newer
                    // Play() cancels this token, and without the check the outgoing monitor
                    // could skip a track the user just started.
                    if (ct.IsCancellationRequested || currentState != PauseState.PLAYING) return;

                    Utils.Logger.LogInfo("Playback monitor: track ended, invoking auto-advance");
                    try
                    {
                        onEnded?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Utils.Logger.LogCrash("Playback onEnded callback", ex);
                    }
                }
                finally
                {
                    // This monitor is done with the token, so it owns the disposal.
                    // Clear the shared reference first so Stop() can't cancel a disposed source.
                    Interlocked.CompareExchange(ref monitorCts, null, cts);
                    cts.Dispose();
                }
            }, ct);
        }

        // Cancel() throws ObjectDisposedException once the owning monitor has disposed it,
        // which is an expected race rather than an error.
        private static void CancelNoThrow(CancellationTokenSource? source)
        {
            if (source == null) return;
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Toggles pause/resume. The state flips only if libZPlay actually honoured the
        /// request — flipping first meant a refused pause left the UI showing "paused"
        /// while audio kept playing, and the next click then tried to resume a stream
        /// that was never paused.
        /// </summary>
        public static void Pause()
        {
            lock (_playerLock)
            {
                if (!_streamOpen) return;

                if (currentState == PauseState.PLAYING)
                {
                    if (player.PausePlayback()) currentState = PauseState.PAUSED;
                    else Utils.Logger.LogWarn("Pause: PausePlayback refused: {Error}", SafeGetError());
                    return;
                }

                if (currentState == PauseState.PAUSED)
                {
                    if (player.ResumePlayback()) currentState = PauseState.PLAYING;
                    else Utils.Logger.LogWarn("Pause: ResumePlayback refused: {Error}", SafeGetError());
                }
            }
        }

        public static void Stop()
        {
            currentState = PauseState.STOPPED;
            CancelNoThrow(monitorCts);
            lock (_playerLock)
            {
                if (_streamOpen)
                {
                    try { player.StopPlayback(); }
                    catch (Exception ex) { Utils.Logger.LogWarn("Player.StopPlayback failed: {Error}", ex.Message); }
                    CloseStreamNoThrow();
                }
            }
            CleanupExtractedTempFile();
        }

        private static void CleanupExtractedTempFile()
        {
            if (string.IsNullOrWhiteSpace(extractedTempOggPath) || !File.Exists(extractedTempOggPath))
            {
                extractedTempOggPath = null;
                return;
            }

            try
            {
                File.Delete(extractedTempOggPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Ignore if the file is still in use; next stop/play will attempt cleanup again.
                Utils.Logger.LogWarn("Could not delete temp playback file '{File}': {Error}",
                    extractedTempOggPath, ex.Message);
            }
            finally
            {
                extractedTempOggPath = null;
            }
        }

        /// <summary>
        /// Deletes decoded OGGs left behind by a previous crash or hard kill. Safe to call
        /// at startup; failures are non-fatal by design.
        /// </summary>
        public static void PurgeStalePlaybackFiles()
        {
            try
            {
                string dir = Path.Combine(Utils.Logger.LogDirectory, "playback");
                if (!Directory.Exists(dir)) return;

                // Snapshot our own in-use file once: it's null at startup (the usual call
                // site) but this method is safe to call mid-playback too.
                string? inUse = extractedTempOggPath;

                int removed = 0, skipped = 0;
                foreach (string file in Directory.EnumerateFiles(dir, "*.ogg"))
                {
                    if (inUse != null && string.Equals(file, inUse, StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Held open by a second instance that is playing it right now.
                        // Windows refuses the delete, which is the behaviour we want —
                        // leave it for whoever owns it to clean up.
                        skipped++;
                    }
                }

                if (removed > 0 || skipped > 0)
                    Utils.Logger.LogInfo("Playback cleanup: removed {Removed}, skipped {Skipped} in-use file(s).",
                        removed, skipped);
            }
            catch (Exception ex)
            {
                Utils.Logger.LogWarn("Stale playback cleanup failed (harmless): {Error}", ex.Message);
            }
        }

        // Every one of these guards must be evaluated INSIDE _playerLock and against
        // _streamOpen, never against currentState from outside it. Checking outside is a
        // check-then-act race: Stop() can close the stream between the check and the
        // native call, and calling into libZPlay with no open stream is an access
        // violation that kills the process without producing a managed exception.

        public static TimeSpan GetPosition()
        {
            var time = new TStreamTime();
            lock (_playerLock)
            {
                if (!_streamOpen) return TimeSpan.Zero;
                player.GetPosition(ref time);
            }
            return TimeSpan.FromMilliseconds(time.ms);
        }

        public static TimeSpan GetDuration()
        {
            var info = new TStreamInfo();
            lock (_playerLock)
            {
                if (!_streamOpen) return TimeSpan.Zero;
                player.GetStreamInfo(ref info);
            }
            return TimeSpan.FromMilliseconds(info.Length.ms);
        }

        public static void Seek(TimeSpan position)
        {
            var time = new TStreamTime { ms = (uint)Math.Max(0, position.TotalMilliseconds) };
            lock (_playerLock)
            {
                if (!_streamOpen) return;
                player.Seek(TTimeFormat.tfMillisecond, ref time, TSeekMethod.smFromBeginning);
            }
        }

        public static int GetVolume() => _volume;

        public static void SetVolume(int percent)
        {
            _volume = Math.Clamp(percent, 0, 100);
            lock (_playerLock)
            {
                if (!_streamOpen) return;
                player.SetPlayerVolume(_volume, _volume);
            }
        }

        public static void ApplyRealtimeEqualizer(EqualizerSettings settings)
        {
            lock (_playerLock)
            {
                if (!_streamOpen) return;

                int[] points = (int[])EqualizerBands.Clone();
                if (!player.SetEqualizerPoints(ref points, points.Length))
                    return;

                int[] bandGains =
                [
                    ConvertToBandGain(settings.BassGain),
                    ConvertToBandGain(settings.LowMidGain),
                    ConvertToBandGain(settings.MidGain),
                    ConvertToBandGain(settings.HighMidGain),
                    ConvertToBandGain(settings.TrebleGain)
                ];

                player.EnableEqualizer(true);
                player.SetEqualizerParam(0, ref bandGains, bandGains.Length);
            }
        }

        public static void DisableRealtimeEqualizer()
        {
            lock (_playerLock)
            {
                if (!_streamOpen) return;
                player.EnableEqualizer(false);
            }
        }

        private static int ConvertToBandGain(float gain)
        {
            return (int)Math.Round(gain);
        }

    }
}
