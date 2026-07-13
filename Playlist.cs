using Newtonsoft.Json;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VfxEditor.ScdFormat;
using System.IO;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading;

namespace Pickles_Playlist_Editor
{
    enum SortDirection
    {
        Ascending,
        Descending
    }

    // Thrown when a playlist's group JSON cannot be located in the mod folder, so a write has
    // nowhere to land. Callers should treat this as "the edit did not happen" and roll back.
    public class PlaylistSaveException : InvalidOperationException
    {
        public string PlaylistName { get; }

        public PlaylistSaveException(string playlistName)
            : base($"Could not save playlist '{playlistName}': its group JSON is missing from the mod " +
                   "folder. The change was not applied. (If Penumbra is running, it may have renamed or " +
                   "rewritten the file — reload the mod list and try again.)")
        {
            PlaylistName = playlistName;
        }
    }

    public class Playlist
    {
        public int Version { get { return 0; } }
        public string Name { get; set; }
        public string Description { get { return string.Empty; } }
        public string Image { get { return string.Empty; } }
        public int Page { get { return 0; } }
        public int Priority { get; set; }
        public string Type { get { return "Single"; } }
        public int DefaultSettings { get { return 0; } }
        public List<Option> Options { get; set; } = new List<Option>();

        private bool? _isVFXGroup;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        

