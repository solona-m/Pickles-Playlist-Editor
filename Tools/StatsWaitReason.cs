using System.Collections.Generic;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>Why a stats song is still waiting for its turn, for the log. Pure.</summary>
    internal static class StatsWaitReason
    {
        public static string Describe(bool overlay, bool dialogPopup, bool dialogGateHeld, bool libraryGateHeld, string? holder)
        {
            var parts = new List<string>();
            if (overlay) parts.Add("busy overlay");
            if (dialogPopup) parts.Add("dialog open");
            if (dialogGateHeld) parts.Add("dialog gate held");
            if (libraryGateHeld) parts.Add("library gate held by " + (string.IsNullOrEmpty(holder) ? "unknown" : holder));
            return parts.Count == 0 ? "nothing found busy" : string.Join(", ", parts);
        }
    }
}
