using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// A song's artist, album and title, from the Vorbis comments in its SCD. Title is kept for later features
    /// (album art lookup) and is never shown or searched. All empty means "read, nothing found".
    /// </summary>
    internal readonly record struct SongTags(string Artist, string Album, string Title)
    {
        public const int MaxLength = 200;

        public static SongTags None => new("", "", "");

        public bool IsEmpty => Artist.Length == 0 && Album.Length == 0 && Title.Length == 0;

        public bool HasArtistOrAlbum => Artist.Length > 0 || Album.Length > 0;

        public static SongTags From(IEnumerable<string?> artists, IEnumerable<string?> albums, IEnumerable<string?> titles) =>
            new(Join(artists), Join(albums), Join(titles));

        /// <summary>Several values joined with ", ", case-insensitive duplicates dropped, first spelling kept.</summary>
        private static string Join(IEnumerable<string?> values)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<string>();
            foreach (var v in values)
            {
                string s = Sanitize(v);
                if (s.Length > 0 && seen.Add(s)) kept.Add(s);
            }
            return Cap(string.Join(", ", kept));
        }

        /// <summary>Control characters become spaces, whitespace runs collapse, trimmed, capped with "…".</summary>
        public static string Sanitize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var sb = new StringBuilder(value.Length);
            bool space = false;
            foreach (char c in value)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    if (!space && sb.Length > 0) sb.Append(' ');
                    space = true;
                }
                else
                {
                    sb.Append(c);
                    space = false;
                }
            }
            return Cap(sb.ToString().Trim());
        }

        // Never splits a surrogate pair (an emoji), which would make the cache unsavable.
        private static string Cap(string s) =>
            s.Length <= MaxLength ? s : s[..(char.IsHighSurrogate(s[MaxLength - 1]) ? MaxLength - 1 : MaxLength)] + "…";
    }
}
