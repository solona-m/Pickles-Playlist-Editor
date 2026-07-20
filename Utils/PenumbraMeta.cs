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
    /// Reads and writes Penumbra's root <c>meta.json</c> mod manifest (FileVersion 4).
    ///
    /// Penumbra's v4 folded the whole mod layout into this one file: option groups moved out of
    /// per-group <c>group_NNN_name.json</c> files into a <c>Groups</c> array, and
    /// <c>default_mod.json</c> became the <c>DefaultData</c> object. Group ORDER is now the array
    /// index — the old filename number is gone — but the meaning is unchanged: lower = higher
    /// priority.
    ///
    /// Everything the app owns now lives in ONE ~360KB file, so a careless write costs the entire
    /// library rather than one playlist. Two rules make that safe, and both are enforced here rather
    /// than left to callers:
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

        // Serializes every read-modify-write in this process. Reorders run on a background thread
        // (MainWindow.DragDrop) and downloads save from their own tasks, so concurrent writes are
        // real. Penumbra is a separate process and can't be locked out — AtomicWrite's retry loop is
        // the mitigation there.
        private static readonly object s_gate = new();

        public static string ModRoot =>
            Path.Combine(Settings.PenumbraLocation ?? string.Empty, Settings.ModName ?? string.Empty);

        public static string MetaPath => Path.Combine(ModRoot, MetaFile);

        /// <summary>The parsed manifest, or null when it is missing or unparseable.</summary>
        public static JObject? Read()
        {
            try
            {
                string path = MetaPath;
                if (!File.Exists(path))
                    return null;
                return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Logger.LogError("PenumbraMeta.Read failed for {Path}: {Error}", MetaPath, ex);
                return null;
            }
        }

        /// <summary>
        /// The mod's option groups in <c>Groups</c> array order. Null — not an empty array — when
        /// there is no v4 <c>Groups</c> array, which is the caller's signal to fall back to the v3
        /// <c>group_*.json</c> layout. An empty array means "v4, and it genuinely has no groups".
        /// Conflating the two makes the v3 fallback unreachable.
        /// </summary>
        public static JArray? TryReadGroups() => TryReadGroups(Read());

        public static JArray? TryReadGroups(JObject? root) => root?["Groups"] as JArray;

        /// <summary>
        /// The one and only write path. Re-reads the manifest under the lock, snapshots it, hands the
        /// fresh copy to <paramref name="edit"/> to splice, then writes it back atomically.
        /// </summary>
        public static void Mutate(Action<JObject> edit)
        {
            lock (s_gate)
            {
                var root = Read()
                    ?? throw new PenumbraMetaException(
                        $"Penumbra's mod manifest could not be read: {MetaPath}. Nothing was written. " +
                        "(If Penumbra is running it may be mid-write — try again in a moment.)");

                // Snapshot the pre-edit state. Cheap when nothing changed since the last one.
                TrySnapshot();

                edit(root);

                root["FileVersion"] = FileVersion;

                // Penumbra keys the mod by Identifier; a manifest that lost it is a new mod as far as
                // Penumbra is concerned, which silently orphans every user setting. Refuse rather
                // than write one.
                if (string.IsNullOrWhiteSpace(root["Identifier"]?.ToString()))
                    throw new PenumbraMetaException(
                        "Refusing to write meta.json: the edit left it without an Identifier.");

                AtomicWrite(MetaPath, Serialize(root));
            }
        }

        // Penumbra writes tab-indented, LF-terminated JSON. Matching both keeps our writes from
        // showing up as a whole-file reformat: without the explicit newline, Newtonsoft uses
        // Environment.NewLine and every one of the ~11,000 lines gains a CR, so a one-song edit
        // rewrites the entire 360KB manifest as far as any diff (or Penumbra's watcher) can tell.
        internal static string Serialize(JObject root)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb) { NewLine = "\n" })
            using (var jw = new JsonTextWriter(sw)
            {
                Formatting = Formatting.Indented,
                Indentation = 1,
                IndentChar = '\t',
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
        /// True when the configured mod folder is still in Penumbra's pre-v4 layout: a manifest with
        /// no <c>Groups</c> array, alongside the old per-group <c>group_NNN_name.json</c> files.
        ///
        /// This app reads and writes v4 only. Penumbra converts such a folder itself, authoritatively,
        /// the first time it loads it — so the only correct response is to tell the user to do that.
        /// Temporary: delete this (and its one caller) once the v4 rollout has settled.
        /// </summary>
        public static bool IsLegacyModFormat()
        {
            try
            {
                string root = ModRoot;
                if (!Directory.Exists(root) || !File.Exists(MetaPath))
                    return false;

                // A *present but empty* Groups array is a valid v4 mod with no groups, not a legacy
                // folder — only a missing array counts.
                if (TryReadGroups() != null)
                    return false;

                return Directory.EnumerateFiles(root, "group_*.json").Any()
                    || File.Exists(Path.Combine(root, LegacyDefaultMod));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Every <c>.scd</c> game-path key referenced anywhere in the mod: <c>DefaultData.Files</c>
        /// (often absent) plus every option's <c>Files</c>. Falls back to the v3 layout for an
        /// unmigrated folder. Used to populate the baseline-SCD picker.
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

            // v3 fallback: an unmigrated folder Penumbra hasn't loaded yet.
            if (root?["Groups"] == null)
            {
                foreach (var jsonPath in Directory.EnumerateFiles(modDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    if (Path.GetFileName(jsonPath).Equals(MetaFile, StringComparison.OrdinalIgnoreCase))
                        continue;
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
        public static string SnapshotDir
        {
            get
            {
                string dir = Path.Combine(Playlist.BackupDir, "meta", SnapshotFolderNameForMod());
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string SnapshotFolderNameForMod()
        {
            string name = Settings.ModName ?? "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim(' ', '.');
            return string.IsNullOrEmpty(name) ? "unknown" : name;
        }

        private const int KeepRecentSnapshots = 20;
        private const int KeepDailyDays = 7;

        /// <summary>
        /// Copies the current manifest into the snapshot dir, skipping the write when it is identical
        /// to the newest snapshot already there. Best-effort: a snapshot failure must never block the
        /// edit the user asked for.
        /// </summary>
        public static string? TrySnapshot()
        {
            try
            {
                string path = MetaPath;
                if (!File.Exists(path)) return null;

                var bytes = File.ReadAllBytes(path);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));

                var existing = SnapshotFiles();
                if (existing.Count > 0)
                {
                    var newest = existing[0];
                    if (newest.Length == bytes.Length
                        && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(newest.FullName))) == hash)
                        return newest.FullName; // unchanged since last snapshot — nothing to record
                }

                string dest = Path.Combine(SnapshotDir,
                    $"meta_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
                File.WriteAllBytes(dest, bytes);
                PruneSnapshots();
                return dest;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Manifest snapshot failed (continuing): {Error}", ex.Message);
                return null;
            }
        }

        // Newest first.
        private static List<FileInfo> SnapshotFiles()
        {
            try
            {
                return new DirectoryInfo(SnapshotDir)
                    .GetFiles("meta_*.json")
                    .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                    .ToList();
            }
            catch
            {
                return new List<FileInfo>();
            }
        }

        // Keep the last N snapshots plus the first of each of the last few days, so a mistake noticed
        // tomorrow is still recoverable without keeping 360KB per save forever.
        private static void PruneSnapshots()
        {
            var all = SnapshotFiles();
            if (all.Count <= KeepRecentSnapshots) return;

            var keep = new HashSet<string>(all.Take(KeepRecentSnapshots).Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.Now.Date.AddDays(-KeepDailyDays);
            foreach (var dayGroup in all.Where(f => f.LastWriteTime >= cutoff).GroupBy(f => f.LastWriteTime.Date))
            {
                var firstOfDay = dayGroup.OrderBy(f => f.Name, StringComparer.Ordinal).First();
                keep.Add(firstOfDay.FullName);
            }

            foreach (var f in all.Where(f => !keep.Contains(f.FullName)))
            {
                try { f.Delete(); } catch { }
            }
        }

        /// <summary>
        /// The newest snapshot that parses and holds at least one group — i.e. the newest one worth
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
        /// Removes the v3 <c>default_mod.json</c> now superseded by <c>DefaultData</c>, plus any
        /// orphaned <c>.tmp</c> siblings left by an interrupted <see cref="AtomicWrite"/>.
        /// </summary>
        public static void CleanLegacyFiles()
        {
            try
            {
                string root = ModRoot;
                if (!Directory.Exists(root)) return;

                foreach (var tmp in Directory.EnumerateFiles(root, MetaFile + ".*.tmp"))
                {
                    try { File.Delete(tmp); } catch { }
                }

                // Only safe to drop default_mod.json once its contents live in the manifest.
                string legacy = Path.Combine(root, LegacyDefaultMod);
                if (File.Exists(legacy) && TryReadGroups() != null)
                {
                    try { File.Delete(legacy); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("CleanLegacyFiles failed (harmless): {Error}", ex.Message);
            }
        }
    }
}
