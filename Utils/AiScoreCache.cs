using PersistentCollection;
using Pickles_Playlist_Editor.Tools;
using System;
using System.IO;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// AI scores by SCD path: 0..1000 (probability × 1000) or -1 for "no score". One file per model tag, so a new
    /// model starts empty. Keys are normalised, so "/" and "\" spellings of the same path are one entry.
    /// </summary>
    internal sealed class AiScoreCache
    {
        private readonly PersistentDictionary<string, int>? _map;
        private readonly object _lock = new();

        private static readonly Lazy<AiScoreCache> s_default = new(() => new AiScoreCache(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pickles Playlist Editor",
            $"ai_score_cache_{AiAssets.Model.Tag}.dat")));

        public static AiScoreCache Default => s_default.Value;

        public AiScoreCache(string path)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { }
            _map = TryOpenOrRecreate(path);
        }

        private static PersistentDictionary<string, int>? TryOpenOrRecreate(string path)
        {
            try { return new PersistentDictionary<string, int>(path); }
            catch
            {
                // Corrupt or from an incompatible version: start over. Without a cache, scoring still works.
                try { File.Delete(path); } catch { }
                try { return new PersistentDictionary<string, int>(path); } catch { return null; }
            }
        }

        internal static string Key(string path)
        {
            string p = path.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            try { p = Path.GetFullPath(p); } catch { } // collapses doubled separators, "." and ".."; keeps a UNC "\\"
            return p.ToLowerInvariant();
        }

        public int? TryGet(string fullPath)
        {
            if (_map == null) return null;
            lock (_lock)
            {
                try { return _map.TryGetValue(Key(fullPath), out int v) ? v : null; }
                catch { return null; }
            }
        }

        /// <summary>The score for a song's path relative to the mod, as the playlist stores it.</summary>
        public int? TryGetForRel(string relScd)
        {
            if (string.IsNullOrWhiteSpace(relScd)) return null;
            try { return TryGet(Path.Combine(Settings.PenumbraLocation, Settings.ModName, Playlist.NormalizeRelativeModPath(relScd))); }
            catch { return null; }
        }

        public void Store(string fullPath, int value)
        {
            if (_map == null) return;
            lock (_lock)
            {
                try { _map[Key(fullPath)] = value; } catch { }
            }
        }

        public void Forget(string fullPath)
        {
            if (_map == null) return;
            lock (_lock)
            {
                try { _map.Remove(Key(fullPath)); } catch { }
            }
        }

        /// <summary>Moves a score with its file. A destination with no source score is cleared, so it can't keep a stale one.</summary>
        public void UpdateCacheForSCD(string oldFullPath, string newFullPath)
        {
            if (_map == null) return;
            string from = Key(oldFullPath), to = Key(newFullPath);
            if (from == to) return;
            lock (_lock)
            {
                try
                {
                    if (_map.TryGetValue(from, out int v))
                    {
                        _map[to] = v;
                        _map.Remove(from);
                    }
                    else _map.Remove(to);
                }
                catch { }
            }
        }
    }
}
