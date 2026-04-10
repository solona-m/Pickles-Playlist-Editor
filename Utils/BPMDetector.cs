using libZPlay;
using PersistentCollection;
using Pickles_Playlist_Editor.Tools;
using System;
using System.IO;

namespace Pickles_Playlist_Editor.Utils
{
    internal class BPMDetector
    {
        private static readonly string s_dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Pickles Playlist Editor");

        private static PersistentDictionary<string, int> bpmCache = new PersistentDictionary<string, int>(
            Path.Combine(EnsureDataDir(), "bpm_cache.dat"));
        private static PersistentDictionary<string, int> durationCache = new PersistentDictionary<string, int>(
            Path.Combine(s_dataDir, "duration_cache.dat"));

        private static string EnsureDataDir()
        {
            Directory.CreateDirectory(s_dataDir);
            return s_dataDir;
        }

        internal static bool IsFirstTimeMessage()
        {
            try
            {
                return bpmCache["FIRST_TIME_MESSAGE_SHOWN"] != 7;
            }
            catch
            {
                return true;
            }
        }

        internal static void MarkFirstTimeMessageShown()
        {
            bpmCache["FIRST_TIME_MESSAGE_SHOWN"] = 7;
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
                    finally
                    {
                        try { File.Delete(tmpOgg); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
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
