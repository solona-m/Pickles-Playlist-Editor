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

        private static PersistentDictionary<string, int> bpmCache;
        private static PersistentDictionary<string, int> durationCache;

        static BPMDetector()
        {
            string dataDir = EnsureDataDir();
            bpmCache = TryOpenOrRecreate(Path.Combine(dataDir, "bpm_cache.dat"));
            durationCache = TryOpenOrRecreate(Path.Combine(dataDir, "duration_cache.dat"));
        }

        private static PersistentDictionary<string, int> TryOpenOrRecreate(string path)
        {
            try
            {
                return new PersistentDictionary<string, int>(path);
            }
            catch
            {
                // Cache file may be corrupt or from an incompatible version — delete and retry.
                try { File.Delete(path); } catch { }
                try { return new PersistentDictionary<string, int>(path); } catch { }
                return null;
            }
        }

        private static string EnsureDataDir()
        {
            Directory.CreateDirectory(s_dataDir);
            return s_dataDir;
        }

        private struct SongAttributes
        {
            public int BPM;
            public TimeSpan Duration;
        }

        static private SongAttributes GetAttribtesFromFile(string oggFile)
        {
            var r = OggAnalysis.Analyze(oggFile);
            return new SongAttributes() { BPM = r.Bpm, Duration = r.Duration };
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
                    try
                    {
                        SongAttributes retval = ScdOggExtractor.WithExtractedOgg(path, GetAttribtesFromFile);
                        StatsCacheWrites.Write(() => bpmCache[path] = retval.BPM);
                        StatsCacheWrites.Write(() => durationCache[path] = (int)retval.Duration.TotalSeconds);

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
                    StatsCacheWrites.Write(() => bpmCache[newFullPath] = bpmCache[oldFullPath]);
                    StatsCacheWrites.Write(() => bpmCache.Remove(oldFullPath));
                }
                if (durationCache.ContainsKey(oldFullPath))
                {
                    StatsCacheWrites.Write(() => durationCache[newFullPath] = durationCache[oldFullPath]);
                    StatsCacheWrites.Write(() => durationCache.Remove(oldFullPath));
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

                SongAttributes retval = ScdOggExtractor.WithExtractedOgg(path, GetAttribtesFromFile);
                StatsCacheWrites.Write(() => bpmCache[path] = retval.BPM);
                StatsCacheWrites.Write(() => durationCache[path] = (int)retval.Duration.TotalSeconds);
                return retval.BPM;
            }
            catch (Exception ex)
            {
                return 0;
            }
        }

        internal static int? TryGetCachedBpm(string scdFile)
        {
            try
            {
                string path = Path.Combine(Settings.PenumbraLocation, Settings.ModName, scdFile);
                return bpmCache.ContainsKey(path) ? bpmCache[path] : (int?)null;
            }
            catch
            {
                return null;
            }
        }

        internal static TimeSpan? TryGetCachedDuration(string scdFile)
        {
            try
            {
                string path = Path.Combine(Settings.PenumbraLocation, Settings.ModName, scdFile);
                return durationCache.ContainsKey(path) ? new TimeSpan(0, 0, durationCache[path]) : (TimeSpan?)null;
            }
            catch
            {
                return null;
            }
        }
    }
}
