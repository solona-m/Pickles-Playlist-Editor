using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Keeps one copy of the Penumbra mod's JSON metadata per app version, so a user who manually
    /// reinstalls an older build has a config in a schema that build understands.
    ///
    /// This is NOT the same thing as <see cref="PenumbraMeta.TrySnapshot"/> or
    /// <see cref="V3GroupFileStore.TrySnapshotSet"/>. Those are per-write and keyed by time; they
    /// answer "undo my last edit" and are pruned on a policy tuned for that (20 newest plus
    /// first-of-day for a week). This is per-version and keyed by the app version that WROTE the
    /// files; it answers "give me the config as 2.5.1 left it" and keeps one entry per version.
    ///
    /// The labelling is the subtle part. On the first launch after an upgrade the mod's JSON is
    /// still exactly as the PREVIOUS build left it — the new build has not written anything yet —
    /// so the snapshot is filed under the version that last ran with this mod, not under the version
    /// now running. Filing it under the running version would mean a downgrade to 2.5.1 restores
    /// a file 2.6.0 wrote, which is the corruption this whole feature exists to avoid.
    ///
    /// Everything here is best-effort. A backup failure must never block startup or an update, so
    /// every entry point swallows to the log — the same contract the existing snapshot code keeps.
    /// </summary>
    internal static class VersionBackup
    {
        /// <summary>How many version folders survive a prune, newest-used first.</summary>
        private const int KeepVersions = 10;

        private const string InfoFile = "backup-info.json";
        private const string SettingsFile = "settings.json";

        // Records which app version last ran with a given mod configured, one file per mod, stored
        // beside that mod's backups.
        //
        // Deliberately NOT a single global registry value. Two things make the question per-mod:
        // the configured mod is changeable at runtime, and the only thing that makes a label
        // truthful is "which version last WROTE this mod". A global marker advanced by mod A made
        // mod B — still holding the old build's untouched JSON — get filed under the new version,
        // and a boot where the mod folder simply was not reachable (external drive, network path)
        // advanced it for a mod the app never even opened, losing that mod's pre-upgrade snapshot.
        private const string LastWriterFile = "last-writer.json";

        // Mod roots captured so far this session. Keyed by mod rather than a single session-wide
        // flag because the configured mod is changeable at runtime from the Settings dialog: a
        // global flag meant that switching from mod A to mod B left B with no backup at all for the
        // rest of the session, even though B is what the user then went on to edit.
        //
        // A mod that is not yet configured is deliberately not recorded, so the post-Settings-dialog
        // call site can still succeed on first run.
        private static readonly HashSet<string> s_capturedMods =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Why a capture did not produce a backup, which decides whether to retry.</summary>
        private enum CaptureOutcome
        {
            /// <summary>A backup for this label is on disk — written now, or already correct.</summary>
            Captured,

            /// <summary>No mod configured or none readable. Retry once the user has set one.</summary>
            NotConfigured,

            /// <summary>A real attempt threw. Do not retry this session — see TryCaptureOnBoot.</summary>
            Failed,
        }

        /// <summary>
        /// Root of every version backup, across mods. Public so Settings can open it in Explorer —
        /// a manual downgrade means copying files out of here by hand, which is impossible if the
        /// user cannot find it.
        /// </summary>
        internal static string VersionsRoot => Path.Combine(Playlist.BackupDir, "versions");

        private static string ModVersionsDir
        {
            get
            {
                // Namespaced per mod for the same reason the existing snapshot dirs are: a flat
                // folder would let a restore graft a DIFFERENT mod's Identifier into this one.
                string dir = Path.Combine(VersionsRoot, PenumbraMeta.SnapshotFolderNameForMod());
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// Captures the mod as the last-running build left it, then records that this build has run.
        /// Idempotent per session and safe to call when nothing is configured yet.
        /// </summary>
        internal static void TryCaptureOnBoot()
        {
            // Nothing below may throw: this runs from PlaylistTreeView_Loaded, an unguarded XAML
            // event handler, so an escape would reach App.UnhandledException and kill startup.
            // Capture guards itself; this covers the settings write and everything around it.
            try
            {
                // Checked here as well as in Capture so the per-mod marker below is never read or
                // written for an unconfigured mod, which would resolve to a bogus "unknown" folder.
                string modRoot = PenumbraMeta.ModRoot;
                if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
                    return; // nothing chosen yet — the post-Settings-dialog call site retries

                if (s_capturedMods.Contains(modRoot)) return;

                string running = AppVersion.Display;
                string lastWriter = ReadLastWriter();

                // Equal on a normal boot, so the capture just refreshes the running version's
                // folder. They differ on the first launch after an upgrade, and that boot is the
                // only moment the previous version's true end-of-life state is still on disk.
                string label = string.IsNullOrWhiteSpace(lastWriter) ? running : lastWriter;

                CaptureOutcome outcome = Capture(label, reason: "boot", targetVersion: null);

                // NotConfigured stays unrecorded so the call site can retry once a mod is chosen.
                // Failed is recorded: retrying later in this session would run after the app has
                // been writing, and would file our own data under the previous version's label —
                // the very thing this class exists to prevent.
                if (outcome == CaptureOutcome.NotConfigured)
                    return;

                s_capturedMods.Add(modRoot);

                // Advanced only now that we have actually reached this mod, and whether or not the
                // copy itself worked: from here on this build can write to it, so a later session
                // must not go on believing the files are still the previous version's. A mod the
                // app never reached keeps its old marker and is labelled correctly next time.
                if (!string.Equals(lastWriter, running, StringComparison.Ordinal))
                {
                    WriteLastWriter(running);
                    Logger.LogInfo("Version backup: '{Mod}' last written by '{Old}', now running '{New}'.",
                        PenumbraMeta.SnapshotFolderNameForMod(),
                        string.IsNullOrWhiteSpace(lastWriter) ? "<unknown>" : lastWriter, running);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Version backup on boot failed (continuing): {Error}", ex.Message);
            }
        }

        /// <summary>
        /// The app version that last ran with the configured mod, or "" if this mod has never been
        /// seen. Never throws: a missing or corrupt marker reads as "unknown", which makes the
        /// caller label with the running version — the safe answer for a mod we know nothing about.
        /// </summary>
        private static string ReadLastWriter()
        {
            try
            {
                string path = Path.Combine(ModVersionsDir, LastWriterFile);
                if (!File.Exists(path)) return "";
                return Newtonsoft.Json.Linq.JObject
                    .Parse(File.ReadAllText(path, Encoding.UTF8))["Version"]?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static void WriteLastWriter(string version)
        {
            try
            {
                var doc = new { Version = version, UpdatedUtc = DateTime.UtcNow.ToString("o") };
                File.WriteAllText(Path.Combine(ModVersionsDir, LastWriterFile),
                    JsonConvert.SerializeObject(doc, Formatting.Indented), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // Losing the marker costs one mislabelled boot at worst; it must never cost startup.
                Logger.LogWarn("Version backup: could not record the last-writing version: {Error}", ex.Message);
            }
        }

        /// <summary>
        /// Captures the mod immediately before an update is downloaded and applied. Labelled with the
        /// running version, because the running build is what wrote the files on disk right now.
        /// </summary>
        internal static void TryCaptureBeforeUpdate(string targetVersion)
        {
            Capture(AppVersion.Display, reason: "before-update", targetVersion: targetVersion);
        }

        /// <summary>
        /// Copies the mod's JSON under <paramref name="label"/>, skipping the write when what is
        /// already filed there is correct.
        ///
        /// The returned <see cref="CaptureOutcome"/> is load-bearing, not just a status: the caller
        /// branches on it to decide whether calling again later in the same session is SAFE. Only
        /// <see cref="CaptureOutcome.NotConfigured"/> — meaning no mod is chosen, so nothing has
        /// been written and nothing can be — permits a retry. Any exit reached with a real mod
        /// configured must return <see cref="CaptureOutcome.Failed"/>, because by the time a retry
        /// runs this build may have written to that mod, and the label would then be a lie.
        /// </summary>
        private static CaptureOutcome Capture(string label, string reason, string? targetVersion)
        {
            try
            {
                string modRoot = PenumbraMeta.ModRoot;
                if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
                    return CaptureOutcome.NotConfigured;

                string modVersionsDir = ModVersionsDir;

                // Before anything else, and deliberately ahead of the format check below: put back a
                // folder left half-swapped by an interrupted capture, and drop stale staging dirs.
                // Prune cannot do this — it only runs on the success path — so gating it behind the
                // format check would strand an orphan for as long as the mod stays unreadable.
                ResolveSidecars(new DirectoryInfo(modVersionsDir));

                ModFormat format = PenumbraMeta.DetectFormat();
                if (format == ModFormat.Unknown)
                {
                    // Nothing readable to back up, and guessing a file set here would write a folder
                    // that looks authoritative during a restore while holding junk.
                    //
                    // Failed, not NotConfigured: the mod IS chosen, it just cannot be read right now
                    // (Penumbra mid-write, a damaged manifest). Marking it retryable would let a
                    // later call this session capture under the previous version's label after this
                    // build has been writing. Missing a backup beats filing one under a lie.
                    Logger.LogWarn("Version backup skipped: '{Mod}' is not a readable Penumbra mod.", modRoot);
                    return CaptureOutcome.Failed;
                }

                string destDir = Path.Combine(modVersionsDir, SanitizeVersion(label));

                bool labelIsRunningBuild = string.Equals(label, AppVersion.Display, StringComparison.Ordinal);
                bool wrote;

                // Under the same gate the stores write behind, so a concurrent Mutate or
                // WriteGroupFile cannot be read half-written.
                lock (PlaylistStore.ModFolderGate)
                {
                    List<string> sources = CollectSources(modRoot, format);
                    if (sources.Count == 0)
                    {
                        // Failed rather than NotConfigured, per the contract above: a mod IS chosen,
                        // so a retry could land after this build has written. Effectively
                        // unreachable — a V3/V4 verdict implies a parseable meta.json — but the
                        // outcome must still be the safe one if it ever is reached.
                        Logger.LogWarn("Version backup skipped: no JSON metadata found in '{Mod}'.", modRoot);
                        return CaptureOutcome.Failed;
                    }

                    // A backup labelled with a version OTHER than the running one is only truthful
                    // when taken before this build has written anything. Who wrote the existing
                    // folder is what tells the two cases apart:
                    //
                    //   * written by an EARLIER build — this is the first boot after an upgrade and
                    //     the files on disk are still that build's, so refreshing is exactly right.
                    //     It also matters: TryCaptureOnBoot runs once per session, so the existing
                    //     copy is from the old build's LAST BOOT and is missing everything the user
                    //     did during that final session. This capture is the only chance to record
                    //     the true end-of-life state of that version.
                    //   * written by THIS build — we already captured this label once, and the mod
                    //     has since been through a session of our own writes. Overwriting now would
                    //     file 2.6.0's data under 2.5.5's name, which is the corruption this class
                    //     exists to prevent.
                    //
                    // The running build may always refresh its own folder: it wrote those files.
                    string? existingWriter = ReadInfoField(destDir, "RunningVersion");
                    bool alreadyCapturedByThisBuild = existingWriter != null
                        && string.Equals(existingWriter, AppVersion.Display, StringComparison.Ordinal);

                    // An incomplete folder is never left alone, whichever branch would apply: the
                    // recorded hash describes the mod, not the backup, so on its own it will happily
                    // certify a folder whose files have since been deleted underneath it.
                    bool intact = BackupIsComplete(destDir);

                    if (!labelIsRunningBuild && alreadyCapturedByThisBuild && intact)
                    {
                        TouchDirectory(destDir);
                        wrote = false;
                    }
                    else
                    {
                        string hash = HashOf(sources);

                        if (intact
                            && string.Equals(ReadInfoField(destDir, "ContentHash"), hash, StringComparison.OrdinalIgnoreCase))
                        {
                            // Identical to what is already filed under this version. Refresh the
                            // timestamp anyway so prune's least-recently-used ordering stays honest.
                            TouchDirectory(destDir);
                            wrote = false;
                        }
                        else
                        {
                            WriteBackup(destDir, sources, hash, label, format, modRoot, reason, targetVersion);
                            wrote = true;
                        }
                    }
                }

                if (wrote)
                    Logger.LogInfo("Version backup written for '{Label}' ({Reason}) at '{Dir}'.", label, reason, destDir);

                // Deliberately on both paths, not just after a write. A version's content stops
                // changing almost immediately, so pruning only when something changed means the
                // retention limit never applies in steady state and old versions pile up forever.
                Prune();
                return CaptureOutcome.Captured;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Version backup failed (continuing): {Error}", ex.Message);
                return CaptureOutcome.Failed;
            }
        }

        // v4 keeps the whole library in the root manifest; v3 spreads it across the group files plus
        // default_mod.json. Reuses the v3 store's own enumeration so the two cannot drift apart.
        private static List<string> CollectSources(string modRoot, ModFormat format)
        {
            if (format == ModFormat.V3)
                return V3GroupFileStore.SnapshotSources(modRoot);

            string meta = PenumbraMeta.MetaPath;
            return File.Exists(meta) ? new List<string> { meta } : new List<string>();
        }

        /// <summary>
        /// Stages into a sibling temp folder and swaps, so an interrupted capture can never leave a
        /// half-written folder that a restore would treat as complete. The previous folder is moved
        /// aside rather than deleted first, so a crash mid-swap still leaves one intact copy.
        /// </summary>
        private static void WriteBackup(string destDir, List<string> sources, string hash, string label,
            ModFormat format, string modRoot, string reason, string? targetVersion)
        {
            string staging = destDir + StagingSuffix;
            string previous = destDir + PreviousSuffix;

            DeleteDirectory(staging);
            Directory.CreateDirectory(staging);

            foreach (string src in sources)
                File.Copy(src, Path.Combine(staging, Path.GetFileName(src)), overwrite: true);

            WriteSettingsExport(Path.Combine(staging, SettingsFile));

            var info = new
            {
                Schema = 1,
                Version = label,
                RunningVersion = AppVersion.Display,
                Reason = reason,
                TargetVersion = targetVersion,
                CapturedUtc = DateTime.UtcNow.ToString("o"),
                Format = format.ToString(),
                ModRoot = modRoot,
                ContentHash = hash,
                Files = sources.Select(Path.GetFileName).ToArray(),
            };
            File.WriteAllText(Path.Combine(staging, InfoFile),
                JsonConvert.SerializeObject(info, Formatting.Indented), new UTF8Encoding(false));

            // Swap, with a rollback if the second move fails. Losing a backup is tolerable; ending up
            // with a folder labelled 2.5.5 that silently holds 2.6.0's content is not, because a
            // restore would trust the label and write back exactly the incompatible file this whole
            // class exists to prevent.
            DeleteDirectory(previous);
            bool movedAside = false;
            try
            {
                if (Directory.Exists(destDir))
                {
                    Directory.Move(destDir, previous);
                    movedAside = true;
                }
                Directory.Move(staging, destDir);
                movedAside = false;
            }
            finally
            {
                if (movedAside && !Directory.Exists(destDir))
                {
                    try { Directory.Move(previous, destDir); } catch { }
                }
            }
            DeleteDirectory(previous);
        }

        // The registry is the only store for app settings, so the export is a plain dump of it minus
        // the credentials Settings refuses to hand out. See Settings.NonExportableValueNames.
        private static void WriteSettingsExport(string path)
        {
            try
            {
                File.WriteAllText(path,
                    JsonConvert.SerializeObject(Settings.ExportableValues(), Formatting.Indented),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // The mod JSON is the part that matters; losing the settings dump is not worth
                // failing the whole capture over.
                Logger.LogWarn("Version backup: settings export failed (continuing): {Error}", ex.Message);
            }
        }

        /// <summary>
        /// One field out of an existing backup's provenance file, or null if the backup or the field
        /// is absent or unreadable. Null always means "re-capture", which is the safe answer for
        /// both callers: an unknown hash forces a rewrite, and an unknown writer means we cannot
        /// claim this build already captured the label.
        /// </summary>
        /// <summary>
        /// True when every file the provenance record claims is present on disk. The ContentHash is
        /// a hash of the MOD, not of the backup, so it cannot notice that the backup itself has lost
        /// files — without this a folder that had its meta.json deleted would match on every boot
        /// forever and never be repaired, and a restore from it would silently yield nothing.
        /// </summary>
        private static bool BackupIsComplete(string destDir)
        {
            try
            {
                string info = Path.Combine(destDir, InfoFile);
                if (!File.Exists(info)) return false;

                var listed = Newtonsoft.Json.Linq.JObject
                    .Parse(File.ReadAllText(info, Encoding.UTF8))["Files"] as Newtonsoft.Json.Linq.JArray;
                if (listed == null || listed.Count == 0) return false;

                foreach (var entry in listed)
                {
                    string? name = entry?.ToString();
                    if (string.IsNullOrEmpty(name) || !File.Exists(Path.Combine(destDir, name)))
                        return false;
                }
                return true;
            }
            catch
            {
                // Unreadable provenance means "re-capture", same as everywhere else here.
                return false;
            }
        }

        private static string? ReadInfoField(string destDir, string field)
        {
            try
            {
                string info = Path.Combine(destDir, InfoFile);
                if (!File.Exists(info)) return null;
                return Newtonsoft.Json.Linq.JObject
                    .Parse(File.ReadAllText(info, Encoding.UTF8))[field]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        // Hashes name and content together, in the order given, so a v3 renumber alone still reads as
        // a change. Mirrors what V3GroupFileStore does for its own sets.
        private static string HashOf(List<string> files)
        {
            using var sha = SHA256.Create();
            foreach (string file in files)
            {
                byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(file).ToLowerInvariant());
                sha.TransformBlock(name, 0, name.Length, null, 0);
                byte[] content = File.ReadAllBytes(file);
                sha.TransformBlock(content, 0, content.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
        }

        /// <summary>
        /// Keeps the <see cref="KeepVersions"/> most recently used version folders, always sparing the
        /// running and last-run versions. Ordered by write time, never by version string: an ordinal
        /// sort puts 2.10.0 below 2.9.0, which would delete the newest backup first.
        /// </summary>
        private static void Prune()
        {
            try
            {
                var root = new DirectoryInfo(ModVersionsDir);
                if (!root.Exists) return;

                var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    SanitizeVersion(AppVersion.Display),
                    SanitizeVersion(ReadLastWriter()),
                };

                // Sidecars were already resolved by Capture before it wrote; they are filtered here
                // rather than deleted, so a recovery that failed keeps its only copy on disk.
                var versions = root.GetDirectories()
                    .Where(d => !IsSidecar(d.Name))
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .ToList();

                foreach (var dir in versions.Skip(KeepVersions).Where(d => !protectedNames.Contains(d.Name)))
                {
                    DeleteDirectory(dir.FullName);
                    Logger.LogInfo("Version backup pruned: '{Version}'.", dir.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Version backup prune failed (continuing): {Error}", ex.Message);
            }
        }

        private const string StagingSuffix = ".tmp";
        private const string PreviousSuffix = ".old";

        private static bool IsSidecar(string name) =>
            name.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(PreviousSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Cleans up after a capture that died mid-swap. A ".tmp" is always litter — staging never
        /// holds the only copy of anything. A ".old" is the opposite: if its live counterpart is
        /// missing, it IS the only surviving copy of that version's backup, so it is moved back
        /// rather than deleted. Deleting it would destroy the older schema and let the next capture
        /// silently refill that version's folder with newer content.
        /// </summary>
        private static void ResolveSidecars(DirectoryInfo root)
        {
            foreach (var dir in root.GetDirectories())
            {
                if (dir.Name.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteDirectory(dir.FullName);
                    continue;
                }

                if (!dir.Name.EndsWith(PreviousSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string live = dir.FullName.Substring(0, dir.FullName.Length - PreviousSuffix.Length);
                if (!Directory.Exists(live))
                {
                    try
                    {
                        Directory.Move(dir.FullName, live);
                        Logger.LogInfo("Version backup: recovered '{Version}' from an interrupted swap.",
                            Path.GetFileName(live));
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarn("Version backup: could not recover '{Dir}': {Error}", dir.Name, ex.Message);
                        continue; // leave it on disk rather than delete the only copy
                    }
                }

                DeleteDirectory(dir.FullName);
            }
        }

        private static string SanitizeVersion(string? version)
        {
            string name = string.IsNullOrWhiteSpace(version) ? "unknown" : version!;
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim(' ', '.');
            return string.IsNullOrEmpty(name) ? "unknown" : name;
        }

        private static void TouchDirectory(string dir)
        {
            try { Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); } catch { }
        }

        private static void DeleteDirectory(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
