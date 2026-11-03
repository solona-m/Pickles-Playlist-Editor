using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Finds a file's own statement that an AI generator made it ("made with suno; created=…"), in comment-type tags only:
    /// an artist, album or title named "Suno" never counts. Linear-time regexes, so hostile tags can't stall a read.
    /// </summary>
    internal static class AiGeneratorMarker
    {
        public const int MaxValue = 512, MaxFields = 8, MaxText = 4096;

        // Ordered: comment and description first, then the rest.
        private static readonly string[] KeyTokens = ["comment", "descr", "encoded", "software", "publisher", "organi", "copyright", "licen"];
        private static readonly string[] NeverKeys = ["artist", "album", "title", "album_artist", "genre", "encoder"];

        private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        private static readonly Regex MadeWith = new(@"\b(made|created|generated)\s+(with|by|using)\s+(suno|udio)\b", Opts);
        private static readonly Regex Domain = new(@"\b(suno|udio)\.(com|ai)\b", Opts);

        /// <summary>"Suno", "Udio" or "".</summary>
        public static string Detect(IEnumerable<KeyValuePair<string, string>> fields)
        {
            foreach (var (_, value) in Qualifying(fields))
            {
                string v = SongTags.SafeCut(value, MaxValue);
                foreach (var tool in new[] { "Suno", "Udio" })
                    if (Says(v, tool)) return tool;
            }
            return "";
        }

        /// <summary>The qualifying fields as escaped key=value lines, capped; "" when none qualify.</summary>
        public static string MarkerText(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var (key, value) in Qualifying(fields))
            {
                if (count == MaxFields) break;
                string line = EscapeKey(key) + "=" + EscapeValue(SongTags.SafeCut(value, MaxValue));
                if (sb.Length + line.Length + 1 > MaxText) break; // whole lines only
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
                count++;
            }
            return sb.ToString();
        }

        /// <summary><see cref="Detect"/> over text built by <see cref="MarkerText"/>.</summary>
        public static string DetectText(string markerText)
        {
            if (string.IsNullOrEmpty(markerText)) return "";
            var fields = new List<KeyValuePair<string, string>>();
            foreach (var line in markerText.Split('\n'))
            {
                int eq = UnescapedEquals(line);
                if (eq < 0) continue;
                fields.Add(new(Unescape(line[..eq]), Unescape(line[(eq + 1)..])));
            }
            return Detect(fields);
        }

        private static bool Says(string value, string tool)
        {
            foreach (Match m in MadeWith.Matches(value))
                if (m.Groups[3].Value.Equals(tool, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (Match m in Domain.Matches(value))
                if (m.Groups[1].Value.Equals(tool, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static IEnumerable<KeyValuePair<string, string>> Qualifying(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var list = fields.Where(f => f.Key != null && f.Value != null
                && !NeverKeys.Any(k => f.Key.Trim().Equals(k, StringComparison.OrdinalIgnoreCase))
                && KeyRank(f.Key) >= 0
                && !f.Value.StartsWith("Lavf", StringComparison.Ordinal) && !f.Value.StartsWith("Lavc", StringComparison.Ordinal)).ToList();
            return list.Select((f, i) => (f, i)).OrderBy(x => KeyRank(x.f.Key)).ThenBy(x => x.i).Select(x => x.f);
        }

        private static int KeyRank(string key)
        {
            for (int i = 0; i < KeyTokens.Length; i++)
                if (key.Contains(KeyTokens[i], StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string EscapeKey(string k) => k.Replace("\\", "\\\\").Replace("=", "\\=").Replace("\n", "\\n").Replace("\r", "\\r");
        private static string EscapeValue(string v) => v.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");

        private static int UnescapedEquals(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\') { i++; continue; }
                if (line[i] == '=') return i;
            }
            return -1;
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char n = s[++i];
                sb.Append(n switch { 'n' => '\n', 'r' => '\r', _ => n });
            }
            return sb.ToString();
        }
    }
}
