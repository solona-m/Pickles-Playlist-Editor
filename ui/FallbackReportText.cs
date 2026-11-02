using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pickles_Playlist_Editor.Tools;

namespace Pickles_Playlist_Editor
{
    /// <summary>Localized templates for the YouTube match report; passed in so the report stays testable.</summary>
    internal sealed record FallbackReportFormats(string SubstitutedHeader, string SubstitutedEntry, string SkippedHeader,
        string SkippedEntry, string More, IReadOnlyDictionary<FallbackSkipReason, string> Reasons);

    internal static class FallbackReportText
    {
        internal const int MaxPerSection = 15;

        public static string Compose(IReadOnlyList<FallbackTrackResult> results, FallbackReportFormats f)
        {
            var sb = new StringBuilder();
            var used = results.Where(r => r.Substituted).ToList();
            var left = results.Where(r => !r.Substituted).ToList();

            if (used.Count > 0)
            {
                sb.AppendLine(f.SubstitutedHeader);
                foreach (var r in used.Take(MaxPerSection))
                    sb.AppendLine(string.Format(f.SubstitutedEntry, r.SoundCloudTitle, r.YouTubeTitle));
                if (used.Count > MaxPerSection) sb.AppendLine(string.Format(f.More, used.Count - MaxPerSection));
            }

            if (left.Count > 0)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(f.SkippedHeader);
                foreach (var r in left.Take(MaxPerSection))
                    sb.AppendLine(string.Format(f.SkippedEntry, r.SoundCloudTitle,
                        f.Reasons.TryGetValue(r.Reason, out var reason) ? reason : r.Reason.ToString()));
                if (left.Count > MaxPerSection) sb.AppendLine(string.Format(f.More, left.Count - MaxPerSection));
            }

            return sb.ToString().TrimEnd();
        }
    }
}
