using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils.Tmb;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>How usable a dance already in the mod looks.</summary>
    internal enum DanceHealth
    {
        Ok,

        /// <summary>The option names a file that is not on disk.</summary>
        MissingFile,

        /// <summary>The animation is there but carries no DJ effect block, so it will not react.</summary>
        NoDjBlock,

        /// <summary>The .pap could not be read at all.</summary>
        Unreadable,
    }

    /// <summary>One dance already in the DJ mod's dances group.</summary>
    internal sealed class DanceEntry
    {
        /// <summary>v4 identity. Null in v3, where position and name are all there is.</summary>
        public Guid? OptionId { get; init; }

        public string Name { get; init; } = string.Empty;

        /// <summary>Position in the group, which is also what <c>DefaultSettings</c> indexes.</summary>
        public int Index { get; init; }

        public IReadOnlyDictionary<string, string> Files { get; init; } =
            new Dictionary<string, string>();

        /// <summary>The folder under the mod this dance lives in, when its files share one.</summary>
        public string? FolderRelative { get; init; }

        public IReadOnlyList<string> Races { get; init; } = Array.Empty<string>();

        public bool HasStart { get; init; }
        public DanceHealth Health { get; init; }

        public override string ToString() => $"{Name} [{string.Join(" ", Races)}] {Health}";
    }

    /// <summary>A mod that might be the one holding the DJ's dances.</summary>
    internal sealed class DjModCandidate
    {
        public string Name { get; init; } = string.Empty;
        public string Folder { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public ModFormat Format { get; init; }
        public DanceGroupRef? DancesGroup { get; init; }
        public int DanceCount { get; init; }
        public bool IsConfiguredMusicMod { get; init; }
        public bool HasOwnScdKeys { get; init; }
        public bool HasDjBlock { get; init; }
        public int Score { get; init; }

        public override string ToString() => $"{Name} ({DanceCount} dances, score {Score})";
    }

    /// <summary>
    /// The DJ mod side of the dances feature: finding it, reading what is in it, and changing it.
    ///
    /// This is the only part that writes to a mod OTHER than the configured playlist mod, so every
    /// operation follows the pattern <see cref="SoundPathRename"/> established: capture the mod root
    /// up front, snapshot the manifest, write the reversible half first, and undo on any failure.
    ///
    /// One rule learned the hard way and worth restating here: Penumbra keeps the mod in memory and
    /// will rewrite the manifest from that copy on its next mod-side action, silently discarding an
    /// external edit. So every write is followed by a reload AND a re-read to confirm it survived —
    /// the reload's 200 response proves nothing, because Penumbra answers 200 for a mod it has never
    /// heard of.
    /// </summary>
    internal static class DanceMod
    {
        /// <summary>What the guide calls the group, and what every DJ pack measured actually calls it.</summary>
        public const string DancesGroupName = "Dances";

        /// <summary>Where dances live inside a DJ pack, when its existing ones do not say otherwise.</summary>
        private const string DefaultDanceFolder = "dances";

        private static readonly Regex DanceGamePath = new(
            @"^chara/human/c(?<race>\d{4})/animation/a(?<anim>\d{4})/bt_common/(?<dir>emote|emote_sp)/(?<slot>[a-z0-9_]+)_(?<kind>loop|start)\.pap$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// How many dance animations each of the mod's groups holds, keyed by group name.
        /// </summary>
        public static Dictionary<string, int> DanceCountsByGroup(string modRoot)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in PenumbraOptions.Read(modRoot))
            {
                if (!option.Files.Keys.Any(k => DanceGamePath.IsMatch(k))) continue;
                counts.TryGetValue(option.GroupName, out int n);
                counts[option.GroupName] = n + 1;
            }
            return counts;
        }

        /// <summary>
        /// The group holding this mod's dances, found by what is IN it rather than by its name.
        ///
        /// Naming was the obvious rule and it is wrong: plenty of DJ packs do not have a group called
        /// "Dances" at all, and a name match would simply report those mods as unusable. The group
        /// carrying the most dance animations is the answer whatever it is called; the name is only a
        /// tie-break for a mod whose groups are all empty of them.
        ///
        /// A remembered id always wins, so a user who picked a different group keeps it.
        /// </summary>
        public static DanceGroupRef? FindDancesGroup(string modRoot, string folder, string? rememberedId = null)
        {
            var groups = DanceGroupIO.ListGroups(modRoot, folder);
            if (groups.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(rememberedId))
            {
                var remembered = groups.FirstOrDefault(g => g.Id?.ToString() == rememberedId);
                if (remembered != null) return remembered;
            }

            var counts = DanceCountsByGroup(modRoot);
            var byContent = groups
                .Where(g => counts.TryGetValue(g.Name, out int n) && n > 0)
                .OrderByDescending(g => counts[g.Name])
                .FirstOrDefault();
            if (byContent != null) return byContent;

            return groups.FirstOrDefault(
                g => string.Equals(g.Name, DancesGroupName, StringComparison.OrdinalIgnoreCase));
        }

        // ---- finding the mod ---------------------------------------------------------------------

        /// <summary>
        /// Every mod that could be the DJ pack, best first.
        ///
        /// The configured playlist mod is INCLUDED rather than skipped. Plenty of DJs run one combined
        /// mod holding both the music and the dances, so treating "is the configured mod" as
        /// disqualifying would hide the right answer from exactly those users; it counts in its
        /// favour instead.
        /// </summary>
        public static List<DjModCandidate> FindCandidates(string penumbraRoot, string? configuredModName)
        {
            var candidates = new List<DjModCandidate>();
            if (string.IsNullOrWhiteSpace(penumbraRoot) || !Directory.Exists(penumbraRoot))
                return candidates;

            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(penumbraRoot); }
            catch (Exception ex)
            {
                Logger.LogWarn("Dance mod scan: could not list '{Root}': {Error}", penumbraRoot, ex.Message);
                return candidates;
            }

            foreach (string directory in directories)
            {
                string folder = Path.GetFileName(directory);
                var dances = DanceSourceScan.DancesIn(directory);
                if (dances.Count == 0) continue;

                string modName = PenumbraOptions.DisplayName(directory);
                var group = FindDancesGroup(directory, folder);

                bool configured = !string.IsNullOrEmpty(configuredModName)
                    && string.Equals(folder, configuredModName, StringComparison.OrdinalIgnoreCase);
                bool ownScd = PenumbraMeta.CollectScdKeys(directory).Count > 0;
                bool djBlock = HasDjBlock(dances);

                int score =
                    (group != null ? 100 : 0) +
                    100 +                            // it ships dances at all, which DancesIn proved
                    (djBlock ? 60 : 0) +
                    (configured ? 40 : 0) +
                    (ownScd ? 0 : 10);

                candidates.Add(new DjModCandidate
                {
                    Name = modName,
                    Folder = folder,
                    FullPath = directory,
                    Format = PenumbraMeta.DetectFormat(PenumbraMeta.Read(directory), directory),
                    DancesGroup = group,
                    DanceCount = dances.Count,
                    IsConfiguredMusicMod = configured,
                    HasOwnScdKeys = ownScd,
                    HasDjBlock = djBlock,
                    Score = score,
                });
            }

            return candidates.OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.DanceCount)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Whether a mod's dances already carry a DJ effect block — the strongest single signal that
        /// this is a DJ pack rather than a mod that merely contains dances.
        /// </summary>
        private static bool HasDjBlock(IReadOnlyList<DanceSource> dances)
        {
            var layouts = ReadTimelines(dances.SelectMany(d => d.LoopByRace.Values).Take(12));
            if (layouts.Count == 0) return false;

            var djStrings = TmbTrackBundle.DjStringSet(layouts);
            return djStrings.Count >= 3 && layouts.Any(l => TmbTrackBundle.IsAlreadyPrepped(l, djStrings));
        }

        private static List<TmbLayout> ReadTimelines(IEnumerable<string> papFiles)
        {
            var layouts = new List<TmbLayout>();
            foreach (string file in papFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { layouts.Add(TmbBinary.Walk(PapFile.Parse(File.ReadAllBytes(file)).GetTimeline())); }
                catch { /* a dance we cannot read tells us nothing either way */ }
            }
            return layouts;
        }

        // ---- the DJ effect block -----------------------------------------------------------------

        /// <summary>
        /// The effect block to give new dances, taken from the mod's own existing ones.
        ///
        /// Never shipped with the app: every DJ pack names its own effects, so a hardcoded block would
        /// be right for one person and wrong for everybody else, and would rot the moment a pack was
        /// updated.
        /// </summary>
        public static (TmbTrackBundle Bundle, HashSet<string> DjStrings) LoadBundle(
            IReadOnlyList<DanceEntry> existing, string modRoot)
        {
            var files = existing
                .SelectMany(e => e.Files.Values)
                .Where(v => v.EndsWith("_loop.pap", StringComparison.OrdinalIgnoreCase))
                .Select(v => Path.Combine(modRoot, v.Replace('\\', Path.DirectorySeparatorChar)));

            var layouts = ReadTimelines(files);
            if (layouts.Count == 0)
                throw new TmbFormatException(
                    "None of this mod's dances could be read, so there is no effect block to copy.");

            var djStrings = TmbTrackBundle.DjStringSet(layouts);
            var (donor, tracks) = TmbTrackBundle.ChooseDonor(layouts, djStrings);
            return (TmbTrackBundle.Extract(donor, tracks), djStrings);
        }

        // ---- reading -----------------------------------------------------------------------------

        /// <summary>The dances currently in the group, in order.</summary>
        public static List<DanceEntry> ReadDances(DanceGroupRef group)
        {
            var dances = new List<DanceEntry>();
            var options = DanceGroupIO.ReadOptions(group);

            for (int i = 0; i < options.Count; i++)
            {
                if (options[i] is not JObject option) continue;

                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (option["Files"] is JObject map)
                {
                    foreach (var property in map.Properties())
                    {
                        string value = property.Value?.ToString() ?? string.Empty;
                        if (value.Length > 0) files[PenumbraMeta.NormalizeScdKey(property.Name)] = value;
                    }
                }

                var races = new List<string>();
                bool start = false;
                foreach (string key in files.Keys)
                {
                    var m = DanceGamePath.Match(key);
                    if (!m.Success) continue;
                    string race = "c" + m.Groups["race"].Value;
                    if (!races.Contains(race)) races.Add(race);
                    if (m.Groups["kind"].Value.Equals("start", StringComparison.OrdinalIgnoreCase))
                        start = true;
                }

                dances.Add(new DanceEntry
                {
                    OptionId = Guid.TryParse(option["Id"]?.ToString(), out var id) ? id : null,
                    Name = option["Name"]?.ToString() ?? string.Empty,
                    Index = i,
                    Files = files,
                    FolderRelative = CommonFolder(files.Values),
                    Races = races,
                    HasStart = start,
                    Health = Assess(files, group.ModRoot),
                });
            }

            return dances;
        }

        private static DanceHealth Assess(IReadOnlyDictionary<string, string> files, string modRoot)
        {
            foreach (string relative in files.Values)
            {
                string full = Path.Combine(modRoot, relative.Replace('\\', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) return DanceHealth.MissingFile;
            }
            return DanceHealth.Ok;
        }

        /// <summary>The folder every one of these files sits under, when there is one.</summary>
        private static string? CommonFolder(IEnumerable<string> relativePaths)
        {
            string? common = null;
            foreach (string relative in relativePaths)
            {
                string[] parts = relative.Replace('/', '\\').Split('\\');
                if (parts.Length < 2) return null;
                string head = parts[0] + "\\" + parts[1];
                if (common == null) common = head;
                else if (!string.Equals(common, head, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return common;
        }

        // ---- shapes taken from the mod's own dances ----------------------------------------------

        /// <summary>
        /// The game-path template this mod's dances use, so a new one is filed the same way.
        ///
        /// Read off the existing options rather than hardcoded: a pack built on a different emote slot
        /// (emote_sp/sp05 rather than emote/dance_male) is perfectly normal, and a new dance filed
        /// under the wrong slot simply never plays.
        /// </summary>
        public static (string AnimNumber, string Directory, string Slot) PathTemplate(
            IReadOnlyList<DanceEntry> existing)
        {
            var counts = new Dictionary<(string, string, string), int>();
            foreach (var entry in existing)
            {
                foreach (string key in entry.Files.Keys)
                {
                    var m = DanceGamePath.Match(key);
                    if (!m.Success) continue;
                    var shape = (m.Groups["anim"].Value, m.Groups["dir"].Value.ToLowerInvariant(),
                        m.Groups["slot"].Value.ToLowerInvariant());
                    counts[shape] = counts.TryGetValue(shape, out int n) ? n + 1 : 1;
                }
            }

            return counts.Count == 0
                ? ("0001", "emote", "dance_male")
                : counts.OrderByDescending(kv => kv.Value).First().Key;
        }

        /// <summary>The folder under the mod that its dances live in — usually <c>dances</c>.</summary>
        public static string DanceFolderRoot(IReadOnlyList<DanceEntry> existing)
        {
            string? modal = existing
                .Select(e => e.FolderRelative)
                .Where(f => f != null)
                .Select(f => f!.Split('\\')[0])
                .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;

            return string.IsNullOrWhiteSpace(modal) ? DefaultDanceFolder : modal;
        }

        /// <summary>
        /// FFXIV body model codes, as they appear in a game path.
        ///
        /// An animation is authored against a body, so a dance mod ships one file per body it
        /// supports and the code in the path says which. They are worth spelling out in the UI: a
        /// column of c0101/c0201/c0301 is meaningless unless you already know the scheme, and picking
        /// the wrong one gives you a dance that silently does nothing on your character.
        ///
        /// Matches the mod author's own guide, which lists Midlander c0101, Miqo c0801, Roe c0901,
        /// Lala c1101, Viera male c1701 and Viera female c1801.
        /// </summary>
        private static readonly Dictionary<string, string> RaceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["c0101"] = "Midlander M",   ["c0201"] = "Midlander F",
            ["c0301"] = "Highlander M",  ["c0401"] = "Highlander F",
            ["c0501"] = "Elezen M",      ["c0601"] = "Elezen F",
            ["c0701"] = "Miqo'te M",     ["c0801"] = "Miqo'te F",
            ["c0901"] = "Roegadyn M",    ["c1001"] = "Roegadyn F",
            ["c1101"] = "Lalafell M",    ["c1201"] = "Lalafell F",
            ["c1301"] = "Au Ra M",       ["c1401"] = "Au Ra F",
            ["c1501"] = "Hrothgar M",    ["c1601"] = "Hrothgar F",
            ["c1701"] = "Viera M",       ["c1801"] = "Viera F",
        };

        /// <summary>The body code with its name, or just the code for one this does not know.</summary>
        public static string RaceLabel(string race) =>
            RaceNames.TryGetValue(race, out string? name) ? $"{race}  {name}" : race;

        /// <summary>
        /// Which bodies to tick by default: all of them.
        ///
        /// Installing only one body is the choice that can go silently wrong. A body with no redirect
        /// simply plays the game's stock emote, so a DJ who picks the wrong one — or later plays an
        /// alt, or shares the pack, which is what DJ packs are for — sees the vanilla dance and no
        /// obvious reason why.
        ///
        /// The cost of taking them all is usually nothing. Measured on real dance mods, bodies very
        /// often share one animation: seven bodies backed by a single 0.4MB file, or eight backed by
        /// two. Those are installed as ONE file with several game paths pointing at it. Where the
        /// bodies really are different animations it does cost real space — one mod ships ten
        /// distinct files totalling 22.7MB — which is why the size is shown before anything is
        /// written and the boxes can be unticked.
        /// </summary>
        public static HashSet<string> DefaultRaces(IReadOnlyList<DanceEntry> existing,
            IReadOnlyCollection<string> available) =>
            new(available, StringComparer.OrdinalIgnoreCase);

        /// <summary>A folder name safe to create, derived from what the user typed.</summary>
        public static string FolderSlug(string danceName)
        {
            string slug = (danceName ?? string.Empty).Trim().ToLowerInvariant();
            foreach (char c in Path.GetInvalidFileNameChars()) slug = slug.Replace(c, '-');
            slug = slug.Replace(':', '-').Trim(' ', '.');
            return string.IsNullOrEmpty(slug) ? "dance" : slug;
        }
    }
}
