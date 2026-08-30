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

            // Under the gate for the same reason as ResolveFiles: a two-phase rename leaves the files
            // it is moving as .reorder_tmp sidecars, and enumerating during that window would report a
            // library that is missing every playlist currently being renamed.
            lock (PlaylistStore.ModFolderGate)
            {
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
            }

            return result;
        }

        // Every group file, in Penumbra's display order: ascending group number, name as the
        // tie-break for the rare duplicate/unparseable number. Penumbra ignores the "Priority" field
        // for display — that only affects conflict resolution — so never sort by it.
        private static string[] GroupFilesOrdered() => GroupFilesOrdered(ModRoot);

        /// <summary>
        /// As above, for a mod named explicitly rather than the configured one.
        ///
        /// The dances feature edits a SECOND mod - the DJ pack - which may still be in this
        /// layout, so the group-file lookup has to be able to point somewhere else. Only reading
        /// is shared: creating, renumbering and reordering groups stay bound to the configured
        /// mod, because those are the operations whose failure modes cost a user their playlist
        /// order.
        /// </summary>
        internal static string[] GroupFilesOrdered(string modDirectory)
        {
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
            var group = TryLoadGroupQuiet(path, out string error);
            if (group == null)
                Logger.LogWarn("Could not parse group file '{File}': {Error}", Path.GetFileName(path), error);
            return group;
        }

        /// <summary>
        /// As <see cref="TryLoadGroup"/> but silent, for callers that retry.
        ///
        /// A failure here is usually transient — Penumbra holding the file open mid-rewrite — so a
        /// caller that is about to back off and try again would otherwise emit one warning per
        /// attempt, burying the single line that actually matters when it finally gives up.
        /// </summary>
        internal static JObject TryLoadGroupQuiet(string path, out string error)
        {
            error = null;
            try { return JObject.Parse(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception ex)
            {
                error = ex.Message;
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
        internal static string TryReadContentName(string path)
            => TryReadContentName(path, out string name, out _) ? name : null;

        /// <summary>
        /// As above, but keeps WHY a read failed instead of collapsing it into null.
        ///
        /// That distinction is the whole point. Penumbra holds these files open while it rewrites
        /// them, so a read can fail transiently — and treating that identically to "no group carries
        /// this name" is what turned a momentary lock into `no matching group file found` and a hard
        /// PlaylistSaveException, after the audio had already been imported. Returning true with a
        /// null <paramref name="name"/> is a real answer (the file parsed, it has no Name); returning
        /// false means we simply could not look.
        /// </summary>
        private static bool TryReadContentName(string path, out string name, out string error)
        {
            name = null;
            error = null;
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
                            name = jsonReader.ReadAsString();
                            return true;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
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

            // Each PASS holds the folder gate; the waits between them do not.
            //
            // The gate is needed because NormalizeGroupFileNames and ReorderAll rename in two phases,
            // and between them the files they are moving exist only as .reorder_tmp sidecars —
            // invisible to the group_*.json glob. A pass running unlocked in that window sees the
            // playlist as absent, and the retry cannot rescue it: nothing was UNREADABLE, the file is
            // simply not there, so it returns empty and callers like Add's pre-flight refuse a
            // perfectly healthy playlist. Since a rename holds the gate for its whole duration, a
            // gated pass sees either the before state or the after state, never the middle.
            //
            // But the backoff must NOT be inside that lock. It is waiting on Penumbra, a separate
            // process the gate has no authority over, so holding it there buys nothing and blocks
            // every other thread for up to 1.55s — long enough to freeze the window if the UI thread
            // wants to save. Re-acquiring per pass is exactly as correct, for the reason above.
            for (int attempt = 0; ; attempt++)
            {
                string[] files;
                bool anyUnreadable;
                lock (PlaylistStore.ModFolderGate)
                {
                    files = MatchByContentName(name, out anyUnreadable);
                }

                // Retry only while an unreadable file could still be hiding the match. A clean sweep
                // that simply found no match is a real answer and returns immediately; retrying it
                // would just add latency to the genuine-miss path.
                if (files.Length > 0 || !anyUnreadable)
                    return files;

                if (attempt >= ResolveRetries)
                {
                    Logger.LogWarn("'{Name}': no group file matched after {Count} retries, and at least " +
                        "one file could not be read — treating as missing.", name, ResolveRetries);
                    return files;
                }

                // Save, Upsert and Delete call in while already holding the gate, so for them this
                // sleep still happens under it — unavoidable, since they need the gate for the write
                // that follows. ResolveWritableFile (and therefore Exists, the pre-flight on every
                // import) deliberately does not hold it, which is the case that was freezing the UI.
                Thread.Sleep(50 << attempt); // 50 100 200 400 800ms, as PenumbraMeta.AtomicWrite
            }
        }

        private const int ResolveRetries = 5;

        /// <summary>
        /// One pass over the folder. <paramref name="anyUnreadable"/> reports whether any candidate
        /// had to be skipped, which is what tells the caller a retry could still change the answer.
        /// </summary>
        private static string[] MatchByContentName(string name, out bool anyUnreadable)
        {
            anyUnreadable = false;
            var matches = new List<string>();

            foreach (string file in GroupFilesOrdered())
            {
                if (!TryReadContentName(file, out string contentName, out _))
                {
                    anyUnreadable = true;
                    continue;
                }
                if (string.Equals(contentName, name, StringComparison.Ordinal))
                    matches.Add(file);
            }

            return matches.ToArray();
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
        /// Everything known about why a playlist could not be located, as one block.
        ///
        /// Written for the user reports this bug arrives as: the old error said only "no matching
        /// group file found", which cannot distinguish a locked file from a renamed group from the app
        /// pointing at the wrong mod entirely. Each of those needs a different fix, so the log has to
        /// say which one it was.
        /// </summary>
        private static void LogResolutionFailure(string name)
        {
            try
            {
                string modDir = ModRoot;
                if (!Directory.Exists(modDir))
                {
                    Logger.LogError("Resolve('{Name}'): mod folder does not exist: {Dir}", name, modDir);
                    return;
                }

                var files = GroupFilesOrdered();
                var sb = new StringBuilder();
                sb.Append($"Resolve('{name}') found no match in {modDir} ({files.Length} group file(s)):");
                foreach (string file in files)
                {
                    sb.Append(Environment.NewLine).Append("  ").Append(Path.GetFileName(file)).Append(" -> ");
                    sb.Append(TryReadContentName(file, out string contentName, out string error)
                        ? (contentName == null ? "(no Name property)" : $"'{contentName}'")
                        : $"UNREADABLE: {error}");
                }
                Logger.LogError("{Report}", sb.ToString());
            }
            catch (Exception ex)
            {
                // Diagnostics must never mask the error they are describing.
                Logger.LogWarn("Could not build the resolution report: {Error}", ex.Message);
            }
        }

        /// <summary>
        /// The file a write would target, or null when there is nothing this app can safely write.
        ///
        /// NOT a pure query: when nothing resolves, this runs the same non-destructive recovery the
        /// write path does, which reclaims sidecars and renames group files to their canonical names.
        /// That is deliberate — a pre-flight that answered "missing" for a folder Save could repair
        /// would refuse imports that would have worked — but it means callers must not treat this as
        /// a cheap predicate to poll. Never put it in a loop, and be aware that the cross-playlist
        /// move in MainWindow.DragDrop calls it twice, once per playlist.
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
            // Deliberately does NOT wrap ResolveFile in the gate.
            //
            // Resolving can back off for over a second waiting on Penumbra, and holding the folder
            // gate across that blocks every other thread — including the UI thread trying to save,
            // which is a visible freeze. ResolveFile already locks each of its own passes, so it is
            // safe to call unlocked; only the confirmation below needs the gate, and it is one file
            // read long.
            //
            // The confirmation still has to be atomic with respect to our renames, which is why it is
            // gated at all: a two-phase rename landing between resolving and re-reading would stage
            // the file out to a sidecar and make a perfectly writable group look unreadable. Catching
            // that case by its absence and resolving once more is enough — the retry sees the settled
            // folder, because the rename held the gate for its whole duration.
            // Report the name resolution actually used, not the in-memory one: mid-rename they differ,
            // and naming the new one would point at a playlist no group file carries yet.
            string reportedName = playlist.PersistedName ?? playlist.Name;
            bool recoveryTried = false;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                string target = ResolveFile(playlist);

                // Try the same non-destructive recovery Save does before concluding the playlist is
                // gone. Without this the pre-flight is STRICTER than the write it guards: Save would
                // reclaim an interrupted reorder's sidecar and succeed, while Add — which checks here
                // first — refuses outright and sends the user back to the Repair button, which is the
                // situation this recovery exists to avoid.
                if (target == null && !recoveryTried)
                {
                    recoveryTried = true;
                    target = RecoverAndReresolve(playlist, "Pre-flight");
                }

                if (target == null)
                    return null;

                // Confirm under the gate, retrying a transient lock exactly as name-matching does.
                // Penumbra holds these files open while it rewrites them, so a single failed parse
                // here says nothing about writability — and collapsing it to "unwritable" would refuse
                // a healthy playlist, which is the failure this whole change set is about.
                for (int confirm = 0; ; confirm++)
                {
                    bool present;
                    bool readable = false;
                    string error = null;

                    lock (PlaylistStore.ModFolderGate)
                    {
                        present = File.Exists(target);
                        if (present)
                            readable = TryLoadGroupQuiet(target, out error) != null;
                    }

                    if (!present)
                        break;              // moved — fall out to the outer loop and resolve again
                    if (readable)
                        return target;

                    if (confirm >= ResolveRetries)
                    {
                        Logger.LogWarn("'{Name}': {File} could not be read after {Count} retries " +
                            "({Error}) — treating as unwritable.",
                            reportedName, Path.GetFileName(target), ResolveRetries, error);
                        return null;
                    }

                    Thread.Sleep(50 << confirm); // 50 100 200 400 800ms
                }

                // Gone between resolve and confirm: a rename moved it. Resolve again against the
                // folder as it now stands.
                Logger.LogInfo("'{Name}': {File} moved while being checked — resolving again.",
                    reportedName, Path.GetFileName(target));
            }

            Logger.LogWarn("'{Name}': the group file moved twice while being checked — treating as " +
                "missing rather than guessing.", reportedName);
            return null;
        }

        public bool Exists(Playlist playlist) => ResolveWritableFile(playlist) != null;

        public bool NameInUse(string name)
        {
            // Also gated: a rename in flight would hide an existing name and let a duplicate through.
            lock (PlaylistStore.ModFolderGate)
                return GroupFilesOrdered().Any(f => string.Equals(TryReadContentName(f), name, StringComparison.Ordinal));
        }

        // ---- writing ---------------------------------------------------------------------------

        public void Save(Playlist playlist)
        {
            lock (PlaylistStore.ModFolderGate)
            {
                AssertStillV3();

                string target = ResolveFile(playlist) ?? RecoverAndReresolve(playlist, "Save");
                if (target == null)
                {
                    // Same contract as the v4 store: a missing group means the edit has nowhere to go,
                    // so throw rather than let a caller commit the destructive half of a two-part edit.
                    LogResolutionFailure(playlist.PersistedName ?? playlist.Name);
                    Logger.LogError("Save('{Name}') in mod '{Mod}': no matching group file found — " +
                        "aborting, nothing written.", playlist.Name, Settings.ModName);
                    throw new PlaylistSaveException(playlist.Name);
                }

                TrySnapshotSet();
                WriteGroupFile(target, playlist.ToJson(LoadGroupForWrite(target), ModFormat.V3));
                playlist.PersistedName = playlist.Name;

                Logger.LogInfo("Saved playlist '{Name}' ({Count} options) -> {File} in mod '{Mod}'",
                    playlist.Name, playlist.Options?.Count ?? 0, Path.GetFileName(target), Settings.ModName);
            }
        }

        /// <summary>
        /// The last non-destructive thing to try before concluding a playlist has no file, so a
        /// recoverable folder never reaches the user as an error they have to know about the Repair
        /// button to fix.
        ///
        /// Shared by the write path and the pre-flight that guards it — if only Save recovered, then
        /// Exists would answer "missing" for a folder Save could have fixed, and Add would refuse an
        /// import that would have worked.
        ///
        /// Both passes are file-level and idempotent: reclaim the sidecars an interrupted rename left
        /// behind, then bring the filenames back into agreement with Penumbra — which is what stops
        /// the next reload rewriting the folder again. Deliberately NOT the rest of Repair:
        /// StripRedundantScdSuffixes saves every playlist (this can be called from inside Save), and
        /// DropMissingSongs deletes songs whose audio merely looks absent, which is exactly the wrong
        /// move when the folder is mid-rewrite or the app is pointed somewhere unexpected.
        /// </summary>
        /// <param name="context">Who is asking, for the log — "Save" or "Pre-flight".</param>
        private static string RecoverAndReresolve(Playlist playlist, string context)
        {
            Logger.LogWarn("{Context}('{Name}'): no group file matched — attempting recovery before " +
                "giving up.", context, playlist.PersistedName ?? playlist.Name);

            try
            {
                foreach (var line in ReclaimReorderTempFiles())
                    Logger.LogInfo("{Message}", line);
                foreach (var line in NormalizeGroupFileNames())
                    Logger.LogInfo("{Message}", line);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Recovery pass failed: {Error}", ex.Message);
                return null;
            }

            string target = ResolveFile(playlist);
            if (target != null)
                Logger.LogInfo("{Context}('{Name}'): recovered — resolved to {File}.",
                    context, playlist.PersistedName ?? playlist.Name, Path.GetFileName(target));
            return target;
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

                // Close the hole immediately. Penumbra numbers each group by its file's position in
                // the folder, so a gap left here means the next reload rewrites and renumbers every
                // group file — and a save landing in that rewrite is the loss this whole change is
                // about. A no-op when the numbering already agrees, which is the usual case.
                //
                // Reclaim before normalizing, for the same reason HealOnLoad does: a .reorder_tmp is a
                // live playlist, and normalizing while one is outstanding would either renumber as
                // though that playlist did not exist or collide with its name.
                foreach (var line in ReclaimReorderTempFiles())
                    Logger.LogInfo("{Message}", line);
                foreach (var line in NormalizeGroupFileNames())
                    Logger.LogInfo("{Message}", line);
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
        internal static void WriteGroupFile(string target, JObject group)
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

        /// <summary>
        /// The lowest unused group number, NOT one past the highest.
        ///
        /// Penumbra derives a group's canonical number from its file's position in
        /// EnumerateFiles("group_*.json") — so the set has to read 001, 002, ... N with no holes, or
        /// the file sitting at position i never matches the group_{i+1:D3} it is compared against and
        /// Penumbra renumbers the whole folder on every reload. Handing out max+1 left a hole behind
        /// every deletion, which condemned the mod to that rewrite loop permanently.
        /// </summary>
        private static int NextFreeGroupNumber()
        {
            var used = new HashSet<int>(GroupFilesOrdered().Select(GroupNumberOf).Where(n => n != int.MaxValue));
            int n = 1;
            while (used.Contains(n))
                n++;
            return n;
        }

        /// <summary>
        /// Renames the group files to the exact names Penumbra would give them — contiguous numbering
        /// from 001 in the current display order, each with <see cref="SanitizeGroupFileName"/> applied
        /// to its content name.
        ///
        /// This is what stops the rewrite loop for folders that are ALREADY wrong: a mod carrying
        /// numbering holes, mixed-case names, or names from an older sanitizer makes Penumbra rewrite
        /// and renumber every group file on every single reload, and a save or resolve landing inside
        /// one of those rewrites is the reported data loss. Bringing the folder into agreement once
        /// makes every later reload a no-op.
        ///
        /// Renames only — the file CONTENTS are never touched, which is the difference between this and
        /// <see cref="ReorderAll"/>. Two-phase via the same sidecars so an intermediate collision (002
        /// wanting a name 003 still holds) cannot clobber a file, and <see cref="HealOnLoad"/> reclaims
        /// the sidecars if this is interrupted.
        /// </summary>
        internal static List<string> NormalizeGroupFileNames()
        {
            var log = new List<string>();
            lock (PlaylistStore.ModFolderGate)
            {
                string modDir = ModRoot;
                if (!Directory.Exists(modDir)) return log;

                var files = GroupFilesOrdered();
                if (files.Length == 0) return log;

                // Plan first. A file we cannot READ makes the whole plan unsafe: its canonical name is
                // unknowable, yet it still occupies a position every later file is numbered against,
                // so renaming around it would hand out a number it may itself want.
                //
                // A file that reads fine but carries no Name is a different case and must NOT abort
                // the pass — Penumbra would simply name it "group_NNN_.json", so that is computable
                // and we stay in agreement. Using the name-only overload here conflated the two and
                // let one malformed file silently disable normalization for the entire mod forever.
                var plan = new List<(string path, string target)>();
                for (int i = 0; i < files.Length; i++)
                {
                    if (!TryReadContentName(files[i], out string name, out string error))
                    {
                        Logger.LogWarn("Normalize: '{File}' could not be read ({Error}) — leaving all " +
                            "group filenames alone this pass.", Path.GetFileName(files[i]), error);
                        return log;
                    }
                    plan.Add((files[i],
                        Path.Combine(modDir, $"group_{i + 1:D3}_{SanitizeGroupFileName(name ?? string.Empty)}.json")));
                }

                var wrong = plan.Where(e => !string.Equals(e.path, e.target, StringComparison.Ordinal)).ToList();
                if (wrong.Count == 0)
                    return log;   // already agrees with Penumbra: the overwhelmingly common case

                // Phase 1: stage every participant out of the way.
                var staged = new List<(string target, string tempPath)>();
                foreach (var (path, target) in wrong)
                {
                    try
                    {
                        string tempPath = path + ReorderSuffix;

                        // Never clear the way by deleting a sidecar. Under v3 a .reorder_tmp is a LIVE
                        // playlist — the only copy of a group an interrupted reorder left behind — so
                        // one sitting on the name we want means recovery has not run yet, and deleting
                        // it would destroy that playlist outright. Abort and let the reclaim pass
                        // (HealOnLoad, or the one ahead of this in the recovery path) restore it first;
                        // normalization is safe to defer, losing a playlist is not.
                        if (File.Exists(tempPath))
                        {
                            Logger.LogWarn("Normalize: '{File}' already exists and may be an unreclaimed " +
                                "playlist — leaving all group filenames alone this pass.",
                                Path.GetFileName(tempPath));
                            RollBackStaging(staged);
                            return log;
                        }

                        File.Move(path, tempPath);
                        staged.Add((target, tempPath));
                    }
                    catch (Exception ex)
                    {
                        // Put back what we staged; a half-renamed set is worse than an unnormalized one.
                        Logger.LogWarn("Normalize: could not stage '{File}' ({Error}) — rolling back.",
                            Path.GetFileName(path), ex.Message);
                        RollBackStaging(staged);
                        return log;
                    }
                }

                // Phase 2: move each back under the name Penumbra expects.
                foreach (var (target, tempPath) in staged)
                {
                    try
                    {
                        File.Move(tempPath, target);
                        log.Add($"NORMALIZED: {Path.GetFileName(tempPath).Replace(ReorderSuffix, string.Empty)} " +
                                $"-> {Path.GetFileName(target)}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("Normalize: failed writing {File}: {Error}", Path.GetFileName(target), ex);
                    }
                }
            }
            return log;
        }

        // Undoes a partial phase 1. Anything that resists being put back is left as a sidecar, which
        // ReclaimReorderTempFiles restores on the next load — the same contract ReorderAll relies on.
        private static void RollBackStaging(List<(string target, string tempPath)> staged)
        {
            foreach (var (_, tempPath) in staged)
            {
                try { File.Move(tempPath, tempPath.Substring(0, tempPath.Length - ReorderSuffix.Length)); }
                catch { /* HealOnLoad reclaims anything left behind on the next load */ }
            }
        }

        /// <summary>
        /// The name part of the group filename, byte-for-byte as Penumbra would write it.
        ///
        /// This has to be EXACT, and the reason is not cosmetic. On every reload of a v3 mod,
        /// Penumbra's ModCreator.LoadAllGroups compares each file on disk against the name it would
        /// have chosen (FilenameService.OptionGroupFile):
        ///
        ///     $"group_{index + 1:D3}_{name.ToLowerInvariant().ReplaceBadXivSymbols(onlyAscii)}.json"
        ///
        /// and if ANY file deviates it calls SaveAllOptionGroups, which rewrites and RENUMBERS the
        /// whole set from the copy it read at the start of that reload. A save landing in that window
        /// is silently reverted, and a resolve landing in it finds no file carrying the playlist's
        /// name — which is exactly the "no matching group file found" abort users were hitting. Since
        /// this app asks for a reload after every save, one character of disagreement here means that
        /// rewrite fires forever.
        ///
        /// Ported from Luna's StringExtensions.ReplaceBadXivSymbols. Note the comparison in
        /// ModCreator always passes onlyAscii:true regardless of the user's ReplaceNonAsciiOnImport
        /// setting, so folding to ASCII unconditionally is what actually matches it.
        /// </summary>
        internal static string SanitizeGroupFileName(string name)
        {
            // Penumbra lowercases before sanitizing, so the order matters: a non-ASCII capital can
            // fold differently than its lowercase form.
            string s = (name ?? string.Empty).ToLowerInvariant();

            // Reserved relative-path names, special-cased by Penumbra before anything else.
            switch (s)
            {
                case ".": return "_";
                case "..": return "__";
            }

            string normalized = s.Normalize(NormalizationForm.FormKC);
            var sb = new StringBuilder(normalized.Length);
            bool encounteredNonWhiteSpace = false;

            foreach (char c in normalized)
            {
                // Leading whitespace is dropped entirely rather than replaced.
                if (!encounteredNonWhiteSpace)
                {
                    if (char.IsWhiteSpace(c))
                        continue;
                    encounteredNonWhiteSpace = true;
                }

                sb.Append(Array.IndexOf(s_invalidFileNameChars, c) >= 0 || c >= 128 ? '_' : c);
            }

            while (sb.Length != 0 && char.IsWhiteSpace(sb[^1]))
                sb.Length--;

            // Deliberately NOT substituted with a placeholder when empty: Penumbra would write
            // "group_001_.json" here, and inventing a nicer name would be a permanent mismatch.
            return sb.ToString();
        }

        private static readonly char[] s_invalidFileNameChars = Path.GetInvalidFileNameChars();

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

            // Reclaim first: a sidecar left by an interrupted rename is a live playlist, and
            // normalizing around it would number the set as if that playlist did not exist.
            try
            {
                foreach (var line in NormalizeGroupFileNames())
                    Logger.LogInfo("{Message}", line);
            }
            catch (Exception ex)
            {
                // HealOnLoad runs on the startup path and must never throw.
                Logger.LogWarn("Group filename normalization failed (harmless): {Error}", ex.Message);
            }
        }

        internal static List<string> ReclaimReorderTempFiles()
        {
            var log = new List<string>();

            // Gated like every other mutator in this class, and it must be: it moves files.
            //
            // Without the gate it can run while a two-phase rename is halfway through, and the two
            // disagree about what a sidecar means. Phase 1 stages a LIVE group out to .reorder_tmp;
            // this method, seeing a sidecar whose content name matches no visible file, concludes it
            // is the only copy and moves it back to its old path. Phase 2 then fails to find it, and
            // where phase 2 had already written its half, the group ends up on disk twice under one
            // name — which trips the duplicate warning in ResolveFile and makes Penumbra's filename
            // check disagree on every reload thereafter.
            //
            // This was reachable only from gated callers until the pre-flight started calling the
            // recovery, which deliberately does not hold the gate. The gate is re-entrant, so callers
            // that already hold it (Delete, Save's recovery) are unaffected.
            lock (PlaylistStore.ModFolderGate)
            {
                return ReclaimReorderTempFilesLocked(log);
            }
        }

        private static List<string> ReclaimReorderTempFilesLocked(List<string> log)
        {
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
        // Internal rather than private because VersionBackup captures the same v3 file set;
        // duplicating the three globs there would let the two drift apart silently.
        internal static List<string> SnapshotSources(string modDir) =>
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
