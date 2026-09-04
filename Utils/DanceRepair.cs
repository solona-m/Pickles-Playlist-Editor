using Pickles_Playlist_Editor.Utils.Tmb;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>One animation in the mod that asks the game for a resource it cannot have.</summary>
    internal sealed class BrokenDanceFile
    {
        /// <summary>Mod-relative path, as the option spells it.</summary>
        public string Relative { get; init; } = string.Empty;

        public string FullPath { get; init; } = string.Empty;

        /// <summary>TMB offsets of the entries to drop, plus any track they would leave empty.</summary>
        public IReadOnlyList<int> EntriesToDrop { get; init; } = Array.Empty<int>();

        /// <summary>One line per fault, for the log and for what the user is told.</summary>
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

        public override string ToString() => $"{Relative}: {string.Join("; ", Problems)}";
    }

    /// <summary>
    /// Finding and repairing animations in a mod whose timelines point at nothing.
    ///
    /// This exists because the damage outlives the bug that caused it. Declaring C173 in
    /// <see cref="TmbBinary"/> stops new merges producing orphaned path offsets, but it does nothing
    /// for the packs already built and already shared — and the file this was written for is not a
    /// missing effect, it is a hard crash to desktop every time the dance loops. Someone who updates
    /// the app and never adds another dance would still be crashing.
    ///
    /// The repair is REMOVAL, not repointing. Aiming a dead offset at some other string in the pool
    /// would give the game a path it can look up and fail to load, which is survivable but arbitrary:
    /// it fires whatever effect happened to be nearby. Dropping the entry gives the outcome the format
    /// already has a shape for — the dances that carry no music simply have no sound entry — and it is
    /// the operation <see cref="TmbSplice.Remove"/> already performs correctly, renumbering ids and
    /// rebuilding the id lists and the pool, rather than a new kind of edit written for one bug.
    ///
    /// Deliberately free of any dependency on the rest of the app, for the same reason the format code
    /// is: it can then be linked into the offline harness and run against a real Penumbra folder. So
    /// nothing here logs or writes — <see cref="Repair"/> hands back bytes and the caller decides what
    /// to do with them.
    /// </summary>
    internal static class DanceRepair
    {
        /// <summary>
        /// The async-VFX entries naming a file this mod does not ship.
        ///
        /// These are structurally perfect — the path is a real, non-empty string in the pool — and
        /// they still kill the game, which is why they need a rule of their own rather than falling
        /// out of <see cref="TmbBinary.BrokenPaths"/>.
        ///
        /// C173 is the only type treated this way, and the evidence is a single crash log that
        /// happens to contain the controlled experiment. A C012 naming <c>vfx/makeyouminestart.avfx</c>
        /// failed to load and the game carried on. Two point eight seconds later — one start
        /// animation — the loop's C173 entries named two .avfx that nothing supplied, and the process
        /// died on the same <c>[null + 0xC0]</c> as an empty path. So for C173 a missing file is not a
        /// missing effect, it is a crash, and "the path is valid" is not enough.
        ///
        /// It bites hardest through sync: the author usually has the source mod on disk, while the
        /// people watching them get only what the author's collection actually resolves. A dance that
        /// is fine locally can be fatal to everyone who sees it.
        ///
        /// A null <paramref name="modSupplies"/> means "not known", and nothing is dropped. That is
        /// not the same as an empty set, which means the mod genuinely supplies nothing.
        /// </summary>
        public static List<TmbBrokenPath> UnsuppliedAsyncEffects(TmbLayout layout,
            ISet<string>? modSupplies)
        {
            var found = new List<TmbBrokenPath>();
            if (modSupplies == null) return found;

            foreach (var entry in layout.Entries)
            {
                if (entry.Magic != TmbBinary.AsyncEffectEntry) continue;
                foreach (var field in entry.Fields)
                {
                    if (field.Kind != TmbFieldKind.String) continue;

                    // A path that does not resolve at all is already a finding elsewhere; reporting it
                    // twice would have the repair drop the same entry for two different reasons.
                    if (!TmbBinary.TryResolveString(layout, entry, field, out string path, out _)) continue;
                    if (path.Length == 0 || IsStockPath(path) || modSupplies.Contains(path)) continue;

                    found.Add(new TmbBrokenPath(entry, field,
                        $"fires async VFX '{path}', which this mod does not provide"));
                }
            }
            return found;
        }

        /// <summary>
        /// Whether the GAME ships this path, so no mod has to.
        ///
        /// Without this the rule above is not "the file is missing", it is "the file is not in this
        /// mod's option list" — and a stock effect is in nobody's option list. Measured over 3,076
        /// real .pap in a 1,040-mod folder, exactly five distinct C173 paths are unsupplied by their
        /// own mod, and the split is total:
        ///
        ///   vfx/makeyoumineloop.avfx, vfx/makeyoumine/smoke.avfx   custom, missing, the crash
        ///   vfx/common/eff/syncactiontimelineclip01t.avfx          stock, present, in three mods
        ///
        /// Treating that last one as unsupplied would have silently deleted a working effect from
        /// every dance in Lilly's Silent Dance Party and Nightlife+ the moment either was opened.
        ///
        /// <c>vfx/common/</c> is the only root the measurement actually exercises. The others are
        /// listed because they are just as certainly the game's own, and the cost of the two mistakes
        /// is not symmetric: keeping a stock path costs nothing, while dropping one destroys content
        /// nobody can get back without re-adding the dance.
        /// </summary>
        public static bool IsStockPath(string path)
        {
            foreach (string root in StockRoots)
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Deliberately confined to the <c>vfx/</c> tree the game owns outright.
        ///
        /// <c>chara/</c> was in this list and had to come out: it is one of the most heavily MODDED
        /// namespaces there is, and the crash report that produced this whole rule contains
        /// <c>chara/accessory/a0031/vfx/eff/va0001.avfx</c> failing to load on the viewer's machine.
        /// Whitelisting that prefix would wave through exactly the paths most likely to be missing —
        /// inverting the rule precisely where it matters. <c>bg/</c> and <c>bgcommon/</c> went with it
        /// for the same reason.
        /// </summary>
        private static readonly string[] StockRoots =
        {
            "vfx/common/", "vfx/monster/", "vfx/action/", "vfx/cut/", "vfx/channeling/",
            "vfx/lockon/", "vfx/omen/", "vfx/aoe/", "vfx/live/", "vfx/temporary/",
        };

        /// <summary>
        /// Game paths a mod provides, as its options spell them, or NULL if that cannot be determined.
        ///
        /// The null matters, and what triggers it matters more. This originally keyed on
        /// <see cref="PenumbraOptions.Read"/> throwing — which it never does. It is documented and
        /// built to return an EMPTY LIST for anything unreadable, so the guard was dead code and the
        /// real failure mode sailed straight past it: a momentarily unreadable manifest came back as
        /// "this mod supplies nothing", every custom async effect in the pack was then unsupplied, and
        /// the load-time repair would have silently stripped the lot.
        ///
        /// That window is real rather than theoretical. Penumbra rewrites <c>meta.json</c> underneath
        /// this app — <see cref="PenumbraMeta.Read"/> carries a retry loop for exactly that — so a
        /// read landing mid-write is ordinary, not exotic.
        ///
        /// So EMPTY is the sentinel, not an exception. A mod holding dances necessarily declares the
        /// paths those dances live at, so a set with nothing in it means the read failed, never that
        /// the mod genuinely supplies nothing. Callers treat null as "do not touch anything".
        /// </summary>
        public static HashSet<string>? SuppliedPaths(string modRoot) =>
            KnownOrNull(new HashSet<string>(
                PenumbraOptions.Read(modRoot).SelectMany(o => o.Files.Keys),
                StringComparer.OrdinalIgnoreCase));

        /// <summary>An empty supplied set is a failed read, not a mod that supplies nothing.</summary>
        private static HashSet<string>? KnownOrNull(HashSet<string> supplied) =>
            supplied.Count == 0 ? null : supplied;

        /// <summary>
        /// Every animation this mod's options point at that carries an unusable path.
        ///
        /// Reads the timeline only — header and tail — so surveying a pack of fifty multi-megabyte
        /// animations costs a few hundred kilobytes rather than a few hundred megabytes.
        ///
        /// Scoped to the files the mod's OPTIONS name, not to every .pap under the folder. A mod
        /// folder routinely holds spares, backups and half-imported leftovers the game never sees, and
        /// rewriting those would mean changing files nobody asked about to fix a problem nobody has.
        ///
        /// <paramref name="notes"/> collects the files that could not be read at all. They are not
        /// findings — nothing can be done to a file that will not parse — but they are the difference
        /// between "clean" and "not looked at", and the caller logs them.
        /// </summary>
        public static List<BrokenDanceFile> Scan(string modRoot, List<string>? notes = null)
        {
            var found = new List<BrokenDanceFile>();
            if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot)) return found;

            List<string> relatives;
            HashSet<string>? supplies;
            try
            {
                var options = PenumbraOptions.Read(modRoot).ToList();
                relatives = options
                    .SelectMany(o => o.Files.Values)
                    .Where(v => v.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // Same sentinel as SuppliedPaths, from this method's own single read: empty means the
                // manifest could not be read, and repairing against it would strip every custom
                // async effect in the mod rather than the broken ones.
                supplies = KnownOrNull(new HashSet<string>(options.SelectMany(o => o.Files.Keys),
                    StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                notes?.Add($"could not list the mod's options: {ex.Message}");
                return found;
            }

            foreach (string relative in relatives)
            {
                string full = Path.Combine(modRoot, relative.Replace('\\', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;

                try
                {
                    var timeline = TmbBinary.Walk(PapFile.ReadTimeline(full));

                    // Two different faults, one repair. A path that does not resolve is a broken
                    // file; a C173 naming a file the mod does not ship is a perfectly formed file
                    // that still crashes anyone who cannot supply it. Both are fixed by dropping the
                    // entry, so both are collected here.
                    var broken = TmbBinary.BrokenPaths(timeline);
                    broken.AddRange(UnsuppliedAsyncEffects(timeline, supplies));
                    if (broken.Count == 0) continue;

                    found.Add(new BrokenDanceFile
                    {
                        Relative = relative,
                        FullPath = full,
                        EntriesToDrop = TmbSplice.WithEmptiedTracks(timeline,
                            broken.Select(b => b.Entry.Offset).Distinct().ToList()),
                        Problems = broken.Select(b => b.ToString()).ToList(),
                    });
                }
                catch (Exception ex) when (ex is PapFormatException or TmbFormatException or IOException)
                {
                    notes?.Add($"{relative}: {ex.Message}");
                }
            }

            return found;
        }

        /// <summary>
        /// The file with its unusable entries taken out, or a throw explaining why it cannot be.
        ///
        /// Verifies its own output before returning it, like everything else here that produces bytes:
        /// the entire purpose is that this file stops asking for a path that is not there, so that is
        /// what gets asserted rather than assumed. Returns bytes rather than writing them, so the
        /// decision to overwrite a user's animation stays with the caller that took a backup.
        /// </summary>
        public static byte[] Repair(BrokenDanceFile file, byte[] original,
            ISet<string>? modSupplies = null)
        {
            var pap = PapFile.Parse(original);
            byte[] result = pap.WithTimeline(TmbSplice.Remove(pap.GetTimeline(), file.EntriesToDrop));

            var after = TmbBinary.Walk(PapFile.Parse(result).GetTimeline());
            var stillBroken = TmbBinary.BrokenPaths(after);
            stillBroken.AddRange(UnsuppliedAsyncEffects(after, modSupplies));
            if (stillBroken.Count > 0)
                throw new TmbFormatException(
                    $"The repaired timeline still {stillBroken[0].Problem}; it was not written.");

            return result;
        }
    }
}
