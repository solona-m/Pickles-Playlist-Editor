using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;

namespace AnimHarness;

/// <summary>The app writes overlay frames (#12 step 2), falling back to full-atlas copies.</summary>
internal static partial class Program
{
    private const string OApp = "gh12-overlay-app";

    private static string AppTag(string ids) =>
        string.Join(" ", ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(id => id[0] == 'A'
            ? $"AC-{OApp}-{id[2..]}" : $"EDGE-{OApp}-{id[2..]}"));

    /// <summary>Runs <paramref name="body"/> on the full-atlas route, as the previous version wrote.</summary>
    private static T WithoutOverlay<T>(Func<T> body) => WithPlanner((_, _) => null, body);

    private static T WithPlanner<T>(Func<DjTableAnimationSite, byte[], OverlayRoute?> planner, Func<T> body)
    {
        var old = DjTableAnimationCore.OverlayPlanner;
        DjTableAnimationCore.OverlayPlanner = planner;
        try { return body(); }
        finally { DjTableAnimationCore.OverlayPlanner = old; }
    }

    private static void RegisterOverlayApp()
    {
    }
}
