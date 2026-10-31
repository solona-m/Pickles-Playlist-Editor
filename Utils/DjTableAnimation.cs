using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// The app's side of the animated table: where the backups live, how files and the manifest
    /// are written, and telling Penumbra afterwards. The logic is in <see cref="DjTableAnimationCore"/>.
    /// </summary>
    internal static class DjTableAnimation
    {
        private sealed class Host(string modRoot, string modName) : IAnimationHost
        {
            public string StateFolder { get; } = StateFolderFor(modName);

            public byte[] ReadFile(string full) => File.ReadAllBytes(full);

            public void WriteFile(string full, byte[] bytes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                PenumbraMeta.AtomicWrite(full, bytes);
            }

            public void DeleteFile(string full)
            {
                if (File.Exists(full)) File.Delete(full);
            }

            public void DeleteDirectory(string full)
            {
                if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            }

            /// <summary>Only groups the edit actually changes are written, each through DanceGroupIO.</summary>
            public void EditGroups(Func<JObject, bool> edit)
            {
                foreach (var group in DanceGroupIO.ListGroups(modRoot, modName))
                {
                    var probe = DanceGroupIO.Load(group);
                    if (probe == null || !edit((JObject)probe.DeepClone())) continue;
                    DanceGroupIO.Mutate(group, json => edit(json));
                }
            }

            public string NewToken() => DjTableAnimationCore.RandomToken();
        }

        static DjTableAnimation()
        {
            DjTableAnimationCore.Failed = ex => Logger.LogError("Changing the table animation failed: {Error}", ex);
            DjTableAnimationCore.OverlaySkipped = reason => Logger.LogInfo("Table frames: full-atlas copies ({Reason}).", reason);
        }

        public static string StateFolderFor(string modName) => Path.Combine(
            Playlist.BackupDir, "textures", PenumbraMeta.SnapshotFolderNameForMod(modName), "animation");

        public static string ModRoot(DjTextureTarget table) =>
            table.FullPath[..^table.Relative.Length].TrimEnd('\\', '/');

        public static DjTableAnimationSite Scan(DjTextureTarget table) =>
            DjAnimationScan.Inspect(ModRoot(table), table, StateFolderFor(table.ModName));

        public static bool Apply(DjTableAnimationSite site, IReadOnlyList<BgraImage> frames, FrameTimeline timeline,
            FitMode fit, IProgress<int>? progress, out IReadOnlyList<string> warnings, out string error)
        {
            var host = new Host(site.ModRoot, site.Table.ModName);
            bool ok = DjTableAnimationCore.Apply(host, site, frames, timeline, fit, progress, out warnings, out error);
            string route = "unknown";
            try { route = AnimationState.Parse(File.ReadAllBytes(DjAnimationScan.StatePath(host.StateFolder))).FrameRect != null ? "overlay" : "full-atlas"; }
            catch { }
            Logger.LogInfo("Animated table in {Mod}: {Frames} frames, ok={Ok}, route={Route}.", site.Table.ModName, frames.Count, ok, route);
            if (!ok) Logger.LogWarn("Animating the table in {Mod} failed: {Error}", site.Table.ModName, error);
            warnings = Settle(site, warnings);
            return ok;
        }

        public static bool Restore(DjTableAnimationSite site, out IReadOnlyList<string> warnings, out string error)
        {
            var host = new Host(site.ModRoot, site.Table.ModName);
            bool ok = DjTableAnimationCore.Restore(host, site, out warnings, out error);
            Logger.LogInfo("Restored the table animation in {Mod}: ok={Ok}.", site.Table.ModName, ok);
            if (!ok) Logger.LogWarn("Restoring the table in {Mod} failed: {Error}", site.Table.ModName, error);
            warnings = Settle(site, warnings);
            return ok;
        }

        /// <summary>A still on a table this app animated by appending: paste into eq55, then remove the animation.</summary>
        public static bool PasteThenRestore(DjTableAnimationSite site, BgraImage picture, FitMode fit,
            out IReadOnlyList<string> warnings, out string error)
        {
            var host = new Host(site.ModRoot, site.Table.ModName);
            bool ok = DjTableAnimationCore.PasteThenRestore(host, site,
                () => DjTextures.Apply(site.Table, picture, fit, out _, out string pasteError) ? null : pasteError,
                out warnings, out error);
            if (!ok) Logger.LogWarn("A picture on the animated table in {Mod} failed: {Error}", site.Table.ModName, error);
            warnings = Settle(site, warnings);
            return ok;
        }

        /// <summary>Reloads the mod in Penumbra, then checks the frame keys survived it.</summary>
        private static IReadOnlyList<string> Settle(DjTableAnimationSite site, IReadOnlyList<string> warnings)
        {
            DjTextures.Forget();
            DjTextures.Reload(site.Table.ModName);

            var result = warnings.ToList();
            try
            {
                var state = DjAnimationScan.TryReadState(site.StateFolder);
                if (state is { Phase: AnimationState.Applied } && state.Frames.Count > 0)
                {
                    var groups = DanceGroupIO.ListGroups(site.ModRoot, site.Table.ModName)
                        .Select(DanceGroupIO.Load).OfType<JObject>().ToList();
                    var relatives = new HashSet<string>(state.Avfx.Select(a => a.Relative), StringComparer.OrdinalIgnoreCase);
                    string? lost = DjTableAnimationCore.VerifyManifest(groups, relatives,
                        state.Frames.ToDictionary(f => f.GamePath, f => f.Relative));
                    if (lost != null) result.Add(lost);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Checking the table frames after a reload failed: {Error}", ex.Message);
            }
            return result;
        }
    }
}
