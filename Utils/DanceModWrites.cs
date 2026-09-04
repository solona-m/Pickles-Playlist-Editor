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

        /// <summary>
        /// The mod-relative folder the dance is written into, so a failed add removes exactly
        /// what it created rather than a path recomputed from a mod that may have changed since.
        /// </summary>
        public string DanceFolder { get; init; } = string.Empty;

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
            var ownEffects = new List<string>();

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

            // What the mod itself provides, read before the animations are inspected: an async-VFX
            // clip naming a file that is not in here has to be dropped rather than carried, so the
            // inspection needs the answer.
            var supplied = DanceRepair.SuppliedPaths(group.ModRoot);
            var droppedEffects = new List<string>();

            // Null means the manifest could not be read, and every check below then silently passes:
            // no missing-effect warning, and no async clip dropped. Said out loud rather than left to
            // look like a clean bill of health, because the checks it disables are the ones that stop
            // a dance crashing the people who watch it.
            if (supplied == null)
                warnings.Add("This mod's file list could not be read, so the effect files this dance " +
                    "needs could not be checked. It will be installed exactly as it is.");

            // Inspect each distinct animation once rather than once per body pointing at it.
            foreach (var (disk, sourceFile) in writes.Where(w => w.Key.EndsWith("_loop.pap",
                         StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var papPlan = DancePap.Inspect(File.ReadAllBytes(sourceFile), bundle, djStrings,
                        PapFile.DanceAnimationName, supplied);
                    if (oldName.Length == 0) oldName = papPlan.OldAnimationName;
                    foreach (string path in papPlan.SoundPathsToBlank)
                        if (!removedSounds.Contains(path)) removedSounds.Add(path);
                    foreach (string path in papPlan.EffectsUsed)
                        if (!ownEffects.Contains(path)) ownEffects.Add(path);
                    foreach (string dropped in papPlan.AsyncEffectsDropped)
                        if (!droppedEffects.Contains(dropped)) droppedEffects.Add(dropped);
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
            var missing = supplied == null
                ? new List<string>()
                : bundle.EffectPaths.Where(e => !supplied.Contains(e)).ToList();
            if (missing.Count > 0)
                warnings.Add($"{missing.Count} of the {bundle.EffectPaths.Count} effects this dance " +
                    $"will trigger are not provided by this mod (for example {missing[0]}).");

            // The dance's OWN effects, which are a different problem with the same symptom. The DJ
            // block is copied from this mod and its files are usually right here; a downloaded dance
            // brings its effect paths with it and nothing brings the .avfx, because only the .pap is
            // installed. Saying so is the whole remedy available — the user has to copy those files
            // across themselves — and it is exactly what nobody was told when a dance shipped
            // pointing at two .avfx that existed nowhere in the pack.
            // Minus the ones being removed. Listing a dropped clip here as well would tell the user
            // to go and find a file AND that the entry using it is gone — two warnings about the
            // same path, one of which is no longer true.
            var unsupplied = supplied == null
                ? new List<string>()
                : ownEffects.Where(e => !supplied.Contains(e) && !droppedEffects.Contains(e)).ToList();
            if (unsupplied.Count > 0)
                warnings.Add($"This dance fires {unsupplied.Count} effect" +
                    (unsupplied.Count == 1 ? "" : "s") + " of its own that this mod does not provide " +
                    $"(for example {unsupplied[0]}). Copy them across from the source mod, or the " +
                    "dance will play without them.");

            // Said separately and more firmly, because this is not "you may want to fix this later".
            // The clip is being removed, and the alternative to removing it is that everyone who sees
            // the dance over sync crashes to desktop.
            if (droppedEffects.Count > 0)
                warnings.Add($"{droppedEffects.Count} async VFX clip" +
                    (droppedEffects.Count == 1 ? " was" : "s were") + " removed from this dance " +
                    "because the effect file is not in this mod, and that combination crashes anyone " +
                    "watching you rather than simply not showing. Add the .avfx to this mod and " +
                    $"re-add the dance to keep them. Removed: {string.Join("; ", droppedEffects)}.");

            return new AddDancePlan
            {
                Source = source,
                Races = races,
                DanceName = danceName,
                FolderSlug = slug,
                IncludeStart = includeStart,
                DanceFolder = root.Length == 0 ? slug : root + "\\" + slug,
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

        /// <summary>
        /// The mod-relative path a prepared animation is written to.
        ///
        /// <paramref name="root"/> may legitimately be empty, for a mod that keeps its dances at the
        /// top level rather than under a "dances" folder — an empty root must not produce a leading
        /// backslash, which would make the path absolute-looking and land it outside the mod.
        /// </summary>
        private static string DiskPath(string root, string slug, string race, string anim,
            string directory, string slot, string kind) =>
            (root.Length == 0 ? slug : root + "\\" + slug)
            + $"\\chara\\human\\{race}\\animation\\a{anim}\\bt_common\\{directory}\\{slot}_{kind}.pap";

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
            string danceFolder = Path.Combine(modRoot,
                plan.DanceFolder.Replace('\\', Path.DirectorySeparatorChar));
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
                        : DancePap.Prepare(File.ReadAllBytes(sourceFile), bundle, djStrings,
                            PapFile.DanceAnimationName, DanceRepair.SuppliedPaths(modRoot));

                    string target = Path.Combine(modRoot, relative.Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    PenumbraMeta.AtomicWrite(target, prepared);

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

            // Before the reload, so Penumbra picks the new dance and the repairs up in one pass. The
            // load-time pass will have run already in the normal case; this catches a mod that went
            // bad while the dialog was open, and the dance just written if it somehow came out wrong.
            warnings.AddRange(CleanBrokenDances(modRoot, backup).Warnings);
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

        // ---- repairing ---------------------------------------------------------------------------

        /// <summary>What a repair pass found and did.</summary>
        internal sealed record RepairOutcome(int Repaired, int Failed, List<string> Warnings)
        {
            public static readonly RepairOutcome Nothing = new(0, 0, new List<string>());
            public bool ChangedAnything => Repaired > 0;
        }

        /// <summary>
        /// Repairs every animation in this mod that would crash the game, whether or not the user was
        /// doing anything to it.
        ///
        /// Called when a dance mod is LOADED, not only when one is written to, and that distinction is
        /// the whole reason this is public. A DJ pack is a thing people download; the person who ends
        /// up with a crashing dance in it is usually not the person who built it, and they have no
        /// reason to ever add a dance. Repairing only on write would have fixed the author's copy and
        /// left everybody they shared it with crashing.
        ///
        /// Penumbra is asked to reload afterwards. Only the .pap bytes changed and not the manifest,
        /// so this is not the usual "or Penumbra overwrites the edit" problem — it is that Penumbra
        /// may already be holding the old resource, and the point of the exercise is that the game
        /// stops being handed it.
        /// </summary>
        public static DanceWriteResult RepairMod(DanceGroupRef group)
        {
            string backup = BackupFolder(group.ModName);
            var outcome = CleanBrokenDances(group.ModRoot, backup);

            if (!outcome.ChangedAnything)
                return new DanceWriteResult { Succeeded = true, Warnings = outcome.Warnings };

            Logger.LogInfo("Repaired {Count} animation(s) in {Mod}. Backup: {Backup}",
                outcome.Repaired, group.ModName, backup);

            var warnings = new List<string>(outcome.Warnings);
            warnings.AddRange(Settle(group, null, null));
            return new DanceWriteResult { Succeeded = true, BackupFolder = backup, Warnings = warnings };
        }

        /// <summary>
        /// Repairs any animation in the mod that asks the game for a path it cannot have.
        ///
        /// Silent and unasked, deliberately. The fault it clears is not cosmetic — an empty VFX path
        /// is a crash to desktop the moment the dance loops — and the packs carrying it were built by
        /// an older version of this very tool. A prompt describing an orphaned pool offset is one most
        /// people would dismiss, and dismissing it means carrying on crashing. It takes a backup and
        /// says afterwards what it changed, which is the honest version of doing it anyway.
        ///
        /// Never fatal. This is a pass over files the user did not ask about, so a failure here must
        /// not fail — or roll back — whatever they actually requested.
        /// </summary>
        private static RepairOutcome CleanBrokenDances(string modRoot, string backupFolder)
        {
            var warnings = new List<string>();

            try
            {
                var notes = new List<string>();
                var broken = DanceRepair.Scan(modRoot, notes);
                var supplies = DanceRepair.SuppliedPaths(modRoot);

                foreach (string note in notes)
                    Logger.LogWarn("Could not check an animation in {Mod}: {Note}", modRoot, note);
                if (broken.Count == 0) return RepairOutcome.Nothing;

                Logger.LogWarn("{Count} animation(s) in {Mod} carry unusable paths: {Files}",
                    broken.Count, modRoot, string.Join(" | ", broken.Select(b => b.ToString())));

                var repaired = new List<string>();
                int failed = 0;

                // Each file stands alone. One that cannot be repaired is logged and skipped rather
                // than abandoning the rest — the case this was written for has two identical bad
                // files, and fixing one of them is not a useful outcome.
                foreach (var file in broken)
                {
                    try
                    {
                        byte[] original = File.ReadAllBytes(file.FullPath);
                        byte[] result = DanceRepair.Repair(file, original, supplies);

                        Backup(original, file.Relative, backupFolder);
                        PenumbraMeta.AtomicWrite(file.FullPath, result);

                        Logger.LogInfo("Repaired {File}: dropped {Count} entries ({Problems})",
                            file.Relative, file.EntriesToDrop.Count, string.Join("; ", file.Problems));
                        repaired.Add(file.Relative);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Logger.LogWarn("Could not repair {File}: {Error}", file.Relative, ex.Message);
                    }
                }

                // Worded to read the same after an add and on its own, because it is shown in both
                // places and "N OTHER animations" is nonsense when nothing else just happened.
                if (repaired.Count > 0)
                    warnings.Add($"{repaired.Count} animation" +
                        (repaired.Count == 1 ? " in this mod was" : "s in this mod were") +
                        " asking the game for effect files that are not there, which can crash the " +
                        "game when the dance plays. The broken entries were removed and the " +
                        $"originals kept in the backup folder: {string.Join(", ", repaired)}.");

                if (failed > 0)
                    warnings.Add($"{failed} animation" + (failed == 1 ? "" : "s") +
                        " in this mod could not be repaired. The log says which; re-adding those " +
                        "dances from their original mods is the way to fix them.");

                return new RepairOutcome(repaired.Count, failed, warnings);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Checking {Mod} for broken dances failed: {Error}", modRoot, ex.Message);
                return new RepairOutcome(0, 0, warnings);
            }
        }

        /// <summary>
        /// The original of a repaired animation, kept under a name that survives being looked at a
        /// month later.
        ///
        /// The mod-relative path is flattened into the filename rather than recreated as folders:
        /// every bad animation in a pack is called <c>dance_male_loop.pap</c>, so the folders are the
        /// only thing telling them apart, and a backup folder full of identically named files is one
        /// nobody can restore from.
        /// </summary>
        private static void Backup(byte[] original, string relative, string backupFolder)
        {
            Directory.CreateDirectory(backupFolder);
            string name = relative.Replace('\\', '-').Replace('/', '-');
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            File.WriteAllBytes(Path.Combine(backupFolder, "broken-" + name), original);
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
    }
}
