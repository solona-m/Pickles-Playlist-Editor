using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils.Tmb;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>Everything adding a dance would do, gathered without writing anything.</summary>
    internal sealed class AddDancePlan
    {
        public DanceSource Source { get; init; } = new();
        public IReadOnlyList<string> Races { get; init; } = Array.Empty<string>();
        public string DanceName { get; init; } = string.Empty;
        public string FolderSlug { get; init; } = string.Empty;
        public bool IncludeStart { get; init; }

        /// <summary>Game path to mod-relative path, exactly as the option will carry it.</summary>
        public IReadOnlyDictionary<string, string> Files { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Mod-relative path to the source file it is prepared from — one entry per file actually
        /// written, which is fewer than <see cref="Files"/> whenever bodies share an animation.
        /// </summary>
        public IReadOnlyDictionary<string, string> Writes { get; init; } =
            new Dictionary<string, string>();

        /// <summary>Bytes the source animations occupy, so a 20MB import is not a surprise.</summary>
        public long Bytes { get; init; }

        public string OldAnimationName { get; init; } = string.Empty;
        public IReadOnlyList<string> SoundPathsRemoved { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Effects { get; init; } = Array.Empty<string>();
        public int Tracks { get; init; }

        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public bool CanApply => Errors.Count == 0;
    }

    internal sealed class DanceWriteResult
    {
        public bool Succeeded { get; init; }
        public string? BackupFolder { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public string? Error { get; init; }
    }

    /// <summary>
    /// The writing half of the dances feature.
    ///
    /// Split from <see cref="DanceMod"/> so the reading and discovery side stays easy to exercise
    /// against a real Penumbra folder without any risk of it changing one.
    ///
    /// Every operation here follows the shape <see cref="SoundPathRename.Apply"/> established: capture
    /// the mod root before doing any work, snapshot the manifest, perform the reversible half first,
    /// and undo it if the other half fails. And every one finishes by asking Penumbra to reload and
    /// then RE-READING to confirm the change survived — Penumbra holds the mod in memory and rewrites
    /// the manifest from that copy on its next mod-side action, which silently discarded an edit
    /// during development.
    /// </summary>
    internal static class DanceModWrites
    {
        /// <summary>Where a dance's files and the manifest go before anything is changed.</summary>
        private static string BackupFolder(string modName) => Path.Combine(
            Playlist.BackupDir, "dances", PenumbraMeta.SnapshotFolderNameForMod(modName),
            DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        // ---- adding ------------------------------------------------------------------------------

        /// <summary>
        /// What adding this dance would involve, and every reason it must not run. Writes nothing.
        /// </summary>
        public static AddDancePlan PlanAdd(DanceGroupRef group, IReadOnlyList<DanceEntry> existing,
            DanceSource source, IReadOnlyList<string> races, string danceName, bool includeStart,
            TmbTrackBundle bundle, ISet<string> djStrings)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string oldName = string.Empty;
            var removedSounds = new List<string>();

            if (string.IsNullOrWhiteSpace(danceName))
                errors.Add("Give the dance a name.");
            else if (existing.Any(e => string.Equals(e.Name, danceName, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"This mod already has a dance called '{danceName}'.");

            if (races.Count == 0)
                errors.Add("Choose at least one race to install the dance for.");

            string slug = DanceMod.FolderSlug(danceName);
            string root = DanceMod.DanceFolderRoot(existing);
            var (anim, directory, slot) = DanceMod.PathTemplate(existing);

            if (Directory.Exists(Path.Combine(group.ModRoot, root, slug)))
                errors.Add($"The folder '{root}\\{slug}' already exists in this mod.");

            // Bodies that share one animation share one installed file, pointed at by several game
            // paths — which is exactly what the source mods do. Measured on real dance mods, one file
            // can serve eight bodies; copying it eight times would waste the space and make the mod
            // harder to read for no benefit.
            var writes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;

            foreach (var kind in new[] { "loop", "start" })
            {
                if (kind == "start" && !includeStart) continue;
                var lookup = kind == "loop" ? source.LoopByRace : source.StartByRace;

                foreach (var shared in races.Where(lookup.ContainsKey)
                             .GroupBy(r => lookup[r], StringComparer.OrdinalIgnoreCase))
                {
                    string disk = DiskPath(root, slug, shared.First(), anim, directory, slot, kind);
                    writes[disk] = shared.Key;
                    try { bytes += new FileInfo(shared.Key).Length; } catch { }

                    foreach (string race in shared)
                        files[GamePath(race, anim, directory, slot, kind)] = disk;
                }
            }

            foreach (string race in races.Where(r => !source.LoopByRace.ContainsKey(r)))
                errors.Add($"This dance has no animation for {DanceMod.RaceLabel(race)}.");

            // Inspect each distinct animation once rather than once per body pointing at it.
            foreach (var (disk, sourceFile) in writes.Where(w => w.Key.EndsWith("_loop.pap",
                         StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var papPlan = DancePap.Inspect(File.ReadAllBytes(sourceFile), bundle, djStrings);
                    if (oldName.Length == 0) oldName = papPlan.OldAnimationName;
                    foreach (string path in papPlan.SoundPathsToBlank)
                        if (!removedSounds.Contains(path)) removedSounds.Add(path);
                    errors.AddRange(papPlan.Errors);
                    warnings.AddRange(papPlan.Warnings);
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(sourceFile)}: {ex.Message}");
                }
            }

            // Not fatal, but the dance will animate and do nothing else if the effects it fires are
            // not supplied by this mod — which is exactly the failure that is hardest to diagnose.
            var supplied = new HashSet<string>(
                PenumbraOptions.Read(group.ModRoot).SelectMany(o => o.Files.Keys),
                StringComparer.OrdinalIgnoreCase);
            var missing = bundle.EffectPaths.Where(e => !supplied.Contains(e)).ToList();
            if (missing.Count > 0)
                warnings.Add($"{missing.Count} of the {bundle.EffectPaths.Count} effects this dance " +
                    $"will trigger are not provided by this mod (for example {missing[0]}).");

            return new AddDancePlan
            {
                Source = source,
                Races = races,
                DanceName = danceName,
                FolderSlug = slug,
                IncludeStart = includeStart,
                Files = files,
                Writes = writes,
                Bytes = bytes,
                OldAnimationName = oldName,
                SoundPathsRemoved = removedSounds,
                Effects = bundle.EffectPaths,
                Tracks = bundle.TrackCount,
                Errors = errors,
                Warnings = warnings,
            };
        }

        private static string GamePath(string race, string anim, string directory, string slot, string kind) =>
            $"chara/human/{race}/animation/a{anim}/bt_common/{directory}/{slot}_{kind}.pap";

        private static string DiskPath(string root, string slug, string race, string anim,
            string directory, string slot, string kind) =>
            $"{root}\\{slug}\\chara\\human\\{race}\\animation\\a{anim}\\bt_common\\{directory}\\{slot}_{kind}.pap";

        /// <summary>
        /// Prepares the animation files, writes them, and adds the option.
        ///
        /// Files first, then the manifest: a stray folder with no option pointing at it is invisible
        /// to Penumbra and harmless, whereas an option pointing at files that are not there is a
        /// broken mod. If the manifest write fails, the folder is removed again.
        /// </summary>
        public static DanceWriteResult Add(DanceGroupRef group, AddDancePlan plan,
            TmbTrackBundle bundle, ISet<string> djStrings)
        {
            if (!plan.CanApply)
                return new DanceWriteResult { Error = string.Join(" ", plan.Errors) };

            string modRoot = group.ModRoot;
            string danceFolder = Path.Combine(modRoot, DanceMod.DanceFolderRoot(DanceMod.ReadDances(group)),
                plan.FolderSlug);
            string backup = BackupFolder(group.ModName);
            var warnings = new List<string>();

            try
            {
                Directory.CreateDirectory(backup);
                PenumbraMeta.TrySnapshot(modRoot, group.ModName);

                // One write per distinct animation, not per body: several bodies can point at the
                // same installed file, exactly as the source mods do.
                int produced = 0;
                foreach (var (relative, sourceFile) in plan.Writes)
                {
                    bool isStart = relative.EndsWith("_start.pap", StringComparison.OrdinalIgnoreCase);

                    byte[] prepared = isStart
                        // The intro carries no effect block in any real dance; it only needs the
                        // animation name the mod's option expects.
                        ? Rename(File.ReadAllBytes(sourceFile), PapFile.StartAnimationName)
                        : DancePap.Prepare(File.ReadAllBytes(sourceFile), bundle, djStrings);

                    string target = Path.Combine(modRoot, relative.Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    AtomicWrite(target, prepared);

                    File.WriteAllBytes(
                        Path.Combine(backup, $"{produced++:D2}." + Path.GetFileName(target)), prepared);
                }

                // No AssertModRootUnchanged here, deliberately. That guard exists because the playlist
                // library derives its target from a mutable setting and can therefore finish against a
                // different mod than it started on. This operation is handed its mod root explicitly
                // and never consults the setting, so there is nothing for it to check.

                DanceGroupIO.Mutate(group, node =>
                {
                    var options = node["Options"] as JArray;
                    if (options == null) node["Options"] = options = new JArray();
                    options.Add(NewOption(plan, group.Format));
                });
            }
            catch (Exception ex)
            {
                try { if (Directory.Exists(danceFolder)) Directory.Delete(danceFolder, true); } catch { }
                Logger.LogError("Adding dance '{Dance}' to {Mod} failed: {Error}", plan.DanceName, group.ModName, ex);
                return new DanceWriteResult { Error = ex.Message, BackupFolder = backup };
            }

            warnings.AddRange(Settle(group, plan.DanceName, shouldExist: true));

            // Logged on success too, not only on failure: "it said it worked and nothing appeared"
            // is otherwise indistinguishable from "the click did nothing at all".
            Logger.LogInfo("Added dance '{Dance}' to {Mod}/{Group} ({Files} files). Backup: {Backup}",
                plan.DanceName, group.ModName, group.Name, plan.Writes.Count, backup);

            return new DanceWriteResult { Succeeded = true, BackupFolder = backup, Warnings = warnings };
        }

        private static byte[] Rename(byte[] pap, string animationName)
        {
            var file = PapFile.Parse((byte[])pap.Clone());
            file.SetAnimationName(0, animationName);
            return file.ToBytes();
        }

        private static JObject NewOption(AddDancePlan plan, ModFormat format)
        {
            var files = new JObject();
            foreach (var (gamePath, relative) in plan.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
                files[gamePath] = relative;

            var option = new JObject();
            // v4 options are keyed by a GUID; v3 options have none and must not be given one.
            if (format != ModFormat.V3) option["Id"] = Guid.NewGuid().ToString();
            option["Name"] = plan.DanceName;
            if (format == ModFormat.V3) option["Description"] = string.Empty;
            option["Files"] = files;
            return option;
        }

        // ---- plumbing ----------------------------------------------------------------------------

        /// <summary>
        /// Tells Penumbra to re-read the mod, then checks that it actually took.
        ///
        /// Both halves matter. Without the reload, Penumbra's in-memory copy wins the next time
        /// anything touches the mod and the edit vanishes. And the reload's own answer proves nothing:
        /// its handler returns 200 for a mod it has never heard of, so the only real confirmation is
        /// reading the manifest back off disk.
        /// </summary>
        private static List<string> Settle(DanceGroupRef group, string? danceName, bool? shouldExist)
        {
            var warnings = new List<string>();

            try
            {
                var result = PenumbraApi.ReloadMod(group.ModName, group.ModName)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                if (result == PenumbraApi.ApiResult.Failed)
                    warnings.Add("Penumbra refused to reload the mod. Reload it yourself before " +
                        "changing anything else there, or it may overwrite this change.");
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Reloading {Mod} after a dance change failed: {Error}", group.ModName, ex.Message);
            }

            if (danceName == null || shouldExist == null) return warnings;

            bool present = DanceMod.ReadDances(group)
                .Any(d => string.Equals(d.Name, danceName, StringComparison.OrdinalIgnoreCase));
            if (present != shouldExist)
                warnings.Add(shouldExist.Value
                    ? $"'{danceName}' is not in the mod after saving. Penumbra may have overwritten " +
                      "the change; reload the mod in Penumbra and try again."
                    : $"'{danceName}' is still in the mod after removing it. Penumbra may have " +
                      "overwritten the change.");

            return warnings;
        }

        /// <summary>
        /// Writes via a sibling temp file and an atomic move, for the same reason
        /// <see cref="PenumbraMeta.AtomicWrite"/> does: Penumbra watches this folder, and a
        /// half-written animation is worse than no animation.
        /// </summary>
        private static void AtomicWrite(string target, byte[] contents)
        {
            string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, contents);
            try { File.Move(tmp, target, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }
    }
}
