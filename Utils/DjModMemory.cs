using System.IO;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>The settings the DJ mod dialogs remember; the registry in the app, a fake in the harness.</summary>
    internal interface IDjModSettings
    {
        string DanceModName { get; set; }
        string DanceGroupId { get; set; }
        string PicturesModName { get; set; }
    }

    /// <summary>
    /// Which mod the Dances and Pictures dialogs open on, and when a pick is remembered. Pictures keeps
    /// its own choice so picking table art in a second pack never repoints the dances.
    /// </summary>
    internal static class DjModMemory
    {
        /// <summary>First non-blank folder that exists under the root; null if none or the root is blank.</summary>
        public static string? FirstExisting(string penumbraRoot, params string?[] folders)
        {
            if (string.IsNullOrWhiteSpace(penumbraRoot)) return null;
            foreach (var folder in folders)
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(Path.Combine(penumbraRoot, folder)))
                    return folder;
            return null;
        }

        public static string? PicturesOpening(string penumbraRoot, IDjModSettings s) =>
            FirstExisting(penumbraRoot, s.PicturesModName, s.DanceModName);

        public static string? DancesOpening(string penumbraRoot, IDjModSettings s) =>
            FirstExisting(penumbraRoot, s.DanceModName);

        /// <summary>The picker's tie-break: the Pictures mod if set, else the Dances mod.</summary>
        public static string PicturesPreferred(IDjModSettings s) =>
            !string.IsNullOrWhiteSpace(s.PicturesModName) ? s.PicturesModName : s.DanceModName ?? string.Empty;

        /// <summary>True = wrote PicturesModName: a picker pick that loaded at least one picture and differs.</summary>
        public static bool PicturesLoaded(IDjModSettings s, string folder, bool picked, int targetCount)
        {
            if (!picked || targetCount <= 0 || s.PicturesModName == folder) return false;
            s.PicturesModName = folder;
            return true;
        }

        /// <summary>True = pick accepted, even if nothing changed. False = no dances group (null); nothing written.</summary>
        public static bool DancesPicked(IDjModSettings s, string folder, string? groupId)
        {
            if (groupId == null) return false;
            if (s.DanceModName != folder) s.DanceModName = folder;
            if (s.DanceGroupId != groupId) s.DanceGroupId = groupId;
            return true;
        }
    }
}
