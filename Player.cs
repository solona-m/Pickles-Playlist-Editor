using libZPlay;
using Pickles_Playlist_Editor.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    internal static class Player
    {
        static ZPlay player = new ZPlay();

        enum PauseState
        {
            PAUSED,
            PLAYING,
            STOPPED
        }

        private static PauseState currentState = PauseState.STOPPED;

        public static event Action PlaybackEnded;

        private static CancellationTokenSource monitorCts;

        // Added optional onEnded callback parameter
        public static void Play(string filePath, Action? onEnded = null)
        {
            if (currentState == PauseState.PLAYING)
            {
                Stop();
            }

            currentState = PauseState.PLAYING;
            string tmpOgg = Path.Combine(System.IO.Path.GetTempPath(), "now_playing.ogg");
            ScdOggExtractor.ExtractOgg(filePath, tmpOgg);

            // open the extracted OGG for playback
            player.OpenFile(tmpOgg, TStreamFormat.sfOgg);

            player.SetPlayerVolume(35, 35); // Set volume to 80%
            player.StartPlayback();

            // If caller provided an onEnded callback, subscribe once and auto-unsubscribe
            if (onEnded != null)
            {
                Action? handler = null;
                handler = () =>
                {
                    try
                    {
                        onEnded();
                    }
                    finally
                    {
                        if (handler != null) PlaybackEnded -= handler;
                    }
                };
                PlaybackEnded += handler;
            }

            // start monitor
            StartPlaybackMonitor();
        }

        private static void StartPlaybackMonitor()
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
                catch (TaskCanceledException) { /* canceled */ }

                if (currentState == PauseState.PLAYING)
                    PlaybackEnded?.Invoke();
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
            else
            {
                currentState = PauseState.PLAYING;
                player.ResumePlayback();
                return;
            }
        }

        public static void Stop()
        {
            currentState = PauseState.STOPPED;
            monitorCts?.Cancel();
            player.StopPlayback();
            player.Close();
        }

    }
}
