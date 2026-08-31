using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// The read side of the three things that write backups.
    ///
    /// <see cref="V3GroupFileStore.TrySnapshotSet"/>, <see cref="PenumbraMeta.TrySnapshot()"/> and
    /// <see cref="VersionBackup"/> have between them been copying the mod's JSON out of harm's way on
    /// every write for a long time. Almost nothing ever read those copies back: Library.Repair looks
    /// at exactly one of them — the newest — and only when the folder has NO playlists left at all.
    ///
    /// That misses the case the backups exist for. A mod folder REPLACED wholesale — the user
    /// reinstalls the DJ mod over the top of their own — is not empty and not damaged. It is a
    /// complete, healthy, wrong library, so Repair correctly reports nothing to fix while the user's
    /// playlists sit in a timestamped folder under %LOCALAPPDATA% that they have no way to find, let
    /// alone choose between.
    ///
    /// So this enumerates every backup of the configured mod with the two numbers that let a human
    /// tell them apart — how many playlists and how many songs each holds — and writes a chosen one
    /// back. Read-only and best-effort throughout except <see cref="Restore"/>: one unreadable folder
    /// must never hide the rest of the list, because the list is what somebody is staring at with
    /// their library gone.
    /// </summary>
    internal static class BackupCatalog
    {
        /// <summary>Which writer produced a backup. Shown, because "before v2.5.4" is a different
        /// kind of promise than "just before an edit at 16:43".</summary>
        internal enum BackupSource
        {
            /// <summary>A pre-write snapshot, taken automatically before an edit.</summary>
            Automatic,

            /// <summary>A per-app-version capture from <see cref="VersionBackup"/>.</summary>
            Version,
        }

        internal sealed class BackupEntry
        {
            /// <summary>The set directory (v3 sets and version backups) or the manifest file
            /// itself (v4 automatic snapshots).</summary>
            internal string Path { get; init; } = string.Empty;

            internal BackupSource Source { get; init; }

            /// <summary>The layout this backup HOLDS, which need not be the folder's layout now.</summary>
            internal ModFormat Format { get; init; }

            internal DateTime TakenAt { get; init; }

            internal int PlaylistCount { get; init; }

            internal int SongCount { get; init; }

            /// <summary>The app version that wrote it. Version backups only; null otherwise.</summary>
            internal string? VersionLabel { get; init; }

            /// <summary>
            /// Why this backup cannot be written into the mod folder as it stands, or null when it
            /// can. Resolved once while the list is built and carried on the entry so the dialog can
            /// explain a refusal without going back to disk on the UI thread — <see
            /// cref="PenumbraMeta.Read"/> retries with sleeps on a manifest it has not already proven
            /// bad, which is a visible freeze from inside a click handler.
            ///
            /// Unrestorable entries stay in the list rather than being filtered out: when Penumbra
            /// converts a mod between layouts every older backup becomes unrestorable at once, and a
            /// dropdown that answers "no backups" to someone whose library just vanished is worse
            /// than one that says why the ones it has cannot be used.
            /// </summary>
            internal string? UnrestorableReason { get; init; }

            internal bool Compatible => UnrestorableReason == null;
        }

        /// <summary>
        /// Every backup of the configured mod that holds at least one playlist, newest first.
        ///
        /// Parses every group file of every set, so it is slow enough to belong on a background
        /// thread — see the cache below for the half of that which is avoidable.
        /// </summary>
        internal static List<BackupEntry> List()
        {
            var entries = new List<BackupEntry>();

            // Each source is guarded separately. A permissions failure or a half-deleted folder under
            // one of the three roots must cost that root's entries and no more.
            Collect(entries, CollectV3Sets, "v3 snapshot sets");
            Collect(entries, CollectManifestSnapshots, "manifest snapshots");
            Collect(entries, CollectVersionBackups, "version backups");

            ModFormat live = SafeDetectFormat();

            return entries
                // A backup with no playlists in it is not a backup of anything. Offering one is how a
                // user restores an empty library over a merely-wrong one and loses the real thing.
                .Where(e => e.PlaylistCount > 0)
                .Select(e => IsRestorable(e.Format, live)
                    ? e
                    : new BackupEntry
                    {
                        Path = e.Path,
                        Source = e.Source,
                        Format = e.Format,
                        TakenAt = e.TakenAt,
                        PlaylistCount = e.PlaylistCount,
                        SongCount = e.SongCount,
                        VersionLabel = e.VersionLabel,
                        UnrestorableReason = WhyNotRestorable(e.Format, live),
                    })
                .OrderByDescending(e => e.TakenAt)
                .ToList();
        }

        private static void Collect(List<BackupEntry> into, Action<List<BackupEntry>> collect, string what)
        {
            try
            {
                collect(into);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Backup list: could not read {What}: {Error}", what, ex.Message);
            }
        }

        /// <summary>
        /// Whether a backup in <paramref name="backup"/> may be written into a folder currently in
        /// <paramref name="live"/>. The single source of truth for that question: the dropdown marks
        /// entries with it and <see cref="Restore"/> enforces it, so the list can never offer
        /// something the restore will refuse.
        ///
        /// Deliberately asymmetric about Unknown. Under v4 the manifest IS the library, so a folder
        /// too damaged to classify is the very thing a manifest restore repairs, and there are no
        /// group files present for a manifest to orphan. Under v3 the reverse holds: group files
        /// written into a folder that turns out to be v4 are ignored wholesale by Penumbra — it reads
        /// FileVersion 4, sees no Groups, and reports a mod with no options — and a v4 mod with a
        /// damaged manifest is indistinguishable from a v3 mod that lost everything. So v3 restores
        /// demand positive proof and refuse to guess.
        /// </summary>
        private static bool IsRestorable(ModFormat backup, ModFormat live) => backup switch
        {
            ModFormat.V3 => live == ModFormat.V3,
            ModFormat.V4 => live == ModFormat.V4 || live == ModFormat.Unknown,
            _ => false,
        };

        /// <summary>
        /// The layout the mod folder is in, falling back to what the folder itself shows when the
        /// manifest cannot be read.
        ///
        /// That fallback is the load-bearing part. <see cref="PenumbraMeta.DetectFormat()"/> consults
        /// the folder only when meta.json PARSES — a missing or damaged one returns Unknown outright,
        /// and a damaged meta.json is exactly the state someone is in when they come looking for a
        /// backup. Left as Unknown, the compatibility check reads it as "nothing to compare against"
        /// and would let a v4 manifest be written over a folder full of group files, which tells
        /// Penumbra the mod has no option groups at all and orphans every playlist in it.
        ///
        /// Group files are the evidence that survives losing the manifest — but only when the
        /// manifest is GONE. DetectFormat answers Unknown for two different states, and they are not
        /// equally informative: an absent meta.json cannot be v4 evidence, so the group files decide;
        /// a meta.json that exists and will not parse could just as easily be a damaged v4 manifest
        /// sitting beside stale group files, and calling that folder v3 would turn the guarantee the
        /// v3 rule is supposed to give into a guess. Only the first case gets the inference.
        ///
        /// Unknown still means Unknown when there is nothing to go on either way.
        /// </summary>
        private static ModFormat SafeDetectFormat()
        {
            ModFormat detected;
            try { detected = PenumbraMeta.DetectFormat(); }
            catch { detected = ModFormat.Unknown; }

            if (detected != ModFormat.Unknown)
                return detected;

            try
            {
                string modRoot = PenumbraMeta.ModRoot;
                if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
                    return ModFormat.Unknown;

                // Present-but-unreadable is the ambiguous case, and it is deliberately NOT inferred.
                if (File.Exists(PenumbraMeta.MetaPathFor(modRoot)))
                    return ModFormat.Unknown;

                if (Directory.EnumerateFiles(modRoot, "group_*.json").Any())
                    return ModFormat.V3;
            }
            catch
            {
                // An enumeration failure tells us nothing; Unknown is still the honest answer.
            }

            return ModFormat.Unknown;
        }

        // ---- sources ---------------------------------------------------------------------------

        private static void CollectV3Sets(List<BackupEntry> into)
        {
            var root = new DirectoryInfo(V3GroupFileStore.SnapshotRoot);
            if (!root.Exists) return;

            foreach (var dir in root.GetDirectories())
            {
                var groups = dir.GetFiles("group_*.json");
                if (groups.Length == 0) continue;

                var found = CountGroupFiles(dir.FullName, groups);
                into.Add(new BackupEntry
                {
                    Path = dir.FullName,
                    Source = BackupSource.Automatic,
                    Format = ModFormat.V3,
                    TakenAt = ParseSetStamp(dir.Name) ?? dir.LastWriteTime,
                    PlaylistCount = found.Playlists,
                    SongCount = found.Songs,
                });
            }
        }

        private static void CollectManifestSnapshots(List<BackupEntry> into)
        {
            var root = new DirectoryInfo(PenumbraMeta.SnapshotDir);
            if (!root.Exists) return;

            foreach (var file in root.GetFiles("meta_*.json"))
            {
                var found = InspectManifest(file.FullName);

                // Both conditions matter, and for different reasons: no groups means there is nothing
                // to restore, while a non-v4 verdict means this snapshot's groups are the WRONG SHAPE
                // to splice into a v4 manifest even though they are present. See InspectManifest.
                if (found.Playlists == 0 || found.Format != ModFormat.V4) continue;

                into.Add(new BackupEntry
                {
                    Path = file.FullName,
                    Source = BackupSource.Automatic,
                    Format = ModFormat.V4,
                    // "meta_20260831_164345_123.json" — the stamp starts after the prefix.
                    TakenAt = ParseSetStamp(Path.GetFileNameWithoutExtension(file.Name)[5..])
                              ?? file.LastWriteTime,
                    PlaylistCount = found.Playlists,
                    SongCount = found.Songs,
                });
            }
        }

        private static void CollectVersionBackups(List<BackupEntry> into)
        {
            var root = new DirectoryInfo(VersionBackup.ModVersionsDir);
            if (!root.Exists) return;

            foreach (var dir in root.GetDirectories())
            {
                // Half-swapped leftovers from an interrupted capture. VersionBackup resolves these on
                // its next run; until then they are duplicates of a folder already in this list.
                if (VersionBackup.IsSidecar(dir.Name)) continue;

                var info = ReadInfo(dir);
                ModFormat format = FormatOfVersionBackup(dir);

                Inspection found;
                if (format == ModFormat.V3)
                {
                    // A v3 payload carrying a v4 manifest is internally inconsistent, and restoring
                    // it would write both halves of a contradiction: the group files, and a meta.json
                    // that tells Penumbra this mod has no option groups. Drop it rather than offer a
                    // backup that would empty the library it claims to bring back. Unknown is fine —
                    // that just means the folder has no meta.json of its own.
                    if (InspectManifest(Path.Combine(dir.FullName, PenumbraMeta.MetaFile)).Format
                        == ModFormat.V4)
                    {
                        Logger.LogWarn("Backup list: skipping version backup '{Dir}' — it holds " +
                            "group files but a v4 manifest.", dir.Name);
                        continue;
                    }

                    found = CountGroupFiles(dir.FullName, dir.GetFiles("group_*.json"));
                }
                else
                {
                    found = InspectManifest(Path.Combine(dir.FullName, PenumbraMeta.MetaFile));

                    // The manifest itself overrules backup-info.json's Format. That record is written
                    // from what DetectFormat said at capture time, but a folder mid-conversion could
                    // have been captured under a label the file does not bear out — and the file is
                    // what a restore actually splices.
                    if (found.Format != ModFormat.V4) continue;
                }

                if (found.Playlists == 0) continue;

                into.Add(new BackupEntry
                {
                    Path = dir.FullName,
                    Source = BackupSource.Version,
                    Format = format,
                    TakenAt = ParseCapturedUtc(info) ?? dir.LastWriteTime,
                    PlaylistCount = found.Playlists,
                    SongCount = found.Songs,
                    // The folder name is the version, but the record inside it is authoritative — the
                    // name has been through SanitizeVersion.
                    VersionLabel = info?["Version"]?.ToString() ?? dir.Name,
                });
            }
        }

        private static JObject? ReadInfo(DirectoryInfo dir)
        {
            try
            {
                string path = Path.Combine(dir.FullName, "backup-info.json");
                return File.Exists(path)
                    ? JObject.Parse(File.ReadAllText(path, Encoding.UTF8))
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Which layout a version backup HOLDS, decided by the files in it rather than by the
        /// provenance record beside them.
        ///
        /// VersionBackup captures one file set or the other — group files plus default_mod.json for
        /// v3, meta.json alone for v4 — so the folder's contents answer this directly and completely.
        /// backup-info.json's <c>Format</c> is a claim written once at capture time that nothing has
        /// re-checked since, and where a claim and the files can disagree it is the files that a
        /// restore actually writes. It used to be trusted outright on the v3 side, which meant a
        /// stale record could get a v4 manifest labelled as a v3 payload and written into a folder
        /// being filled with group files — telling Penumbra to ignore every one of them.
        /// </summary>
        private static ModFormat FormatOfVersionBackup(DirectoryInfo dir) =>
            dir.GetFiles("group_*.json").Length > 0 ? ModFormat.V3 : ModFormat.V4;

        private static DateTime? ParseCapturedUtc(JObject? info)
        {
            string? captured = info?["CapturedUtc"]?.ToString();
            if (string.IsNullOrWhiteSpace(captured)) return null;

            return DateTime.TryParse(captured, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var utc)
                ? utc.ToLocalTime()
                : null;
        }

        // Set folders are named "yyyyMMdd_HHmmss_fff" by both snapshot writers. Null on anything else,
        // so the caller falls back to the folder's write time rather than inventing a date.
        private static DateTime? ParseSetStamp(string name) =>
            DateTime.TryParseExact(name, "yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var stamp)
                ? stamp
                : null;

        // ---- counting --------------------------------------------------------------------------

        // Twenty sets of seventeen group files is several hundred JSON documents, and the dialog
        // re-enumerates every time it opens and after every restore. Backups are immutable once
        // written — nothing ever edits a set in place — so the answer for a given path is cached and
        // only re-derived if its write time moves.
        private static readonly Dictionary<string, Inspection> s_counts =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What one backup turned out to hold. Format is carried alongside the counts
        /// because for a manifest the two come from the same parse.</summary>
        private readonly record struct Inspection(DateTime Stamp, ModFormat Format, int Playlists, int Songs);

        private static bool TryGetCached(string key, DateTime stamp, out Inspection hit)
        {
            lock (s_counts)
            {
                if (s_counts.TryGetValue(key, out hit) && hit.Stamp == stamp)
                    return true;
            }
            hit = default;
            return false;
        }

        private static Inspection Cache(string key, Inspection result)
        {
            lock (s_counts)
                s_counts[key] = result;
            return result;
        }

        /// <summary>
        /// Playlists and songs in a v3 file set. Songs are summed the same way the startup summary
        /// sums them — one per option — so the numbers here and the "Loaded N playlist(s), M song(s)"
        /// line in the log are directly comparable. That comparison is the whole point of showing
        /// them: it is how a user picks the backup from before the loss.
        /// </summary>
        private static Inspection CountGroupFiles(string key, FileInfo[] groups)
        {
            DateTime stamp = groups.Length == 0 ? default : groups.Max(f => f.LastWriteTimeUtc);
            if (TryGetCached(key, stamp, out var cached)) return cached;

            int playlists = 0, songs = 0;
            foreach (var file in groups)
            {
                var group = V3GroupFileStore.TryLoadGroupQuiet(file.FullName, out _);
                if (group == null) continue;   // an unreadable member costs its own count, not the set

                playlists++;
                songs += (group["Options"] as JArray)?.Count ?? 0;
            }

            return Cache(key, new Inspection(stamp, ModFormat.V3, playlists, songs));
        }

        /// <summary>
        /// What a backed-up manifest holds, and — the part that must not be assumed — which layout it
        /// is a manifest FOR.
        ///
        /// A <c>Groups</c> array is not proof of v4. <see cref="PenumbraMeta.DetectFormat(JObject?,
        /// string?)"/> reads <c>FileVersion</c> and calls anything below 4 a v3 manifest even when
        /// Groups is present, which is what a snapshot taken across a v4-to-v3 reversion looks like.
        /// <see cref="PenumbraMeta.NewestUsableSnapshot"/> refuses those for the same reason this
        /// does: splicing a pre-v4 Groups array into a live v4 manifest is a graft between two
        /// different shapes of the same key.
        /// </summary>
        /// <inheritdoc cref="CountGroupFiles"/>
        private static Inspection InspectManifest(string metaPath)
        {
            var empty = new Inspection(default, ModFormat.Unknown, 0, 0);
            try
            {
                var file = new FileInfo(metaPath);
                if (!file.Exists) return empty;
                if (TryGetCached(metaPath, file.LastWriteTimeUtc, out var cached)) return cached;

                var root = JObject.Parse(File.ReadAllText(metaPath, Encoding.UTF8));

                // Null for the directory: the backup folder is not the mod folder, and letting
                // DetectFormat fall back to scanning it would classify by the wrong folder's files.
                ModFormat format = PenumbraMeta.DetectFormat(root, modDirectory: null);

                if (root["Groups"] is not JArray groups)
                    return Cache(metaPath, new Inspection(file.LastWriteTimeUtc, format, 0, 0));

                int songs = groups.OfType<JObject>().Sum(g => (g["Options"] as JArray)?.Count ?? 0);
                return Cache(metaPath, new Inspection(file.LastWriteTimeUtc, format, groups.Count, songs));
            }
            catch
            {
                // Same contract as everywhere else here: an unreadable backup drops out of the list
                // rather than taking the list with it.
                return empty;
            }
        }

        // ---- restore ---------------------------------------------------------------------------

        /// <summary>
        /// Writes a backup's playlists back into the mod folder.
        ///
        /// Snapshots the folder as it is first, so the restore itself appears at the top of this same
        /// list as an undo — which matters more than usual, because the user reaching for this has
        /// already lost data once and is about to overwrite the only copy of whatever replaced it.
        ///
        /// Throws <see cref="PenumbraMetaException"/> rather than reporting a failure quietly: every
        /// exit that is not a completed restore must be visible, since the alternative is a user who
        /// believes their library is back.
        /// </summary>
        internal static List<string> Restore(BackupEntry entry)
        {
            var log = new List<string>();
            string modRoot = PenumbraMeta.ModRoot;

            if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
                throw new PenumbraMetaException(
                    $"The mod folder '{modRoot}' does not exist, so there is nowhere to restore to. " +
                    "Nothing was changed.");

            lock (PlaylistStore.ModFolderGate)
            {
                // The folder every write below targets was resolved before the gate; everything else
                // here re-derives it from settings, which are mutable at runtime. If those two ever
                // disagree, the snapshot is taken of one mod while the group files land in another.
                PenumbraMeta.AssertModRootUnchanged(modRoot);

                // Re-checked under the gate rather than trusted from List(): the dropdown may have
                // been sitting open while Penumbra converted the folder between layouts, and writing
                // one layout's files into the other's folder is how a mod loses every playlist at
                // once — see PenumbraMeta.Mutate for the same guard on the write path.
                ModFormat live = SafeDetectFormat();

                if (!IsRestorable(entry.Format, live))
                    throw new PenumbraMetaException(WhyNotRestorable(entry.Format, live));

                // Everything the backup itself can be wrong about is checked HERE, before the try
                // below, so that every "Nothing was changed" exit really does leave the folder — and
                // Penumbra's view of it — untouched.
                FileInfo[] sourceGroups = Array.Empty<FileInfo>();
                JObject? sourceManifest = null;
                if (entry.Format == ModFormat.V3)
                    sourceGroups = LoadBackupGroupFiles(entry);
                else
                    sourceManifest = LoadBackupManifest(entry);

                Logger.LogInfo("Restoring backup '{Path}' ({Playlists} playlist(s), {Songs} song(s), " +
                    "{Format}) into '{Mod}'.",
                    entry.Path, entry.PlaylistCount, entry.SongCount, entry.Format, modRoot);

                // Past every guard, so from here the folder may have been written to. A failure
                // part-way is the case that most needs Penumbra to re-read the mod — it is the one
                // that leaves a blend of two libraries on disk — so the reload is not allowed to be
                // skipped by the exception on its way out.
                try
                {
                    if (entry.Format == ModFormat.V3)
                        RestoreGroupFiles(entry, sourceGroups, modRoot, log);
                    else
                        RestoreManifest(entry, sourceManifest!, live, log);
                }
                finally
                {
                    foreach (string line in log)
                        Logger.LogInfo("Restore: {Line}", line);
                    Playlist.RefreshPenumbraMod();
                }
            }

            return log;
        }

        private static string Describe(ModFormat format) => format switch
        {
            ModFormat.V3 => "v3 (one file per playlist)",
            ModFormat.V4 => "v4 (one meta.json)",
            _ => "unrecognized",
        };

        /// <summary>
        /// Why <see cref="IsRestorable"/> said no, in words a user can act on. Public to the dialog
        /// so a click on an entry marked unrestorable explains ITSELF rather than showing one generic
        /// sentence for two quite different situations.
        /// </summary>
        internal static string WhyNotRestorable(ModFormat backup, ModFormat live)
        {
            if (backup != ModFormat.V3 && backup != ModFormat.V4)
                return "That backup is not in a layout this app recognizes. Nothing was changed.";

            // Guarded on the backup's own layout, not just on live. Restore can only reach this with
            // a v3 backup — IsRestorable lets v4 through on Unknown — but the dialog re-derives the
            // reason at click time, and the folder can have changed since the list was built, so the
            // v4 entry that was refused for a mismatch a moment ago can arrive here with live now
            // Unknown. Describing it as "one file per playlist" would be plainly wrong.
            if (live == ModFormat.Unknown && backup == ModFormat.V3)
                return "This backup holds one file per playlist, which is only safe to restore into a " +
                       "mod folder still in that layout — and this folder cannot be read well enough " +
                       "to tell, so restoring could hide every playlist instead of bringing them " +
                       "back. Use Open Backups Folder to copy the files across by hand. Nothing was " +
                       "changed.";

            return $"This backup is in Penumbra's {Describe(backup)} layout but the mod folder is in " +
                   $"the {Describe(live)} layout now, so it cannot be restored. Nothing was changed.";
        }

        // An automatic v4 snapshot IS the manifest; a version backup is a folder holding one.
        private static string ManifestPathOf(BackupEntry entry) =>
            Directory.Exists(entry.Path)
                ? Path.Combine(entry.Path, PenumbraMeta.MetaFile)
                : entry.Path;

        /// <summary>The backup's playlist files, or a throw naming what is wrong with it. Read before
        /// anything is written, so its failure can honestly claim nothing was changed.</summary>
        private static FileInfo[] LoadBackupGroupFiles(BackupEntry entry)
        {
            var groups = new DirectoryInfo(entry.Path).GetFiles("group_*.json");
            if (groups.Length == 0)
                throw new PenumbraMetaException(
                    "That backup no longer holds any playlist files. Nothing was changed.");
            return groups;
        }

        /// <inheritdoc cref="LoadBackupGroupFiles"/>
        private static JObject LoadBackupManifest(BackupEntry entry)
        {
            string path = ManifestPathOf(entry);

            if (!File.Exists(path))
                throw new PenumbraMetaException(
                    "That backup no longer holds a mod manifest. Nothing was changed.");

            var backup = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));

            if (backup["Groups"] is not JArray groups || groups.Count == 0)
                throw new PenumbraMetaException(
                    "That backup holds no playlists. Nothing was changed.");

            // Re-checked at the point of use, not merely when the list was built: the verdict there
            // came from a cache, and this is the last moment before those groups are spliced into a
            // live v4 manifest. See InspectManifest for why a Groups array is not proof of v4.
            if (PenumbraMeta.DetectFormat(backup, modDirectory: null) != ModFormat.V4)
                throw new PenumbraMetaException(
                    "That backup's manifest is not in Penumbra's current layout, so its playlists " +
                    "cannot be written into this mod. Nothing was changed.");

            return backup;
        }

        private static void RestoreGroupFiles(BackupEntry entry, FileInfo[] groups, string modRoot,
            List<string> log)
        {
            var source = new DirectoryInfo(entry.Path);

            // Recover interrupted reorders BEFORE anything else, the same order Library.Repair uses.
            // A ".reorder_tmp" sidecar is a live playlist that the sweep below cannot see — its name
            // does not match group_*.json — so left in place it survives the restore and the next load
            // reclaims it as a real playlist, putting back one the backup does not contain. Doing it
            // first also means the snapshot records the reclaimed state rather than a half-reordered
            // one, so the undo is a folder that actually loads.
            log.AddRange(V3GroupFileStore.ReclaimReorderTempFiles());

            // The undo. Cheap when the folder is unchanged since the last snapshot, and it is what
            // lets someone who restores the wrong one simply pick again.
            V3GroupFileStore.TrySnapshotSet();

            var restored = new HashSet<string>(groups.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var file in groups)
                File.Copy(file.FullName, Path.Combine(modRoot, file.Name), overwrite: true);

            // Copy first, delete second, deliberately. The leftovers are playlists this backup does
            // not have, and leaving them would blend two libraries — Penumbra keys groups by file
            // name, so the blend can also collide on a number and drop one. But an exception partway
            // through this loop must leave a merged folder, never an empty one, which is what the
            // opposite order would risk.
            int removed = 0;
            foreach (string existing in Directory.GetFiles(modRoot, "group_*.json"))
            {
                if (restored.Contains(Path.GetFileName(existing))) continue;
                try
                {
                    File.Delete(existing);
                    removed++;
                }
                catch (Exception ex)
                {
                    log.Add($"Could not remove '{Path.GetFileName(existing)}': {ex.Message}");
                }
            }

            // default_mod.json holds the v3 mod's redirects outside any group — the baseline .scd
            // among them — so a restore that skipped it could leave the playlists pointing through a
            // mapping that no longer matches them.
            var defaults = source.GetFiles(PenumbraMeta.LegacyDefaultMod).FirstOrDefault();
            if (defaults != null)
                File.Copy(defaults.FullName,
                    Path.Combine(modRoot, PenumbraMeta.LegacyDefaultMod), overwrite: true);

            // meta.json is normally NOT restored, for the same reason RestoreManifest splices instead
            // of copying: it carries Penumbra's own identity for the mod, and a mod that was
            // reinstalled — the case this feature exists for — has a new one.
            //
            // Unless there is no manifest at all. A v3 folder is only a MOD because of meta.json;
            // without it Penumbra does not see the folder, so restoring the group files alone would
            // put every playlist back on disk and still leave the user with nothing, under a dialog
            // saying it worked. Nothing is overwritten in that case — there is no live Identifier to
            // lose — so the backup's copy is strictly better than the hole it fills.
            //
            // "No manifest at all", not "no readable manifest": SafeDetectFormat only calls a folder
            // v3 on the strength of its group files when meta.json is ABSENT, so reaching here with
            // a null read means the file is gone rather than damaged. A folder holding a manifest
            // this app cannot parse never gets classified, and so never gets here.
            if (PenumbraMeta.Read(modRoot) == null)
            {
                var meta = source.GetFiles(PenumbraMeta.MetaFile).FirstOrDefault();

                // Last line of defence, and the one that also covers automatic snapshot sets, which
                // never pass through FormatOfVersionBackup's consistency check. Writing a v4 manifest
                // beside the group files just restored would tell Penumbra the mod has no option
                // groups and hide every one of them — the exact failure this whole guard chain exists
                // to prevent, arriving through the one file the v3 path writes.
                bool safeToRestoreMeta = meta != null
                    && InspectManifest(meta.FullName).Format != ModFormat.V4;

                if (safeToRestoreMeta)
                {
                    File.Copy(meta!.FullName, Path.Combine(modRoot, PenumbraMeta.MetaFile), overwrite: true);
                    log.Add("The mod's meta.json was missing, so the backup's copy was restored with it.");
                }
                else
                {
                    log.Add("WARNING: this mod has no meta.json and the backup has no usable one " +
                            "either, so Penumbra may still not load it. The playlists are on disk " +
                            "and a meta.json from Penumbra will pick them up.");
                }
            }

            log.Add($"Restored {groups.Length} playlist file(s) from {source.Name}.");
            if (removed > 0)
                log.Add($"Removed {removed} playlist file(s) that the backup does not contain.");
        }

        private static void RestoreManifest(BackupEntry entry, JObject backup, ModFormat live,
            List<string> log)
        {
            // Both already validated by LoadBackupManifest, above the try — this only re-derives what
            // it proved, so neither can fail here.
            string path = ManifestPathOf(entry);
            var groups = (JArray)backup["Groups"]!;

            if (live == ModFormat.V4)
            {
                // Splice rather than copy the file over. The live manifest carries Penumbra's own
                // bookkeeping, Identifier above all — that is how Penumbra keys the mod and every
                // setting the user has for it — and a REINSTALLED mod has a brand new one. Writing
                // the backup's Identifier back would hand Penumbra a mod it has never seen and orphan
                // all of it, while appearing to succeed.
                //
                // Mutate takes its own pre-edit snapshot, so this path has the same undo as v3.
                PenumbraMeta.Mutate(root =>
                {
                    root["Groups"] = groups.DeepClone();

                    // DefaultData is the v4 home of the redirects default_mod.json holds under v3, and
                    // is restored for the same reason: the playlists and the mapping they read through
                    // have to come from the same moment.
                    if (backup["DefaultData"] is JObject defaults)
                        root["DefaultData"] = (JObject)defaults.DeepClone();
                });
            }
            else
            {
                // Nothing readable to splice into: the manifest itself is what is missing or damaged,
                // so there is no live Identifier to protect and the whole file is the restore. Same
                // fallback Library.Repair takes — including its snapshot of the broken file, which
                // Mutate would have taken on the other branch. Without it this one path would quietly
                // have no undo, while the confirmation the user just clicked promised one.
                PenumbraMeta.TrySnapshot();
                PenumbraMeta.AtomicWrite(PenumbraMeta.MetaPath, File.ReadAllText(path, Encoding.UTF8));
                log.Add("The mod's meta.json was unreadable, so it was replaced wholesale.");
            }

            int songs = groups.OfType<JObject>().Sum(g => (g["Options"] as JArray)?.Count ?? 0);
            log.Add($"Restored {groups.Count} playlist(s) and {songs} song(s) from {Path.GetFileName(path)}.");
        }
    }
}
