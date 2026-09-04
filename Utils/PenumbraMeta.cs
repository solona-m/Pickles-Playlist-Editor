using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Pickles_Playlist_Editor.Utils
{
    // Thrown when meta.json cannot be read or is not a usable Penumbra manifest, so there is nowhere
    // safe to write. Distinct from PlaylistSaveException, which means "the manifest is fine but this
    // playlist isn't in it".
    public class PenumbraMetaException : InvalidOperationException
    {
        public PenumbraMetaException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Reads and writes Penumbra's root <c>meta.json</c> mod manifest, and detects which layout the
    /// mod folder is in (see <see cref="DetectFormat()"/>).
    ///
    /// Penumbra's v4 folded the whole mod layout into this one file: option groups moved out of
    /// per-group <c>group_NNN_name.json</c> files into a <c>Groups</c> array, and
    /// <c>default_mod.json</c> became the <c>DefaultData</c> object. Group ORDER is now the array
    /// index — the old filename number is gone — but the meaning is unchanged: lower = higher
    /// priority. Penumbra then reversed course, so v3 folders are still out there and
    /// <see cref="V3GroupFileStore"/> handles those; everything here is the v4 half.
    ///
    /// Under v4 everything the app owns lives in ONE ~360KB file, so a careless write costs the
    /// entire library rather than one playlist. Two rules make that safe, and both are enforced here
    /// rather than left to callers:
    ///   1. Every write goes through <see cref="Mutate"/>, which re-reads the manifest under a lock
    ///      immediately before writing. Callers splice only the sub-object they own into that fresh
    ///      copy, so a stale in-memory model can never clobber a group it doesn't represent.
    ///   2. Nothing is ever rebuilt from scratch. Unknown keys (Identifier, LastWrite, ModTags,
    ///      Image, per-group Description/Page/DefaultSettings/Priority) survive by construction.
    /// </summary>
    internal static class PenumbraMeta
    {
        public const string MetaFile = "meta.json";
        public const string LegacyDefaultMod = "default_mod.json";
        public const int FileVersion = 4;

        public static string ModRoot =>
            Path.Combine(Settings.PenumbraLocation ?? string.Empty, Settings.ModName ?? string.Empty);

        public static string MetaPath => Path.Combine(ModRoot, MetaFile);

        /// <summary>
        /// The manifest of a mod named explicitly rather than of the configured one.
        ///
        /// Everything in this class used to derive its target from <see cref="Settings.ModName"/>,
        /// which is right for the playlist library but not for the DJ mod that holds the dances:
        /// that is a SECOND mod, often a different folder entirely, and repointing the setting at it
        /// mid-operation is exactly the mod swap <see cref="AssertModRootUnchanged"/> exists to
        /// catch. So the bodies take a root and the old members pass the configured one in.
        /// </summary>
        public static string MetaPathFor(string modRoot) => Path.Combine(modRoot, MetaFile);

        /// <summary>
        /// Throws if the configured mod has changed since <paramref name="captured"/> was taken.
        ///
        /// Nothing binds a <see cref="Playlist"/> to the mod it was loaded from: <see cref="ModRoot"/>
        /// is recomputed from mutable settings on every single access. A long operation — importing a
        /// track takes tens of seconds — can therefore start against one mod and finish against
        /// another if the folder is changed in Settings meanwhile, and if the new mod happens to have a
        /// group with the same name the save lands there, silently overwriting an unrelated playlist.
        /// Callers capture ModRoot before doing any work and call this immediately before writing.
        /// </summary>
        public static void AssertModRootUnchanged(string captured)
        {
            string current = ModRoot;
            if (string.Equals(captured, current, StringComparison.OrdinalIgnoreCase))
                return;

            throw new PenumbraMetaException(
                $"The selected mod changed from '{captured}' to '{current}' while this was running, " +
                "so the change was not applied — writing it now would have edited a different mod. " +
                "Nothing was written.");
        }

        /// <summary>
        /// The parsed manifest, or null when it is missing or unparseable.
        ///
        /// Retries before giving up, for the same reason the v3 store retries resolution: Penumbra is
        /// a separate process that rewrites this file, and under v4 this ONE file is the entire
        /// library. A read that loses a race with Penumbra's writer returns null here, which reads
        /// downstream as "this mod has no playlists" — indistinguishable from real data loss, and
        /// alarming to a user who just watched their library empty itself.
        ///
        /// Parse failures are retried too, not just IO ones: catching the file mid-write yields
        /// malformed JSON, which is transient in exactly the same way.
        /// </summary>
        public static JObject? Read() => Read(ModRoot);

        /// <inheritdoc cref="Read()"/>
        public static JObject? Read(string modRoot)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(MetaPathFor(modRoot));
                if (!info.Exists)
                    return null;
            }
            catch
            {
                return null;
            }

            string path = info.FullName;

            // A file already proven unreadable, and untouched since, is not going to become readable
            // by sleeping at it again. Without this the retry cost multiplies: PlaylistStore.Current
            // re-detects the format on EVERY access, so one genuinely corrupt manifest would add
            // ~1.5s to every save — and Repair, which saves once per playlist, would spend minutes
            // asleep on a UI-triggered button press.
            // Read the field once — it is volatile and another thread may replace it mid-check — and
            // compare against it field by field. Read() sits on the hottest path in the app
            // (PlaylistStore.Current re-detects the format on every access), and the cache is null in
            // the overwhelmingly common healthy case, so allocating a record just to test equality
            // would be per-access garbage for nothing.
            var known = s_knownBadMeta;
            if (known != null
                && known.Length == info.Length
                && known.Stamp == info.LastWriteTimeUtc
                && string.Equals(known.Path, path, StringComparison.OrdinalIgnoreCase))
                return null;

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var parsed = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    // Guarded to keep the healthy path a plain read: this is the hottest path in the
                    // app (PlaylistStore.Current re-detects the format on every access) and the field
                    // is null in every healthy session, so an unconditional store would be a memory
                    // barrier per access for no state change.
                    //
                    // Tests the field NOW rather than the `known` captured above: another thread may
                    // have recorded a failure in between, and we have just proved the file reads, so
                    // that record is stale whenever it was written. A concurrent failure landing
                    // immediately after this check still survives, but that window is the same one
                    // the original unconditional clear had — narrow, and self-clearing on the next
                    // rewrite, since any rewrite changes the stamp.
                    if (s_knownBadMeta != null)
                        s_knownBadMeta = null;
                    return parsed;
                }
                catch (Exception ex)
                {
                    if (attempt >= ReadRetries)
                    {
                        Logger.LogError("PenumbraMeta.Read failed for {Path} after {Count} retries: {Error}",
                            path, ReadRetries, ex);
                        // Allocated only here, on the rare failing path. Records the exact bytes that
                        // failed as well as the path: any rewrite by Penumbra changes the stamp or
                        // length and earns a fresh set of retries.
                        s_knownBadMeta = new BadMeta(path, info.LastWriteTimeUtc, info.Length);
                        return null;
                    }
                    Logger.LogWarn("PenumbraMeta.Read: {Path} unreadable ({Error}) — retrying.",
                        path, ex.Message);
                    Thread.Sleep(50 << attempt); // 50 100 200 400 800ms, as AtomicWrite
                }
            }
        }

        private const int ReadRetries = 5;

        /// <summary>
        /// Identity of a manifest that exhausted its retries. See <see cref="Read"/>.
        ///
        /// Includes the PATH, not just the file's stamp and length: ModRoot follows mutable settings,
        /// so this cache spans every mod visited in a session, and two mods installed together from
        /// one archive can easily share a timestamp and size. Keying on those alone would let mod A's
        /// corrupt manifest suppress the read of mod B's perfectly good one, emptying B's library.
        /// </summary>
        private sealed record BadMeta(string Path, DateTime Stamp, long Length);

        // A reference, and volatile, so cross-thread publication is atomic. Read() runs on the UI
        // thread, the reload timer and download tasks concurrently; a multi-word struct here could be
        // read half-updated, and a spurious match makes a healthy library read as empty.
        private static volatile BadMeta? s_knownBadMeta;

        /// <summary>
        /// The mod's option groups in <c>Groups</c> array order, or null when the manifest has no
        /// <c>Groups</c> key.
        ///
        /// Null does NOT mean "not v4". Penumbra OMITS the key entirely for a mod with no option
        /// groups and never writes <c>"Groups": []</c> — verified against a real Penumbra root, where
        /// 209 of 847 v4 mods have no Groups key and not one has an empty array. So a null here is
        /// equally consistent with a valid v3 mod, a valid v4 mod with zero groups, and a v4 manifest
        /// that lost its groups. Use <see cref="DetectFormat()"/> to tell those apart; shape can't.
        /// </summary>
        public static JArray? TryReadGroups() => TryReadGroups(Read());

        public static JArray? TryReadGroups(JObject? root) => root?["Groups"] as JArray;

        /// <summary>
        /// Which layout the mod folder is in right now.
        ///
        /// <c>FileVersion</c> is the discriminator, not shape. See <see cref="TryReadGroups()"/> for
        /// why the presence of a <c>Groups</c> array cannot be used: Penumbra omits it for a groupless
        /// v4 mod, which makes a healthy v4 mod indistinguishable from a v3 one by shape alone.
        /// </summary>
        public static ModFormat DetectFormat() => DetectFormat(Read(), ModRoot);

        /// <param name="modDirectory">
        /// The folder <paramref name="root"/> was read from. Only consulted when the manifest carries
        /// no usable FileVersion, and it must match — passing the configured mod folder while
        /// inspecting a different one would classify by the wrong folder's contents.
        /// </param>
        public static ModFormat DetectFormat(JObject? root, string? modDirectory)
        {
            // Missing folder, missing meta.json, or unparseable. Read() has already logged why.
            if (root == null)
                return ModFormat.Unknown;

            if (root["FileVersion"] is JValue { Type: JTokenType.Integer } version)
            {
                int value = (int)version;
                if (value >= FileVersion) return ModFormat.V4;
                if (value >= 1) return ModFormat.V3;  // 1..3 all use the meta + group_*.json family
            }

            // No usable FileVersion. Positive v4 evidence first: DefaultData is a v4-only key and is
            // present even on a groupless mod, so it identifies the case Groups cannot.
            if (root["Groups"] is JArray || root["DefaultData"] is JObject)
                return ModFormat.V4;

            try
            {
                if (!string.IsNullOrWhiteSpace(modDirectory) && Directory.Exists(modDirectory)
                    && (Directory.EnumerateFiles(modDirectory, "group_*.json").Any()
                        || File.Exists(Path.Combine(modDirectory, LegacyDefaultMod))))
                    return ModFormat.V3;
            }
            catch
            {
                // Enumeration failure tells us nothing; fall through to Unknown.
            }

            return ModFormat.Unknown;
        }

        /// <summary>
        /// The one and only v4 write path. Re-reads the manifest under the lock, snapshots it, hands
        /// the fresh copy to <paramref name="edit"/> to splice, then writes it back atomically.
        /// </summary>
        public static void Mutate(Action<JObject> edit) =>
            Mutate(ModRoot, Settings.ModName ?? string.Empty, edit);

        /// <inheritdoc cref="Mutate(Action{JObject})"/>
        public static void Mutate(string modRoot, string modName, Action<JObject> edit)
        {
            lock (PlaylistStore.ModFolderGate)
            {
                var root = Read(modRoot)
                    ?? throw new PenumbraMetaException(
                        $"Penumbra's mod manifest could not be read: {MetaPathFor(modRoot)}. Nothing was written. " +
                        "(If Penumbra is running it may be mid-write — try again in a moment.)");

                // Penumbra may have converted the folder to v3 since the caller resolved its store.
                // Refuse rather than write v4 structure into a v3 manifest: that would tell Penumbra
                // the mod has zero option groups, so it would ignore every group_NNN_*.json on disk
                // and orphan every playlist along with the user's current selection for each.
                if (DetectFormat(root, modRoot) == ModFormat.V3)
                    throw new PenumbraMetaException(
                        "The mod folder is in Penumbra's v3 layout; refusing to write it as v4. " +
                        "Nothing was written.");

                // Snapshot the pre-edit state. Cheap when nothing changed since the last one.
                TrySnapshot(modRoot, modName);

                edit(root);

                // Only ever ADD a missing version to a manifest that is unambiguously v4. Stamping it
                // unconditionally is what made Delete() on a v3 folder rewrite meta.json as v4 with no
                // Groups key at all, which reads to Penumbra as "this mod has no option groups".
                if (root["FileVersion"] == null && (root["Groups"] is JArray || root["DefaultData"] is JObject))
                    root["FileVersion"] = FileVersion;

                // Penumbra keys the mod by Identifier; a manifest that lost it is a new mod as far as
                // Penumbra is concerned, which silently orphans every user setting. Refuse rather
                // than write one.
                if (string.IsNullOrWhiteSpace(root["Identifier"]?.ToString()))
                    throw new PenumbraMetaException(
                        "Refusing to write meta.json: the edit left it without an Identifier.");

                AtomicWrite(MetaPathFor(modRoot), Serialize(root));
            }
        }

        /// <summary>
        /// Serializes exactly the way Penumbra does for the given layout, because matching its
        /// whitespace is what keeps our writes from reading as a whole-file reformat. Get it wrong and
        /// a one-song edit rewrites every line of the file as far as any diff — or Penumbra's own
        /// file watcher — can tell.
        ///
        /// The two layouts are formatted differently, verified against files Penumbra wrote:
        ///   v4  tab-indented, LF line endings   (e.g. a 360KB manifest of ~11,000 lines)
        ///   v3  two-space indented, CRLF line endings
        /// Neither carries a BOM. Note the default JsonTextWriter newline is Environment.NewLine,
        /// which is CRLF here — so the v4 case is the one that must be set explicitly.
        /// </summary>
        internal static string Serialize(JObject root, ModFormat format = ModFormat.V4)
        {
            bool v3 = format == ModFormat.V3;
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb) { NewLine = v3 ? "\r\n" : "\n" })
            using (var jw = new JsonTextWriter(sw)
            {
                Formatting = Formatting.Indented,
                Indentation = v3 ? 2 : 1,
                IndentChar = v3 ? ' ' : '\t',
            })
            {
                root.WriteTo(jw);
            }
            return sb.ToString();
        }

        public static bool TryGetGroupById(JObject root, Guid id, out JObject? group, out int index)
        {
            group = null;
            index = -1;
            if (id == Guid.Empty || root["Groups"] is not JArray groups)
                return false;

            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i] is not JObject g) continue;
                if (Guid.TryParse(g["Id"]?.ToString(), out var gid) && gid == id)
                {
                    group = g;
                    index = i;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// First group with this exact name. v4 permits duplicate group names, so this is only a
        /// fallback for groups that have no Id yet (just created, or imported from v3).
        /// </summary>
        public static JObject? FindGroupByName(JObject root, string name, out int index)
        {
            index = -1;
            if (root["Groups"] is not JArray groups)
                return null;

            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i] is not JObject g) continue;
                if (string.Equals(g["Name"]?.ToString(), name, StringComparison.Ordinal))
                {
                    index = i;
                    return g;
                }
            }
            return null;
        }

        public static JObject? FindGroupByName(JObject root, string name) => FindGroupByName(root, name, out _);

        /// <summary>
        /// Every <c>.scd</c> game-path key referenced anywhere in the mod: <c>DefaultData.Files</c>
        /// (often absent) plus every option's <c>Files</c>. Reads the v3 layout instead for a v3
        /// folder. Used to populate the baseline-SCD picker.
        /// </summary>
        public static List<string> CollectScdKeys(string? modDirectory)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory))
                return new List<string>();

            JObject? root = null;
            try
            {
                string metaPath = Path.Combine(modDirectory, MetaFile);
                if (File.Exists(metaPath))
                    root = JObject.Parse(File.ReadAllText(metaPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Logger.LogWarn("CollectScdKeys: could not parse meta.json: {Error}", ex.Message);
            }

            if (root != null)
            {
                AddScdKeys(root["DefaultData"]?["Files"] as JObject, keys);
                if (root["Groups"] is JArray groups)
                {
                    foreach (var g in groups.OfType<JObject>())
                        AddOptionScdKeys(g["Options"] as JArray, keys);
                }
            }

            // v3 reads the per-group files instead. Branch on the detected FORMAT, not on whether a
            // Groups array is present: Penumbra omits that key for any v4 mod with no option groups,
            // so keying off it would send hundreds of healthy v4 mods down this path for nothing.
            //
            // Only Penumbra's own files are read. Widening this to every top-level *.json (as it once
            // did) lets an unrelated file the user happened to drop in the mod folder inject bogus
            // entries into the baseline-SCD picker.
            if (DetectFormat(root, modDirectory) == ModFormat.V3)
            {
                var legacyPaths = Directory.EnumerateFiles(modDirectory, "group_*.json", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(modDirectory, LegacyDefaultMod, SearchOption.TopDirectoryOnly));

                foreach (var jsonPath in legacyPaths)
                {
                    try
                    {
                        var legacy = JObject.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
                        AddScdKeys(legacy["Files"] as JObject, keys);
                        AddOptionScdKeys(legacy["Options"] as JArray, keys);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarn("CollectScdKeys: skipping '{File}': {Error}", Path.GetFileName(jsonPath), ex.Message);
                    }
                }
            }

            return keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddOptionScdKeys(JArray? options, HashSet<string> keys)
        {
            if (options == null) return;
            foreach (var o in options.OfType<JObject>())
                AddScdKeys(o["Files"] as JObject, keys);
        }

        private static void AddScdKeys(JObject? files, HashSet<string> keys)
        {
            if (files == null) return;
            foreach (var p in files.Properties())
            {
                string key = NormalizeScdKey(p.Name);
                if (key.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                    keys.Add(key);
            }
        }

        public static string NormalizeScdKey(string? key) =>
            (key ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');

        /// <summary>
        /// Writes via a sibling temp file + atomic move, retrying with backoff: Penumbra's own file
        /// watcher can hold the target open for a moment right after a reload. The temp must be a
        /// sibling (same volume) so the move is a rename rather than a copy — a copy would leave a
        /// window in which a crash yields a truncated manifest, which is now the whole library.
        /// </summary>
        public static void AtomicWrite(string target, string contents)
        {
            string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, contents, new UTF8Encoding(false));
            MoveIntoPlace(tmp, target);
        }

        /// <summary>
        /// The same, for a file whose contents are bytes rather than text — an animation, say.
        ///
        /// Shares the retry loop rather than reimplementing the easy half: an animation lands in the
        /// same watched folder as the manifest and hits the same held handle, and the copies of this
        /// that did not retry were the ones that occasionally failed for no reason a user could see.
        /// </summary>
        public static void AtomicWrite(string target, byte[] contents)
        {
            string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, contents);
            MoveIntoPlace(tmp, target);
        }

        private static void MoveIntoPlace(string tmp, string target)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    File.Move(tmp, target, overwrite: true);
                    return;
                }
                catch (Exception) when (i < 5)
                {
                    Thread.Sleep(50 << i); // 50 100 200 400 800ms
                }
                catch
                {
                    try { File.Delete(tmp); } catch { } // never litter the mod folder
                    throw;
                }
            }
        }

        // ---- snapshots -------------------------------------------------------------------------

        /// <summary>
        /// Where this mod's manifest snapshots live. Deliberately outside the mod folder — Penumbra
        /// scans that directory, and in v4 a stray JSON there is noise around the one file that
        /// matters.
        ///
        /// Namespaced per mod. A flat shared folder would let a restore pull the newest snapshot of a
        /// DIFFERENT mod into this one — overwriting its Identifier, which is how Penumbra keys the
        /// mod — and it would do so precisely when the user is already recovering from damage. The
        /// mod folder name is the key rather than the Identifier because the Identifier lives in the
        /// manifest we may be unable to read, which is the whole reason we're restoring.
        /// </summary>
        public static string SnapshotDir => SnapshotDirFor(Settings.ModName ?? string.Empty);

        /// <inheritdoc cref="SnapshotDir"/>
        public static string SnapshotDirFor(string modName)
        {
            string dir = Path.Combine(Playlist.BackupDir, "meta", SnapshotFolderNameForMod(modName));
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal static string SnapshotFolderNameForMod() =>
            SnapshotFolderNameForMod(Settings.ModName ?? "unknown");

        internal static string SnapshotFolderNameForMod(string modName)
        {
            string name = string.IsNullOrWhiteSpace(modName) ? "unknown" : modName;
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim(' ', '.');
            return string.IsNullOrEmpty(name) ? "unknown" : name;
        }

        internal const int KeepRecentSnapshots = 20;
        internal const int KeepDailyDays = 7;

        /// <summary>
        /// Copies the current manifest into the snapshot dir, skipping the write when it is identical
        /// to the newest snapshot already there. Best-effort: a snapshot failure must never block the
        /// edit the user asked for.
        /// </summary>
        public static string? TrySnapshot() =>
            TrySnapshot(ModRoot, Settings.ModName ?? string.Empty);

        /// <inheritdoc cref="TrySnapshot()"/>
        /// <param name="ourWrite">
        /// True for the ordinary case — a mutator snapshotting immediately before it writes — which
        /// is also how <see cref="ModFolderGuard"/> learns that the next change to the folder is
        /// ours. False for the one caller that snapshots before somebody ELSE writes: the failed
        /// reload, which is protecting the folder from Penumbra rather than from itself.
        /// </param>
        public static string? TrySnapshot(string modRoot, string modName, bool ourWrite = true)
        {
            try
            {
                if (ourWrite) ModFolderGuard.NoteWrite();

                string path = MetaPathFor(modRoot);
                if (!File.Exists(path)) return null;

                var bytes = File.ReadAllBytes(path);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));

                var existing = SnapshotFiles(modName);
                if (existing.Count > 0)
                {
                    var newest = existing[0];
                    if (newest.Length == bytes.Length
                        && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(newest.FullName))) == hash)
                        return newest.FullName; // unchanged since last snapshot — nothing to record
                }

                string dest = Path.Combine(SnapshotDirFor(modName),
                    $"meta_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
                File.WriteAllBytes(dest, bytes);
                PruneSnapshots(modName);
                return dest;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Manifest snapshot failed (continuing): {Error}", ex.Message);
                return null;
            }
        }

        // Newest first.
        private static List<FileInfo> SnapshotFiles() =>
            SnapshotFiles(Settings.ModName ?? string.Empty);

        private static List<FileInfo> SnapshotFiles(string modName)
        {
            try
            {
                return new DirectoryInfo(SnapshotDirFor(modName))
                    .GetFiles("meta_*.json")
                    .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                    .ToList();
            }
            catch
            {
                return new List<FileInfo>();
            }
        }

        private static void PruneSnapshots(string modName) =>
            PruneByPolicy(SnapshotFiles(modName), f => { try { f.Delete(); } catch { } });

        /// <summary>
        /// Keeps the last <see cref="KeepRecentSnapshots"/> entries plus the first of each of the last
        /// <see cref="KeepDailyDays"/> days, so a mistake noticed tomorrow is still recoverable
        /// without hoarding a full copy per save forever. <paramref name="entries"/> must be newest
        /// first, named so an ordinal sort is chronological.
        ///
        /// Shared by the v4 manifest snapshots here and V3GroupFileStore's snapshot sets, which are
        /// directories rather than files — hence the FileSystemInfo and the delete callback.
        /// </summary>
        internal static void PruneByPolicy<T>(IReadOnlyList<T> entries, Action<T> delete)
            where T : FileSystemInfo
        {
            if (entries.Count <= KeepRecentSnapshots) return;

            var keep = new HashSet<string>(
                entries.Take(KeepRecentSnapshots).Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.Now.Date.AddDays(-KeepDailyDays);
            foreach (var dayGroup in entries.Where(f => f.LastWriteTime >= cutoff).GroupBy(f => f.LastWriteTime.Date))
            {
                var firstOfDay = dayGroup.OrderBy(f => f.Name, StringComparer.Ordinal).First();
                keep.Add(firstOfDay.FullName);
            }

            foreach (var f in entries.Where(f => !keep.Contains(f.FullName)))
                delete(f);
        }

        /// <summary>
        /// The newest v4 snapshot that parses and holds at least one group — i.e. the newest one worth
        /// restoring from. Null when there is nothing usable.
        /// </summary>
        public static string? NewestUsableSnapshot()
        {
            // When the current manifest is still readable (e.g. it parses but lost its Groups), its
            // Identifier tells us which mod this folder is. Refuse any snapshot that disagrees rather
            // than graft another mod's manifest — and its Identifier — into this folder.
            string? expectedId = Read()?["Identifier"]?.ToString();

            foreach (var f in SnapshotFiles())
            {
                try
                {
                    var root = JObject.Parse(File.ReadAllText(f.FullName, Encoding.UTF8));
                    string? id = root["Identifier"]?.ToString();

                    if (root["Groups"] is not JArray g || g.Count == 0 || string.IsNullOrWhiteSpace(id))
                        continue;

                    // Never graft a pre-v4 snapshot onto a v4 folder. A user who was converted to v4
                    // and then reverted by Penumbra can have snapshots of both vintages side by side.
                    if (DetectFormat(root, modDirectory: null) != ModFormat.V4)
                    {
                        Logger.LogWarn("Skipping snapshot {File}: it is not a v4 manifest.", f.Name);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(expectedId)
                        && !string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogWarn("Skipping snapshot {File}: it belongs to a different mod " +
                            "(Identifier {Found}, expected {Expected}).", f.Name, id, expectedId);
                        continue;
                    }

                    return f.FullName;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Clears orphaned <c>.tmp</c> siblings left by an interrupted <see cref="AtomicWrite"/>.
        ///
        /// Nothing else in the mod folder is touched. This used to also delete <c>default_mod.json</c>
        /// once the manifest had a <c>Groups</c> array, on the reasoning that v4's <c>DefaultData</c>
        /// superseded it. Both halves of that were wrong: a missing Groups array says nothing about
        /// the layout (Penumbra omits it for any mod with no option groups), and Penumbra manages the
        /// v3→v4 transition itself — it deletes <c>default_mod.json</c> and keeps its own
        /// <c>default_mod.json.bak</c> to roll back from. Having already reversed course once, the
        /// files it left behind are its business, not ours.
        /// </summary>
        public static void CleanLegacyFiles()
        {
            try
            {
                string root = ModRoot;
                if (!Directory.Exists(root)) return;

                // A meta.json.<guid>.tmp is unambiguously our own half-written file, in either layout.
                foreach (var tmp in Directory.EnumerateFiles(root, MetaFile + ".*.tmp"))
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("CleanLegacyFiles failed (harmless): {Error}", ex.Message);
            }
        }
    }
}
