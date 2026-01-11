using libZPlay;
using Pickles_Playlist_Editor.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        public static void Play(string filePath)
        {
            if (currentState == PauseState.PLAYING)
            {
                Stop();
            }

            currentState = PauseState.PLAYING;
            string tmpOgg = Path.Combine(System.IO.Path.GetTempPath(), "now_playing.ogg");
            ScdOggExtractor.ExtractOgg(filePath, tmpOgg);
            player.OpenFile(filePath, TStreamFormat.sfOgg);

            player.SetPlayerVolume(35, 35); // Set volume to 80%
            player.StartPlayback();
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
            player.StopPlayback();
        }
    }
}
