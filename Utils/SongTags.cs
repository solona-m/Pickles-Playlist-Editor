using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// A song's artist, album and title, from the Vorbis comments in its SCD or, for mp3/m4a/flac/wav imports, from the
    /// source file. Title is kept for later features and is never shown or searched. AiTool: the AI generator the file's
    /// own tags name ("Suno"/"Udio"), or "". MarkerText: the source file's comment-type fields, kept so the marker
    /// vocabulary can be re-applied without the source. All empty means "read, nothing found".
    /// </summary>
    internal readonly record struct SongTags(string Artist, string Album, string Title, string AiTool = "", string MarkerText = "")
    {
        public const int MaxLength = 200;

        public static SongTags None => new("", "", "");

        public bool IsEmpty => Artist.Length == 0 && Album.Length == 0 && Title.Length == 0 && AiTool.Length == 0 && MarkerText.Length == 0;

        public bool HasArtistOrAlbum => Artist.Length > 0 || Album.Length > 0;

        public bool IsAiTagged => AiTool.Length > 0;

        public static SongTags From(IEnumerable<string?> artists, IEnumerable<string?> albums, IEnumerable<string?> titles,
            string aiTool = "", string markerText = "") =>
            new(Join(artists), Join(albums), Join(titles), aiTool ?? "", markerText ?? "");

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
        private static string Cap(string s) => s.Length <= MaxLength ? s : SafeCut(s, MaxLength) + "…";

        /// <summary>At most <paramref name="max"/> chars, never ending on half a surrogate pair.</summary>
        internal static string SafeCut(string s, int max) =>
            s.Length <= max ? s : s[..(char.IsHighSurrogate(s[max - 1]) ? max - 1 : max)];
    }
}
