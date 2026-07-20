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
    /// In Penumbra's v4 format every group lives in the single root <c>meta.json</c>, identified by a
    /// stable <c>Id</c> GUID rather than by a <c>group_NNN_name.json</c> filename. Display order is
    /// the group's index in the manifest's <c>Groups</c> array.
    ///
    /// Because the whole library is now one file, this class never serializes itself wholesale: every
    /// write goes through <see cref="PenumbraMeta.Mutate"/> and splices this one group into a
    /// freshly-read manifest. See <see cref="Save"/>.
    /// </summary>
    public class Playlist
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public List<Option> Options { get; set; } = new List<Option>();

        // The group object this playlist was read from. Kept so a save can clone it and preserve keys
        // this app doesn't model — Description, Image, Page, DefaultSettings, Priority, and anything a
        // future Penumbra adds. Null for a playlist that hasn't been persisted yet.
        [JsonIgnore]
        internal JObject Raw { get; set; }

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
                int count = 0, totalCount = 0;
                foreach (string ext in Settings.SupportedFileTypes)
                    totalCount += Directory.GetFiles(dir, "*" + ext, SearchOption.AllDirectories).Length;

                foreach (string ext in Settings.SupportedFileTypes)
                {
                    foreach (string file in Directory.GetFiles(dir, "*" + ext, SearchOption.AllDirectories))
                    {
                        AddFiles(playlistName, group, file);
                        if (callback != null)
                            callback((int)((float)(++count) / totalCount * 100));
                    }
                }
            }

            group.Upsert();
            Directory.CreateDirectory(Path.Combine(Settings.PenumbraLocation, Settings.ModName,
                ResolvePlaylistScdDirectory(playlistName, group)));

            // Notify Penumbra (if present) to refresh this mod because files/config changed.
            RefreshPenumbraMod();
        }

        private static Playlist NewPlaylist(string playlistName)
        {
            var group = new Playlist { Id = Guid.NewGuid(), Name = playlistName };
            group.Options.Add(new Option { Id = Guid.NewGuid(), Name = "Off" });
            return group;
        }

        static Option AddFiles(string playlistName, Playlist group, string file)
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

                string targetPath = GetNonCollidingPath(Path.Combine(outDir, safeFileName + ".scd"));
                using (BinaryWriter writer = new BinaryWriter(new FileStream(targetPath, FileMode.CreateNew)))
                {
                    scdFile.Write(writer);
                }
                opt = new Option();
                opt.Name = filenameroot;
                opt.Files.Add(
                    Settings.BaselineScdKey,
                    Path.Combine(playlistScdDirectory, Path.GetFileName(targetPath)));
                group.Options.Add(opt);
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
            string outDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, playlistScdDirectory);
            Directory.CreateDirectory(outDir);
            List<Option> optionsToRemove = new List<Option>();
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

                        File.Move(oldPath, newPath);
                        BPMDetector.UpdateCacheForSCD(oldPath, newPath);
                        KeyDetector.UpdateCacheForSCD(oldPath, newPath);
                        song.Files[song.Files.Keys.First()] = Path.Combine(playlistScdDirectory, Path.GetFileName(newPath));
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
                playlist.Options.Remove(opt);
            }
            this.Save();
            // Save() will refresh Penumbra; no need to call here.
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

        /// <summary>Builds a playlist from a v4 manifest group object.</summary>
        internal static Playlist FromJson(JObject g)
        {
            var playlist = new Playlist
            {
                Name = g["Name"]?.ToString() ?? string.Empty,
                Raw = g,
            };

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

            // Clear the .bak/.tmp litter older builds left in the mod folder Penumbra scans. Backups
            // are moved, not deleted. Runs at most once per session.
            MigrateStrayBackupsOnce();

            var groups = PenumbraMeta.TryReadGroups();
            if (groups == null)
            {
                // No "Groups" key at all. Two very different causes, and telling them apart matters:
                // a folder still in the pre-v4 layout (one file per option group) needs Penumbra to
                // convert it, whereas a manifest with no group files beside it is damaged and needs
                // Repair. Reporting the first for both sends the second case chasing the wrong fix.
                // (An *empty* Groups array is a valid v4 mod with no groups and does not land here.)
                if (PenumbraMeta.IsLegacyModFormat())
                    Logger.LogWarn(
                        "meta.json has no Groups array and group_*.json files are present — '{Mod}' is " +
                        "still in Penumbra's old (pre-v4) mod format. Load the mod in Penumbra once to " +
                        "convert it, then reopen this app.",
                        Settings.ModName);
                else
                    Logger.LogError(
                        "meta.json has no Groups array and no legacy group files — '{Mod}' looks " +
                        "damaged, so no playlists could be loaded. Nothing has been written. Use " +
                        "Repair Library to restore it from the most recent backup.",
                        Settings.ModName);
                return playlists;
            }

            // Array order IS the order — Penumbra displays groups by their index in this array. There
            // is nothing left to sort by.
            foreach (var g in groups.OfType<JObject>())
            {
                var playlist = FromJson(g);
                if (string.IsNullOrEmpty(playlist.Name))
                    continue;

                // v4 permits duplicate group names, but this dictionary is keyed by name and the UI
                // addresses playlists by name, so the second one would be unreachable anyway.
                if (playlists.ContainsKey(playlist.Name))
                {
                    Logger.LogWarn("GetAll: duplicate playlist name '{Name}' in meta.json — keeping the first.",
                        playlist.Name);
                    continue;
                }
                playlists[playlist.Name] = playlist;
            }
            return playlists;
        }

        public void Add(string[] fileNames, Action<int>? callback = null)
        {
            Logger.LogInfo("Add: {Count} file(s) into '{Name}' (currently {Existing} options)",
                fileNames?.Length ?? 0, Name, Options?.Count ?? 0);
            int count = 0;
            foreach (string file in fileNames)
            {
                if (Settings.SupportedFileTypes.Contains(Path.GetExtension(file).ToLower()))
                    AddFiles(Name, this, file);
                if (callback != null)
                    callback((int)((float)(++count)/fileNames.Length*100));
            }
            Save();
        }

        public void Insert(string[] fileNames, int index, Action<int>? callback = null)
        {
            Logger.LogInfo("Insert: {Count} file(s) into '{Name}' at index {Index} (currently {Existing} options)",
                fileNames?.Length ?? 0, Name, index, Options?.Count ?? 0);
            int offIndex = Options.FindIndex(o => o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase));
            if (offIndex >= 0)
                index = Math.Max(index, offIndex + 1);

            int count = 0;
            foreach (string file in fileNames)
            {
                Option opt = AddFiles(Name, this, file);
                Options.RemoveAt(Options.Count - 1);
                Options.Insert(index, opt);
                index++;
                if (callback != null)
                    callback((int)((float)(++count) / fileNames.Length * 100));
            }
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

            var root = PenumbraMeta.Read()
                ?? throw new PenumbraMetaException("Penumbra's mod manifest could not be read; the rename was not applied.");
            if (PenumbraMeta.FindGroupByName(root, newName) != null)
                throw new InvalidOperationException($"A playlist named '{newName}' already exists.");
            if (!ResolveTarget(root, out _, out _))
                throw new PlaylistSaveException(oldName);

            string modDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName);

            // Derive the SCD folder from the songs themselves rather than assuming it equals the old
            // playlist name — post-merge/repair playlists can store their audio in a differently
            // named folder, and the old name-guess would move the wrong (or no) folder and repoint
            // paths that never matched, orphaning every song.
            string oldScdDir = GetScdDirectoryForNewFiles();
            string newScdDir = GetDefaultPlaylistDirectoryName(newName);
            bool scdDirChanged = !string.Equals(oldScdDir, newScdDir, StringComparison.OrdinalIgnoreCase);

            if (scdDirChanged)
            {
                string oldFolder = Path.Combine(modDirectory, NormalizeRelativeModPath(oldScdDir));
                string newFolder = Path.Combine(modDirectory, NormalizeRelativeModPath(newScdDir));
                if (Directory.Exists(oldFolder))
                {
                    if (Directory.Exists(newFolder))
                        throw new InvalidOperationException($"A playlist folder named '{newScdDir}' already exists.");
                    Directory.Move(oldFolder, newFolder);
                }
            }

            Name = newName;

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
                            string oldFull = Path.Combine(Settings.PenumbraLocation, Settings.ModName, rel);
                            string newFull = Path.Combine(Settings.PenumbraLocation, Settings.ModName, newRel);
                            BPMDetector.UpdateCacheForSCD(oldFull, newFull);
                            KeyDetector.UpdateCacheForSCD(oldFull, newFull);
                            song.Files[key] = newRel;
                        }
                    }
                }
            }

            // Resolution is by Id, so the manifest still saying oldName is irrelevant.
            Save();
            Logger.LogInfo("Renamed playlist '{Old}' -> '{New}' (scd folder '{OldDir}' -> '{NewDir}', changed={Changed})",
                oldName, newName, oldScdDir, newScdDir, scdDirChanged);
            return true;
        }

        /// <summary>
        /// Locates this playlist's group in a freshly-read manifest. Id first — that is the stable
        /// identity Penumbra keys user selections against — falling back to an exact name match for a
        /// group that has no Id yet (just created, or imported from v3). A successful name match
        /// adopts the Id, so the fallback fires at most once per playlist.
        /// </summary>
        private bool ResolveTarget(JObject root, out JObject group, out int index)
        {
            if (PenumbraMeta.TryGetGroupById(root, Id, out group, out index))
                return true;

            var byName = PenumbraMeta.FindGroupByName(root, Name, out index);
            if (byName == null)
                return false;

            group = byName;
            if (Guid.TryParse(byName["Id"]?.ToString(), out var found))
                Id = found;
            return true;
        }

        /// <summary>
        /// True when this playlist's group can actually be located in the manifest, i.e. Save() will
        /// have somewhere to write. Callers that are about to do something destructive (move a song
        /// out of another playlist, delete an audio file) should pre-flight with this so they never
        /// commit half an edit against a playlist that cannot be saved.
        /// </summary>
        internal bool ExistsInManifest()
        {
            var root = PenumbraMeta.Read();
            return root != null && ResolveTarget(root, out _, out _);
        }

        /// <summary>
        /// This playlist as a v4 manifest group. Built by cloning <paramref name="existing"/> so keys
        /// this app doesn't model — Description, Image, Page, DefaultSettings, Priority — survive the
        /// round trip. Only Id, Name, Type and Options are ours to write.
        /// </summary>
        internal JObject ToJson(JObject existing)
        {
            var g = existing != null ? (JObject)existing.DeepClone() : new JObject();

            if (Id == Guid.Empty)
                Id = Guid.NewGuid();
            g["Id"] = Id.ToString();
            g["Name"] = Name ?? string.Empty;
            if (g["Type"] == null)
                g["Type"] = "Single";

            var existingOptions = (existing?["Options"] as JArray)?.OfType<JObject>().ToList();
            g["Options"] = new JArray(Options.Select(o => o.ToJson(FindExistingOption(existingOptions, o))));

            return g;
        }

        // Match by Id so an option keeps whatever Penumbra stored on it even after being reordered or
        // moved between playlists. Name is only a fallback for options this app just created.
        private static JObject FindExistingOption(List<JObject> existingOptions, Option option)
        {
            if (existingOptions == null) return null;

            if (option.Id != Guid.Empty)
            {
                var byId = existingOptions.FirstOrDefault(o =>
                    Guid.TryParse(o["Id"]?.ToString(), out var id) && id == option.Id);
                if (byId != null) return byId;
            }

            return existingOptions.FirstOrDefault(o =>
                string.Equals(o["Name"]?.ToString(), option.Name, StringComparison.Ordinal));
        }

        /// <summary>
        /// Writes this playlist back to Penumbra's manifest.
        ///
        /// The whole library now lives in one file, so this deliberately does NOT serialize the app's
        /// model of the mod. It re-reads the manifest, splices in this one group, and writes the rest
        /// back verbatim — otherwise a stale in-memory Playlist would clobber every other playlist,
        /// including any the user edited in Penumbra since this one was loaded.
        /// </summary>
        public void Save()
        {
            try
            {
                PenumbraMeta.Mutate(root =>
                {
                    if (!ResolveTarget(root, out var existing, out int index))
                    {
                        // The whole "adding deleted my playlist" class of bug lives here: if the group
                        // is gone from the manifest, the edit has nowhere to go. Throw so callers can
                        // roll back and the user sees an error instead of silent data loss.
                        Logger.LogError("Save('{Name}'): group not found in meta.json — aborting, nothing written.", Name);
                        throw new PlaylistSaveException(Name);
                    }

                    ((JArray)root["Groups"])[index] = ToJson(existing);
                });

                Logger.LogInfo("Saved playlist '{Name}' ({Count} options) to meta.json", Name, Options?.Count ?? 0);
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
        /// Like <see cref="Save"/>, but appends the group when it isn't in the manifest yet instead of
        /// throwing. Only for genuinely new playlists — everything else should fail loudly.
        /// </summary>
        internal void Upsert()
        {
            PenumbraMeta.Mutate(root =>
            {
                if (root["Groups"] is not JArray groups)
                {
                    groups = new JArray();
                    root["Groups"] = groups;
                }

                if (ResolveTarget(root, out var existing, out int index))
                    groups[index] = ToJson(existing);
                else
                    groups.Add(ToJson(null));
            });

            Logger.LogInfo("Upserted playlist '{Name}' ({Count} options) into meta.json", Name, Options?.Count ?? 0);
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

        // Older builds wrote .bak/.tmp files directly into the Penumbra mod folder — hundreds of them
        // accumulated, because every renumbering minted a new .bak name. Since v4 these are dead
        // weight: the library lives in meta.json and nothing reads a group_*.json.bak any more. Move
        // (never delete) them out to the backup dir so the folder Penumbra scans holds only live mod
        // files, while the user's pre-migration copies stay on disk.
        internal static void MigrateStrayBackupsOnce()
        {
            if (_strayBackupsMigrated) return;
            if (Settings.PenumbraLocation == null || Settings.ModName == null) return;

            string modDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(modDirectory)) return;

            _strayBackupsMigrated = true;
            int moved = 0;
            try
            {
                string backupDir = BackupDir;
                var stray = Directory.EnumerateFiles(modDirectory, "group_*.json.*")
                    .Concat(Directory.EnumerateFiles(modDirectory, "default_mod.json.bak"))
                    .Concat(Directory.EnumerateFiles(modDirectory, "meta.json.*.tmp"))
                    .ToList();

                foreach (var file in stray)
                {
                    string ext = Path.GetExtension(file);
                    bool isBak = ext.Equals(".bak", StringComparison.OrdinalIgnoreCase);
                    bool isTmp = ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase);
                    bool isReorderTmp = file.EndsWith(".reorder_tmp", StringComparison.OrdinalIgnoreCase);
                    if (!isBak && !isTmp && !isReorderTmp) continue;

                    try
                    {
                        // A stale .tmp is a half-written file from an interrupted save — no value.
                        if (isTmp) { File.Delete(file); moved++; continue; }

                        string dest = Path.Combine(backupDir, Path.GetFileName(file));
                        File.Move(file, dest, overwrite: true);
                        moved++;
                    }
                    catch { /* locked or already gone; skip */ }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Stray-backup migration failed (harmless): {Error}", ex.Message);
                return;
            }

            if (moved > 0)
                Logger.LogInfo("Moved {Count} stray file(s) out of the mod folder into {Dir}.", moved, BackupDir);
        }

        public void Delete()
        {
            if (string.IsNullOrEmpty(Name))
                return;
            Logger.LogInfo("Deleting playlist '{Name}' ({Count} options)", Name, Options?.Count ?? 0);

            // Derive the audio folder from the songs rather than guessing it from the name — a
            // post-merge playlist can store its audio in a differently named folder, and the old
            // name-guess would either miss it or, worse, delete a folder belonging to someone else.
            string scdDir = GetScdDirectoryForNewFiles();

            PenumbraMeta.Mutate(root =>
            {
                if (root["Groups"] is not JArray groups) return;
                if (ResolveTarget(root, out _, out int index))
                    groups.RemoveAt(index);
            });

            // Only remove the audio once no surviving group points into that folder.
            if (!string.IsNullOrWhiteSpace(scdDir) && !AnyGroupUsesDirectory(scdDir))
            {
                string folder = Path.Combine(Settings.PenumbraLocation, Settings.ModName,
                    NormalizeRelativeModPath(scdDir));
                if (Directory.Exists(folder))
                {
                    try { Directory.Delete(folder, true); }
                    catch (Exception ex)
                    {
                        Logger.LogWarn("Deleted playlist '{Name}' but could not remove '{Folder}': {Error}",
                            Name, folder, ex.Message);
                    }
                }
            }

            RefreshPenumbraMod();
        }

        private static bool AnyGroupUsesDirectory(string scdDir)
        {
            string prefix = NormalizeRelativeModPath(scdDir) + Path.DirectorySeparatorChar;
            return GetAll().Values
                .SelectMany(p => p.Options)
                .Where(o => o?.Files != null)
                .SelectMany(o => o.Files.Values)
                .Select(NormalizeRelativeModPath)
                .Any(rel => rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
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
        /// Reorders the playlists to match <paramref name="orderedNames"/>.
        ///
        /// Penumbra orders groups by their index in the manifest's Groups array, so this is a single
        /// permutation of that array — no more renumbering filenames, and no intermediate on-disk
        /// state to recover from if it fails.
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

            PenumbraMeta.Mutate(root =>
            {
                if (root["Groups"] is not JArray groups)
                    throw new PenumbraMetaException("meta.json has no Groups array; the reorder was not applied.");

                var remaining = groups.OfType<JObject>().ToList();
                var reordered = new JArray();

                foreach (string name in allNames)
                {
                    var match = remaining.FirstOrDefault(g =>
                        string.Equals(g["Name"]?.ToString(), name, StringComparison.Ordinal));
                    if (match == null)
                    {
                        Logger.LogWarn("ReorderAll: no group found for '{Name}' — it keeps its old position.", name);
                        continue;
                    }
                    remaining.Remove(match);
                    reordered.Add(match);
                }

                // Anything the app didn't model (or couldn't match) still has to land in the output.
                foreach (var leftover in remaining)
                    reordered.Add(leftover);

                if (reordered.Count != groups.Count)
                    throw new PenumbraMetaException(
                        $"Reorder would have changed the group count ({groups.Count} -> {reordered.Count}); " +
                        "refusing to write.");

                root["Groups"] = reordered;
            });

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
                // Restart the countdown — only the last change in a burst triggers the reload.
                s_reloadTimer.Change(PenumbraReloadDebounceMs, Timeout.Infinite);
            }
        }

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
            FirePenumbraReload();
        }

        private static void FirePenumbraReload()
        {
            try
            {
                if (Settings.AutoReloadMod)
                {
                    Logger.LogInfo("Penumbra: reloading mod '{Mod}' (debounced).", Settings.ModName);
                    PenumbraApi.ReloadMod(Settings.ModName, Settings.ModName);
                }
            }
            catch
            {
                // best-effort only; swallow any errors to avoid breaking the UI
            }
        }
    }
}
