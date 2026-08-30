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

            foreach (string race in races)
            {
                if (!source.LoopByRace.TryGetValue(race, out string? loop))
                {
                    errors.Add($"This dance has no animation for {race}.");
                    continue;
                }

                files[GamePath(race, anim, directory, slot, "loop")] = DiskPath(root, slug, race, anim, directory, slot, "loop");

                if (includeStart && source.StartByRace.TryGetValue(race, out _))
                    files[GamePath(race, anim, directory, slot, "start")] = DiskPath(root, slug, race, anim, directory, slot, "start");

                try
                {
                    var papPlan = DancePap.Inspect(File.ReadAllBytes(loop), bundle, djStrings);
                    if (oldName.Length == 0) oldName = papPlan.OldAnimationName;
                    foreach (string path in papPlan.SoundPathsToBlank)
                        if (!removedSounds.Contains(path)) removedSounds.Add(path);
                    errors.AddRange(papPlan.Errors.Select(e => $"{race}: {e}"));
                    warnings.AddRange(papPlan.Warnings.Select(w => $"{race}: {w}"));
                }
                catch (Exception ex)
                {
                    errors.Add($"{race}: {ex.Message}");
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

                foreach (var (gamePath, relative) in plan.Files)
                {
                    bool isStart = gamePath.EndsWith("_start.pap", StringComparison.OrdinalIgnoreCase);
                    string race = RaceOf(gamePath);
                    var lookup = isStart ? plan.Source.StartByRace : plan.Source.LoopByRace;
                    if (!lookup.TryGetValue(race, out string? sourceFile)) continue;

                    byte[] prepared = isStart
                        // The intro carries no effect block in any real dance; it only needs the
                        // animation name the mod's option expects.
                        ? Rename(File.ReadAllBytes(sourceFile), PapFile.StartAnimationName)
                        : DancePap.Prepare(File.ReadAllBytes(sourceFile), bundle, djStrings);

                    string target = Path.Combine(modRoot, relative.Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    AtomicWrite(target, prepared);

                    File.WriteAllBytes(Path.Combine(backup, Path.GetFileName(target) + "." + race + ".produced"), prepared);
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
            return new DanceWriteResult { Succeeded = true, BackupFolder = backup, Warnings = warnings };
        }

        private static string RaceOf(string gamePath)
        {
            var parts = gamePath.Split('/');
            return parts.Length > 2 ? parts[2] : "c0101";
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

        // ---- removing, renaming, reordering ------------------------------------------------------

        /// <summary>
        /// Drops the option, then moves its folder into the backup directory.
        ///
        /// The folder is MOVED, never deleted: a dance the user spent time preparing should be
        /// recoverable from a mistaken click, and the backup folder is outside the directory Penumbra
        /// scans so the move is invisible to it.
        /// </summary>
        public static DanceWriteResult Remove(DanceGroupRef group, DanceEntry dance)
        {
            string backup = BackupFolder(group.ModName);
            JObject? removed = null;
            int removedAt = -1;

            try
            {
                Directory.CreateDirectory(backup);

                DanceGroupIO.Mutate(group, node =>
                {
                    if (node["Options"] is not JArray options) return;
                    removedAt = IndexOf(options, dance);
                    if (removedAt < 0)
                        throw new PenumbraMetaException($"'{dance.Name}' is no longer in this group.");

                    removed = options[removedAt] as JObject;
                    options.RemoveAt(removedAt);
                    DanceGroupIO.RemapDefaultSettings(node, ShiftAfterRemoval(options.Count + 1, removedAt));
                });

                if (dance.FolderRelative != null)
                {
                    string from = Path.Combine(group.ModRoot,
                        dance.FolderRelative.Replace('\\', Path.DirectorySeparatorChar));
                    if (Directory.Exists(from))
                        Directory.Move(from, Path.Combine(backup, Path.GetFileName(from)));
                }
            }
            catch (Exception ex)
            {
                // The manifest write is the half that already landed, so put the option back.
                if (removed != null && removedAt >= 0)
                {
                    try
                    {
                        DanceGroupIO.Mutate(group, node =>
                        {
                            if (node["Options"] is JArray options && removedAt <= options.Count)
                                options.Insert(removedAt, removed);
                        });
                    }
                    catch (Exception undo)
                    {
                        Logger.LogError("Could not restore '{Dance}' after a failed removal: {Error}",
                            dance.Name, undo);
                    }
                }

                Logger.LogError("Removing dance '{Dance}' from {Mod} failed: {Error}", dance.Name, group.ModName, ex);
                return new DanceWriteResult { Error = ex.Message, BackupFolder = backup };
            }

            var warnings = Settle(group, dance.Name, shouldExist: false);
            return new DanceWriteResult { Succeeded = true, BackupFolder = backup, Warnings = warnings };
        }

        /// <summary>Changes the option's display name. The folder on disk is left alone.</summary>
        public static DanceWriteResult Rename(DanceGroupRef group, DanceEntry dance, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                return new DanceWriteResult { Error = "Give the dance a name." };

            try
            {
                DanceGroupIO.Mutate(group, node =>
                {
                    if (node["Options"] is not JArray options) return;
                    int at = IndexOf(options, dance);
                    if (at < 0) throw new PenumbraMetaException($"'{dance.Name}' is no longer in this group.");
                    ((JObject)options[at])["Name"] = newName;
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("Renaming dance '{Dance}' failed: {Error}", dance.Name, ex);
                return new DanceWriteResult { Error = ex.Message };
            }

            return new DanceWriteResult { Succeeded = true, Warnings = Settle(group, newName, shouldExist: true) };
        }

        /// <summary>
        /// Rewrites the group's options into the given order.
        ///
        /// <paramref name="order"/> lists the current indices in their new positions.
        /// <c>DefaultSettings</c> follows, or the mod quietly changes which dance it defaults to.
        /// </summary>
        public static DanceWriteResult Reorder(DanceGroupRef group, IReadOnlyList<int> order)
        {
            try
            {
                DanceGroupIO.Mutate(group, node =>
                {
                    if (node["Options"] is not JArray options) return;
                    if (order.Count != options.Count || order.Distinct().Count() != order.Count
                        || order.Any(i => i < 0 || i >= options.Count))
                        throw new PenumbraMetaException("The new order does not match this group's options.");

                    var reordered = new JArray();
                    var moved = new Dictionary<int, int>();
                    for (int i = 0; i < order.Count; i++)
                    {
                        reordered.Add(options[order[i]]);
                        moved[order[i]] = i;
                    }

                    node["Options"] = reordered;
                    DanceGroupIO.RemapDefaultSettings(node, moved);
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("Reordering dances in {Mod} failed: {Error}", group.ModName, ex);
                return new DanceWriteResult { Error = ex.Message };
            }

            return new DanceWriteResult { Succeeded = true, Warnings = Settle(group, null, shouldExist: null) };
        }

        // ---- plumbing ----------------------------------------------------------------------------

        private static int IndexOf(JArray options, DanceEntry dance)
        {
            // By Id first, then by name, and only then by the position it was read at: Penumbra can
            // rewrite the manifest between a read and a write, so a bare index can point at a
            // different dance by the time it is used.
            if (dance.OptionId is { } id)
            {
                for (int i = 0; i < options.Count; i++)
                    if (Guid.TryParse(options[i]?["Id"]?.ToString(), out var candidate) && candidate == id)
                        return i;
            }

            for (int i = 0; i < options.Count; i++)
                if (string.Equals(options[i]?["Name"]?.ToString(), dance.Name, StringComparison.Ordinal))
                    return i;

            return dance.Index < options.Count ? dance.Index : -1;
        }

        private static Dictionary<int, int> ShiftAfterRemoval(int originalCount, int removedAt)
        {
            var moved = new Dictionary<int, int>();
            for (int i = 0; i < originalCount; i++)
                moved[i] = i == removedAt ? -1 : i > removedAt ? i - 1 : i;
            return moved;
        }

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