        public static void Create(string playlistName, string dir, Action<int>? callback)
        {
            if (playlistName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Playlist name cannot contain any of the following characters: "
                    + string.Join(" ", Path.GetInvalidFileNameChars()));

            Playlist group = new Playlist();
            group.Name = playlistName;
            group.Options = new List<Option>();

            Playlist mergedGroup = null;
            var groupFileNames = GetJsonFiles(playlistName);
            string fileName;
            if (groupFileNames.Length == 1)
            {
                fileName = groupFileNames[0];
                mergedGroup = JsonConvert.DeserializeObject<Playlist>(File.ReadAllText(fileName));
            }
            else
            {
                Option opt = new Option();
                opt.Name = "Off";
                opt.Files = new Dictionary<string, string>();
                group.Options.Add(opt);
                groupFileNames = Directory.GetFiles(Path.Combine(Settings.PenumbraLocation, Settings.ModName), "group_*");
                List<string> groupfiles = new List<string>(groupFileNames);
                groupfiles.Sort();
                mergedGroup = group;

                int groupNumber = 1;
                if (groupfiles.Count > 0)
                {
                    string lastName = groupfiles[groupfiles.Count - 1];
                    groupNumber = Int32.Parse(Path.GetFileNameWithoutExtension(lastName).Substring(6, 3)) + 1;
                    mergedGroup.Priority = groupNumber;
                }

                fileName = string.Format("group_{0}_{1}.json", string.Format("{0:D3}", groupNumber), playlistName);
            }

            if (!string.IsNullOrEmpty(dir))
            {
                int count = 0, totalCount = 0;
                foreach (string ext in Settings.SupportedFileTypes)
                {
                    string[] fileNames = Directory.GetFiles(dir, "*" + ext, SearchOption.AllDirectories);
                    totalCount += fileNames.Length;
                }

                foreach (string ext in Settings.SupportedFileTypes)
                {
                    string[] fileNames = Directory.GetFiles(dir, "*"+ext, SearchOption.AllDirectories);

                    foreach (string file in fileNames)
                    {
                        AddFiles(playlistName, mergedGroup, file);
                        if (callback != null)
                            callback((int)((float)(++count) / totalCount * 100));
                    }
                }
            }

            string json = JsonConvert.SerializeObject(mergedGroup, Formatting.Indented);


            File.WriteAllText(Path.Combine(Settings.PenumbraLocation, Settings.ModName, fileName), json);
            Directory.CreateDirectory(Path.Combine(Settings.PenumbraLocation, Settings.ModName, ResolvePlaylistScdDirectory(playlistName, mergedGroup)));

            // Notify Penumbra (if present) to refresh this mod because files/config changed.
            RefreshPenumbraMod();
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
                opt.Files = new Dictionary<string, string>();
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
            // disk and only then writes the JSON that points at the new names — if that write were
            // dropped, every song in this playlist would end up pointing at a file that no longer
            // exists under that name.
            if (!HasGroupJson())
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

                if (song.Files != null)
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

        public static Dictionary<string, Playlist> GetAll()
        {
            Dictionary<string, Playlist> playlists = new Dictionary<string, Playlist>();
            if (Settings.PenumbraLocation == null || Settings.ModName == null)
                return playlists;

            string modDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(modDirectory))
                return playlists;

            // Heal any leftover filename/content name mismatches before reading, so playlists that
            // the old wildcard bug scrambled become visible again. Runs at most once per session.
            Library.HealNameMismatchesOnce();

            // Clear the .bak/.tmp litter older builds left in the mod folder Penumbra scans. Backups
            // are moved, not deleted — Repair still finds them. Runs at most once per session.
            MigrateStrayBackupsOnce();

            var loaded = new List<(int number, Playlist playlist)>();
            foreach (string file in Directory.GetFiles(modDirectory, "group_*.json"))
            {
                try
                {
                    Playlist playlist = JsonConvert.DeserializeObject<Playlist>(File.ReadAllText(file));
                    if (playlist == null)
                        Console.Error.WriteLine("Error loading playlist from file " + file);
                    else
                        loaded.Add((GroupNumberOf(file), playlist));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Error loading playlist from file " + file + ": " + ex);
                }
            }

            // Order by the group_NNN filename index to mirror Penumbra exactly — Penumbra orders
            // groups by that number and ignores the "Priority" field for display. Name is only a
            // tie-breaker for the rare unparseable/duplicate-number case.
            foreach (var entry in loaded
                .OrderBy(e => e.number)
                .ThenBy(e => e.playlist.Name, StringComparer.OrdinalIgnoreCase))
            {
                // On a duplicate Name (leftover corruption), keep the first in this order.
                if (!playlists.ContainsKey(entry.playlist.Name))
                    playlists[entry.playlist.Name] = entry.playlist;
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

            if (GetJsonFiles(newName).Length > 0)
                throw new InvalidOperationException($"A playlist named '{newName}' already exists.");

            string modDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            string? oldJsonPath = GetJsonFiles(oldName).FirstOrDefault();
            if (string.IsNullOrEmpty(oldJsonPath) || !File.Exists(oldJsonPath))
                throw new FileNotFoundException("Playlist JSON file not found.", oldJsonPath);

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

            string newJsonPath = Path.Combine(modDirectory, Path.GetFileName(oldJsonPath).Replace(oldName.Replace("/", "_"), newName.Replace("/", "_")));
            if (!string.Equals(oldJsonPath, newJsonPath, StringComparison.OrdinalIgnoreCase))
                File.Move(oldJsonPath, newJsonPath);

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

            // Write to the renamed file directly: the on-disk content still says oldName until this
            // save, so the content-Name lookup in Save() would not find it yet.
            Save(newJsonPath);
            Logger.LogInfo("Renamed playlist '{Old}' -> '{New}' (scd folder '{OldDir}' -> '{NewDir}', changed={Changed})",
                oldName, newName, oldScdDir, newScdDir, scdDirChanged);
            return true;
        }

        // True when this playlist's group JSON can actually be located on disk, i.e. Save() will
        // have somewhere to write. Callers that are about to do something destructive (move a song
        // out of another playlist, delete an audio file) should pre-flight with this so they never
        // commit half an edit against a playlist that cannot be saved.
        internal bool HasGroupJson() => GetJsonFiles(Name).Length > 0;

        public void Save()
        {
            var fileNames = GetJsonFiles(Name);
            if (fileNames.Length == 0)
            {
                // The whole "adding deleted my playlist" class of bug lives here: if no group JSON
                // matches this playlist's content Name, the edit has nowhere to go. This used to log
                // and return, which let callers commit the destructive half of an edit and quietly
                // drop the other half — a cross-playlist drag removed the song from the source,
                // saved that, then no-opped the target write and lost the song. Throw so callers can
                // roll back and the user sees an error instead of silent data loss.
                Logger.LogError("Save('{Name}'): no matching group JSON found — aborting, nothing written.", Name);
                throw new PlaylistSaveException(Name);
            }
            if (fileNames.Length > 1)
                Logger.LogWarn("Save('{Name}'): {Count} files share this name ({Files}); writing the first.",
                    Name, fileNames.Length, string.Join(", ", fileNames.Select(Path.GetFileName)));
            Save(fileNames[0]);
        }

        // Writes this playlist to an explicit file. Used by Save() (which resolves the file by
        // content Name) and by Rename(), which already knows the destination path and whose
        // in-memory Name no longer matches the on-disk content, so a content-Name lookup would miss.
        internal void Save(string targetPath)
        {
            try
            {
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                WriteJsonAtomic(targetPath, json);
                Logger.LogInfo("Saved playlist '{Name}' ({Count} options) -> {File}",
                    Name, Options?.Count ?? 0, Path.GetFileName(targetPath));
            }
            catch (Exception ex)
            {
                Logger.LogError("Save('{Name}') failed writing {File}: {Error}", Name, targetPath, ex);
                throw;
            }

            // Notify Penumbra (if present) that the mod directory changed so it can refresh.
            RefreshPenumbraMod();
        }

        // Where group-JSON backups live. Deliberately OUTSIDE the Penumbra mod folder: Penumbra
        // scans that directory, and we were leaving hundreds of .bak/.tmp files in it.
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

        // Crash-safe write that Penumbra sees as a MODIFY, never a delete+create.
        //
        // This used to stage a .tmp beside the target and File.Replace() it into place. Replace
        // unlinks the target's directory entry and swaps a different file in — to Penumbra's folder
        // watcher that reads as "the group file disappeared, then a new one appeared", so Penumbra
        // compacted and renumbered the remaining group_NNN_*.json files. Since playlist order is
        // derived from that number, our own save was shuffling the user's playlist order (observed:
        // 'Beach' bouncing 001 -> 002 -> 001 across three consecutive song moves). It also raced
        // GetJsonFiles, which is how a save could report "no matching group JSON found" and silently
        // drop an edit.
        //
        // Writing the bytes in place keeps the directory entry — and therefore the group number —
        // untouched, so Penumbra just re-reads the file and leaves the ordering alone.
        private static void WriteJsonAtomic(string targetPath, string json)
        {
            // Back up the previous contents outside the mod folder before overwriting.
            if (File.Exists(targetPath))
            {
                try
                {
                    File.Copy(targetPath, Path.Combine(BackupDir, Path.GetFileName(targetPath) + ".bak"), true);
                }
                catch (Exception ex)
                {
                    Logger.LogWarn("Backup of {File} failed (continuing): {Error}", Path.GetFileName(targetPath), ex.Message);
                }
            }

            // Stage in the temp dir (outside the mod folder) so a half-written file is never visible
            // to Penumbra, then overwrite the target in place. File.Copy(overwrite: true) truncates
            // and rewrites the existing file rather than replacing the directory entry.
            string tmp = Path.Combine(Path.GetTempPath(), Path.GetFileName(targetPath) + ".tmp");
            try
            {
                File.WriteAllText(tmp, json);
                File.Copy(tmp, targetPath, true);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        private static bool _strayBackupsMigrated;

        // Older builds wrote .bak/.tmp files directly into the Penumbra mod folder — hundreds of them
        // accumulated, because every renumbering minted a new .bak name. Move (never delete) them out
        // to the backup dir: it declutters the directory Penumbra scans while keeping every backup
        // available to the Repair button, which searches both locations.
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
                foreach (var file in Directory.EnumerateFiles(modDirectory, "group_*.json.*").ToList())
                {
                    string ext = Path.GetExtension(file);
                    bool isBak = ext.Equals(".bak", StringComparison.OrdinalIgnoreCase);
                    bool isTmp = ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase);
                    if (!isBak && !isTmp) continue;

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
                Logger.LogInfo("Moved {Count} stray .bak/.tmp file(s) out of the mod folder into {Dir}.",
                    moved, BackupDir);
        }

        public void Delete()
        {
            if (string.IsNullOrEmpty(Name))
                return;
            Logger.LogInfo("Deleting playlist '{Name}' ({Count} options)", Name, Options?.Count ?? 0);
            if (GetJsonFiles(Name).Length > 0)
                File.Delete(GetJsonFiles(Name)[0]);

            if (Directory.Exists(Path.Combine(Settings.PenumbraLocation, Settings.ModName, Name)))
                Directory.Delete(Path.Combine(Settings.PenumbraLocation, Settings.ModName, Name), true);
            // Notify Penumbra after removing files
            RefreshPenumbraMod();
        }

        private static string[] GetJsonFiles(string name)
        {
            if (Settings.PenumbraLocation == null || Settings.ModName == null)
                return Array.Empty<string>();

            string modDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(modDirectory))
                return Array.Empty<string>();

            // Penumbra rewrites the group_NNN_<name>.json *filenames* (renumbering, re-sanitizing)
            // on every mod change, so the filename is not a stable identity — matching on it made
            // Save() miss the file and silently no-op, so edits vanished and playlists looked
            // deleted. Match instead on the "Name" *inside* each JSON, the one field Penumbra never
            // touches, exactly as GetAll() keys playlists. Ordered by group number so callers that
            // take [0] get a deterministic file.
            return Directory.GetFiles(modDirectory, "group_*.json")
                .Where(f => string.Equals(TryReadContentName(f), name, StringComparison.Ordinal))
                .OrderBy(GroupNumberOf)
                .ToArray();
        }

        // Reads just the "Name" field from a group JSON, tolerating parse/IO errors (returns null).
        private static string? TryReadContentName(string path)
        {
            try
            {
                using var reader = new StreamReader(path);
                using var jsonReader = new JsonTextReader(reader);
                var obj = Newtonsoft.Json.Linq.JToken.ReadFrom(jsonReader) as Newtonsoft.Json.Linq.JObject;
                return obj?["Name"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static readonly Regex GroupNumberPattern = new(@"^group_(\d+)_", RegexOptions.IgnoreCase);

        // Parses the NNN group index out of a "group_NNN_Name.json" path so playlists can be ordered
        // deterministically by number rather than by filesystem enumeration order. Unparseable names
        // sort last, keeping them out of the meaningful range.
        private static int GroupNumberOf(string path)
        {
            var m = GroupNumberPattern.Match(Path.GetFileName(path));
            return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
        }

        internal void Shuffle()
        {
            var jsonFiles = GetJsonFiles(Name);
            string? backupPath = null;
            if (jsonFiles.Length > 0 && File.Exists(jsonFiles[0]))
            {
                backupPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(jsonFiles[0]));
                File.Copy(jsonFiles[0], backupPath, true);
            }
            try
            {
                int n = Options.Count;
                List<Option> shuffledOptions = new List<Option>(n);
                shuffledOptions.Add(Options[0]); // Keep the "Off" option in place
                Options.RemoveAt(0);
                Random rng = new Random();
                while (Options.Count > 0)
                {
                    int k = rng.Next(Options.Count);
                    shuffledOptions.Add(Options[k]);
                    Options.RemoveAt(k);
                }
                Options = shuffledOptions;
                Save();
            }
            catch (Exception ex)
            {
                if (backupPath != null && File.Exists(backupPath) && jsonFiles.Length > 0)
                    File.Copy(backupPath, jsonFiles[0], true);
                MessageBoxW(IntPtr.Zero, ex.ToString(), "Shuffle Error", 0x00000010); // MB_OK | MB_ICONERROR
                throw;
            }
        }

        internal void Sort(SortDirection direction)
        {
            var jsonFiles = GetJsonFiles(Name);
            string? backupPath = null;
            if (jsonFiles.Length > 0 && File.Exists(jsonFiles[0]))
            {
                backupPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(jsonFiles[0]));
                File.Copy(jsonFiles[0], backupPath, true);
            }
            try
            {
                Option offOption = Options.FirstOrDefault(o => o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase));
                List<Option> otherOptions = Options.Where(o => !o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase)).ToList();
                if (direction == SortDirection.Ascending)
                    otherOptions = otherOptions.OrderBy(o => BPMDetector.GetBPMFromSCD(GetScdPath(o))).ToList();
                else
                    otherOptions = otherOptions.OrderByDescending(o => BPMDetector.GetBPMFromSCD(GetScdPath(o))).ToList();
                Options = new List<Option>();
                if (offOption != null)
                    Options.Add(offOption);
                Options.AddRange(otherOptions);
                Save();
            }
            catch (Exception ex)
            {
                if (backupPath != null && File.Exists(backupPath) && jsonFiles.Length > 0)
                    File.Copy(backupPath, jsonFiles[0], true);
                MessageBoxW(IntPtr.Zero, ex.ToString(), "Sort Error", 0x00000010); // MB_OK | MB_ICONERROR
                throw;
            }
        }

