using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VfxEditor.ScdFormat;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Pickles_Playlist_Editor
{
    enum SortDirection
    {
        Ascending,
        Descending
    }

    // Thrown when a playlist's group cannot be located in Penumbra's manifest, so a write has nowhere
    // to land. Callers should treat this as "the edit did not happen" and roll back.
    public class PlaylistSaveException : InvalidOperationException
    {
        public string PlaylistName { get; }

        public PlaylistSaveException(string playlistName)
            : base($"Could not save playlist '{playlistName}': it is no longer in Penumbra's mod " +
                   "manifest (meta.json). The change was not applied. (If Penumbra is running, it may " +
                   "have rewritten the mod — reload the mod list and try again.)")
        {
            PlaylistName = playlistName;
        }
    }

    /// <summary>
    /// One Penumbra option group — for this app, one playlist of songs.
    ///
    /// Penumbra has two live layouts and this class works with both, delegating all persistence to
    /// <see cref="PlaylistStore.Current"/>:
    ///   v4 — every group lives in the single root <c>meta.json</c>, identified by a stable
    ///        <c>Id</c> GUID, ordered by its index in the <c>Groups</c> array.
    ///   v3 — one <c>group_NNN_name.json</c> per group, no Ids at all; identity is the <c>Name</c>
    ///        INSIDE the file and order is the NNN in its filename.
    ///
    /// Either way this class never serializes itself wholesale. A write re-reads storage and splices
    /// in just this one group, so a stale in-memory model can never clobber a playlist it doesn't
    /// represent. See <see cref="Save"/>.
    /// </summary>
    public class Playlist
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public List<Option> Options { get; set; } = new List<Option>();

        // The group object this playlist was read from — a node of meta.json under v4, the whole
        // parsed group file under v3. Kept so a save can clone it and preserve keys this app doesn't
        // model: Description, Image, Page, DefaultSettings, Priority, v3's Version, and anything a
        // future Penumbra adds. Null for a playlist that hasn't been persisted yet.
        [JsonIgnore]
        internal JObject Raw { get; set; }

        // The Name as it currently reads ON DISK. Set when a store loads this playlist, and again
        // after a successful write. Rename() changes Name in memory and only then saves — and under
        // v3 the durable identity IS the content Name, so without this a rename's own save could no
        // longer find its file. Null for a playlist that has never been persisted.
        [JsonIgnore]
        internal string PersistedName { get; set; }

        [JsonIgnore]
        public string Type => Raw?["Type"]?.ToString() ?? "Single";

        private bool? _isVFXGroup;

        public static void Create(string playlistName, string dir, Action<int>? callback)
        {
            if (playlistName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Playlist name cannot contain any of the following characters: "
                    + string.Join(" ", Path.GetInvalidFileNameChars()));

            // Adding to an existing playlist of the same name must keep its Id (and its songs'), or
            // Penumbra loses the user's current selection for it.
            Playlist group = GetAll().TryGetValue(playlistName, out var existing)
                ? existing
                : NewPlaylist(playlistName);

            if (!string.IsNullOrEmpty(dir))
            {
                // Enumerated once instead of twice. The old counting pass and importing pass walked
                // the tree separately, so any filtering had to be kept in step across both or the
                // progress bar would drift; one list cannot disagree with itself.
                var files = EnumerateImportableFiles(dir);
                var referenced = ReferencedScdPaths();

                int count = 0;
                foreach (string file in files)
                {
                    AddFiles(playlistName, group, file, referenced);
                    if (callback != null)
                        callback((int)((float)(++count) / files.Count * 100));
                }
            }

            group.Upsert();
            Directory.CreateDirectory(Path.Combine(Settings.PenumbraLocation, Settings.ModName,
                ResolvePlaylistScdDirectory(playlistName, group)));

            // Notify Penumbra (if present) to refresh this mod because files/config changed.
            RefreshPenumbraMod();
        }

        /// <summary>
        /// Every supported audio file under <paramref name="dir"/>, in the extension order
        /// <see cref="Settings.SupportedFileTypes"/> declares, minus this app's own playback previews.
        ///
        /// The exclusion is not hypothetical tidiness. A user rebuilding a lost library pointed New
        /// Playlist at a folder holding a handful of them and got playlist entries named
        /// "Faith No More - Epic_now_playing_0ec27e89434f400d9866b88648b88d32" — the app importing its
        /// own scratch files as though they were music, twice, because the recursion sweeps up
        /// everything with a supported extension.
        /// </summary>
        private static List<string> EnumerateImportableFiles(string dir)
        {
            var files = new List<string>();
            foreach (string ext in Settings.SupportedFileTypes)
                files.AddRange(Directory.GetFiles(dir, "*" + ext, SearchOption.AllDirectories));

            int before = files.Count;
            files.RemoveAll(Player.IsExtractedPlaybackFile);

            int skipped = before - files.Count;
            if (skipped > 0)
                Logger.LogInfo("Import: skipped {Count} of this app's own playback preview file(s) " +
                    "found under '{Dir}'.", skipped, dir);

            return files;
        }

        private static Playlist NewPlaylist(string playlistName)
        {
            var group = new Playlist { Id = Guid.NewGuid(), Name = playlistName };
            group.Options.Add(new Option { Id = Guid.NewGuid(), Name = "Off" });
            return group;
        }

        /// <summary>
        /// Every .scd path any playlist in this mod currently points at, normalized for comparison.
        ///
        /// Built once per import batch and handed to <see cref="AddFiles"/>, which will only reuse a
        /// file that appears nowhere in it. That restriction is what keeps reuse safe: deleting a song
        /// deletes its .scd outright, with no check for other options referencing it, so two entries
        /// sharing one file means deleting either destroys the audio the other still needs. Reuse is
        /// therefore confined to ORPHANED audio — which is exactly the case it exists for, rebuilding
        /// a playlist from the .scd files a lost group file left behind.
        ///
        /// Entries are added as the batch proceeds, so a source listed twice in one import does not
        /// have its second copy reuse the file the first just wrote.
        /// </summary>
        private static HashSet<string> ReferencedScdPaths()
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var playlist in GetAll().Values)
                {
                    if (playlist.Options == null) continue;
                    foreach (var option in playlist.Options)
                    {
                        if (option?.Files == null) continue;
                        foreach (string path in option.Files.Values)
                            referenced.Add(NormalizeRelativeModPath(path));
                    }
                }
            }
            catch (Exception ex)
            {
                // An empty set only costs the optimization — every import then writes its own copy,
                // which is what this code did before reuse existed. Never worth failing an import for.
                Logger.LogWarn("Import: could not list referenced audio, so nothing will be reused " +
                    "this batch: {Error}", ex.Message);
            }
            return referenced;
        }

        static Option AddFiles(string playlistName, Playlist group, string file,
            HashSet<string> referencedScdPaths = null)
        {
            Option opt = null;
            try
            {
                ScdFile scdFile = ScdFile.Import(file);
                string filenameroot = Path.GetFileNameWithoutExtension(file);
                if (filenameroot.Equals("bpmloop", StringComparison.OrdinalIgnoreCase))
                {
                    filenameroot = Path.GetDirectoryName(file);
                    filenameroot = filenameroot.Split(Path.DirectorySeparatorChar).Last();
                }
                string playlistScdDirectory = ResolvePlaylistScdDirectory(playlistName, group);
                string outDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, playlistScdDirectory);
                Directory.CreateDirectory(outDir);

                string safeFileName = SanitizeFileName(Path.GetFileName(filenameroot));
                if (string.IsNullOrWhiteSpace(safeFileName))
                    safeFileName = "audio";

                // Encoded up front rather than straight to the file, so it can be compared against
                // what is already sitting at the target name. Importing re-encodes — the bytes here
                // are not the source file's — so comparing our OUTPUT to the existing file is the
                // only test that answers "is this already in the mod".
                byte[] encoded;
                using (var buffer = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
                        scdFile.Write(writer);
                    encoded = buffer.ToArray();
                }

                string desiredPath = Path.Combine(outDir, safeFileName + ".scd");
                string desiredRelative = NormalizeRelativeModPath(
                    Path.Combine(playlistScdDirectory, Path.GetFileName(desiredPath)));

                // Three conditions, all necessary. The file has to be there, it has to be the same
                // audio byte for byte, and — the one that makes this safe — nothing may already point
                // at it. See ReferencedScdPaths.
                bool reused = referencedScdPaths != null
                    && !referencedScdPaths.Contains(desiredRelative)
                    && File.Exists(desiredPath)
                    && FileHasBytes(desiredPath, encoded);

                string targetPath;
                if (reused)
                {
                    // Byte-for-byte what is already there and referenced by nothing, so writing a
                    // "_1" beside it would buy a second copy of the same audio and nothing else. This
                    // is the shape of rebuilding a lost playlist from the orphaned .scd files still in
                    // the mod folder: every track re-imports onto itself, and one user's mod grew
                    // "X_1.scd" and then "X_1_1.scd" across two attempts before the playlist came back.
                    targetPath = desiredPath;
                }
                else
                {
                    targetPath = GetNonCollidingPath(desiredPath);
                    // CreateNew, not a plain write: GetNonCollidingPath says the name is free, and if
                    // something claimed it in between, failing is right. Nothing here may clobber
                    // audio another playlist is pointing at.
                    using var stream = new FileStream(targetPath, FileMode.CreateNew);
                    stream.Write(encoded, 0, encoded.Length);
                }

                string targetRelative = Path.Combine(playlistScdDirectory, Path.GetFileName(targetPath));

                // Claimed for the rest of the batch, whether written or reused, so a source file
                // listed twice cannot have its second occurrence reuse what its first just produced.
                referencedScdPaths?.Add(NormalizeRelativeModPath(targetRelative));

                opt = new Option();
                opt.Name = filenameroot;
                opt.Files.Add(Settings.BaselineScdKey, targetRelative);
                group.Options.Add(opt);

                if (reused)
                    Logger.LogInfo("Imported '{Source}' -> {Target} (playlist '{Playlist}') — reused " +
                        "the identical copy already in this mod instead of duplicating it.",
                        Path.GetFileName(file), Path.GetFileName(targetPath), playlistName);
                else
                    Logger.LogInfo("Imported '{Source}' -> {Target} (playlist '{Playlist}')",
                        Path.GetFileName(file), Path.GetFileName(targetPath), playlistName);

                try
                {
                    string scdPath = Playlist.GetScdPath(opt);
                    int bpm = BPMDetector.GetBPMFromSCD(scdPath);
                    string key = KeyDetector.GetKeyFromSCD(scdPath);
                    TimeSpan duration = BPMDetector.GetDuration(scdPath);
                    OptionStatsNaming.UpdateName(opt, bpm, key, duration);
                }
                catch
                {
                    // Stats detection failure must not block import.
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed importing '{File}' into '{Playlist}': {Error}", file, playlistName, ex);
                throw new InvalidOperationException("Error adding file " + file + ": " + ex.Message, ex);
            }
            return opt;
        }

        /// <summary>
        /// Whether the file at <paramref name="path"/> is exactly <paramref name="expected"/>.
        ///
        /// Streamed rather than read whole: a track is megabytes, and the overwhelmingly common answer
        /// is "no" at the length check, before a single byte is read. An unreadable file answers false
        /// — the caller then writes a fresh copy, which is the safe way to be wrong.
        /// </summary>
        private static bool FileHasBytes(string path, byte[] expected)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != expected.Length) return false;

                using var stream = File.OpenRead(path);
                byte[] chunk = new byte[64 * 1024];
                int offset = 0, read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (offset + read > expected.Length) return false;
                    if (!chunk.AsSpan(0, read).SequenceEqual(expected.AsSpan(offset, read)))
                        return false;
                    offset += read;
                }
                return offset == expected.Length;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolvePlaylistScdDirectory(string playlistName, Playlist group)
        {
            var directories = group.Options?
                .Where(option => option?.Files != null)
                .SelectMany(option => option.Files.Values)
                .Select(NormalizeRelativeModPath)
                .Where(path => path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetDirectoryName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .GroupBy(path => path!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(grouping => grouping.Count())
                .ThenBy(grouping => grouping.Key, StringComparer.OrdinalIgnoreCase)
                .Select(grouping => grouping.Key)
                .FirstOrDefault();

            return directories ?? GetDefaultPlaylistDirectoryName(playlistName);
        }

        internal string GetScdDirectoryForNewFiles() => ResolvePlaylistScdDirectory(Name, this);

        private static string GetDefaultPlaylistDirectoryName(string playlistName) =>
            playlistName.Replace("/", "_");

        internal static string NormalizeRelativeModPath(string? path) =>
            (path ?? string.Empty)
                .Trim()
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);

        public void Cleanup()
        {
            Playlist playlist = this;

            // Bail before renaming anything if the save can't land. Cleanup renames .scd files on
            // disk and only then writes the manifest that points at the new names — if that write were
            // dropped, every song in this playlist would end up pointing at a file that no longer
            // exists under that name.
            if (!ExistsInManifest())
                throw new PlaylistSaveException(Name);

            string playlistScdDirectory = playlist.GetScdDirectoryForNewFiles();
            // Pinned here, before any renaming, so the save at the end can verify it is still writing
            // to the mod those renames happened in.
            string modAtStart = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            string outDir = Path.Combine(modAtStart, playlistScdDirectory);
            Directory.CreateDirectory(outDir);
            List<Option> optionsToRemove = new List<Option>();

            // Every rename below is undone if the save at the end can't land. The pre-flight above
            // catches the common case, but it cannot rule out the file becoming unwritable in between —
            // and without this, a failed save would leave every renamed .scd stranded under a name the
            // group file doesn't know about.
            var renamed = new List<(Option song, string key, string oldRel, string newRel, string oldPath, string newPath)>();

            // Options dropped for missing audio, with the index each sat at, so a rollback can put
            // them back where they were rather than at the end.
            var removed = new List<(int index, Option opt)>();

            try
            {
                foreach (Option song in playlist.Options)
                {
                    if (song.Name.Equals("Off", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (song.Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (song.Files != null && song.Files.Count > 0)
                    {
                        string oldPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, song.Files[song.Files.Keys.First()]);
                        if (!File.Exists(oldPath))
                        {
                            optionsToRemove.Add(song);
                            continue;
                        }
                        if (Path.GetExtension(oldPath) != ".scd")
                        {
                            continue;
                        }

                        // Sanitize the desired filename so it is valid on Windows
                        var safeName = SanitizeFileName(song.Name);
                        if (string.IsNullOrWhiteSpace(safeName))
                            safeName = "audio";

                        // Ensure extension is .scd
                        string fileName = safeName.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) ? safeName : safeName + ".scd";

                        string newPath = Path.Combine(outDir, fileName);

                        if (oldPath != newPath)
                        {
                            // If target file already exists, append a numeric suffix to avoid collision
                            newPath = GetNonCollidingPath(newPath);

                            string key = song.Files.Keys.First();
                            string oldRel = song.Files[key];
                            string newRel = Path.Combine(playlistScdDirectory, Path.GetFileName(newPath));

                            File.Move(oldPath, newPath);
                            BPMDetector.UpdateCacheForSCD(oldPath, newPath);
                            KeyDetector.UpdateCacheForSCD(oldPath, newPath);
                            song.Files[key] = newRel;
                            renamed.Add((song, key, oldRel, newRel, oldPath, newPath));
                        }
                    }

                    // delete empty folders
                    string playlistFolder = outDir;
                    foreach (string subDir in Directory.GetDirectories(playlistFolder))
                    {
                        if (Directory.GetFiles(subDir).Length == 0 && Directory.GetDirectories(subDir).Length == 0)
                        {
                            Directory.Delete(subDir);
                        }
                    }
                }

                foreach (Option opt in optionsToRemove)
                {
                    int index = playlist.Options.IndexOf(opt);
                    if (index < 0) continue;
                    playlist.Options.RemoveAt(index);
                    removed.Add((index, opt));
                }

                // Cleanup has already renamed .scd files on disk by this point, so the save must land
                // in the mod those renames happened in and no other.
                PenumbraMeta.AssertModRootUnchanged(modAtStart);
                this.Save();
                // Save() will refresh Penumbra; no need to call here.
            }
            catch
            {
                // Nothing was persisted, so put the model and the disk back to match each other.
                // Reverse order for both: a name freed by one rename has to be available again to the
                // rename that originally held it, and re-inserting at a recorded index is only correct
                // if the later removals go back first.
                for (int i = removed.Count - 1; i >= 0; i--)
                    playlist.Options.Insert(removed[i].index, removed[i].opt);

                for (int i = renamed.Count - 1; i >= 0; i--)
                {
                    var (song, key, oldRel, _, oldPath, newPath) = renamed[i];
                    try
                    {
                        if (File.Exists(newPath) && !File.Exists(oldPath))
                        {
                            File.Move(newPath, oldPath);
                            BPMDetector.UpdateCacheForSCD(newPath, oldPath);
                            KeyDetector.UpdateCacheForSCD(newPath, oldPath);
                        }
                        song.Files[key] = oldRel;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("Organize of '{Name}' failed and '{File}' could not be renamed back " +
                            "to '{Old}': {Error}. That song will not play until it is renamed by hand.",
                            Name, Path.GetFileName(newPath), Path.GetFileName(oldPath), ex);
                    }
                }
                throw;
            }
        }

        // The BPM/key/duration caches are keyed by full .scd path, so moving or renaming audio
        // invalidates their entries. Both take mod-relative paths.
        private static void MigrateStatsCache(string oldRel, string newRel)
        {
            string oldFull = Path.Combine(Settings.PenumbraLocation, Settings.ModName, oldRel);
            string newFull = Path.Combine(Settings.PenumbraLocation, Settings.ModName, newRel);
            BPMDetector.UpdateCacheForSCD(oldFull, newFull);
            KeyDetector.UpdateCacheForSCD(oldFull, newFull);
        }

        internal static string GetNonCollidingPath(string path)
        {
            if (!File.Exists(path)) return path;

            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int idx = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name}_{idx}{ext}");
                idx++;
            } while (File.Exists(candidate));
            return candidate;
        }

        public static string? GetScdKey(Option opt)
        {
            if (opt?.Files == null || opt.Files.Count == 0)
                return null;

            if (opt.Files.ContainsKey(Settings.BaselineScdKey))
                return Settings.BaselineScdKey;

            if (opt.Files.ContainsKey("sound/bpmloop.scd"))
                return "sound/bpmloop.scd";

            return opt.Files.Keys.FirstOrDefault(k => k.EndsWith(".scd", StringComparison.OrdinalIgnoreCase));
        }

        public static string GetScdPath(Option opt)
        {
            string? key = GetScdKey(opt);
            if (key == null)
                return string.Empty;
            return opt.Files[key];
        }

        public static string GetBaselineScdFileName()
        {
            string key = Settings.BaselineScdKey.Replace('/', Path.DirectorySeparatorChar);
            string fileName = Path.GetFileName(key);
            if (string.IsNullOrWhiteSpace(fileName))
                return "bpmloop.scd";
            return fileName;
        }

        /// <summary>
        /// Builds a playlist from a group object. Works for both layouts unchanged: a v3 group file
        /// has <c>Name</c> and <c>Options</c> at its top level just as a v4 group node does, and its
        /// missing <c>Id</c> simply fails to parse and stays <see cref="Guid.Empty"/>.
        /// </summary>
        internal static Playlist FromJson(JObject g)
        {
            var playlist = new Playlist
            {
                Name = g["Name"]?.ToString() ?? string.Empty,
                Raw = g,
            };
            playlist.PersistedName = playlist.Name;

            if (Guid.TryParse(g["Id"]?.ToString(), out var id))
                playlist.Id = id;

            if (g["Options"] is JArray options)
            {
                foreach (var o in options.OfType<JObject>())
                    playlist.Options.Add(Option.FromJson(o));
            }

            return playlist;
        }

        public static Dictionary<string, Playlist> GetAll()
        {
            Dictionary<string, Playlist> playlists = new Dictionary<string, Playlist>();
            if (Settings.PenumbraLocation == null || Settings.ModName == null)
                return playlists;

            string modDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(modDirectory))
                return playlists;

            var store = PlaylistStore.Current;

            // Captured BEFORE the read, so a write of ours landing during it cannot be mistaken for
            // part of what was read. A baseline whose write count is older than its contents is inert
            // rather than wrong, which is the direction to fail in — see ModFolderGuard.NoteBaseline
            // for what happens when counts and the write counter disagree the other way round.
            // HealOnLoad's own repairs deliberately do not move this counter.
            long writesBeforeRead = ModFolderGuard.CurrentWrites;

            // Recover first, sweep second: under v3 a .reorder_tmp is a live playlist, and the sweep
            // must never see one that HealOnLoad would have restored.
            store.HealOnLoad();

            // Delete the half-written .tmp files an interrupted save can leave in the folder Penumbra
            // scans. Only our own temps — every .bak is left alone, because those are Penumbra's
            // conversion backups. Runs at most once per session.
            MigrateStrayBackupsOnce();

            foreach (var playlist in store.ReadAll())
            {
                if (string.IsNullOrEmpty(playlist.Name))
                    continue;

                // Penumbra permits duplicate group names, but this dictionary is keyed by name and the
                // UI addresses playlists by name, so the second one would be unreachable anyway.
                if (playlists.ContainsKey(playlist.Name))
                {
                    Logger.LogWarn("GetAll: duplicate playlist name '{Name}' — keeping the first.",
                        playlist.Name);
                    continue;
                }
                playlists[playlist.Name] = playlist;
            }

            LogLibrarySummary(playlists, modDirectory, writesBeforeRead);
            return playlists;
        }

        /// <summary>
        /// Counts a set of group objects the way <see cref="GetAll"/> loads them: a group with no name
        /// is skipped, and a name that repeats counts only once — because the dictionary above is keyed
        /// by name and every later group with that name is unreachable.
        ///
        /// Shared rather than reimplemented per caller. Three places count a group set — this loader,
        /// <see cref="Utils.ModFolderGuard"/> deciding whether the folder lost content, and
        /// <see cref="Utils.BackupCatalog"/> deciding which backup is big enough to restore — and they
        /// only mean anything when compared against each other. Two of them counting raw files while
        /// the third de-duplicated is how the "never restore a smaller set than was lost" test came to
        /// compare two different kinds of number.
        ///
        /// The caller owns the enumeration and what to do about a member it cannot read: a live folder
        /// treats one unreadable group as "do not answer at all", while a backup treats it as costing
        /// only its own count. Those are different policies about different things, and neither
        /// belongs here.
        /// </summary>
        internal sealed class GroupTally
        {
            private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

            internal int Playlists { get; private set; }
            internal int Songs { get; private set; }

            internal void Add(JObject group)
            {
                string name = group["Name"]?.ToString() ?? string.Empty;
                if (name.Length == 0) return;
                if (!_seen.Add(name)) return;

                Playlists++;
                Songs += (group["Options"] as JArray)?.Count ?? 0;
            }
        }

        /// <summary>
        /// One line describing what was loaded, plus a warning for the one group shape Penumbra will
        /// take apart on its own.
        ///
        /// Penumbra caps grouped options per TYPE — IModGroup.MaxMultiOptions is 32 for Multi,
        /// MaxCombiningOptions is 8 for Combining — and splits anything larger into
        /// "&lt;name&gt;, Part 1"/"Part 2" the next time it adds the mod, renumbering the rest of the
        /// folder as it goes. The playlist then no longer exists under the name this app resolves by.
        /// Playlists are Single groups — which have no such cap, and are why 100+ song playlists work
        /// at all — but the group type is one toggle away in Penumbra's own UI, so a report of "my
        /// playlist split in two" should be answerable from the log rather than by guesswork.
        /// </summary>
        /// <param name="writesBeforeRead">
        /// The write counter as it stood before the folder was read — see <see cref="GetAll"/>.
        /// </param>
        private static void LogLibrarySummary(Dictionary<string, Playlist> playlists, string modDirectory,
            long writesBeforeRead)
        {
            try
            {
                int songs = playlists.Values.Sum(p => p.Options?.Count ?? 0);
                Logger.LogInfo("Loaded {Playlists} playlist(s), {Songs} song(s) from '{Dir}'.",
                    playlists.Count, songs, modDirectory);

                // Judged here because this is the one place that already knows both counts, and it
                // runs on every load — so a folder Penumbra rewrote while the app sat idle is noticed
                // the next time anything refreshes the tree, without a watcher of its own. Returns
                // immediately; the confirmation and any report happen off this thread.
                ModFolderGuard.ReportIfExternalChange(modDirectory, playlists.Count, songs);

                // Then bring the baseline up to date, AFTER the judgement above has had this load to
                // compare against. The reload path is the stronger moment to record one — Penumbra has
                // just agreed with the folder there — but it was until now the ONLY moment, which made
                // the whole guard depend on a setting that has nothing to do with it: with "auto-reload
                // mod" unticked no reload ever fires, so no baseline was ever taken and the detection,
                // the banner and the undo were all silently inert. A reload dropped because the folder
                // gate was busy left the same hole, and so did the whole first stretch of any session
                // before the user's first edit.
                ModFolderGuard.NoteBaselineFromLoad(modDirectory, playlists.Count, songs, writesBeforeRead);

                foreach (var p in playlists.Values)
                {
                    int limit = MaxOptionsForGroupType(p.Type);
                    int count = p.Options?.Count ?? 0;
                    if (count <= limit)
                        continue;

                    // Every placeholder must appear exactly once: Logger forwards to
                    // Microsoft.Extensions.Logging, whose formatter maps named placeholders to
                    // positional indices WITHOUT deduplicating repeats. Naming one twice produced a
                    // {4} against a 4-element array, so this warning threw a FormatException and was
                    // swallowed by the catch below — the diagnostic never once reached the log.
                    Logger.LogWarn("Playlist '{Name}' is a {Type} group with {Count} songs, but " +
                        "Penumbra allows at most {Limit} and will split it into numbered parts. " +
                        "Change it to a Single group in Penumbra to keep it in one piece.",
                        p.Name, p.Type, count, limit);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Could not summarise the loaded library: {Error}", ex.Message);
            }
        }

        // Penumbra's per-type option limits. Single is uncapped — its setting is an index, not the
        // bitmask that constrains Multi — which is the only reason long playlists work.
        //
        // Matched case-insensitively on purpose. A switch on string compares ordinally, and the whole
        // point of this check is to catch group files that came from somewhere other than Penumbra —
        // hand edits, other tools — which are exactly the ones liable to write "multi". Falling to
        // the uncapped arm on a casing difference would silently skip the warning.
        private static int MaxOptionsForGroupType(string? type) =>
            string.Equals(type, "Multi", StringComparison.OrdinalIgnoreCase) ? 32      // MaxMultiOptions
            : string.Equals(type, "Combining", StringComparison.OrdinalIgnoreCase) ? 8 // MaxCombiningOptions
            : int.MaxValue;                                     // Single, Imc, and anything newer

        public void Add(string[] fileNames, Action<int>? callback = null)
        {
            Logger.LogInfo("Add: {Count} file(s) into '{Name}' (currently {Existing} options)",
                fileNames?.Length ?? 0, Name, Options?.Count ?? 0);

            // Bail before importing anything if the save at the end can't land, exactly as Cleanup()
            // does. Importing writes a .scd per track into the mod folder — tens of seconds each — and
            // only the Save below records them in the group file. Without this, a failed save left
            // every imported track orphaned on disk: the next attempt then imported them a second time
            // under "_1" names, which is how users ended up with duplicate audio and a playlist that
            // never gained the songs.
            if (!ExistsInManifest())
            {
                Logger.LogError("Add into '{Name}': playlist has no group file to save into — " +
                    "importing nothing.", Name);
                throw new PlaylistSaveException(Name);
            }

            // Importing runs for tens of seconds per track, and the mod folder is changeable from the
            // Settings dialog the whole time. Pin the one we started against so the save at the end
            // can refuse rather than write this playlist into whichever mod is selected by then.
            string modAtStart = PenumbraMeta.ModRoot;

            var referenced = ReferencedScdPaths();

            int count = 0;
            foreach (string file in fileNames)
            {
                if (Settings.SupportedFileTypes.Contains(Path.GetExtension(file).ToLower())
                    && !SkipPlaybackPreview(file, Name))
                    AddFiles(Name, this, file, referenced);
                if (callback != null)
                    callback((int)((float)(++count)/fileNames.Length*100));
            }

            PenumbraMeta.AssertModRootUnchanged(modAtStart);
            Save();
        }

        // Logged rather than dropped quietly: a file the user explicitly selected vanishing from the
        // result with no explanation is its own kind of confusing.
        private static bool SkipPlaybackPreview(string file, string playlistName)
        {
            if (!Player.IsExtractedPlaybackFile(file)) return false;

            Logger.LogInfo("Import: skipping '{File}' into '{Playlist}' — it is one of this app's own " +
                "playback preview files, not a song.", Path.GetFileName(file), playlistName);
            return true;
        }

        public void Insert(string[] fileNames, int index, Action<int>? callback = null)
        {
            Logger.LogInfo("Insert: {Count} file(s) into '{Name}' at index {Index} (currently {Existing} options)",
                fileNames?.Length ?? 0, Name, index, Options?.Count ?? 0);
            // Same two guards as Add: nothing goes on disk if the save has nowhere to land, and the
            // mod we started against is the only one this may write to.
            if (!ExistsInManifest())
            {
                Logger.LogError("Insert into '{Name}': playlist has no group file to save into — " +
                    "importing nothing.", Name);
                throw new PlaylistSaveException(Name);
            }

            int offIndex = Options.FindIndex(o => o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase));
            if (offIndex >= 0)
                index = Math.Max(index, offIndex + 1);

            string modAtStart = PenumbraMeta.ModRoot;
            var referenced = ReferencedScdPaths();

            int count = 0;
            foreach (string file in fileNames)
            {
                // Only the playback previews are filtered here, not the full extension check Add
                // applies: Insert's callers hand it a list they have already vetted, and tightening
                // that would silently drop files this path accepts today.
                if (SkipPlaybackPreview(file, Name))
                {
                    if (callback != null)
                        callback((int)((float)(++count) / fileNames.Length * 100));
                    continue;
                }

                Option opt = AddFiles(Name, this, file, referenced);
                Options.RemoveAt(Options.Count - 1);
                Options.Insert(index, opt);
                index++;
                if (callback != null)
                    callback((int)((float)(++count) / fileNames.Length * 100));
            }

            PenumbraMeta.AssertModRootUnchanged(modAtStart);
            Save();
        }


        public static bool IsValidName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        public bool Rename(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                return false;

            newName = newName.Trim();
            if (!IsValidName(newName))
                throw new ArgumentException("Playlist name cannot contain any invalid filename characters.");

            string oldName = Name;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return false;

            var store = PlaylistStore.Current;
            if (store.NameInUse(newName))
                throw new InvalidOperationException($"A playlist named '{newName}' already exists.");
            if (!store.Exists(this))
                throw new PlaylistSaveException(oldName);

            string modDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName);

            // Derive the SCD folder from the songs themselves rather than assuming it equals the old
            // playlist name — post-merge/repair playlists can store their audio in a differently
            // named folder, and the old name-guess would move the wrong (or no) folder and repoint
            // paths that never matched, orphaning every song.
            string oldScdDir = GetScdDirectoryForNewFiles();
            string newScdDir = GetDefaultPlaylistDirectoryName(newName);
            bool scdDirChanged = !string.Equals(oldScdDir, newScdDir, StringComparison.OrdinalIgnoreCase);

            string oldFolder = Path.Combine(modDirectory, NormalizeRelativeModPath(oldScdDir));
            string newFolder = Path.Combine(modDirectory, NormalizeRelativeModPath(newScdDir));
            bool folderMoved = false;

            if (scdDirChanged && Directory.Exists(oldFolder))
            {
                if (Directory.Exists(newFolder))
                    throw new InvalidOperationException($"A playlist folder named '{newScdDir}' already exists.");
                Directory.Move(oldFolder, newFolder);
                folderMoved = true;
            }

            // Everything from here on is undoable, and it has to be: the audio has already moved on
            // disk, but the group that points at it is only updated by the Save at the end. If that
            // throws, leaving the move in place would point every song in this playlist at a folder
            // that no longer exists. Record each repoint so the whole thing can be put back.
            var repointed = new List<(Option song, string key, string oldRel, string newRel)>();
            Name = newName;

            try
            {
                if (scdDirChanged && Options != null)
                {
                    string oldPrefix = NormalizeRelativeModPath(oldScdDir) + Path.DirectorySeparatorChar;
                    foreach (var song in Options)
                    {
                        if (song?.Files == null) continue;
                        foreach (var key in song.Files.Keys.ToList())
                        {
                            var rel = song.Files[key];
                            if (string.IsNullOrWhiteSpace(rel)) continue;
                            var norm = NormalizeRelativeModPath(rel);
                            // Only repoint paths that actually lived in the moved folder; leave songs
                            // stored elsewhere untouched.
                            if (norm.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                var newRel = Path.Combine(newScdDir, norm.Substring(oldPrefix.Length));
                                // The BPM/key/duration caches are keyed by full file path, so the folder
                                // move invalidates their keys. Migrate them (as Cleanup() does on a song
                                // rename) or every moved song reads as 0:00 and the renamed playlist's
                                // displayed duration collapses to 00:00:00.
                                MigrateStatsCache(rel, newRel);
                                song.Files[key] = newRel;
                                repointed.Add((song, key, rel, newRel));
                            }
                        }
                    }
                }

                // Resolution is by Id under v4 and by PersistedName (still the old name) under v3, so
                // storage still saying oldName is fine — the save is what updates it.
                PenumbraMeta.AssertModRootUnchanged(modDirectory);
                Save();
            }
            catch
            {
                // Put the model and the disk back exactly as they were, so a failed rename is a no-op
                // rather than a playlist full of dead references.
                Name = oldName;
                foreach (var (song, key, oldRel, newRel) in repointed)
                {
                    song.Files[key] = oldRel;
                    MigrateStatsCache(newRel, oldRel);
                }
                if (folderMoved)
                {
                    try { Directory.Move(newFolder, oldFolder); }
                    catch (Exception moveBack)
                    {
                        // The audio is now under the new name while the group still points at the old
                        // one. Say so loudly — this is the one case the user has to fix by hand.
                        Logger.LogError("Rename of '{Old}' failed AND its audio folder could not be moved " +
                            "back from '{New}' to '{Old2}': {Error}. Songs in this playlist will not play " +
                            "until that folder is renamed back.", oldName, newFolder, oldFolder, moveBack);
                    }
                }
                throw;
            }

            Logger.LogInfo("Renamed playlist '{Old}' -> '{New}' (scd folder '{OldDir}' -> '{NewDir}', changed={Changed})",
                oldName, newName, oldScdDir, newScdDir, scdDirChanged);
            return true;
        }

        /// <summary>
        /// What a merge did, so the UI can report it without re-deriving any of it.
        /// </summary>
        internal sealed class MergeResult
        {
            /// <summary>Songs moved into the target.</summary>
            internal int Moved { get; init; }

            /// <summary>Songs dropped because a song of that name was already in the merge.</summary>
            internal List<string> Discarded { get; init; } = new();

            /// <summary>Source playlists whose group was removed.</summary>
            internal List<string> MergedSources { get; init; } = new();
        }

        /// <summary>
        /// Folds every <paramref name="sources"/> playlist into this one: their songs are appended in
        /// order, their audio moves into this playlist's .scd folder, and the emptied source groups
        /// are deleted.
        ///
        /// Ordered the way <c>MainWindow.DragDrop</c>'s cross-playlist move is, and for the same
        /// reason — that ordering was paid for with 25 lost songs. Everything ADDITIVE happens first
        /// and commits in a single Save; only once the target is durable does anything DESTRUCTIVE
        /// run. A failure part-way therefore leaves the songs in their source playlists (a no-op) or,
        /// at worst, in both — never nowhere.
        ///
        /// Songs are matched by name: the first occurrence wins, so this playlist's own songs beat
        /// the sources' and earlier sources beat later ones. A discarded duplicate keeps its .scd in
        /// the source folder and goes when that folder does.
        /// </summary>
        internal MergeResult MergeFrom(IReadOnlyList<Playlist> sources, Action<int>? progress = null)
        {
            if (sources == null || sources.Count == 0)
                return new MergeResult();

            // Merging a playlist into itself would move every one of its files onto itself — which
            // GetNonCollidingPath turns into a pointless "name_1.scd" rename — and then delete its own
            // group. The dialog cannot offer it, so this is belt and braces, along with dropping any
            // playlist listed twice.
            var seenSources = new HashSet<string>(StringComparer.Ordinal);
            sources = sources
                .Where(s => s != null && !ReferenceEquals(s, this))
                .Where(s => !string.Equals(s.Name, Name, StringComparison.Ordinal))
                .Where(s => seenSources.Add(s.Name))
                .ToList();

            if (sources.Count == 0)
                return new MergeResult();

            // The mod folder is changeable from the Settings dialog while this runs, and a merge is
            // not quick. Pin the one we started against so the save can refuse rather than write this
            // playlist into whichever mod is selected by then.
            string modAtStart = PenumbraMeta.ModRoot;

            // Pre-flight every group ONCE. ExistsInManifest costs a folder scan and can move files on
            // a miss, so it must never run per song. Bail before touching anything if any group has
            // nowhere to write — a merge that can't commit must not have moved audio first.
            if (!ExistsInManifest())
                throw new PlaylistSaveException(Name);
            foreach (var source in sources)
            {
                if (!source.ExistsInManifest())
                    throw new PlaylistSaveException(source.Name);
            }

            // Capture each source's audio folder BEFORE anything is repointed. After the move the
            // moved Options are the same instances now held by this playlist and point at OUR folder,
            // so asking a source for its folder afterwards would answer with the target's.
            var sourceFolders = sources.ToDictionary(s => s, s => s.GetScdDirectoryForNewFiles());

            var (off, targetSongs) = SplitOff(Options);

            // First-wins de-duplication, seeded with what this playlist already holds.
            //
            // Matched on the name with its stats suffix stripped: BackfillStatsFromCacheIfNeeded folds
            // " (128 BPM) [Am] (00:03:41)" into the in-memory names, and only sometimes persists it, so
            // a raw comparison would keep both "Epic" and "Epic (128 BPM)" as separate songs. Casing is
            // ignored too, so "Epic" and "epic" collapse.
            var seenNames = new HashSet<string>(
                targetSongs.Select(MergeKey), StringComparer.OrdinalIgnoreCase);
            var usedIds = new HashSet<Guid>(Options.Select(s => s.Id).Where(id => id != Guid.Empty));

            var plan = new List<(Playlist source, Option song)>();
            var discarded = new List<string>();

            foreach (var source in sources)
            {
                var (_, songs) = SplitOff(source.Options);
                foreach (var song in songs)
                {
                    if (!seenNames.Add(MergeKey(song)))
                    {
                        discarded.Add(song.Name ?? string.Empty);
                        continue;
                    }
                    plan.Add((source, song));
                }
            }

            // Penumbra caps some group types. Check now, while nothing has been written and nothing
            // has moved, so an over-cap merge costs the user nothing.
            int cap = MaxOptionsForGroupType(Type);
            int finalCount = (off != null ? 1 : 0) + targetSongs.Count + plan.Count;
            if (finalCount > cap)
            {
                throw new InvalidOperationException(
                    $"Merging would give '{Name}' {finalCount} options, but a '{Type}' group holds at " +
                    $"most {cap}. Merge fewer playlists at a time.");
            }

            Logger.LogInfo("Merge into '{Target}': {Sources} source(s), {Move} song(s) to move, " +
                "{Drop} duplicate(s) discarded", Name, sources.Count, plan.Count, discarded.Count);

            string targetScdDir = GetScdDirectoryForNewFiles();
            string targetFolder = Path.Combine(modAtStart, NormalizeRelativeModPath(targetScdDir));
            Directory.CreateDirectory(targetFolder);

            // Every physical move, so a failed commit can be undone completely.
            var moves = new List<(Option song, string key, string oldRel, string newRel,
                                  string oldFull, string newFull)>();
            var snapshot = new List<Option>(Options);
            var mintedIds = new List<(Option song, Guid oldId)>();

            try
            {
                int done = 0;
                foreach (var (source, song) in plan)
                {
                    // Three kinds of song have nothing to move and must NOT abort the merge:
                    //   - no .scd key at all (the "Off" placeholder shape, or a swap-only option),
                    //   - a path whose file is gone — dead pointers are common enough that Cleanup()
                    //     exists to reap them, and File.Move would throw and roll back the whole merge,
                    //   - a song already stored in this playlist's own folder, where a move would be a
                    //     GetNonCollidingPath rename to "name_1.scd" and nothing else.
                    // All three are carried across untouched, keeping their existing path.
                    var scdKey = GetScdKey(song);
                    string oldRel = scdKey != null ? NormalizeRelativeModPath(song.Files[scdKey]) : "";
                    string oldFull = scdKey != null ? Path.Combine(modAtStart, oldRel) : "";
                    bool alreadyHere = scdKey != null && string.Equals(
                        Path.GetDirectoryName(oldRel) ?? "", targetScdDir, StringComparison.OrdinalIgnoreCase);

                    if (scdKey != null && !alreadyHere && File.Exists(oldFull))
                    {
                        string newFull = GetNonCollidingPath(
                            Path.Combine(targetFolder, Path.GetFileName(oldRel)));
                        string newRel = Path.Combine(targetScdDir, Path.GetFileName(newFull));

                        // Physical move first: if it throws, nothing has been mutated yet.
                        File.Move(oldFull, newFull);
                        // The BPM/key/duration caches are keyed by full path, so the move orphans this
                        // song's entries. Migrate them or every moved song reads 0:00 and the merged
                        // playlist's displayed duration collapses.
                        MigrateStatsCache(oldRel, newRel);
                        song.Files[scdKey] = newRel;
                        moves.Add((song, scdKey, oldRel, newRel, oldFull, newFull));
                    }
                    else if (scdKey != null && !alreadyHere)
                    {
                        Logger.LogWarn("Merge into '{Target}': '{Song}' from '{Source}' points at " +
                            "'{Path}', which is not on disk — carrying the entry across unmoved.",
                            Name, song.Name, source.Name, oldRel);
                    }

                    // Penumbra stores the user's current selection for a group against the option Id,
                    // so two options sharing one inside a single group is a corrupt state.
                    if (song.Id == Guid.Empty || !usedIds.Add(song.Id))
                    {
                        mintedIds.Add((song, song.Id));
                        song.Id = Guid.NewGuid();
                        usedIds.Add(song.Id);
                    }

                    progress?.Invoke((int)((float)(++done) / plan.Count * 100));
                }

                Options = Rejoin(off, targetSongs.Concat(plan.Select(p => p.song)));

                PenumbraMeta.AssertModRootUnchanged(modAtStart);
                Save();
            }
            catch
            {
                // Put everything back. The sources have not been touched yet, so a full undo here
                // leaves the user exactly where they started rather than short a playlist.
                Options = snapshot;
                foreach (var (song, oldId) in mintedIds)
                    song.Id = oldId;
                for (int i = moves.Count - 1; i >= 0; i--)
                {
                    var (song, key, oldRel, newRel, oldFull, newFull) = moves[i];
                    try
                    {
                        File.Move(newFull, oldFull);
                        MigrateStatsCache(newRel, oldRel);
                    }
                    catch (Exception moveBack)
                    {
                        Logger.LogError("Merge rollback: could not restore '{File}' to '{Dest}': {Error}",
                            newFull, oldFull, moveBack);
                    }
                    song.Files[key] = oldRel;
                }
                throw;
            }

            // Destructive half last. The songs are committed to the target, so a failure from here on
            // leaves a source playlist behind — untidy, but nothing is lost.
            var merged = new List<string>();
            foreach (var source in sources)
            {
                try
                {
                    // Drop the moved entries first: they belong to this playlist now and point into
                    // its folder, so leaving them on the source would misreport what it still owns.
                    var movedFromHere = new HashSet<Option>(
                        plan.Where(p => p.source == source).Select(p => p.song));
                    source.Options = source.Options.Where(o => !movedFromHere.Contains(o)).ToList();

                    // Group only — the audio is reclaimed below, against the folders captured before
                    // anything was repointed. Letting Delete() work the folder out for itself here
                    // would have it guess from the name and possibly delete an unrelated one.
                    source.DeleteGroupOnly();
                    merged.Add(source.Name);
                }
                catch (Exception ex)
                {
                    Logger.LogError("Merge into '{Target}': songs were committed but source '{Source}' " +
                        "could not be removed: {Error}", Name, source.Name, ex);
                }
            }

            // One reclaim pass for every emptied folder, using the directories captured up front. Any
            // folder something still points into — a source that failed to delete, another playlist
            // sharing it, or this playlist when source and target stored audio together — is left
            // alone. Discarded duplicates' .scd files go with their folder, which is what discarding
            // them means.
            ReclaimUnusedAudioDirectories(merged.Select(name =>
                sourceFolders.First(kvp => kvp.Key.Name == name).Value));

            RefreshPenumbraMod();

            Logger.LogInfo("Merged {Count} playlist(s) into '{Target}': {Moved} song(s) moved, " +
                "{Dropped} discarded", merged.Count, Name, moves.Count, discarded.Count);

            return new MergeResult { Moved = plan.Count, Discarded = discarded, MergedSources = merged };
        }

        /// <summary>
        /// The name a merge treats as a song's identity: the display name with any BPM/key/duration
        /// suffix stripped, so a song counts as the same track whether or not its stats have been
        /// folded into its name yet. Compared case-insensitively by the caller.
        /// </summary>
        internal static string MergeKey(Option song) =>
            OptionStatsNaming.StripSuffix(song?.Name ?? string.Empty).Trim();

        /// <summary>
        /// True when this playlist's group can actually be located in storage, i.e. Save() will have
        /// somewhere to write. Callers that are about to do something destructive (move a song out of
        /// another playlist, delete an audio file) should pre-flight with this so they never commit
        /// half an edit against a playlist that cannot be saved.
        ///
        /// Costs a folder scan, and on a miss it also attempts recovery, which MOVES FILES — see
        /// V3GroupFileStore.ResolveWritableFile. Call it once per operation, not in a loop.
        /// </summary>
        internal bool ExistsInManifest() => PlaylistStore.Current.Exists(this);

        /// <summary>
        /// This playlist as a manifest group. Built by cloning <paramref name="existing"/> so keys
        /// this app doesn't model — Description, Image, Page, DefaultSettings, Priority, v3's
        /// Version — survive the round trip. Only Id, Name, Type and Options are ours to write.
        /// </summary>
        internal JObject ToJson(JObject existing, ModFormat format)
        {
            var g = existing != null ? (JObject)existing.DeepClone() : new JObject();

            if (Id == Guid.Empty)
                Id = Guid.NewGuid();

            // v3 group files carry no Id; see Option.ToJson for why writing one anyway is worse than
            // having none.
            if (format == ModFormat.V4)
                g["Id"] = Id.ToString();

            g["Name"] = Name ?? string.Empty;
            if (g["Type"] == null)
                g["Type"] = "Single";

            if (format == ModFormat.V3)
            {
                // Drop the inherited Options before reshaping. Reshape deep-clones every property it
                // reorders, and Options is by far the largest — yet it is rebuilt from the model a few
                // lines down, so that clone is pure waste on every save. Removing it first is safe
                // because Options comes last in Penumbra's canonical v3 order, which is exactly where
                // the assignment below re-adds it. (Only v3 reshapes, so v4 key order is untouched.)
                g.Remove("Options");
                SeedV3Defaults(g);
            }

            // Read from `existing`, not `g` — `g` no longer has Options under v3, and `existing` is
            // never mutated.
            var existingOptions = (existing?["Options"] as JArray)?.OfType<JObject>().ToList();
            g["Options"] = new JArray(Options.Select(o => o.ToJson(TakeExistingOption(existingOptions, o), format)));

            return g;
        }

        // Penumbra's v3 group key order, taken from a file it wrote itself.
        private static readonly string[] V3GroupKeyOrder =
            { "Version", "Name", "Description", "Image", "Page", "Priority", "Type", "DefaultSettings", "Options" };

        // Rewrites the group with Penumbra's own key set and key ORDER, so a file this app touches is
        // shaped exactly like one Penumbra wrote. Existing values always win — only genuinely absent
        // keys get a default — and any key not in the canonical list is appended rather than dropped,
        // so nothing a future Penumbra adds is lost.
        //
        // Order matters for the same reason the v3 indent and CRLF do: if we emit the same data in a
        // different shape, Penumbra rewrites the whole file the next time it loads the mod, and every
        // line shows up as changed to its watcher.
        private static void SeedV3Defaults(JObject g) => Reshape(g, V3GroupKeyOrder, key => key switch
        {
            "Version" => 0,
            "Description" => string.Empty,
            "Image" => string.Empty,
            "Page" => 0,
            "Priority" => 0,          // ReorderAll renumbers this
            "DefaultSettings" => 0,
            _ => null,
        });

        /// <summary>
        /// Rewrites <paramref name="o"/> in <paramref name="keyOrder"/>, filling absent keys from
        /// <paramref name="defaultFor"/> and appending anything not in the list, so unknown keys — and
        /// anything a future Penumbra adds — survive.
        ///
        /// Every value is detached first: a JToken still parented to the object being rebuilt cannot
        /// be re-added to it, and Newtonsoft's behaviour when you try is exactly the sort of quiet
        /// aliasing this app must not have in its round-trip path.
        /// </summary>
        internal static void Reshape(JObject o, string[] keyOrder, Func<string, object> defaultFor)
        {
            var existing = o.Properties().Select(p => (p.Name, Value: p.Value?.DeepClone())).ToList();
            JToken Existing(string key) =>
                existing.FirstOrDefault(e => string.Equals(e.Name, key, StringComparison.Ordinal)).Value;

            o.RemoveAll();
            foreach (string key in keyOrder)
            {
                var value = Existing(key);
                if (value != null) { o[key] = value; continue; }

                var fallback = defaultFor(key);
                if (fallback != null) o[key] = JToken.FromObject(fallback);
            }
            foreach (var (name, value) in existing)
            {
                if (!keyOrder.Contains(name, StringComparer.Ordinal) && value != null)
                    o[name] = value;
            }
        }

        // Match by Id so an option keeps whatever Penumbra stored on it even after being reordered or
        // moved between playlists. Name is only a fallback for options this app just created — and the
        // only match available under v3, which has no Ids.
        //
        // Each match is REMOVED once used: two options sharing a name would otherwise both clone the
        // first one's extras, duplicating its manipulations onto its namesake. Consuming the list maps
        // same-named options 1:1 in order instead.
        private static JObject TakeExistingOption(List<JObject> existingOptions, Option option)
        {
            if (existingOptions == null) return null;

            JObject match = null;
            if (option.Id != Guid.Empty)
            {
                match = existingOptions.FirstOrDefault(o =>
                    Guid.TryParse(o["Id"]?.ToString(), out var id) && id == option.Id);
            }

            match ??= existingOptions.FirstOrDefault(o =>
                string.Equals(o["Name"]?.ToString(), option.Name, StringComparison.Ordinal));

            if (match != null)
                existingOptions.Remove(match);
            return match;
        }

        /// <summary>
        /// Writes this playlist back to Penumbra's storage, in whichever layout the mod folder is
        /// currently in.
        ///
        /// This deliberately does NOT serialize the app's model of the mod. The store re-reads what is
        /// on disk and splices in this one group, leaving everything else verbatim — otherwise a stale
        /// in-memory Playlist would clobber every other playlist, including any the user edited in
        /// Penumbra since this one was loaded.
        /// </summary>
        public void Save()
        {
            try
            {
                PlaylistStore.Current.Save(this);
            }
            catch (Exception ex)
            {
                Logger.LogError("Save('{Name}') failed: {Error}", Name, ex);
                throw;
            }

            // Notify Penumbra (if present) that the mod changed so it can refresh.
            RefreshPenumbraMod();
        }

        /// <summary>
        /// Like <see cref="Save"/>, but creates the group when it isn't in storage yet instead of
        /// throwing. Only for genuinely new playlists — everything else should fail loudly.
        /// </summary>
        internal void Upsert()
        {
            PlaylistStore.Current.Upsert(this);
            RefreshPenumbraMod();
        }

        // Where backups live. Deliberately OUTSIDE the Penumbra mod folder: Penumbra scans that
        // directory, and in v4 the one file that matters there is meta.json.
        internal static string BackupDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PicklesPlaylistEditor", "backups");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static bool _strayBackupsMigrated;

        // Clears the half-written .tmp files an interrupted save can leave in the folder Penumbra
        // scans. Those are unambiguously ours and worthless — a complete copy of the pre-edit state
        // is already in BackupDir.
        //
        // Deliberately does NOT touch .bak files any more. It used to move group_*.json.bak and
        // default_mod.json.bak out, on the theory that they were litter from old builds of this app.
        // They are not: Penumbra writes exactly those names when it converts a mod from v3 to v4, and
        // they are its own rollback path. Since it has already reversed course once, relocating them
        // could cost the user their way back. Nothing this app writes lands in the mod folder as a
        // .bak, so leaving every .bak alone is both correct and safe.
        //
        // Nor does it touch .reorder_tmp files. Under v3 one of those is a live playlist caught
        // mid-reorder — the only copy of that group — and moving it to %LOCALAPPDATA% would look
        // exactly like the playlist vanishing. IPlaylistStore.HealOnLoad owns them, and GetAll runs
        // it first.
        internal static void MigrateStrayBackupsOnce()
        {
            if (_strayBackupsMigrated) return;
            if (Settings.PenumbraLocation == null || Settings.ModName == null) return;

            string modDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(modDirectory)) return;

            _strayBackupsMigrated = true;
            int removed = 0;
            try
            {
                var stale = Directory.EnumerateFiles(modDirectory, "group_*.json.tmp")
                    .Concat(Directory.EnumerateFiles(modDirectory, "meta.json.*.tmp"))
                    .ToList();

                foreach (var file in stale)
                {
                    try { File.Delete(file); removed++; }
                    catch { /* locked or already gone; skip */ }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Stale-temp cleanup failed (harmless): {Error}", ex.Message);
                return;
            }

            if (removed > 0)
                Logger.LogInfo("Removed {Count} stale temp file(s) from the mod folder.", removed);
        }

        public void Delete()
        {
            if (string.IsNullOrEmpty(Name))
                return;

            // Derive the audio folder from the songs rather than guessing it from the name — a
            // post-merge playlist can store its audio in a differently named folder, and the old
            // name-guess would either miss it or, worse, delete a folder belonging to someone else.
            string scdDir = GetScdDirectoryForNewFiles();

            DeleteGroupOnly();
            ReclaimUnusedAudioDirectories(new[] { scdDir });

            RefreshPenumbraMod();
        }

        /// <summary>
        /// Removes the group and nothing else — no audio, no reload.
        ///
        /// Split out for the merge, which must never let <see cref="Delete"/> work out the audio
        /// folder for itself. A merged-out playlist has had its songs taken away, so
        /// <see cref="ResolvePlaylistScdDirectory"/> has nothing left to read the folder from and falls
        /// back to GUESSING it from the playlist name. For a renamed or repaired playlist that guess
        /// names a folder it never used, and nothing references that folder, so the reclaim would
        /// happily delete somebody else's audio. The merge captures the real folder up front instead
        /// and reclaims it itself.
        /// </summary>
        internal void DeleteGroupOnly()
        {
            if (string.IsNullOrEmpty(Name))
                return;
            Logger.LogInfo("Deleting playlist '{Name}' ({Count} options)", Name, Options?.Count ?? 0);
            PlaylistStore.Current.Delete(this);
        }

        /// <summary>
        /// Deletes each of these audio folders, but only the ones no surviving group points into.
        ///
        /// Takes the whole set at once because the "is anything still using it" test costs a full read
        /// of every group in the mod; doing that per folder turns a merge of ten playlists into ten
        /// library reads. Callers must pass folders they OBSERVED, never ones derived from a name —
        /// see <see cref="DeleteGroupOnly"/>.
        /// </summary>
        private static void ReclaimUnusedAudioDirectories(IEnumerable<string> candidates)
        {
            var folders = candidates
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Select(NormalizeRelativeModPath)
                .Where(dir => dir.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (folders.Count == 0)
                return;

            // One read of the library for every candidate, not one each.
            var referenced = GetAll().Values
                .SelectMany(p => p.Options ?? new List<Option>())
                .Where(o => o?.Files != null)
                .SelectMany(o => o.Files.Values)
                .Select(NormalizeRelativeModPath)
                .ToList();

            foreach (var dir in folders)
            {
                string prefix = dir + Path.DirectorySeparatorChar;
                if (referenced.Any(rel => rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    continue;

                string folder = Path.Combine(Settings.PenumbraLocation, Settings.ModName, dir);
                if (!Directory.Exists(folder))
                    continue;

                try { Directory.Delete(folder, true); }
                catch (Exception ex)
                {
                    Logger.LogWarn("Could not remove unused audio folder '{Folder}': {Error}",
                        folder, ex.Message);
                }
            }
        }

        /// <summary>
        /// Applies a reordering of this playlist's songs and saves it.
        ///
        /// The permutation itself cannot fail, so the only failure is the save — and Save() is
        /// all-or-nothing (it writes atomically or throws before writing anything). That makes
        /// restoring the in-memory list a complete undo. Note the old per-file backup-and-restore is
        /// gone deliberately: with every playlist in one manifest, restoring the whole file on a
        /// failure would roll back any other playlist edited in between.
        /// </summary>
        private void ReorderOptions(Func<List<Option>, List<Option>> permute)
        {
            var snapshot = new List<Option>(Options);
            try
            {
                Options = permute(new List<Option>(Options));
                Save();
            }
            catch
            {
                Options = snapshot;
                throw;
            }
        }

        // "Off" is a fixed first entry, not a song — every reorder keeps it pinned at the top.
        private static (Option off, List<Option> songs) SplitOff(List<Option> options)
        {
            var off = options.FirstOrDefault(o => o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase));
            var songs = options.Where(o => !o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase)).ToList();
            return (off, songs);
        }

        private static List<Option> Rejoin(Option off, IEnumerable<Option> songs)
        {
            var result = new List<Option>();
            if (off != null) result.Add(off);
            result.AddRange(songs);
            return result;
        }

        internal void Shuffle() => ReorderOptions(options =>
        {
            var (off, songs) = SplitOff(options);
            var rng = new Random();
            var shuffled = new List<Option>(songs.Count);
            while (songs.Count > 0)
            {
                int k = rng.Next(songs.Count);
                shuffled.Add(songs[k]);
                songs.RemoveAt(k);
            }
            return Rejoin(off, shuffled);
        });

        internal void Sort(SortDirection direction) => ReorderOptions(options =>
        {
            var (off, songs) = SplitOff(options);
            songs = direction == SortDirection.Ascending
                ? songs.OrderBy(o => BPMDetector.GetBPMFromSCD(GetScdPath(o))).ToList()
                : songs.OrderByDescending(o => BPMDetector.GetBPMFromSCD(GetScdPath(o))).ToList();
            return Rejoin(off, songs);
        });

        /// <summary>
        /// Alternates fast and slow songs instead of running monotonically up or down: fastest,
        /// slowest, second fastest, second slowest, and so on, so the set keeps swinging between
        /// peaks and breathers rather than climbing once and staying there.
        ///
        /// <paramref name="direction"/> picks which end it opens on — Descending starts on the
        /// fastest song, Ascending starts on the slowest. Songs whose BPM could not be detected
        /// are parked at the end rather than interleaved.
        /// </summary>
        internal void SortByBpmZigZag(SortDirection direction) => ReorderOptions(options =>
        {
            var (off, songs) = SplitOff(options);

            // Pair each song with its BPM up front so the unknown check and the ordering below
            // share one lookup.
            var scored = songs
                .Select(o => (Option: o, Bpm: BPMDetector.GetBPMFromSCD(GetScdPath(o))))
                .ToList();

            // GetBPMFromSCD returns 0 when detection fails. Those songs are unknowns, not slow
            // songs — interleaving them would drop one into the second slot as the set's opening
            // breather. They keep their relative order and go after the zig-zag instead.
            var byBpm = scored.Where(x => x.Bpm > 0).OrderBy(x => x.Bpm).ToList();
            var undetected = scored.Where(x => x.Bpm <= 0).Select(x => x.Option);

            var zigzag = new List<Option>(songs.Count);
            int low = 0, high = byBpm.Count - 1;
            bool takeHigh = direction == SortDirection.Descending;
            while (low <= high)
            {
                zigzag.Add(takeHigh ? byBpm[high--].Option : byBpm[low++].Option);
                takeHigh = !takeHigh;
            }
            zigzag.AddRange(undetected);

            return Rejoin(off, zigzag);
        });

        internal void SortByKey(SortDirection direction) => ReorderOptions(options =>
        {
            var (off, songs) = SplitOff(options);
            songs = direction == SortDirection.Ascending
                ? songs.OrderBy(o => KeyDetector.GetKeyFromSCD(GetScdPath(o))).ToList()
                : songs.OrderByDescending(o => KeyDetector.GetKeyFromSCD(GetScdPath(o))).ToList();
            return Rejoin(off, songs);
        });

        internal void SortByName() => ReorderOptions(options =>
        {
            var (off, songs) = SplitOff(options);
            return Rejoin(off, songs.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase));
        });

        /// <summary>
        /// Replace invalid characters for Windows filenames, trim trailing dots/spaces and limit length.
        /// </summary>
        private static string SanitizeFileName(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            // Replace directory separators and invalid filename characters with underscore
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                if (invalid.Contains(ch) || char.IsControl(ch))
                    sb.Append('_');
                else
                    sb.Append(ch);
            }

            var result = sb.ToString();

            // Trim trailing spaces and dots (Windows does not allow names that end with dot/space)
            result = result.TrimEnd(' ', '.');

            // Limit filename length (reserve space for extension .scd)
            const int maxFileName = 200;
            if (result.Length > maxFileName)
                result = result.Substring(0, maxFileName);

            // As an extra safeguard remove any remaining invalid subsequences
            result = Regex.Replace(result, @"[\\\/:\*\?""<>\|]", "_");

            return result;
        }

        /// <summary>
        /// Reorders the playlists to match <paramref name="orderedNames"/>. How that lands on disk is
        /// the store's business — a permutation of the Groups array under v4, a renumbering of the
        /// group filenames under v3.
        /// </summary>
        internal static void ReorderAll(List<string> orderedNames)
        {
            // Collect VFX group names that weren't in the treeview so they keep their place rather
            // than being dropped from the ordering.
            var orderedSet = new HashSet<string>(orderedNames);
            var vfxNames = GetAll()
                .Where(kvp => !orderedSet.Contains(kvp.Key) && kvp.Value.IsVFXGroup())
                .Select(kvp => kvp.Key)
                .ToList();

            var allNames = new List<string>(orderedNames);
            allNames.AddRange(vfxNames);

            Logger.LogInfo("ReorderAll: reordering {Count} group(s): {Order}",
                allNames.Count, string.Join(" > ", allNames));

            PlaylistStore.Current.ReorderAll(allNames);

            // Reload Penumbra once, after everything has landed, to minimise the window in which it
            // could see a half-applied ordering.
            RefreshPenumbraMod();
        }

        public bool IsVFXGroup()
        {
            if (_isVFXGroup.HasValue)
                return _isVFXGroup.Value;

            _isVFXGroup = ComputeIsVFXGroup();
            return _isVFXGroup.Value;
        }

        private bool ComputeIsVFXGroup()
        {
            if (Options == null || Options.Count < 2)
                return false;

            foreach (var option in Options)
            {
                if (option.Files == null)
                    continue;

                foreach (var value in option.Files.Values)
                {
                    if (value != null && value.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            return true;
        }

        // Penumbra rewrites the mod when it reloads. Firing a reload after each individual Save()
        // meant a burst of edits (e.g. dragging songs one by one) had Penumbra rewriting meta.json
        // every couple of seconds, racing our own reads and writes. Debounce instead: each change
        // restarts a 20s countdown, so Penumbra is only asked to reload once the user has stopped.
        private const int PenumbraReloadDebounceMs = 20_000;
        private static readonly object s_reloadLock = new();
        private static Timer? s_reloadTimer;

        internal static void RefreshPenumbraMod()
        {
            if (!Settings.AutoReloadMod)
                return;

            lock (s_reloadLock)
            {
                s_reloadTimer ??= new Timer(_ => FirePenumbraReload(), null, Timeout.Infinite, Timeout.Infinite);

                // Penumbra has been refusing reloads, so asking again after every single edit only
                // gives it more chances to write its stale copy over the folder. Wait out the rest of
                // the pause instead — the REST of it, not a fresh interval each time, or a steady
                // stream of edits would push the retry back on every one of them and it would never
                // fire while the user was working. Aiming every edit at the same moment means the
                // retry lands when the pause ends, whatever happens in between.
                var pause = ModFolderGuard.RemainingPause;
                int due = pause > TimeSpan.Zero
                    ? (int)Math.Max(pause.TotalMilliseconds, PenumbraReloadDebounceMs)
                    : PenumbraReloadDebounceMs;

                // Restart the countdown — only the last change in a burst triggers the reload.
                s_reloadTimer.Change(due, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Re-aims the timer at the end of the pause, so the retry happens whether or not the user
        /// edits anything else.
        ///
        /// Without this the retry rides entirely on the next edit: nothing else ever touches the
        /// timer, so an app left sitting with a wedged Penumbra simply never asks again, and the
        /// "retrying every N minutes" that both the log line and the banner promise does not happen.
        ///
        /// Only the debounced path calls this. The shutdown flush must not leave a timer armed behind
        /// a process that is on its way out.
        /// </summary>
        private static void ArmPauseRetry()
        {
            var pause = ModFolderGuard.RemainingPause;
            if (pause <= TimeSpan.Zero)
                return;

            // Safe to take under the folder gate: nothing anywhere holds s_reloadLock while waiting
            // for that gate — FlushPenumbraMod releases this lock before it asks for it — so there is
            // no cycle to deadlock on.
            lock (s_reloadLock)
                s_reloadTimer?.Change((int)pause.TotalMilliseconds, Timeout.Infinite);
        }

        // The TOTAL the shutdown flush is willing to spend — waiting for the folder gate and waiting
        // for Penumbra's answer come out of this one allowance, not one each. Deliberately short: this
        // runs on the UI thread from AppWindow.Closing, so every millisecond here is a window that
        // will not close. Missing the reload only means Penumbra serves the previous version until it
        // next reloads; the edit itself is already on disk.
        private static readonly TimeSpan ShutdownReloadBudget = TimeSpan.FromMilliseconds(1500);

        // What the debounced reload will wait for the folder gate. Short, but not zero: reads hold the
        // gate too now, so a zero wait would drop the reload whenever it happened to land during a
        // library load. Waiting here costs nothing — it runs on a timer thread, and it waits for an
        // edit to finish rather than making one wait.
        private static readonly TimeSpan DebouncedGateWait = TimeSpan.FromMilliseconds(250);

        // Fire any reload still waiting out its debounce, right now. Call on shutdown so a pending
        // reload isn't dropped when the app closes seconds after the last edit.
        internal static void FlushPenumbraMod()
        {
            lock (s_reloadLock)
            {
                if (s_reloadTimer == null)
                    return;
                s_reloadTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }

            // Unlike the timer path, this one waits for the gate. Skipping when an edit is in flight
            // would silently drop the reload this method exists to guarantee — the rescheduled
            // debounce never fires, because the process is exiting.
            FirePenumbraReload(ShutdownReloadBudget, ShutdownReloadBudget);
        }

        /// <param name="gateWait">
        /// How long to wait for the mod-folder gate; null uses <see cref="DebouncedGateWait"/>.
        /// Short but not zero on the debounced path: reads hold the gate too, so a zero wait would
        /// drop the reload whenever it landed during a library load, and nothing reschedules it —
        /// only Save does that. Waiting here never makes a save wait, because this acquires the gate
        /// after the edit releases it. The shutdown flush passes a larger budget, since for it there
        /// is no next time.
        /// </param>
        /// <param name="answerWait">
        /// How long to wait for Penumbra's reply before giving up on it. Null on the debounced path
        /// (a background timer thread has nothing better to do, and the HTTP client's own timeout
        /// bounds it); bounded on shutdown so a wedged Penumbra cannot hold the window open.
        /// </param>
        private static void FirePenumbraReload(TimeSpan? gateWait = null, TimeSpan? answerWait = null)
        {
            try
            {
                if (!Settings.AutoReloadMod)
                    return;

                string mod = Settings.ModName;

                // Hold the folder gate across the whole reload, and wait for the answer.
                //
                // Reloading a v3 mod makes Penumbra read every group file and then, if any filename
                // disagrees with the one it would have chosen, write the whole set back out from what
                // it just read. A save landing inside that read-to-write window is silently discarded
                // — which is how a playlist that logged a successful save came back with the songs
                // missing. Serialising on the same gate every write takes closes that window.
                //
                // Penumbra answers only once the reload has completed, so the response IS the
                // "safe to write again" signal; the call used to be fired and forgotten, which threw
                // that signal away.
                //
                // TryEnter with the caller's budget — see the gateWait doc above for why it is short
                // rather than zero.
                // Timestamp rather than a Stopwatch instance, and only when there is a budget to spend
                // it against: the debounced path fires on every save and would otherwise allocate a
                // stopwatch nothing ever reads.
                long startTicks = answerWait.HasValue ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

                if (!Monitor.TryEnter(PlaylistStore.ModFolderGate, gateWait ?? DebouncedGateWait))
                {
                    // Not "it will reschedule": only Save does that. Losing the reload to a long read
                    // just means Penumbra serves the previous version until something else reloads it.
                    Logger.LogInfo("Penumbra: reload of '{Mod}' skipped, the mod folder was busy.", mod);
                    return;
                }

                try
                {
                    Logger.LogInfo("Penumbra: reloading mod '{Mod}' (debounced).", mod);

                    // Task.Run is load-bearing, not stylistic. FlushPenumbraMod is called from
                    // AppWindow.Closing on the UI thread, and the awaits inside Request would capture
                    // that SynchronizationContext and post their continuation back to the very thread
                    // blocked here — hanging the app on exit, with the HTTP timeout unable to break it
                    // because delivering the cancellation needs that same thread. Running the call on
                    // the pool leaves no context to capture.
                    var call = Task.Run(() => PenumbraApi.ReloadMod(mod, mod));

                    // The budget covers the WHOLE flush, not each phase of it: waiting for the gate has
                    // already spent some of it, so only the remainder is left for the reply. Spending
                    // it twice over would let a close block for double what the constant says.
                    if (answerWait is TimeSpan budget)
                    {
                        var remaining = budget - System.Diagnostics.Stopwatch.GetElapsedTime(startTicks);
                        if (remaining <= TimeSpan.Zero || !call.Wait(remaining))
                        {
                            // Shutdown only. Abandon the wait, not the request — it completes on the
                            // pool and Penumbra still reloads if it can. Blocking a closing window any
                            // longer buys nothing the user can see.
                            Logger.LogInfo("Penumbra: reload of '{Mod}' still running at shutdown — not " +
                                "waiting for it.", mod);
                            return;
                        }
                    }

                    // Only warn when Penumbra actually answered and something went wrong. NoAnswer
                    // means it is not running, or is busy mid-zone-load — both normal — and the API
                    // layer already reports each once per session; warning here too would put a line
                    // in the log for every single save.
                    //
                    // Note this cannot detect an unregistered mod: Penumbra's HTTP handler returns 200
                    // whether or not it recognises the mod (verified against 1.7 — a reload for a name
                    // that exists nowhere still answers 200 with an empty body), so the ModMissing code
                    // its IPC returns never reaches us. Failed here means a non-2xx or a transport
                    // error, not "the mod is unknown".
                    var result = call.GetAwaiter().GetResult();

                    // All of this is best-effort bookkeeping around a best-effort reload. Its own
                    // try/catch because the outer one swallows silently, and a detector that dies
                    // without saying so is worse than no detector.
                    try
                    {
                        // answerWait is set only by the shutdown flush, so its absence is what marks
                        // this as the ordinary debounced path — the one allowed to arm a retry.
                        GuardAfterReload(result, mod, debounced: answerWait is null);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarn("Mod folder guard failed after a reload (continuing): {Error}",
                            ex.Message);
                    }
                }
                finally
                {
                    Monitor.Exit(PlaylistStore.ModFolderGate);
                }
            }
            catch
            {
                // best-effort only; swallow any errors to avoid breaking the UI
            }
        }

        /// <summary>
        /// What to do about the reload's answer, beyond logging it.
        ///
        /// Runs with the folder gate still held, which is what makes the snapshot safe: the gate does
        /// not lock Penumbra out, but it does keep our own reorders and downloads from writing while
        /// the copy is taken.
        /// </summary>
        private static void GuardAfterReload(PenumbraApi.ApiResult result, string mod, bool debounced)
        {
            switch (result)
            {
                case PenumbraApi.ApiResult.Success:
                    if (ModFolderGuard.NoteReloadSucceeded())
                    {
                        Logger.LogInfo("Penumbra: reloads are being accepted again — resuming the " +
                            "usual refresh after each edit.");

                        // The banner says edits are not reaching Penumbra. They are again, and this is
                        // the only thing that can say so: the event used to be raised with true and
                        // never with false, so the notice sat there until dismissed by hand.
                        ReloadPausedChanged?.Invoke(false);
                    }

                    // Penumbra has just re-read the folder, so its copy and ours agree. This is the
                    // one moment where a baseline is unambiguously true.
                    ModFolderGuard.NoteBaseline(PenumbraMeta.ModRoot);
                    break;

                case PenumbraApi.ApiResult.Failed:
                    Logger.LogWarn("Penumbra: reload of '{Mod}' did not complete — it answered but " +
                        "the call failed (see the API error above). New songs will not appear in " +
                        "game until it reloads. Penumbra {Penumbra}.",
                        mod, Utils.PenumbraInstall.CachedDescription);

                    // Penumbra answered but did not re-read the folder, so its in-memory copy is now
                    // older than what is on disk — and the next thing that makes it write this mod
                    // puts that copy back. Take a copy of the good state while it still exists.
                    // ourWrite:false, because the write being guarded against is not ours. Skipped
                    // when nothing has been edited since the last copy: retries go on for as long as
                    // Penumbra stays wedged, and re-copying an unchanged folder only buries the early
                    // snapshot that is actually worth keeping.
                    if (ModFolderGuard.ShouldSnapshotForFailure())
                    {
                        if (PenumbraMeta.DetectFormat() == ModFormat.V3)
                            V3GroupFileStore.TrySnapshotSet(ourWrite: false);
                        else
                            PenumbraMeta.TrySnapshot(PenumbraMeta.ModRoot,
                                Settings.ModName ?? string.Empty, ourWrite: false);
                    }

                    // Still a baseline: the folder as it stands is ours, whatever Penumbra thinks.
                    ModFolderGuard.NoteBaseline(PenumbraMeta.ModRoot);

                    if (ModFolderGuard.NoteReloadFailed())
                    {
                        Logger.LogWarn("Penumbra has refused {Count} reloads in a row — pausing the " +
                            "refresh after each edit and retrying every {Minutes} minute(s). Edits " +
                            "are still saved to disk.",
                            ModFolderGuard.ConsecutiveFailures,
                            (int)ModFolderGuard.RetryAfterPause.TotalMinutes);
                        ReloadPausedChanged?.Invoke(true);
                    }

                    // Make the retry the log line just promised actually happen, rather than leaving
                    // it to whenever the user next edits something.
                    if (debounced)
                        ArmPauseRetry();
                    break;

                // NoAnswer is deliberately not COUNTED. It is the ordinary state of editing with the
                // game closed, or of Penumbra mid-zone-load, and the API layer already reports each
                // cause once per session. Letting it spend the failure budget would pause reloads
                // permanently for every offline editor — and a Penumbra that is not running cannot
                // rewrite the folder, so there is nothing to guard against either.
                //
                // It does, however, END a pause that has already run out. That is the same reasoning
                // from the other side: nothing is there to be paused against. Left armed, the stale
                // value silently swallowed the report the NEXT run of refusals should have produced.
                default:
                    if (ModFolderGuard.NoteReloadUnanswered())
                    {
                        Logger.LogInfo("Penumbra: no answer to a reload, so the pause after its " +
                            "earlier refusals is cleared — nothing is there to write the folder back.");
                        ReloadPausedChanged?.Invoke(false);
                    }
                    break;
            }
        }

        /// <summary>Raised when the per-edit reload is paused or resumes.</summary>
        internal static event Action<bool>? ReloadPausedChanged;
    }
}
