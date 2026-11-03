using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;

namespace Pickles_Playlist_Editor
{
    /// <summary>Which resw string describes an AI score outcome or band. Pure, so every key can be checked in tests.</summary>
    internal static class AiScoreText
    {
        public static string BandKey(AiBand band) => band switch
        {
            AiBand.LikelyAi => "AiScore_Band_Ai",
            AiBand.PossiblyAi => "AiScore_Band_PossiblyAi",
            AiBand.LikelyHuman => "AiScore_Band_Human",
            _ => "AiScore_Band_Unclear",
        };

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
