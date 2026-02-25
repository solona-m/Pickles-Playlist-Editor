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
        private static readonly int[] EqualizerBands = [64, 250, 1000, 4000, 12000];

        enum PauseState
        {
            PAUSED,
            PLAYING,
            STOPPED
        }

        private static PauseState currentState = PauseState.STOPPED;
        public static bool IsPlaying => currentState == PauseState.PLAYING || currentState == PauseState.PAUSED;

        private static CancellationTokenSource monitorCts;
        private static string? extractedTempOggPath;

        // Added optional onEnded callback parameter
        public static void Play(string filePath, Action? onEnded = null)
        {
            // Release any existing playback handle before writing a new temporary file.
            if (currentState == PauseState.PLAYING || currentState == PauseState.PAUSED)
            {
                Stop();
            }

            CleanupExtractedTempFile();

            string extractedOgg = CreateExtractedOggPathNearScd(filePath, "now_playing");
            ScdOggExtractor.ExtractOgg(filePath, extractedOgg);
            extractedTempOggPath = extractedOgg;
            PlayOgg(extractedOgg, onEnded);
        }

        public static string CreateExtractedOggPathNearScd(string scdPath, string fileTag)
        {
            if (string.IsNullOrWhiteSpace(scdPath))
                throw new ArgumentException("SCD path is required.", nameof(scdPath));

            string? sourceDir = Path.GetDirectoryName(scdPath);
            if (string.IsNullOrWhiteSpace(sourceDir))
                sourceDir = Path.GetTempPath();

            string baseName = Path.GetFileNameWithoutExtension(scdPath);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "audio";

            string suffix = string.IsNullOrWhiteSpace(fileTag) ? "preview" : fileTag;
            return Path.Combine(sourceDir, $"{baseName}_{suffix}_{Guid.NewGuid():N}.ogg");
        }

        public static void PlayOgg(string oggPath, Action? onEnded = null)
        {
            if (currentState == PauseState.PLAYING || currentState == PauseState.PAUSED)
            {
                Stop();
            }

            currentState = PauseState.PLAYING;
            player.OpenFile(oggPath, TStreamFormat.sfOgg);
            player.SetPlayerVolume(35, 35);
            player.StartPlayback();

            StartPlaybackMonitor(onEnded);
        }

        private static void StartPlaybackMonitor(Action? onEnded)
        {
            monitorCts?.Cancel();
            monitorCts = new CancellationTokenSource();
            var ct = monitorCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            // Check playback status; break when playback is no longer active
                            var status = new TStreamStatus();
                            player.GetStatus(ref status);
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
                        catch
                        {
                            // ignore and continue polling
                        }

                        await Task.Delay(500, ct);
                    }
                }
                catch (TaskCanceledException) { currentState = PauseState.STOPPED; }

                if (currentState == PauseState.PLAYING)
                    onEnded?.Invoke();
            }, ct);
        }

        public static void Pause()
        {
            if (currentState == PauseState.PLAYING)
            {
                currentState = PauseState.PAUSED;
                player.PausePlayback();
                return;
            }

            if (currentState == PauseState.PAUSED)
            {
                currentState = PauseState.PLAYING;
                player.ResumePlayback();
            }
        }

        public static void Stop()
        {
            currentState = PauseState.STOPPED;
            monitorCts?.Cancel();
            player.StopPlayback();
            player.Close();
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
            catch (IOException)
            {
                // Ignore if the file is still in use; next stop/play will attempt cleanup again.
            }
            finally
            {
                extractedTempOggPath = null;
            }
        }

        public static void ApplyRealtimeEqualizer(EqualizerSettings settings)
        {
            if (!IsPlaying)
                return;

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

        public static void DisableRealtimeEqualizer()
        {
            if (!IsPlaying)
                return;

            player.EnableEqualizer(false);
        }

        private static int ConvertToBandGain(float gain)
        {
            return (int)Math.Round(gain);
        }

    }
}
