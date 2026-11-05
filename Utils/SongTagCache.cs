using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Song tags by SCD path, in two versioned JSON files with the same rules as <see cref="AiScoreCache"/>: normalised
    /// keys, tags follow moves, a generation guard for background stores.
    /// - SCD tags (song_tags_cache_v2.json): rebuildable by re-reading the SCDs; scan stores are batched.
    /// - Source tags (song_source_tags_v1.json): read from imported mp3/m4a/flac/wav files that are gone afterwards, so
    ///   irreplaceable. Looked up first.
    /// A format change gets a NEW file name and a migration, never these names; a version mismatch inside a file means
    /// corruption, and such a file is renamed aside rather than overwritten.
    /// </summary>
    internal sealed class SongTagCache
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, long> _generation = new();
        private readonly TagFile _scd, _source;

        private static readonly Lazy<SongTagCache> s_default = new(() =>
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pickles Playlist Editor");
            return new SongTagCache(Path.Combine(dir, "song_tags_cache_v2.json"), Path.Combine(dir, "song_source_tags_v1.json"));
        });

        public static SongTagCache Default => s_default.Value;

        /// <param name="sourcePath">Null: song_source_tags_v1.json beside <paramref name="scdPath"/>.</param>
        public SongTagCache(string scdPath, string? sourcePath = null)
        {
            _scd = new TagFile(scdPath, 2, "Song tag cache", _lock, source: false);
            _source = new TagFile(sourcePath ?? Path.Combine(Path.GetDirectoryName(scdPath)!, "song_source_tags_v1.json"), 1,
                "Song source tags", _lock, source: true);
        }

        public void Flush()
        {
            _scd.Flush();
            _source.Flush();
        }

        /// <summary>Source tags when an import captured them, otherwise the SCD's own.</summary>
        public SongTags? TryGet(string fullPath)
        {
            lock (_lock)
            {
                try
                {
                    string key = AiScoreCache.Key(fullPath);
                    return _source.TryGet(key) ?? _scd.TryGet(key);
                }
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

        /// <summary>SCD tags, in memory only (call <see cref="Flush"/>); refused if the path was forgotten or moved since.</summary>
        public bool StoreIfUnchanged(string fullPath, SongTags tags, long generation)
        {
            string key = AiScoreCache.Key(fullPath);
            lock (_lock)
            {
                if ((_generation.TryGetValue(key, out long g) ? g : 0) != generation) return false;
                _scd.Set(key, tags);
                return true;
            }
        }

        /// <summary>SCD tags, written through.</summary>
        public void Store(string fullPath, SongTags tags)
        {
            lock (_lock) _scd.Set(AiScoreCache.Key(fullPath), tags);
            _scd.Flush();
        }

        /// <summary>Tags read from an import's source file (#20), written through.</summary>
        public void StoreSource(string fullPath, SongTags tags)
        {
            lock (_lock) _source.Set(AiScoreCache.Key(fullPath), tags);
            _source.Flush();
        }

        /// <summary>True when the source-tag file is loaded or can be (checked before a backfill writes anything).</summary>
        public bool CanLoadSource()
        {
            lock (_lock) return _source.CanLoad();
        }

        /// <summary>
        /// Source tags for a song that shows none, in memory only (call <see cref="Flush"/>). Refused when the path was
        /// forgotten or moved since, already has source tags, its SCD tags now show artist/album/AI, or the file can't load.
        /// </summary>
        public bool StoreSourceIfUntagged(string fullPath, SongTags tags, long generation)
        {
            string key = AiScoreCache.Key(fullPath);
            lock (_lock)
            {
                if ((_generation.TryGetValue(key, out long g) ? g : 0) != generation) return false;
                if (_scd.TryGet(key) is { } s && (s.HasArtistOrAlbum || s.IsAiTagged)) return false;
                return _source.TrySetIfAbsent(key, tags);
            }
        }

        public void Forget(string fullPath)
        {
            string key = AiScoreCache.Key(fullPath);
            lock (_lock)
            {
                Bump(key);
                _scd.Remove(key);
                _source.Remove(key);
            }
            Flush();
        }

        /// <summary>Moves tags with their file, in each store on its own. A destination with no entry to move is cleared.</summary>
        public void UpdateCacheForSCD(string oldFullPath, string newFullPath)
        {
            string from = AiScoreCache.Key(oldFullPath), to = AiScoreCache.Key(newFullPath);
            if (from == to) return;
            lock (_lock)
            {
                Bump(from);
                Bump(to);
                _scd.Move(from, to);
                _source.Move(from, to);
            }
            Flush();
        }

        private void Bump(string key) => _generation[key] = (_generation.TryGetValue(key, out long g) ? g : 0) + 1;

        /// <summary>One JSON file: its map, lazy load, rename-aside of a bad file, and an atomic flush outside the lock.</summary>
        private sealed class TagFile(string path, int version, string label, object sync, bool source)
        {
            private readonly object _writeLock = new();
            private readonly Dictionary<string, SongTags> _map = new();
            private readonly HashSet<string> _removedWhileUnloaded = new(); // so a later load can't bring them back
            private bool _loaded, _dirty;
            private long _changes; // counts edits, so a flush that raced one stays dirty

            // Caller holds the lock. An I/O failure leaves it unloaded (no writes, retried on the next access).
            private bool EnsureLoaded()
            {
                if (_loaded) return true;
                Dictionary<string, SongTags> fromFile = new();
                try
                {
                    if (File.Exists(path))
                    {
                        var parsed = Parse(File.ReadAllText(path), out int? fileVersion);
                        if (parsed == null)
                        {
                            if (!SetAside(fileVersion)) return false;
                        }
                        else fromFile = parsed;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
                foreach (var (k, v) in fromFile)
                    if (!_removedWhileUnloaded.Contains(k)) _map.TryAdd(k, v); // entries stored while unloaded win
                if (_removedWhileUnloaded.Count > 0) Changed();
                _removedWhileUnloaded.Clear();
                _loaded = true;
                return true;
            }

            // Null when the file can't be used as this version; each entry is checked on its own.
            private Dictionary<string, SongTags>? Parse(string json, out int? fileVersion)
            {
                fileVersion = null;
                // JsonObject builds lazily, so a duplicate key can throw at any access: all of it counts as unreadable
                try
                {
                    if (JsonNode.Parse(json) is not JsonObject obj) return null;
                    try { fileVersion = obj["version"]?.GetValue<int>(); } catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return null; }
                    if (fileVersion != version || obj["songs"] is not JsonObject songs) return null;
                    var result = new Dictionary<string, SongTags>();
                    foreach (var (key, node) in songs)
                        if (Decode(node) is { } t) result[key] = t;
                    return result;
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException) { return null; }
            }

            private SongTags? Decode(JsonNode? node)
            {
                if (node is not JsonArray a || a.Count != 4) return null;
                var s = new string[4];
                for (int i = 0; i < 4; i++)
                {
                    if (a[i] is not JsonValue v || !v.TryGetValue(out string? text) || text == null) return null;
                    s[i] = text;
                }
                return source
                    ? new SongTags(s[0], s[1], s[2], AiGeneratorMarker.DetectText(s[3]), s[3])
                    : new SongTags(s[0], s[1], s[2], s[3]);
            }

            private JsonArray Encode(SongTags t) => new(t.Artist, t.Album, t.Title, source ? t.MarkerText : t.AiTool);

            // An unreadable or other-version file is kept beside, never overwritten. False if it can't be moved.
            private bool SetAside(int? fileVersion)
            {
                string suffix = fileVersion is int n && n != version ? $".v{n}.bak" : ".bad";
                string dest = path + suffix;
                if (File.Exists(dest)) dest = path + $".{DateTime.Now:yyyyMMdd_HHmmss_fff}" + suffix;
                try
                {
                    File.Move(path, dest);
                    Logger.LogWarn("{Label}: {File} could not be read; kept as {Dest} and starting empty", label, Path.GetFileName(path), Path.GetFileName(dest));
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogWarn("{Label}: {File} could not be read or set aside: {Error}", label, Path.GetFileName(path), ex.Message);
                    return false;
                }
            }

            public SongTags? TryGet(string key) => EnsureLoaded() && _map.TryGetValue(key, out var t) ? t : null;

            public void Set(string key, SongTags tags)
            {
                EnsureLoaded();
                _map[key] = tags;
                _removedWhileUnloaded.Remove(key);
                Changed();
            }

            public bool CanLoad() => EnsureLoaded();

            // Refused while unloaded: an unloaded Set would win over the file's entry when it loads later.
            public bool TrySetIfAbsent(string key, SongTags tags)
            {
                if (!EnsureLoaded() || _map.ContainsKey(key)) return false;
                Set(key, tags);
                return true;
            }

            public void Remove(string key)
            {
                EnsureLoaded();
                if (_map.Remove(key)) Changed();
                if (!_loaded) { _removedWhileUnloaded.Add(key); Changed(); } // the next flush loads, drops it, and saves
            }

            public void Move(string from, string to)
            {
                if (EnsureLoaded() && _map.Remove(from, out var t))
                {
                    _map[to] = t;
                    Changed();
                }
                else
                {
                    // Unloaded, or nothing to move here: neither path keeps an entry in this file.
                    Remove(from);
                    Remove(to);
                }
            }

            private void Changed() { _dirty = true; _changes++; }

            /// <summary>Temp file, then replace; written outside the shared lock. A failure is logged and stays pending.</summary>
            public void Flush()
            {
                lock (_writeLock)
                {
                    string json;
                    long snapshot;
                    lock (sync)
                    {
                        if (!_dirty || !EnsureLoaded()) return;
                        try
                        {
                            var songs = new JsonObject();
                            foreach (var (k, v) in _map) songs[k] = Encode(v);
                            json = new JsonObject { ["version"] = version, ["songs"] = songs }.ToJsonString();
                        }
                        catch (Exception ex) { Logger.LogWarn("{Label} could not be saved: {Error}", label, ex.Message); return; }
                        snapshot = _changes;
                    }
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        string tmp = path + ".tmp";
                        File.WriteAllText(tmp, json);
                        File.Move(tmp, path, true);
                        lock (sync) { if (_changes == snapshot) _dirty = false; }
                    }
                    catch (Exception ex) { Logger.LogWarn("{Label} could not be saved: {Error}", label, ex.Message); }
                }
            }
        }
    }
}
