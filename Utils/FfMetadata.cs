using System.Collections.Generic;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Reads the global section of ffmpeg's ffmetadata output. A value ending in an unescaped "\" continues on the next
    /// line whatever that line starts with, so lyrics full of "[Verse]" lines don't end the section early. Never throws.
    /// </summary>
    internal static class FfMetadata
    {
        public static IReadOnlyList<KeyValuePair<string, string>> ParseGlobal(string text)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(text)) return result;
            var lines = text.Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                string line = StripCr(lines[i++]);
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[') break; // a stream or chapter section: the global one is over
                var raw = new StringBuilder(line);
                while (EndsWithContinuation(raw) && i < lines.Length)
                {
                    raw.Length--; // the continuation backslash
                    raw.Append('\n').Append(StripCr(lines[i++]));
                }
                if (Split(raw.ToString()) is { } pair) result.Add(pair);
            }
            return result;
        }

        private static string StripCr(string s) => s.EndsWith('\r') ? s[..^1] : s;

        // An odd number of trailing backslashes: the last one escapes the newline.
        private static bool EndsWithContinuation(StringBuilder s)
        {
            int n = 0;
            for (int k = s.Length - 1; k >= 0 && s[k] == '\\'; k--) n++;
            return n % 2 == 1;
        }

        private static KeyValuePair<string, string>? Split(string raw)
        {
            var key = new StringBuilder();
            int k = 0;
            for (; k < raw.Length; k++)
            {
                char c = raw[k];
                if (c == '\\' && k + 1 < raw.Length) { key.Append(raw[++k]); continue; }
                if (c == '=') break;
                key.Append(c);
            }
            if (k >= raw.Length) return null; // no "="
            var value = new StringBuilder();
            for (k++; k < raw.Length; k++)
            {
                char c = raw[k];
                if (c == '\\' && k + 1 < raw.Length) { value.Append(raw[++k]); continue; }
                value.Append(c);
            }
            return new KeyValuePair<string, string>(key.ToString(), value.ToString());
        }
    }
}
