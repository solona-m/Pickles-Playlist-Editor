using System;
using System.Reflection;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Single source of truth for the running build's version and release channel.
    ///
    /// Testing builds published by .github/workflows/testing-release.yml carry a SemVer
    /// prerelease suffix (e.g. "2.5.1-testing.42"). AssemblyVersion cannot represent that —
    /// it is restricted to four numeric parts — so the suffix survives only in
    /// InformationalVersion.
    ///
    /// InformationalVersion is deliberately NOT trusted unconditionally: the csproj sets
    /// AssemblyVersion but never &lt;Version&gt;, so a local or hand-packed build reports the
    /// SDK default "1.0.0" there. Preferring it outright would label every developer build
    /// "1.0.0". A prerelease suffix — which only the testing pipeline ever produces — is
    /// what makes it the better source.
    /// </summary>
    internal static class AppVersion
    {
        /// <summary>Version to show the user, e.g. "2.5.1" or "2.5.1-testing.42".</summary>
        public static string Display { get; }

        /// <summary>
        /// True when this build came from the testing pipeline. Drives whether the updater
        /// follows the prerelease channel, so a test build keeps receiving test builds while
        /// a stable build never sees them.
        /// </summary>
        public static bool IsPrerelease { get; }

        static AppVersion()
        {
            string numeric = "";
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null) numeric = $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch
            {
                // Fall through to the informational version below.
            }

            string? informational = null;
            try
            {
                informational = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
            }
            catch
            {
                // Version reporting must never be the reason startup fails.
            }

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Strip the "+<commit sha>" build metadata the .NET SDK appends by default.
                int plus = informational!.IndexOf('+');
                if (plus >= 0) informational = informational.Substring(0, plus);
                informational = informational.Trim();
            }

            // A '-' is a SemVer prerelease suffix, i.e. a testing build.
            bool prerelease = !string.IsNullOrEmpty(informational) && informational!.Contains('-');

            IsPrerelease = prerelease;
            Display = prerelease ? informational! : numeric;
        }
    }
}
