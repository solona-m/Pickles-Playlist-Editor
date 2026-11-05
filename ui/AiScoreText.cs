using System.Linq;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;

namespace Pickles_Playlist_Editor
{
    /// <summary>Which resw string describes an AI score outcome or band. Pure, so every key can be checked in tests.</summary>
    internal static class AiScoreText
    {
        /// <summary>The download size in the Settings and intro hint, in MB rounded up.</summary>
        public static long DownloadMb => (AiAssets.TotalBytes + 999_999) / 1_000_000;

        public static string BandKey(AiBand band) => band switch
        {
            AiBand.CertainAi => "AiScore_Band_Certain",
            AiBand.LikelyAi => "AiScore_Band_Ai",
            AiBand.PossiblyAi => "AiScore_Band_PossiblyAi",
            AiBand.LikelyHuman => "AiScore_Band_Human",
            _ => "AiScore_Band_Unclear",
        };

        public const string TaggedKey = "AiScore_Tagged", TooltipTaggedKey = "AiScore_TooltipTagged";

        /// <summary>Amber and pale amber for model estimates (an estimate, not an accusation); red only for a file's own generator tag.</summary>
        public static uint ChipArgb(AiBand band) => band switch
        {
            AiBand.CertainAi => 0xFFFF6B6B,
            AiBand.LikelyAi => 0xFFF2A33A,
            _ => 0xFFF5D08A,
        };

        /// <summary>The chip's tooltip: "AI likelihood: 100%." and the tagged line, or the model's usual line.</summary>
        public static string ChipTooltip(string? tool, int percent, System.Func<string, string> get) =>
            !string.IsNullOrEmpty(tool)
                ? string.Format(get(TooltipTaggedKey), AiScoreDisplay.TaggedPercent) + "\n" + string.Format(get(TaggedKey), tool)
                : string.Format(get("AiScore_Tooltip"), percent);

        /// <summary>What a screen reader says for the chip; tagged lines are joined with "; " and lose their full stops.</summary>
        public static string AutomationName(string text, string tooltip, bool tagged) =>
            tagged
                ? string.Join("; ", new[] { text }.Concat(tooltip.Split('\n').Select(l => l.Trim().TrimEnd('.', '。'))).Where(l => l.Length > 0))
                : text + ", " + tooltip;

        /// <summary>The reason shown when the download failed; null for outcomes that are not failures.</summary>
        public static string? FailureKey(AiOptInOutcome outcome, AiAssetFailure failure) => outcome switch
        {
            AiOptInOutcome.Cancelled => "AiScore_Fail_Cancelled",
            AiOptInOutcome.NotFound => "AiScore_Fail_NotFound",
            AiOptInOutcome.Failed => failure switch
            {
                AiAssetFailure.Checksum => "AiScore_Fail_Checksum",
                AiAssetFailure.Transport => "AiScore_Fail_Transport",
                AiAssetFailure.Disk => "AiScore_Fail_Disk",
                AiAssetFailure.NotFound => "AiScore_Fail_NotFound",
                _ => "AiScore_Fail_Http",
            },
            _ => null,
        };
    }
}
