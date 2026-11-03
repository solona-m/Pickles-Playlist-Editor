using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Song tags by SCD path, in a versioned JSON file. Same rules as <see cref="AiScoreCache"/>: normalised keys,
    /// tags follow moves, a generation guard for background stores. Scan stores are batched and flushed atomically.
    /// </summary>
    internal sealed class SongTagCache
    {
        private const int Version = 1;
        private readonly string _path;
        private readonly object _lock = new();
        private readonly Dictionary<string, SongTags> _map = new();
        private readonly Dictionary<string, long> _generation = new();
        private bool _loaded, _dirty;

        private static readonly Lazy<SongTagCache> s_default = new(() => new SongTagCache(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pickles Playlist Editor", "song_tags_cache_v1.json")));

        public static SongTagCache Default => s_default.Value;

        public SongTagCache(string path) => _path = path;

        // Caller holds _lock. A missing, unparseable or other-version file loads empty; an I/O failure leaves it
        // unloaded (no writes, retried on the next access) so a locked file is never overwritten with less.
        private bool EnsureLoaded()
        {
            if (_loaded) return true;
            Dictionary<string, SongTags> fromFile = new();
            try
            {
                if (File.Exists(_path)) fromFile = Parse(File.ReadAllText(_path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            catch { fromFile = new(); }
            foreach (var (k, v) in fromFile) _map.TryAdd(k, v); // entries stored while unloaded win
            _loaded = true;
            return true;
        }

        private static Dictionary<string, SongTags> Parse(string json)
        {
            var result = new Dictionary<string, SongTags>();
            var root = JsonNode.Parse(json);
            if (root?["version"]?.GetValue<int>() != Version || root["songs"] is not JsonObject songs) return result;
            foreach (var (key, node) in songs)
                if (node is JsonArray a && a.Count == 3)
                    result[key] = new SongTags(a[0]?.GetValue<string>() ?? "", a[1]?.GetValue<string>() ?? "", a[2]?.GetValue<string>() ?? "");
            return result;
        }

        /// <summary>Writes pending changes: temp file, then replace. A failure is logged and stays pending.</summary>
        public void Flush()
        {
            lock (_lock)
            {
                if (!_dirty || !EnsureLoaded()) return;
                try
                {
                    var songs = new JsonObject();
                    foreach (var (k, v) in _map) songs[k] = new JsonArray(v.Artist, v.Album, v.Title);
                    var root = new JsonObject { ["version"] = Version, ["songs"] = songs };
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    string tmp = _path + ".tmp";
                    File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                    File.Move(tmp, _path, true);
                    _dirty = false;
                }
                catch (Exception ex) { Logger.LogWarn("Song tag cache could not be saved: {Error}", ex.Message); }
            }
        }

        public SongTags? TryGet(string fullPath)
        {
            lock (_lock)
            {
                try { return EnsureLoaded() && _map.TryGetValue(AiScoreCache.Key(fullPath), out var t) ? t : null; }
                catch { return null; }
            }
        }

        /// <summary>Tags for a song's path relative to the mod. Never throws.</summary>
        public SongTags? TryGetForRel(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return null;
            try { return TryGet(AiScoreCache.FullPathOf(rel)); }
            catch { return null; }
        }

        /// <summary>As <see cref="TryGetForRel(string)"/> with the mod root resolved once by the caller; null root falls back.</summary>
        public SongTags? TryGetForRel(string rel, string? modRoot)
        {
            if (modRoot == null) return TryGetForRel(rel);
            if (string.IsNullOrWhiteSpace(rel)) return null;
            try { return TryGet(FullPathOf(rel, modRoot)); }
            catch { return null; }
        }

        /// <summary>The same path <see cref="AiScoreCache.FullPathOf"/> builds, with an explicit mod root.</summary>
        public static string FullPathOf(string rel, string modRoot) => Path.Combine(modRoot, Playlist.NormalizeRelativeModPath(rel));

        /// <summary>The mod folder, or null when it isn't configured.</summary>
        public static string? ModRootOrNull(string? penumbraLocation, string? modName)
        {
            if (string.IsNullOrWhiteSpace(penumbraLocation) || string.IsNullOrWhiteSpace(modName)) return null;
            try { return Path.Combine(penumbraLocation, modName); }
            catch { return null; }
        }

        public long Generation(string fullPath)
        {
            lock (_lock) return _generation.TryGetValue(AiScoreCache.Key(fullPath), out long g) ? g : 0;
        }

        /// <summary>In memory only (call <see cref="Flush"/>); refused if the path was forgotten or moved since.</summary>
        public bool StoreIfUnchanged(string fullPath, SongTags tags, long generation)
        {
            string key = AiScoreCache.Key(fullPath);
            lock (_lock)
            {
                if ((_generation.TryGetValue(key, out long g) ? g : 0) != generation) return false;
                EnsureLoaded();
                _map[key] = tags;
                _dirty = true;
                return true;
            }
        }

        public void Store(string fullPath, SongTags tags)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _map[AiScoreCache.Key(fullPath)] = tags;
                _dirty = true;
            }
            Flush();
        }

        public void Forget(string fullPath)
        {
            string key = AiScoreCache.Key(fullPath);
            lock (_lock)
            {
                Bump(key);
                EnsureLoaded();
                if (_map.Remove(key)) _dirty = true;
            }
            Flush();
        }

        /// <summary>Moves tags with their file. A destination with no source entry is cleared.</summary>
        public void UpdateCacheForSCD(string oldFullPath, string newFullPath)
        {
            string from = AiScoreCache.Key(oldFullPath), to = AiScoreCache.Key(newFullPath);
            if (from == to) return;
            lock (_lock)
            {
                Bump(from);
                Bump(to);
                EnsureLoaded();
                if (_map.Remove(from, out var t)) { _map[to] = t; _dirty = true; }
                else if (_map.Remove(to)) _dirty = true;
            }
            Flush();
        }

        private void Bump(string key) => _generation[key] = (_generation.TryGetValue(key, out long g) ? g : 0) + 1;
    }
}
