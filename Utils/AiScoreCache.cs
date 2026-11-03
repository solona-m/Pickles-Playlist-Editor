using PersistentCollection;
using Pickles_Playlist_Editor.Tools;
using System;
using System.Collections.Generic;
using System.IO;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// AI scores by SCD path: 0..1000 (probability × 1000) or -1 for "no score". One file per model tag, so a new
    /// model starts empty. Keys are normalised, so "/" and "\" spellings of the same path are one entry.
    /// </summary>
    internal sealed class AiScoreCache
    {
        private readonly string _path;
        private PersistentDictionary<string, int>? _map;
        private bool _opened;
        private readonly object _lock = new();
        private readonly Dictionary<string, long> _generation = new(); // bumped by Forget and moves, per key

        private static readonly Lazy<AiScoreCache> s_default = new(() => new AiScoreCache(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pickles Playlist Editor",
            $"ai_score_cache_{AiAssets.Model.Tag}.dat")));

        public static AiScoreCache Default => s_default.Value;

        public AiScoreCache(string path) => _path = path;

        /// <summary>Whether this model's scores have been stored before (a new model tag starts with no file).</summary>
        public bool HasStore => File.Exists(_path);

        // Opened on first use. Lookups, moves and forgets on a cache that was never written create no file, so a
        // user who never turned the score on gets none. Caller holds _lock.
        private PersistentDictionary<string, int>? Map(bool create)
        {
            if (_opened) return _map;
            if (!create && !File.Exists(_path)) return null;
            try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); } catch { }
            _map = TryOpenOrRecreate(_path);
            _opened = true;
            return _map;
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
            lock (_lock)
            {
                try { return Map(false) is { } m && m.TryGetValue(Key(fullPath), out int v) ? v : null; }
                catch { return null; }
            }
        }

        /// <summary>The score for a song's path relative to the mod, as the playlist stores it.</summary>
        public int? TryGetForRel(string relScd)
        {
            if (string.IsNullOrWhiteSpace(relScd)) return null;
            try { return TryGet(FullPathOf(relScd)); }
            catch { return null; }
        }

        /// <summary>A song's SCD path as every AI-score caller spells it.</summary>
        public static string FullPathOf(string relScd) =>
            Path.Combine(Settings.PenumbraLocation, Settings.ModName, Playlist.NormalizeRelativeModPath(relScd));

        /// <summary>Taken before scoring a path; <see cref="StoreIfUnchanged"/> refuses if the path changed hands since.</summary>
        public long Generation(string fullPath)
        {
            lock (_lock) return _generation.TryGetValue(Key(fullPath), out long g) ? g : 0;
        }

        /// <summary>Stores unless the path was forgotten or moved since <paramref name="generation"/> (another song may own it now).</summary>
        public bool StoreIfUnchanged(string fullPath, int value, long generation)
        {
            string key = Key(fullPath);
            lock (_lock)
            {
                if ((_generation.TryGetValue(key, out long g) ? g : 0) != generation) return false;
                try { if (Map(true) is { } m) m[key] = value; } catch { }
                return true;
            }
        }

        private void Bump(string key) => _generation[key] = (_generation.TryGetValue(key, out long g) ? g : 0) + 1;

        public void Store(string fullPath, int value)
        {
            lock (_lock)
            {
                try { if (Map(true) is { } m) m[Key(fullPath)] = value; } catch { }
            }
        }

        public void Forget(string fullPath)
        {
            string key = Key(fullPath);
            lock (_lock)
            {
                Bump(key);
                try { Map(false)?.Remove(key); } catch { }
            }
        }

        /// <summary>Moves a score with its file. A destination with no source score is cleared, so it can't keep a stale one.</summary>
        public void UpdateCacheForSCD(string oldFullPath, string newFullPath)
        {
            string from = Key(oldFullPath), to = Key(newFullPath);
            if (from == to) return;
            lock (_lock)
            {
                Bump(from);
                Bump(to);
                try
                {
                    if (Map(false) is not { } m) return;
                    if (m.TryGetValue(from, out int v))
                    {
                        m[to] = v;
                        m.Remove(from);
                    }
                    else m.Remove(to);
                }
                catch { }
            }
        }
    }
}
