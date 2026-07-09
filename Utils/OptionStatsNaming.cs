using System;
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

        internal static void UpdateName(Option option, int? bpm, string? key, TimeSpan? duration)
        {
            string baseName = StripSuffix(option.Name);
            if (bpm.HasValue && bpm.Value > 0) baseName += $" ({bpm.Value} BPM)";
            if (!string.IsNullOrEmpty(key)) baseName += $" [{key}]";
            if (duration.HasValue && duration.Value > TimeSpan.Zero)
                baseName += $" ({(int)duration.Value.TotalHours:D2}:{duration.Value.Minutes:D2}:{duration.Value.Seconds:D2})";
            option.Name = baseName;
        }
    }
}
