using libZPlay;
using PersistentCollection;
using Pickles_Playlist_Editor.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TaskbarClock;

namespace Pickles_Playlist_Editor.Utils
{
    internal class BPMDetector
    {
        private static PersistentDictionary<string, int> bpmCache = new PersistentDictionary<string, int>("bpm_cache.dat");
        private static PersistentDictionary<string, int> durationCache = new PersistentDictionary<string, int>("duration_cache.dat");

        internal static bool ShowFirstTimeMessage()
        {
            try
            {
                if (bpmCache["FIRST_TIME_MESSAGE_SHOWN"] == 7)
                    return false;
            }
            catch (Exception ex)
            {
            }

            System.Windows.Forms.MessageBox.Show("BPM and length detection may take some time depending on the length and number of tracks being analyzed. Values are cached for faster access in the future. Do not close the app until this completes.");

            bpmCache["FIRST_TIME_MESSAGE_SHOWN"] = 7;
            return true;
        }

        private struct SongAttributes
        {
            public int BPM;
            public TimeSpan Duration;
        }

        static private SongAttributes GetAttribtesFromFile(string oggFile)
        {
            ZPlay player = null;
            try
            {
                player = new ZPlay();
                int duration = 0;

                // Open the OGG file
                if (!player.OpenFile(oggFile, TStreamFormat.sfOgg))
                {
                    Console.WriteLine("Failed to open audio file.");
                    return new SongAttributes();
                }

                // Attempt to get stream info (duration) and cache it
                try
                {
                    var info = new TStreamInfo();
                    player.GetStreamInfo(ref info);
                    // info.Length.ms holds length in milliseconds
                    var ms = info.Length.ms;
                    if (ms > 0)
                    {
                        duration = (int)Math.Round(ms / 1000.0);
                    }
                }
                catch
                {
                    // ignore failures to obtain duration but continue BPM detection
                }

                // Detect BPM
                int retval = 0;

                // Parameters: using peak method (tbmDefault recommended in libZPlay docs)
                try
                {
                    retval = player.DetectBPM(TBPMDetectionMethod.dmPeaks);
                }
                catch
                {
                    retval = 0;
                }

                return new SongAttributes() { BPM = retval, Duration = new TimeSpan(0, 0, duration) };
            }
            catch (Exception ex)
            {
                return new SongAttributes();
            }
            finally
            {
                player?.Close();
            }
        }

        public static TimeSpan GetDuration(string scdFile)
        {
            try
            {
                string path = Path.Combine(Settings.PenumbraLocation, Settings.ModName, scdFile);
                if (durationCache.ContainsKey(path))
                {
                    return new TimeSpan(0, 0, durationCache[path]);
                }
                else
                {
                    string tmpOgg = Path.Combine(System.IO.Path.GetTempPath(), "temp_extracted.ogg");
                    try
                    {
                        ScdOggExtractor.ExtractOgg(path, tmpOgg);
                        SongAttributes retval = GetAttribtesFromFile(tmpOgg);
                        bpmCache[path] = retval.BPM;
                        durationCache[path] = (int)retval.Duration.TotalSeconds;

                        return retval.Duration;
                    }
                    catch
                    {
                        return new TimeSpan(0);
                    }
                }
            }
            catch (Exception ex)
            {

                MessageBox.Show(ex.ToString());
                return new TimeSpan(0);
            }
        }

        public static void UpdateCacheForSCD(string oldFullPath, string newFullPath)
        {
            try
            {
                if (bpmCache.ContainsKey(oldFullPath))
                {
                    bpmCache[newFullPath] = bpmCache[oldFullPath];
                    bpmCache.Remove(oldFullPath);
                }
                if (durationCache.ContainsKey(oldFullPath))
                {
                    durationCache[newFullPath] = durationCache[oldFullPath];
                    durationCache.Remove(oldFullPath);
                }
            }
            catch (Exception ex)
            {
                // ignore cache update failures
            }
        }

        public static int GetBPMFromSCD(string scdFile)
        {
            try
            {
                string path = Path.Combine(Settings.PenumbraLocation, Settings.ModName, scdFile);

                if (bpmCache.ContainsKey(path))
                {
                    return bpmCache[path];
                }

                string tmpOgg = Path.Combine(System.IO.Path.GetTempPath(), "temp_extracted.ogg");
                ScdOggExtractor.ExtractOgg(path, tmpOgg);
                SongAttributes retval = GetAttribtesFromFile(tmpOgg);
                bpmCache[path] = retval.BPM;
                durationCache[path] = (int)retval.Duration.TotalSeconds;
                return retval.BPM;
            }
            catch (Exception ex)
            {
                return 0;
            }
        }
    }
}
