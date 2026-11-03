using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;

namespace Pickles_Playlist_Editor
{
    /// <summary>A song row's tooltip: artist, album, then the AI likelihood. Pure, so every line can be tested.</summary>
    internal static class SongTooltip
    {
        public const string ArtistKey = "SongTip_Artist", AlbumKey = "SongTip_Album";

        /// <summary>Null when there is nothing to show. The title tag is never shown.</summary>
        public static string? Build(SongTags? tags, int? aiStored, bool aiEnabled, Func<string, string> getString)
        {
            var lines = new List<string>(3);
            if (tags is { } t)
            {
                if (t.Artist.Length > 0) lines.Add(string.Format(getString(ArtistKey), t.Artist));
                if (t.Album.Length > 0) lines.Add(string.Format(getString(AlbumKey), t.Album));
            }
            if (AiScoreDisplay.TooltipPercent(aiStored, aiEnabled) is int pct)
                lines.Add(string.Format(getString("AiScore_Tooltip"), pct));
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
    }

    internal static class SongSearch
    {
        /// <summary>Case-insensitive match on playlist name, song name, then (looked up only if needed) artist or album.</summary>
        public static bool Matches(string filter, string? playlistName, string? songName, Func<SongTags?> tags)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
            if (playlistName?.IndexOf(filter, cmp) >= 0 || songName?.IndexOf(filter, cmp) >= 0) return true;
            return tags() is { } t && (t.Artist.IndexOf(filter, cmp) >= 0 || t.Album.IndexOf(filter, cmp) >= 0);
        }
    }
}
