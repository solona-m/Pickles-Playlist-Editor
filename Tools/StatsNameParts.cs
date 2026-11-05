using Pickles_Playlist_Editor.Utils;
using System;
using System.Text.RegularExpressions;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>
    /// A song name split into its base and the stats parts it already carries, each kept verbatim. Used by the
    /// automatic run, which only ever adds missing parts: hand-corrected values and tags like "[Remix]" survive.
    /// </summary>
    internal readonly record struct StatsNameParts(string Base, string? BpmText, string? KeyText, string? LengthText,
        bool HasBpm, bool HasKey, bool HasLength)
    {
        private static readonly Regex TrailingLength = new(@" \(\d{2}:\d{2}:\d{2}\)$");
        private static readonly Regex TrailingBracket = new(@" \[([^\[\]]+)\]$");
        private static readonly Regex TrailingBpm = new(@" \(\d+ BPM\)$");
        private static readonly Regex AnyLength = new(@"\(\d{2}:\d{2}:\d{2}\)");
        private static readonly Regex AnyBracket = new(@"\[([^\[\]]+)\]");
        private static readonly Regex AnyBpm = new(@"\(\d+ BPM\)");
        private static readonly Regex CamelotCode = new(@"^(1[0-2]|[1-9])[AB]$");

        /// <summary>Peels the suffix in the order StripSuffix uses; a trailing [...] only counts if it reads as a key.</summary>
        public static StatsNameParts Parse(string name)
        {
            string s = name ?? "";
            string? length = null, key = null, bpm = null;
            var m = TrailingLength.Match(s);
            if (m.Success) { length = m.Value; s = s[..m.Index]; }
            m = TrailingBracket.Match(s);
            if (m.Success && IsKeyBracket(m.Groups[1].Value)) { key = m.Value; s = s[..m.Index]; }
            m = TrailingBpm.Match(s);
            if (m.Success) { bpm = m.Value; s = s[..m.Index]; }

            bool keyInBase = false;
            foreach (Match b in AnyBracket.Matches(s))
                if (IsKeyBracket(b.Groups[1].Value)) { keyInBase = true; break; }
            return new StatsNameParts(s, bpm, key, length,
                bpm != null || AnyBpm.IsMatch(s), key != null || keyInBase, length != null || AnyLength.IsMatch(s));
        }

        /// <summary>"8A Am", "3B C# Major", "Am" or "11A" read as a key; "Remix" and "Official Video" don't.</summary>
        public static bool IsKeyBracket(string inner)
        {
            string t = (inner ?? "").Trim();
            if (t.Length == 0) return false;
            string rest = t;
            int space = t.IndexOf(' ');
            string first = space < 0 ? t : t[..space];
            if (CamelotCode.IsMatch(first)) rest = space < 0 ? "" : t[(space + 1)..].Trim();
            if (rest.Length == 0) return rest != t; // a bare code
            return Camelot.Code(rest) != null;
        }

        /// <summary>The name with only the missing (enabled, known) parts added, in their usual places.</summary>
        public static string AddMissing(StatsNameParts p, int? bpm, string? key, TimeSpan? length, OptionStatsNaming.Parts parts) =>
            p.Base
            + (p.BpmText ?? (p.HasBpm ? "" : OptionStatsNaming.BpmPart(bpm, parts)))
            + (p.KeyText ?? (p.HasKey ? "" : OptionStatsNaming.KeyPart(key, parts)))
            + (p.LengthText ?? (p.HasLength ? "" : OptionStatsNaming.LengthPart(length, parts)));
    }
}
