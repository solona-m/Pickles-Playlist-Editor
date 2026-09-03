using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// What Penumbra is installed, read off disk rather than asked over the API.
    ///
    /// Its HTTP API has no version endpoint, and it is only reachable while the game is running —
    /// which is exactly not the case for someone editing playlists between sessions and then filing a
    /// bug report. Dalamud's own plugin manifest is on disk either way.
    ///
    /// This matters more than a version line usually does, because Penumbra's mod layout is a moving
    /// target: it went to v4 (everything in one meta.json) and then reversed course, and this app has
    /// to read and write whichever it finds. A log that records the mod's layout but not the version
    /// of the program that chose that layout makes those reports guesswork.
    ///
    /// Best-effort throughout. Every failure resolves to "unknown", never to an exception: this runs
    /// on the startup path and nothing here is worth a millisecond of the user's launch, let alone a
    /// crash.
    /// </summary>
    internal static class PenumbraInstall
    {
        private const string PluginName = "Penumbra";

        /// <summary>What one installed copy of Penumbra says about itself.</summary>
        internal sealed record PenumbraVersion(string Version, bool Testing, bool Disabled, string Source);

        /// <summary>
        /// The installed Penumbra, or null if none can be found.
        ///
        /// Dalamud keeps one folder per installed version and does not always prune the old ones, so
        /// the highest version present is the answer — not the first, and not the newest by write
        /// time, which is whichever folder Dalamud last touched for any reason.
        /// </summary>
        internal static PenumbraVersion? Detect()
        {
            try
            {
                var found = new List<PenumbraVersion>();

                // devPlugins wins on ties below: a locally built Penumbra is what Dalamud loads, and
                // it is also the copy most likely to be behind a report of odd behaviour.
                Collect(found, Path.Combine(DalamudRoot, "installedPlugins", PluginName), "installed");
                Collect(found, Path.Combine(DalamudRoot, "devPlugins", PluginName), "dev");

                // Version first, source only as the tie-break. Ordering by source first made ANY
                // devPlugins copy outrank every installed one — a stale devPlugins\Penumbra\0.9.0
                // left behind from a debugging session would have this report 0.9.0 while 1.7 is what
                // is actually loaded, which is worse than no version line at all.
                return found
                    .OrderBy(p => Parse(p.Version))
                    .ThenBy(p => p.Source == "dev" ? 1 : 0)
                    .LastOrDefault();
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Could not determine the installed Penumbra version: {Error}", ex.Message);
                return null;
            }
        }

        private static string? s_cachedDescription;

        /// <summary>
        /// <see cref="DescribeForLog"/>, resolved once and kept.
        ///
        /// For naming the Penumbra build inside incident lines — a failed reload, a mod folder that
        /// changed underneath us — rather than only in the startup line. A report arrives as a couple
        /// of thousand lines and the boot line can be a long way from the incident: in the log that
        /// prompted this, the reversion was at line 2042 and the last startup at 1870. Putting the
        /// version on the line that matters makes "which Penumbra did this" a single grep.
        ///
        /// Cached because those lines can repeat often and each resolve reads the plugin manifest off
        /// disk. The installed version cannot change without restarting the game anyway.
        /// </summary>
        internal static string CachedDescription =>
            s_cachedDescription ??= DescribeForLog();

        /// <summary>
        /// One line for the startup log. Always returns something printable — "unknown" is itself a
        /// useful datum in a report, since it means Penumbra is not installed where this app looks.
        /// </summary>
        internal static string DescribeForLog()
        {
            var found = Detect();
            if (found == null)
                return $"unknown (no plugin manifest under '{Path.Combine(DalamudRoot, "installedPlugins", PluginName)}')";

            var notes = new List<string>();
            if (found.Testing) notes.Add("testing build");
            if (found.Disabled) notes.Add("DISABLED");
            if (found.Source == "dev") notes.Add("dev plugin");

            return notes.Count > 0
                ? $"{found.Version} ({string.Join(", ", notes)})"
                : found.Version;
        }

        /// <summary>
        /// Where Dalamud keeps its plugins and their configs. Shared with
        /// <see cref="PenumbraApi.GetPenumbraDirectory"/> so the two places that reach into
        /// XIVLauncher's folder cannot drift apart — which is how the config path there went stale
        /// through a Penumbra release in the first place.
        /// </summary>
        internal static string DalamudRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher");

        private static void Collect(List<PenumbraVersion> into, string pluginRoot, string source)
        {
            if (!Directory.Exists(pluginRoot)) return;

            foreach (var dir in new DirectoryInfo(pluginRoot).GetDirectories())
            {
                var manifest = ReadManifest(Path.Combine(dir.FullName, PluginName + ".json"));

                // The folder name is the version, so a missing or unreadable manifest still yields
                // the number — it just cannot say whether the build is a testing one.
                string version = manifest?["AssemblyVersion"]?.ToString() is { Length: > 0 } declared
                    ? declared
                    : dir.Name;

                into.Add(new PenumbraVersion(
                    version,
                    manifest?["Testing"]?.Value<bool>() ?? false,
                    manifest?["Disabled"]?.Value<bool>() ?? false,
                    source));
            }
        }

        private static JObject? ReadManifest(string path)
        {
            try
            {
                return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        // Ordering by the parsed version, not the string: "1.7.0.10" sorts before "1.7.0.9"
        // alphabetically, which would report the wrong copy as the installed one.
        private static Version Parse(string version) =>
            Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0);
    }
}
