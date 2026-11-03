using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>Manual runs rebuild the stats suffix as today; the automatic run only adds what is missing.</summary>
    internal enum StatsRenameMode { Additive, Rebuild }

    internal readonly record struct CachedStats(int? Bpm, string? Key, TimeSpan? Length)
    {
        public bool IsEmpty => !Bpm.HasValue && string.IsNullOrEmpty(Key) && !Length.HasValue;
    }

    internal sealed record StatsRenameChange(Playlist Playlist, Option Option, string OldName, string NewName);

    internal static class StatsRename
    {
        /// <summary>What each song should be called. Songs with no cached stats at all are left out, so a missing cache never strips a name.</summary>
        public static Dictionary<StatsEntry, string> ExpectedNames(IEnumerable<StatsEntry> entries, Func<string, CachedStats> cached,
            OptionStatsNaming.Parts parts, StatsRenameMode mode)
        {
            var result = new Dictionary<StatsEntry, string>();
            foreach (var e in entries)
            {
                var c = cached(e.Rel);
                if (c.IsEmpty) continue;
                if (mode == StatsRenameMode.Additive)
                    result[e] = StatsNameParts.AddMissing(StatsNameParts.Parse(e.Name), c.Bpm, c.Key, c.Length, parts);
                else
                {
                    var scratch = new Option { Name = e.Name };
                    OptionStatsNaming.UpdateName(scratch, c.Bpm, c.Key, c.Length, parts);
                    result[e] = scratch.Name;
                }
            }
            return result;
        }

        /// <summary>
        /// Renames the songs of one live playlist that are still as the run saw them: same exact SCD path and same name
        /// (or, for a name the first load decorated in memory, the undecorated name read back from disk). Renamed, moved
        /// and deleted songs find no match and are left alone. Dirty: something changed, or a decorated playlist matched.
        /// </summary>
        public static (List<StatsRenameChange> Changes, bool Dirty, int Matched) ApplyOne(Playlist pl,
            IReadOnlyDictionary<StatsEntry, string> expected, Func<Option, string> relOf, bool isBackfilledPlaylist)
        {
            var byRel = expected.Keys.GroupBy(e => e.Rel, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var changes = new List<StatsRenameChange>();
            int matched = 0;
            if (pl.Options != null)
            {
                foreach (var o in pl.Options)
                {
                    if (o?.Name == null) continue;
                    string rel = relOf(o) ?? "";
                    if (!byRel.TryGetValue(rel, out var candidates)) continue;
                    var matches = candidates.Where(e => e.Name == o.Name
                        || (e.Backfilled && o.Name == OptionStatsNaming.StripSuffix(e.Name))).ToList();
                    if (matches.Count == 0) continue;
                    var entry = matches.FirstOrDefault(e => e.PlaylistName == pl.Name) ?? matches[0];
                    matched++;
                    string newName = expected[entry];
                    if (o.Name == newName) continue;
                    changes.Add(new StatsRenameChange(pl, o, o.Name, newName));
                    o.Name = newName;
                }
            }
            return (changes, changes.Count > 0 || (isBackfilledPlaylist && matched > 0), matched);
        }

        public static void Revert(IEnumerable<StatsRenameChange> changes)
        {
            foreach (var c in changes) c.Option.Name = c.OldName;
        }
    }
}
