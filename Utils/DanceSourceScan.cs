using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>One dance a user could add, as some other mod ships it.</summary>
    internal sealed class DanceSource
    {
        /// <summary>The source mod's own name for it, which beats any folder name.</summary>
        public string Label { get; init; } = string.Empty;

        /// <summary>Race code to the looping animation file on disk. Never empty.</summary>
        public IReadOnlyDictionary<string, string> LoopByRace { get; init; } =
            new Dictionary<string, string>();

        /// <summary>Race code to the intro animation, where the mod ships one.</summary>
        public IReadOnlyDictionary<string, string> StartByRace { get; init; } =
            new Dictionary<string, string>();

        /// <summary>The emote slot it was authored for — dance03, sp05, dance_male, ...</summary>
        public string Slot { get; init; } = string.Empty;

        public IReadOnlyList<string> Races => LoopByRace.Keys.OrderBy(r => r, StringComparer.Ordinal).ToList();

        public override string ToString() => $"{Label} [{string.Join(" ", Races)}]";
    }

    /// <summary>A mod that ships dances, and what it ships.</summary>
    internal sealed class DanceSourceMod
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public string? Identifier { get; init; }
        public IReadOnlyList<DanceSource> Dances { get; init; } = Array.Empty<DanceSource>();

        /// <summary>Folders holding the same mod, collapsed into this one.</summary>
        public IReadOnlyList<string> DuplicateFolders { get; init; } = Array.Empty<string>();

        public override string ToString() => $"{Name} ({Dances.Count} dances)";
    }

    /// <summary>
    /// Finds the dances a user already has installed, so adding one is a matter of picking it rather
    /// than of hunting down a .pap on disk.
    ///
    /// The filter is the whole problem. A Penumbra folder holds around a thousand mods, and matching
    /// "any .pap under chara/human" finds roughly 22,000 game paths across 78 mods — overwhelmingly
    /// idles, /pose stances, facial expressions and NSFW sit animations, with one venue mod
    /// contributing 14,759 on its own. Measured against a real folder, the rules below cut that to 42
    /// mods and about 670 dances, and the result is almost purely real dance mods.
    ///
    /// Each clause earns its place, and the evidence for each is a mod that breaks without it:
    ///   - emote_sp as well as emote: K-pop Demon Hunters ships ONLY under emote_sp, so leaving it out
    ///     loses the whole mod.
    ///   - not resident/: those are idles. Idles 2.0 Megapack goes from 141 options to none.
    ///   - not pose slots: /pose stances are not dances.
    ///   - _loop only: the guide's own rule. An intro is imported with its loop, never on its own.
    ///   - DefaultData as well as groups: nine dance mods have no option groups at all and ship the
    ///     dance in their default files. Waltz Dance is one.
    /// </summary>
    internal static class DanceSourceScan
    {
        /// <summary>
        /// A looping full-body emote animation: the shape a dance actually has.
        /// </summary>
        private static readonly Regex DancePath = new(
            @"^chara/human/c(?<race>\d{4})/animation/a\d{4}/bt_common/(emote|emote_sp)/(?<slot>[a-z0-9_]+)_loop\.pap$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The paired intro, which shares a slot with its loop.</summary>
        private static readonly Regex StartPath = new(
            @"^chara/human/c(?<race>\d{4})/animation/a\d{4}/bt_common/(emote|emote_sp)/(?<slot>[a-z0-9_]+)_start\.pap$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>/pose stances, which live in the same folder as dances and are not dances.</summary>
        private static readonly Regex PoseSlot = new(
            @"^(j_|s_|l_)?pose\d*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Trailing " (2)" and friends, which is how a duplicated download is named.</summary>
        private static readonly Regex CopySuffix = new(@"\s*\(\d+\)$", RegexOptions.Compiled);

        public static bool IsDanceSlot(string slot) => !PoseSlot.IsMatch(slot);

        /// <summary>
        /// Every mod under the Penumbra root that ships at least one dance.
        ///
        /// JSON only — around a thousand manifest reads, no recursive byte scanning — so it finishes
        /// in seconds and can be run on a background thread while the dialog opens.
        /// </summary>
        public static List<DanceSourceMod> Scan(string penumbraRoot, string? skipModFolder = null)
        {
            var found = new List<DanceSourceMod>();
            if (string.IsNullOrWhiteSpace(penumbraRoot) || !Directory.Exists(penumbraRoot))
                return found;

            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(penumbraRoot); }
            catch { return found; }

            foreach (string directory in directories)
            {
                string folder = Path.GetFileName(directory);
                if (!string.IsNullOrEmpty(skipModFolder)
                    && string.Equals(folder, skipModFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dances = DancesIn(directory);
                if (dances.Count == 0) continue;

                found.Add(new DanceSourceMod
                {
                    Name = PenumbraOptions.DisplayName(directory),
                    FullPath = directory,
                    Identifier = PenumbraOptions.Identifier(directory),
                    Dances = dances,
                });
            }

            return Deduplicate(found);
        }

        /// <summary>The dances one mod ships, read from its manifest.</summary>
        public static List<DanceSource> DancesIn(string modDirectory)
        {
            var dances = new List<DanceSource>();

            foreach (var option in PenumbraOptions.Read(modDirectory))
            {
                // Grouped by SLOT, not merged into one bucket per option. A single option can carry
                // two entirely different dances: Waltz Dance ships dance03 for five bodies and
                // dance05 for five more from one DefaultData block, overlapping on three of them.
                // Keying by race alone let the second slot overwrite the first on the shared bodies,
                // which both lost a dance and left one DanceSource holding two unrelated
                // choreographies — so which one you got depended on your character's body.
                var bySlot = new Dictionary<string, (Dictionary<string, string> Loops,
                    Dictionary<string, string> Starts)>(StringComparer.OrdinalIgnoreCase);

                foreach (var (gamePath, diskPath) in option.Files)
                {
                    var loop = DancePath.Match(gamePath);
                    if (loop.Success && IsDanceSlot(loop.Groups["slot"].Value))
                    {
                        Slot(bySlot, loop.Groups["slot"].Value).Loops["c" + loop.Groups["race"].Value]
                            = Resolve(modDirectory, diskPath);
                        continue;
                    }

                    var start = StartPath.Match(gamePath);
                    if (start.Success && IsDanceSlot(start.Groups["slot"].Value))
                        Slot(bySlot, start.Groups["slot"].Value).Starts["c" + start.Groups["race"].Value]
                            = Resolve(modDirectory, diskPath);
                }

                // The mod's own label, falling back to the mod name for a dance shipped as default
                // files — those have no option name at all.
                string label = option.IsDefaultData || string.IsNullOrWhiteSpace(option.Name)
                    ? PenumbraOptions.DisplayName(modDirectory)
                    : option.Name;

                var withLoops = bySlot.Where(s => s.Value.Loops.Count > 0).ToList();

                foreach (var (slot, files) in withLoops)
                {
                    dances.Add(new DanceSource
                    {
                        // Two dances from one option would otherwise be indistinguishable in the list.
                        Label = withLoops.Count > 1 ? $"{label} ({slot})" : label,
                        LoopByRace = files.Loops,
                        StartByRace = files.Starts,
                        Slot = slot,
                    });
                }
            }

            return dances;
        }

        private static (Dictionary<string, string> Loops, Dictionary<string, string> Starts) Slot(
            Dictionary<string, (Dictionary<string, string> Loops, Dictionary<string, string> Starts)> bySlot,
            string slot)
        {
            if (!bySlot.TryGetValue(slot, out var files))
            {
                files = (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                         new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                bySlot[slot] = files;
            }
            return files;
        }

        private static string Resolve(string modDirectory, string diskPath) =>
            Path.GetFullPath(Path.Combine(modDirectory, diskPath.Replace('\\', Path.DirectorySeparatorChar)));

        /// <summary>
        /// Collapses folders that hold the same mod.
        ///
        /// A real Penumbra folder has "Lilly's Silent Dance Party" beside "Lilly's Silent Dance Party
        /// (2)" and the same for the DJ packs — re-downloads, not different mods. Listing both doubles
        /// the browser and invites the user to install from whichever copy happens to be stale.
        /// Matched on Penumbra's own Identifier first, then on the name with a copy suffix stripped.
        /// </summary>
        private static List<DanceSourceMod> Deduplicate(List<DanceSourceMod> mods)
        {
            var kept = new List<DanceSourceMod>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in mods.OrderBy(m => m.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                // Name and dance count, NOT Penumbra's Identifier. Re-importing a mod mints a fresh
                // Identifier, so the two copies of "Lilly's Silent Dance Party" in a real folder
                // disagree on it while agreeing on everything a user can see — and the folder's " (2)"
                // suffix never reaches the display name, which comes from the manifest. Pairing the
                // name with the dance count keeps two genuinely different mods that happen to share a
                // title from being merged.
                string key = CopySuffix.Replace(mod.Name, string.Empty).Trim()
                    + "|" + mod.Dances.Count;

                if (seen.TryGetValue(key, out int at))
                {
                    var first = kept[at];
                    kept[at] = new DanceSourceMod
                    {
                        Name = first.Name,
                        FullPath = first.FullPath,
                        Identifier = first.Identifier,
                        Dances = first.Dances,
                        DuplicateFolders = first.DuplicateFolders.Append(mod.FullPath).ToList(),
                    };
                    continue;
                }

                seen[key] = kept.Count;
                kept.Add(mod);
            }

            return kept.OrderByDescending(m => m.Dances.Count)
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
