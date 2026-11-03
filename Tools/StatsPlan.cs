using System;
using System.Collections.Generic;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>
    /// A song as it was when the run started. Rel is the exact spelling the stats caches key by; RelKey is normalised
    /// and only used to find tree nodes. Backfilled: the name carried in-memory stats added at first load.
    /// </summary>
    internal sealed record StatsEntry(string PlaylistName, string Rel, string RelKey, string Name, bool Backfilled);

    internal sealed record StatsPlan(IReadOnlyList<StatsEntry> Entries, IReadOnlyList<string> ToDetect)
    {
        public static string RelKeyOf(string rel) => Playlist.NormalizeRelativeModPath(rel).ToLowerInvariant();

        /// <summary>Every song with an SCD, skipping the "Off"/"Default" options.</summary>
        public static List<StatsEntry> Snapshot(IEnumerable<(Playlist playlist, Option option)> targets,
            Func<Option, string> relOf, Func<Option, bool> isBackfilled)
        {
            var list = new List<StatsEntry>();
            foreach (var (pl, o) in targets)
            {
                if (o?.Name == null || o.Name == "Off" || o.Name == "Default") continue;
                string rel = relOf(o) ?? "";
                if (rel.Length == 0) continue;
                list.Add(new StatsEntry(pl.Name ?? "", rel, RelKeyOf(rel), o.Name, isBackfilled(o)));
            }
            return list;
        }

        /// <summary>Songs to analyse: distinct exact rels, in first-seen order, missing any cached stat.</summary>
        public static StatsPlan Build(IReadOnlyList<StatsEntry> entries, Func<string, bool> hasAllStats)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var detect = new List<string>();
            foreach (var e in entries)
                if (seen.Add(e.Rel) && !hasAllStats(e.Rel)) detect.Add(e.Rel);
            return new StatsPlan(entries, detect);
        }
    }
}
