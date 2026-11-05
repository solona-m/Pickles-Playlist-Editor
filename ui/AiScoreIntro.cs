using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    internal enum AiIntroResult { Skipped, NotShown, Dismissed, Declined, Accepted }

    /// <summary>How the intro dialog closed: Opened is false when it collided with another dialog.</summary>
    internal readonly record struct AiIntroShown(bool Opened, bool Ok, bool Checked);

    /// <summary>What the startup intro needs from the window. Data only, so the flow is testable without WinUI.</summary>
    internal interface IAiScoreIntroHost
    {
        bool AiChosenBefore { get; }
        bool Answered { get; }
        bool Closing { get; }
        void MarkAnswered();
        Task<AiIntroShown> ShowAsync();
        Task<AiOptInOutcome?> EnableAsync(); // null when the opt-in threw
    }

    /// <summary>The one-time startup dialog that offers the AI score. Shown until the user clicks OK.</summary>
    internal static class AiScoreIntro
    {
        public static bool ShouldShow(bool chosenBefore, bool answered) => !chosenBefore && !answered;

        public static AiIntroResult Classify(AiIntroShown s) =>
            !s.Opened ? AiIntroResult.NotShown : !s.Ok ? AiIntroResult.Dismissed : s.Checked ? AiIntroResult.Accepted : AiIntroResult.Declined;

        /// <summary>NotFound means the files aren't on the server yet: keep the offer for next launch.</summary>
        public static bool MarksAfterOptIn(AiOptInOutcome? outcome, bool closing) => !closing && outcome != AiOptInOutcome.NotFound;

        public static async Task<AiIntroResult> RunAsync(IAiScoreIntroHost host)
        {
            if (host.AiChosenBefore)
            {
                if (!host.Answered) host.MarkAnswered();
                return AiIntroResult.Skipped;
            }
            if (!ShouldShow(false, host.Answered)) return AiIntroResult.Skipped;

            var result = Classify(await host.ShowAsync());
            if (result == AiIntroResult.Declined) host.MarkAnswered();
            else if (result == AiIntroResult.Accepted)
            {
                AiOptInOutcome? outcome = null;
                try { outcome = await host.EnableAsync(); }
                finally { if (MarksAfterOptIn(outcome, host.Closing)) host.MarkAnswered(); }
            }
            return result;
        }
    }
}
