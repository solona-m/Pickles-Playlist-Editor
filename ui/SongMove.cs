using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pickles_Playlist_Editor
{
    /// <summary>What a batch does with songs that are already in the target: drag re-lays them after the anchor, the menu leaves them.</summary>
    internal enum InTargetPolicy { Reorder, StayPut }

    /// <summary>Pure rules for moving songs between playlists, shared by drag-and-drop and the "Move to playlist" menu.</summary>
    internal static class SongMove
    {
        internal readonly record struct BatchError(Playlist Playlist, Option? Song, Exception Error); // Song null = target save

        internal sealed record BatchResult(List<Option> Landed, List<Option> Moved, List<BatchError> Errors);

        internal static bool IsPlaceholder(string? name) =>
            string.Equals(name, "Off", StringComparison.InvariantCultureIgnoreCase);

        /// <summary>The Option a song row shows: by position when the whole playlist is shown, else name + scd, else name.</summary>
        internal static Option? PickOption(IReadOnlyList<Option> options, int siblingCount, int rowIndex,
            string name, string? scdRel, IReadOnlySet<Option> taken)
        {
            if (siblingCount == options.Count && rowIndex >= 0 && rowIndex < options.Count)
                return options[rowIndex];
            if (!string.IsNullOrEmpty(scdRel))
            {
                var exact = options.FirstOrDefault(o => !taken.Contains(o) && o.Name == name
                    && string.Equals(Playlist.GetScdPath(o), scdRel, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }
            return options.FirstOrDefault(o => !taken.Contains(o) && o.Name == name);
        }

        /// <summary>Playlists a selection can move to, in order; the source is left out when there is only one.</summary>
        internal static List<Playlist> Targets(IEnumerable<Playlist> all, IReadOnlyCollection<string> sourceNames, Func<Playlist, bool> hidden)
        {
            var distinct = sourceNames.Distinct(StringComparer.Ordinal).ToList();
            string? only = distinct.Count == 1 ? distinct[0] : null;
            return all.Where(p => !hidden(p) && !string.Equals(p.Name, only, StringComparison.Ordinal)).ToList();
        }

        /// <summary>Moves each song in after the anchor (null = end), carrying on past failures. Does no I/O itself.</summary>
        internal static BatchResult Run(Playlist target, IReadOnlyList<(Playlist source, Option song)> moves, Option? anchor,
            InTargetPolicy policy, Action<Playlist, Playlist, Option, int> move, Action<Playlist> save)
        {
            var landed = new List<Option>();
            var moved = new List<Option>();
            var errors = new List<BatchError>();

            int anchorIndex = anchor == null ? -1 : target.Options.IndexOf(anchor);
            int insertIndex = anchorIndex >= 0 ? anchorIndex + 1 : target.Options.Count;

            foreach (var (source, song) in moves)
            {
                if (ReferenceEquals(song, anchor)) continue;
                if (ReferenceEquals(source, target)) { landed.Add(song); continue; }
                try
                {
                    move(source, target, song, insertIndex);
                    insertIndex++;
                    landed.Add(song);
                    moved.Add(song);
                }
                catch (Exception ex) { errors.Add(new BatchError(source, song, ex)); }
            }

            if (policy == InTargetPolicy.Reorder && landed.Count > 0)
            {
                // Anchor index is taken again after the lift, since songs above it shift it.
                foreach (var song in landed) target.Options.Remove(song);
                int at = anchor == null ? -1 : target.Options.IndexOf(anchor);
                int runStart = at >= 0 ? at + 1 : target.Options.Count;
                target.Options.InsertRange(Math.Clamp(runStart, 0, target.Options.Count), landed);
                try { save(target); }
                catch (Exception ex) { errors.Add(new BatchError(target, null, ex)); }
            }

            return new BatchResult(landed, moved, errors);
        }

        internal static string Describe(BatchError e, Func<Exception, string> format) =>
            e.Song == null ? $"{e.Playlist.Name}: {format(e.Error)}" : $"{e.Playlist.Name}/{e.Song.Name}: {format(e.Error)}";

        /// <summary>How many moved songs the active search no longer shows (same rule as the tree filter).</summary>
        internal static int CountHiddenBySearch(string filter, string targetName, IEnumerable<Option> songs, Func<Option, SongTags?> tags) =>
            songs.Count(s => !SongSearch.Matches(filter, targetName, s.Name, () => tags(s)));
    }
}