        internal void SortByKey(SortDirection direction)
        {
            var jsonFiles = GetJsonFiles(Name);
            string? backupPath = null;
            if (jsonFiles.Length > 0 && File.Exists(jsonFiles[0]))
            {
                backupPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(jsonFiles[0]));
                File.Copy(jsonFiles[0], backupPath, true);
            }
            try
            {
                Option offOption = Options.FirstOrDefault(o => o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase));
                List<Option> otherOptions = Options.Where(o => !o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase)).ToList();
                if (direction == SortDirection.Ascending)
                    otherOptions = otherOptions.OrderBy(o => KeyDetector.GetKeyFromSCD(GetScdPath(o))).ToList();
                else
                    otherOptions = otherOptions.OrderByDescending(o => KeyDetector.GetKeyFromSCD(GetScdPath(o))).ToList();
                Options = new List<Option>();
                if (offOption != null)
                    Options.Add(offOption);
                Options.AddRange(otherOptions);
                Save();
            }
            catch (Exception ex)
            {
                if (backupPath != null && File.Exists(backupPath) && jsonFiles.Length > 0)
                    File.Copy(backupPath, jsonFiles[0], true);
                MessageBoxW(IntPtr.Zero, ex.ToString(), "Sort Error", 0x00000010); // MB_OK | MB_ICONERROR
                throw;
            }
        }

        internal void SortByName()
        {
            var jsonFiles = GetJsonFiles(Name);
            string? backupPath = null;
            if (jsonFiles.Length > 0 && File.Exists(jsonFiles[0]))
            {
                backupPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(jsonFiles[0]));
                File.Copy(jsonFiles[0], backupPath, true);
            }
            try
            {
                Option offOption = Options.FirstOrDefault(o => o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase));
                List<Option> otherOptions = Options.Where(o => !o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
                Options = new List<Option>();
                if (offOption != null) Options.Add(offOption);
                Options.AddRange(otherOptions);
                Save();
            }
            catch (Exception ex)
            {
                if (backupPath != null && File.Exists(backupPath) && jsonFiles.Length > 0)
                    File.Copy(backupPath, jsonFiles[0], true);
                MessageBoxW(IntPtr.Zero, ex.ToString(), "Sort Error", 0x00000010); // MB_OK | MB_ICONERROR
                throw;
            }
        }

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
        /// Attempt to notify Penumbra (FFXIV Dalamud plugin) that the mod folder changed so Penumbra can refresh its cache.
        /// This is a best-effort notification: Penumbra watches file changes; touching meta.json or creating a transient marker file
        /// commonly triggers a refresh. This function does not interact with Dalamud directly.
        /// </summary>
        internal static void ReorderAll(List<string> orderedNames)
        {
            string modDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(modDir)) return;

            // Collect VFX group names that weren't in the treeview
            var orderedSet = new HashSet<string>(orderedNames);
            var allPlaylists = GetAll();
            var vfxNames = new List<string>();
            foreach (var kvp in allPlaylists)
            {
                if (!orderedSet.Contains(kvp.Key) && kvp.Value.IsVFXGroup())
                    vfxNames.Add(kvp.Key);
            }

            // Combined list: treeview order first, then VFX groups
            var allNames = new List<string>(orderedNames);
            allNames.AddRange(vfxNames);

            // Penumbra orders groups solely by the NNN index in each "group_NNN_<name>.json"
            // filename (it ignores the "Priority" field for display — that only affects conflict
            // resolution). So reordering means renumbering the files, not rewriting Priority.
            //
            // Rename in two phases so intermediate collisions can't clobber a file: first move every
            // participating file to a ".reorder_tmp" sidecar, then write each back at its new number.
            // A crash between phases leaves ".reorder_tmp" files, which ReclaimReorderTempFiles
            // restores on next load.
            Logger.LogInfo("ReorderAll: renumbering {Count} group(s): {Order}",
                allNames.Count, string.Join(" > ", allNames));

            var staged = new List<(string name, string tempPath)>();
            foreach (string name in allNames)
            {
                var files = GetJsonFiles(name);
                if (files.Length == 0)
                {
                    Logger.LogWarn("ReorderAll: no file found for '{Name}' — it will keep its old position.", name);
                    continue;
                }
                try
                {
                    string tempPath = files[0] + ".reorder_tmp";
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    File.Move(files[0], tempPath);
                    staged.Add((name, tempPath));
                }
                catch (Exception ex)
                {
                    Logger.LogError("ReorderAll: failed staging '{Name}' ({File}): {Error}",
                        name, Path.GetFileName(files[0]), ex);
                }
            }

            for (int i = 0; i < staged.Count; i++)
            {
                string cleanName = staged[i].name.Replace("/", "_");
                string newPath = Path.Combine(modDir, $"group_{i + 1:D3}_{cleanName}.json");
                try
                {
                    // Keep Priority in step with the number so anything that still reads Priority
                    // (and the app's own tie-breaking) agrees with Penumbra's filename order.
                    var jobj = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(staged[i].tempPath, Encoding.UTF8));
                    jobj["Priority"] = i + 1;
                    File.WriteAllText(newPath, jobj.ToString(Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);
                    File.Delete(staged[i].tempPath);
                }
                catch (Exception ex)
                {
                    // Leave the .reorder_tmp in place; ReclaimReorderTempFiles recovers it on next load.
                    Logger.LogError("ReorderAll: failed writing '{Name}' -> {File}: {Error}",
                        staged[i].name, Path.GetFileName(newPath), ex);
                }
            }

            // Reload Penumbra once, after all renames are done, to minimise the window where it sees
            // a half-renumbered folder.
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

        // Penumbra renumbers and rewrites every group_NNN_*.json in the mod folder when it reloads.
        // Firing a reload after each individual Save() meant a burst of edits (e.g. dragging songs
        // one by one) had Penumbra rewriting the whole folder every couple of seconds, racing our
        // own reads and writes. Debounce instead: each change restarts a 20s countdown, so Penumbra
        // is only asked to reload once the user has actually stopped editing.
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