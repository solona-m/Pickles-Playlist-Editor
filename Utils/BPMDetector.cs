using libZPlay;
using PersistentCollection;
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
        private static PersistentDictionary<string, int> bpmCache = new PersistentDictionary<string, int>("bpm_cache.dat");

        static private int GetBPMFromFile(string oggFile)
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

            if (bpmCache.ContainsKey(path))
            {
                return bpmCache[path];
            }

            string tmpOgg = Path.Combine(System.IO.Path.GetTempPath(), "temp_extracted.ogg");
            ScdOggExtractor.ExtractOgg(path, tmpOgg);
            int retval = GetBPMFromFile(tmpOgg);
            bpmCache[path] = retval;
            return retval;
        }
    }

    
}
