using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Persistence for Penumbra's pre-v4 layout: one <c>group_NNN_&lt;name&gt;.json</c> per option
    /// group beside <c>default_mod.json</c>, with a <c>meta.json</c> that holds mod-level metadata and
    /// nothing about groups.
    ///
    /// Two properties of this layout drive everything below, and both are the opposite of v4's:
    ///
    ///   IDENTITY IS THE CONTENT NAME, NEVER THE FILENAME. Penumbra rewrites these filenames —
    ///   renumbering and re-sanitizing them — every time it reloads the mod. Matching on the filename
    ///   makes a save miss its file and silently no-op, which is how edits used to vanish and
    ///   playlists looked deleted. Every lookup here reads the <c>Name</c> field inside each file.
    ///
    ///   WRITES MUST NOT REPLACE THE DIRECTORY ENTRY. <c>File.Move</c>/<c>File.Replace</c> unlink the
    ///   target and swap a different file in, which Penumbra's folder watcher reads as "the group file
    ///   disappeared, then a new one appeared" and answers by compacting and renumbering every
    ///   group_NNN file. Since display order IS that number, our own save would shuffle the user's
    ///   playlist order (observed: 'Beach' bouncing 001 -> 002 -> 001 across three consecutive song
    ///   moves). <see cref="WriteGroupFile"/> copies bytes in place instead, keeping the entry — and
    ///   therefore the group number — untouched. This is exactly inverted from v4's
    ///   <see cref="PenumbraMeta.AtomicWrite"/>, which must swap the entry; do not unify them.
    /// </summary>
    internal sealed class V3GroupFileStore : IPlaylistStore
    {
        public ModFormat Format => ModFormat.V3;

        private const string GroupGlob = "group_*.json";
        private const string ReorderSuffix = ".reorder_tmp";
        private static readonly Regex GroupNumberPattern = new(@"^group_(\d+)_", RegexOptions.IgnoreCase);

        private static string ModRoot => PenumbraMeta.ModRoot;

        // ---- reading ---------------------------------------------------------------------------

        public List<Playlist> ReadAll()
        {
            var result = new List<Playlist>();
            string modDirectory = ModRoot;
            if (!Directory.Exists(modDirectory))
                return result;

            foreach (string file in GroupFilesOrdered())
            {
                var group = TryLoadGroup(file);
                if (group == null)
                {
                    Logger.LogWarn("Skipping unreadable group file '{File}'.", Path.GetFileName(file));
                    continue;
                }
                result.Add(Playlist.FromJson(group));
            }

            return result;
        }

        // Every group file, in Penumbra's display order: ascending group number, name as the
        // tie-break for the rare duplicate/unparseable number. Penumbra ignores the "Priority" field
        // for display — that only affects conflict resolution — so never sort by it.
        private static string[] GroupFilesOrdered()
        {
            string modDirectory = ModRoot;
            if (!Directory.Exists(modDirectory))
                return Array.Empty<string>();

            return Directory.GetFiles(modDirectory, GroupGlob)
                .OrderBy(GroupNumberOf)
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // Parses the NNN out of "group_NNN_Name.json". Unparseable names sort last, keeping them out
        // of the meaningful range rather than colliding at zero.
        private static int GroupNumberOf(string path)
        {
            var m = GroupNumberPattern.Match(Path.GetFileName(path));
            return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
        }

        private static JObject TryLoadGroup(string path)
        {
            try { return JObject.Parse(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception ex)
            {
                Logger.LogWarn("Could not parse group file '{File}': {Error}", Path.GetFileName(path), ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The group file re-read immediately before a write, or a throw.
        ///
        /// Never let a failed re-read fall through as null. <see cref="Playlist.ToJson"/> treats a null
        /// "existing" as "there was nothing here before" and builds the group from scratch, so a
        /// swallowed parse error would be written out as a perfectly successful save that had quietly
        /// reset Description, Image, Page, Priority and DefaultSettings to defaults and dropped every
        /// per-option key this app doesn't model.
        ///
        /// This is reachable: <see cref="TryReadContentName"/> resolved the file with JToken.ReadFrom,
        /// which stops at the end of the first token, while this parses with JObject.Parse, which
        /// rejects trailing content — so a file with garbage after the closing brace resolves fine and
        /// then fails here. Penumbra also rewrites these files on reload (which this app itself asks
        /// for), so the two reads can simply land either side of one.
        /// </summary>
        private static JObject LoadGroupForWrite(string path)
        {
            return TryLoadGroup(path)
                ?? throw new PenumbraMetaException(
                    $"'{Path.GetFileName(path)}' could not be re-read just before saving, so the edit " +
                    "was not applied — writing it now would have discarded the settings already in " +
                    "that file. Nothing was written. (If Penumbra is running it may be mid-write — " +
                    "try again in a moment.)");
        }

        /// <summary>
        /// Reads just the top-level <c>Name</c>, tolerating parse/IO errors.
        ///
        /// Stops at that property instead of materialising the document. Resolving one playlist reads
        /// the name of EVERY group file in the folder, and the Options array behind the name is
        /// essentially the whole file — so parsing it in full made a single save cost a full parse of
        /// the entire library, and a Repair (which saves per playlist) quadratic in playlist count.
        /// Penumbra writes Name as the second key, so this normally reads a few dozen bytes.
        /// </summary>
        private static string TryReadContentName(string path)
        {
            try
            {
                using var reader = new StreamReader(path);
                using var jsonReader = new JsonTextReader(reader);

                int depth = 0;
                while (jsonReader.Read())
                {
                    switch (jsonReader.TokenType)
                    {
                        case JsonToken.StartObject:
                        case JsonToken.StartArray:
                            depth++;
                            break;
                        case JsonToken.EndObject:
                        case JsonToken.EndArray:
                            depth--;
                            break;
                        case JsonToken.PropertyName when depth == 1
                            && string.Equals((string)jsonReader.Value, "Name", StringComparison.Ordinal):
                            // Depth 1 is the group object itself, so this can't match an option's Name.
                            return jsonReader.ReadAsString();
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ---- resolution ------------------------------------------------------------------------

        /// <summary>
        /// Every group file whose content Name matches this playlist, ordered by group number so
        /// callers taking the first get a deterministic file. Resolves against the name currently ON
        /// DISK, so a rename that has changed Name in memory but not yet saved still finds its file.
        /// </summary>
        internal static string[] ResolveFiles(Playlist playlist)
        {
            string name = playlist.PersistedName ?? playlist.Name;
            if (string.IsNullOrEmpty(name))
                return Array.Empty<string>();

            return GroupFilesOrdered()
                .Where(f => string.Equals(TryReadContentName(f), name, StringComparison.Ordinal))
                .ToArray();
        }

        internal static string ResolveFile(Playlist playlist)
        {
            var files = ResolveFiles(playlist);
            if (files.Length > 1)
                Logger.LogWarn("'{Name}': {Count} group files share this name ({Files}); using the first.",
                    playlist.Name, files.Length, string.Join(", ", files.Select(Path.GetFileName)));
            return files.FirstOrDefault();
        }

        /// <summary>
        /// The file a write would target, or null when there is nothing this app can safely write.
        ///
        /// Resolution only reads the <c>Name</c> field, so a file can resolve and still be unwritable —
        /// <see cref="LoadGroupForWrite"/> parses the whole document and rejects what
        /// <see cref="TryReadContentName"/> tolerates. That gap matters because callers pre-flight with
        /// <see cref="Exists"/> and then do irreversible work on disk (Rename moves the audio folder,
        /// Cleanup renames every .scd) before saving. A pre-flight weaker than the write's own
        /// precondition lets that work happen and then aborts, leaving the songs pointing at paths that
        /// no longer exist — so this checks exactly what the write checks.
        /// </summary>
        internal static string ResolveWritableFile(Playlist playlist)
        {
            string target = ResolveFile(playlist);
            return target != null && TryLoadGroup(target) != null ? target : null;
        }

        public bool Exists(Playlist playlist) => ResolveWritableFile(playlist) != null;

        public bool NameInUse(string name) =>
            GroupFilesOrdered().Any(f => string.Equals(TryReadContentName(f), name, StringComparison.Ordinal));

        // ---- writing ---------------------------------------------------------------------------

        public void Save(Playlist playlist)
        {
            lock (PlaylistStore.ModFolderGate)
            {
                AssertStillV3();

                string target = ResolveFile(playlist);
                if (target == null)
                {
                    // Same contract as the v4 store: a missing group means the edit has nowhere to go,
                    // so throw rather than let a caller commit the destructive half of a two-part edit.
                    Logger.LogError("Save('{Name}'): no matching group file found — aborting, nothing written.",
                        playlist.Name);
                    throw new PlaylistSaveException(playlist.Name);
                }

                TrySnapshotSet();
                WriteGroupFile(target, playlist.ToJson(LoadGroupForWrite(target), ModFormat.V3));
                playlist.PersistedName = playlist.Name;

                Logger.LogInfo("Saved playlist '{Name}' ({Count} options) -> {File}",
                    playlist.Name, playlist.Options?.Count ?? 0, Path.GetFileName(target));
            }
        }

        public void Upsert(Playlist playlist)
        {
            lock (PlaylistStore.ModFolderGate)
            {
                AssertStillV3();
                TrySnapshotSet();

                string target = ResolveFile(playlist);

                // A file that exists but can't be re-read is a hard stop, exactly as in Save: only a
                // genuinely absent group may be built from scratch, or an unreadable one gets
                // overwritten with a stripped copy of itself.
                JObject existing = target != null ? LoadGroupForWrite(target) : null;

                if (target == null)
                {
                    string cleanName = SanitizeGroupFileName(playlist.Name);
                    target = Path.Combine(ModRoot, $"group_{NextFreeGroupNumber():D3}_{cleanName}.json");
                }

                WriteGroupFile(target, playlist.ToJson(existing, ModFormat.V3));
                playlist.PersistedName = playlist.Name;

                Logger.LogInfo("Upserted playlist '{Name}' ({Count} options) -> {File}",
                    playlist.Name, playlist.Options?.Count ?? 0, Path.GetFileName(target));
            }
        }

        public void Delete(Playlist playlist)
        {
            lock (PlaylistStore.ModFolderGate)
            {
                AssertStillV3();

                string target = ResolveFile(playlist);
                if (target == null)
                    return;

                TrySnapshotSet();
                try
                {
                    File.Delete(target);
                    Logger.LogInfo("Deleted group file {File} for playlist '{Name}'.",
                        Path.GetFileName(target), playlist.Name);
                }
                catch (Exception ex)
                {
                    throw new PenumbraMetaException(
                        $"Could not delete '{Path.GetFileName(target)}': {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Renumbers the group files to match <paramref name="orderedNames"/>, because under v3 the
        /// NNN in the filename IS the display order.
        ///
        /// Renaming in two phases so an intermediate collision can't clobber a file: first move every
        /// participant to a <c>.reorder_tmp</c> sidecar, then write each back at its new number. This
        /// is the one operation with real intermediate on-disk state — a crash between the phases
        /// leaves every playlist as a sidecar and Penumbra briefly sees an empty mod. Two things
        /// mitigate that: <see cref="HealOnLoad"/> reclaims the sidecars on the next load, and the set
        /// snapshot taken here is the fallback if reclaiming can't.
        /// </summary>
        public void ReorderAll(IReadOnlyList<string> orderedNames)
        {
            lock (PlaylistStore.ModFolderGate)
            {
                AssertStillV3();
                string modDir = ModRoot;
                if (!Directory.Exists(modDir)) return;

                TrySnapshotSet();

                var byName = GroupFilesOrdered()
                    .Select(f => (path: f, name: TryReadContentName(f)))
                    .Where(e => e.name != null)
                    .ToList();

                // The full target order: the names the caller gave, then anything it didn't mention,
                // which keeps its relative place at the end. Every existing group file has to be in
                // here — one left unstaged would squat on a number phase 2 is about to hand out.
                var plan = new List<(string path, string name)>();
                foreach (string name in orderedNames)
                {
                    var entry = byName.FirstOrDefault(e => string.Equals(e.name, name, StringComparison.Ordinal));
                    if (entry.path == null)
                    {
                        Logger.LogWarn("ReorderAll: no group file found for '{Name}' — skipping it.", name);
                        continue;
                    }
                    byName.Remove(entry);
                    plan.Add(entry);
                }
                plan.AddRange(byName);

                // Phase 1: stage every participant out of the way.
                var staged = new List<(string name, string tempPath)>();
                foreach (var entry in plan)
                {
                    try
                    {
                        string tempPath = entry.path + ReorderSuffix;
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                        File.Move(entry.path, tempPath);
                        staged.Add((entry.name, tempPath));
                    }
                    catch (Exception ex)
                    {
                        // Renumbering only part of the set would leave two files claiming the same
                        // number, i.e. an ambiguous playlist order — which is the very thing this
                        // operation exists to control. Put back what we staged and change nothing.
                        Logger.LogError("ReorderAll: failed staging '{Name}' ({File}): {Error} — " +
                            "rolling back, the order was not changed.",
                            entry.name, Path.GetFileName(entry.path), ex);
                        foreach (var (_, tempPath) in staged)
                        {
                            try { File.Move(tempPath, tempPath.Substring(0, tempPath.Length - ReorderSuffix.Length)); }
                            catch { /* HealOnLoad reclaims anything left behind on the next load */ }
                        }
                        throw new PenumbraMetaException(
                            $"Could not reorder the playlists: '{entry.name}' is locked. Nothing was changed.", ex);
                    }
                }

                Logger.LogInfo("ReorderAll: renumbering {Count} group file(s): {Order}",
                    staged.Count, string.Join(" > ", staged.Select(s => s.name)));

                // Phase 2: write each back at its new number.
                for (int i = 0; i < staged.Count; i++)
                {
                    string newPath = Path.Combine(modDir,
                        $"group_{i + 1:D3}_{SanitizeGroupFileName(staged[i].name)}.json");
                    try
                    {
                        var group = JObject.Parse(File.ReadAllText(staged[i].tempPath, Encoding.UTF8));

                        // Keep Priority in step with the number so anything that does read Priority
                        // agrees with the filename order Penumbra actually displays.
                        group["Priority"] = i + 1;

                        File.WriteAllText(newPath, PenumbraMeta.Serialize(group, ModFormat.V3), new UTF8Encoding(false));
                        File.Delete(staged[i].tempPath);
                    }
                    catch (Exception ex)
                    {
                        // Leave the sidecar in place; HealOnLoad recovers it on the next load.
                        Logger.LogError("ReorderAll: failed writing '{Name}' -> {File}: {Error}",
                            staged[i].name, Path.GetFileName(newPath), ex);
                    }
                }
            }
        }

        // Refuses to touch group files when the folder has become v4 since the caller resolved its
        // store — the mirror image of the v3 refusal inside PenumbraMeta.Mutate. Writing group files
        // into a v4 folder wouldn't destroy anything, but it would silently drop the user's edit:
        // Penumbra reads meta.json and would never look at what we wrote.
        private static void AssertStillV3()
        {
            if (PenumbraMeta.DetectFormat() == ModFormat.V4)
                throw new PenumbraMetaException(
                    "The mod folder is now in Penumbra's v4 layout; refusing to write v3 group files. " +
                    "Nothing was written. Reload the playlist list and try again.");
        }

        /// <summary>
        /// Crash-safe write that Penumbra sees as a MODIFY, never a delete+create — see the class
        /// remarks for why that distinction costs the user their playlist order if we get it wrong.
        /// </summary>
        private static void WriteGroupFile(string target, JObject group)
        {
            // Back up the previous contents outside the mod folder before overwriting.
            if (File.Exists(target))
            {
                try
                {
                    File.Copy(target, Path.Combine(Playlist.BackupDir, Path.GetFileName(target) + ".bak"), true);
                }
                catch (Exception ex)
                {
                    Logger.LogWarn("Backup of {File} failed (continuing): {Error}", Path.GetFileName(target), ex.Message);
                }
            }

            // Stage in the temp dir (outside the folder Penumbra scans) so a half-written file is
            // never visible to it, then overwrite in place: File.Copy(overwrite: true) truncates and
            // rewrites the existing file rather than replacing the directory entry.
            string tmp = Path.Combine(Path.GetTempPath(),
                Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(tmp, PenumbraMeta.Serialize(group, ModFormat.V3), new UTF8Encoding(false));
                File.Copy(tmp, target, true);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        // One past the highest number currently in use, so a new group lands at the end of the display
        // order rather than colliding with an existing one.
        private static int NextFreeGroupNumber()
        {
            var used = GroupFilesOrdered().Select(GroupNumberOf).Where(n => n != int.MaxValue).ToList();
            return used.Count == 0 ? 1 : used.Max() + 1;
        }

        // Penumbra derives the filename from the group name; "/" is the one separator this app's
        // playlist names are allowed to contain, and it becomes "_" exactly as Penumbra writes it.
        private static string SanitizeGroupFileName(string name)
        {
            string clean = (name ?? string.Empty).Replace("/", "_");
            foreach (char c in Path.GetInvalidFileNameChars())
                clean = clean.Replace(c, '_');
            clean = clean.Trim(' ', '.');
            return string.IsNullOrEmpty(clean) ? "group" : clean;
        }

        // ---- crash recovery --------------------------------------------------------------------

        /// <summary>
        /// Restores the sidecars an interrupted <see cref="ReorderAll"/> left behind. Under v3 a
        /// <c>.reorder_tmp</c> is a LIVE playlist, not litter — it is the only copy of that group —
        /// so it is restored in place rather than swept out to the backup dir.
        /// </summary>
        public void HealOnLoad()
        {
            foreach (var line in ReclaimReorderTempFiles())
                Logger.LogInfo("{Message}", line);
        }

        internal static List<string> ReclaimReorderTempFiles()
        {
            var log = new List<string>();
            try
            {
                string modDir = ModRoot;
                if (!Directory.Exists(modDir)) return log;

                var sidecars = Directory.GetFiles(modDir, GroupGlob + ReorderSuffix);
                if (sidecars.Length == 0) return log;

                // Decide against the CONTENT name, never the sidecar's filename. Phase 2 rewrites each
                // group at a NEW number, so stripping ".reorder_tmp" yields the group's old path — a
                // path phase 2 deliberately vacated. Testing that path instead would conclude "nothing
                // there, restore it", putting the playlist back under its old number alongside the copy
                // phase 2 already wrote, i.e. one playlist showing up twice.
                var liveNames = new HashSet<string>(
                    GroupFilesOrdered().Select(TryReadContentName).Where(n => n != null), StringComparer.Ordinal);

                foreach (var path in sidecars)
                {
                    string name = TryReadContentName(path);

                    if (name != null && liveNames.Contains(name))
                    {
                        // Phase 2 already wrote this group; the sidecar is superseded. Move it out
                        // rather than leave it to be reconsidered (and re-logged) on every load.
                        try
                        {
                            File.Move(path, Path.Combine(Playlist.BackupDir, Path.GetFileName(path)), overwrite: true);
                            log.Add($"DISCARDED (superseded, kept in backups): {Path.GetFileName(path)}");
                        }
                        catch (Exception ex)
                        {
                            log.Add($"SKIP (superseded but could not move {Path.GetFileName(path)}): {ex.Message}");
                        }
                        continue;
                    }

                    // This group exists nowhere else, so the sidecar is its only copy. Any free
                    // filename will do — Penumbra renumbers and re-sanitizes them on its next reload
                    // anyway — so prefer the original name and fall back to the next free number.
                    string restored = path.Substring(0, path.Length - ReorderSuffix.Length);
                    if (File.Exists(restored))
                    {
                        if (name == null)
                        {
                            log.Add($"ORPHAN (unreadable, left in place): {Path.GetFileName(path)}");
                            continue;
                        }
                        restored = Path.Combine(modDir,
                            $"group_{NextFreeGroupNumber():D3}_{SanitizeGroupFileName(name)}.json");
                    }

                    try
                    {
                        File.Move(path, restored);
                        if (name != null) liveNames.Add(name);
                        log.Add($"RECLAIMED: {Path.GetFileName(path)} -> {Path.GetFileName(restored)}");
                    }
                    catch (Exception ex)
                    {
                        log.Add($"SKIP (could not reclaim {Path.GetFileName(path)}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Reorder-file reclaim failed (harmless): {Error}", ex.Message);
            }
            return log;
        }

        // ---- snapshots -------------------------------------------------------------------------

        /// <summary>
        /// Where this mod's v3 snapshot sets live — one directory per set, outside the mod folder so
        /// Penumbra never scans them. Namespaced per mod for the same reason the v4 snapshots are:
        /// a restore must never pull a different mod's files into this folder.
        /// </summary>
        internal static string SnapshotRoot
        {
            get
            {
                string dir = Path.Combine(Playlist.BackupDir, "v3", PenumbraMeta.SnapshotFolderNameForMod());
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// Copies meta.json, default_mod.json and every group file into a timestamped set, skipping
        /// the write when the folder is identical to the newest set already recorded.
        ///
        /// The per-file .bak that <see cref="WriteGroupFile"/> takes covers the common case — one bad
        /// write costs one playlist — but it is keyed by FILENAME, and Penumbra renumbers those on
        /// every reload, so a given playlist's .bak drifts under a stale name. Sets are what survive
        /// the multi-file failure: a ReorderAll that dies mid-flight touches every group at once.
        ///
        /// Best-effort: a snapshot failure must never block the edit the user asked for.
        /// </summary>
        internal static string TrySnapshotSet()
        {
            try
            {
                string modDir = ModRoot;
                if (!Directory.Exists(modDir)) return null;

                var sources = SnapshotSources(modDir);
                if (sources.Count == 0) return null;

                string hash = HashOf(sources);
                var existing = SnapshotSets();
                if (existing.Count > 0 && HashOfSet(existing[0]) == hash)
                    return existing[0].FullName;   // unchanged since the last set — nothing to record

                string dest = Path.Combine(SnapshotRoot, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}");
                Directory.CreateDirectory(dest);
                foreach (var src in sources)
                    File.Copy(src, Path.Combine(dest, Path.GetFileName(src)), true);

                PenumbraMeta.PruneByPolicy(SnapshotSets(), d => { try { d.Delete(true); } catch { } });
                return dest;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("v3 snapshot failed (continuing): {Error}", ex.Message);
                return null;
            }
        }

        // Penumbra's own files only, in a stable order so the hash is reproducible.
        private static List<string> SnapshotSources(string modDir) =>
            Directory.EnumerateFiles(modDir, GroupGlob)
                .Concat(Directory.EnumerateFiles(modDir, PenumbraMeta.LegacyDefaultMod))
                .Concat(Directory.EnumerateFiles(modDir, PenumbraMeta.MetaFile))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Newest first. Directory names are timestamps, so an ordinal sort is chronological.
        private static List<DirectoryInfo> SnapshotSets()
        {
            try
            {
                return new DirectoryInfo(SnapshotRoot)
                    .GetDirectories()
                    .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                    .ToList();
            }
            catch
            {
                return new List<DirectoryInfo>();
            }
        }

        // Hashes filenames as well as contents, so a set that gained or lost a group file — or had one
        // renumbered — never compares equal to one that didn't.
        private static string HashOf(IEnumerable<string> files)
        {
            using var sha = SHA256.Create();
            foreach (var f in files)
            {
                var nameBytes = Encoding.UTF8.GetBytes(Path.GetFileName(f) + "\n");
                sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
                var bytes = File.ReadAllBytes(f);
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash);
        }

        private static string HashOfSet(DirectoryInfo set)
        {
            try
            {
                return HashOf(set.GetFiles()
                    .Select(f => f.FullName)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The newest snapshot set holding at least one group file — the newest one worth restoring
        /// from. Null when there is nothing usable.
        /// </summary>
        internal static DirectoryInfo NewestUsableSnapshotSet() =>
            SnapshotSets().FirstOrDefault(d =>
            {
                try { return d.GetFiles(GroupGlob).Length > 0; }
                catch { return false; }
            });
    }
}
