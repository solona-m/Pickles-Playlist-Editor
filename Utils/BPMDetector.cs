using libZPlay;
using Pickles_Playlist_Editor.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Utils
{
    internal class BPMDetector
    {
        static public int GetBPM(string oggFile)
        {
            ZPlay player = null;
            try
            { 
                player = new ZPlay();

                // Open the OGG file
                if (!player.OpenFile(oggFile, TStreamFormat.sfOgg))
                {
                    Console.WriteLine("Failed to open audio file.");
                    return 0;
                }

                // Detect BPM
                int bpm = 0;
                int beats = 0;

                // Parameters:
                //   startSec = 0  → analyze from beginning
                //   endSec   = 0  → analyze full track
                //   flags    = tbmDefault (recommended)
                int retval = player.DetectBPM(
                    TBPMDetectionMethod.dmPeaks);
                return retval;
            }
            catch (Exception ex)
            {
                return 0;
            }
            finally
            {
                player?.Close();
            }   
        }

        public static int GetBPMFromSCD(string scdFile)
        {
            string path = Path.Combine(Settings.PenumbraLocation, Settings.ModName, scdFile);
            string tmpOgg = Path.Combine(System.IO.Path.GetTempPath(), "temp_extracted.ogg");
            ScdOggExtractor.ExtractOgg(path, tmpOgg);
            return GetBPM(tmpOgg);
        }
    }

    
}
