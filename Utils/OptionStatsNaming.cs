using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Pickles_Playlist_Editor.Utils
{
    internal static class OptionStatsNaming
    {
        private static readonly Regex DurationSuffixPattern = new(@" \(\d{2}:\d{2}:\d{2}\)$");
        private static readonly Regex KeySuffixPattern = new(@" \[[^\[\]]+\]$");
        private static readonly Regex BpmSuffixPattern = new(@" \(\d+ BPM\)$");

        internal static string StripSuffix(string name)
        {
            name = DurationSuffixPattern.Replace(name ?? "", "");
            name = KeySuffixPattern.Replace(name, "");
            name = BpmSuffixPattern.Replace(name, "");
            return name;
        }

        /// <summary>
        /// Which parts of the stats a name carries. Each getter on Settings opens the registry, so
        /// passes over the whole library read them once and hand the result down.
        /// </summary>
        internal readonly record struct Parts(bool Bpm, bool Key, bool Camelot, bool Length)
        {
            internal static Parts FromSettings() => new(
                Settings.ShowBpmInName, Settings.ShowKeyInName,
                Settings.ShowCamelotInName, Settings.ShowLengthInName);
        }

        internal static void UpdateName(Option option, int? bpm, string? key, TimeSpan? duration)
            => UpdateName(option, bpm, key, duration, Parts.FromSettings());

        /// <summary>
        /// Rebuilds the name's stats suffix. Appending in the same order StripSuffix removes means
        /// any subset of the parts still round-trips, so the Camelot code shares the key's brackets
        /// rather than adding a fourth segment.
        /// </summary>
        internal static void UpdateName(Option option, int? bpm, string? key, TimeSpan? duration, Parts parts)
        {
            string baseName = StripSuffix(option.Name);

            if (parts.Bpm && bpm.HasValue && bpm.Value > 0)
                baseName += $" ({bpm.Value} BPM)";

            // Normalized, so a song whose cache entry predates the current spelling reads
            // "[3B C#]" rather than "[3B C# Major]" beside its neighbours.
            string? shortKey = Camelot.Normalize(key);
            var keyParts = new List<string>(2);
            if (parts.Camelot && Camelot.Code(key) is string code) keyParts.Add(code);
            if (parts.Key && !string.IsNullOrEmpty(shortKey)) keyParts.Add(shortKey);
            if (keyParts.Count > 0) baseName += $" [{string.Join(' ', keyParts)}]";

            if (parts.Length && duration.HasValue && duration.Value > TimeSpan.Zero)
                baseName += $" ({(int)duration.Value.TotalHours:D2}:{duration.Value.Minutes:D2}:{duration.Value.Seconds:D2})";

            option.Name = baseName;
        }
    }
}
