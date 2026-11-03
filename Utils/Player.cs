using Pickles_Playlist_Editor.Tools;
using System;
using Pickles_Playlist_Editor.Utils;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    internal static class Player
    {
        private static readonly PlaybackEngine s_engine = new(() => new WaveOutAudioOutput());
        private static readonly object _playerLock = new object();

        public static bool IsPlaying => s_engine.IsPlaying;
        public static bool IsPaused => s_engine.IsPaused;

        private static string? extractedTempOggPath;

        /// <summary>True while no Play or Stop has happened since the session with this generation started.</summary>
        public static bool IsCurrent(long generation) => s_engine.IsCurrent(generation);

        public static void Play(string filePath, Action<long>? onEnded = null)
        {
            Utils.Logger.LogInfo("Play: requested '{File}'", filePath);

            // Release any existing playback handle before writing a new temporary file.
            if (IsPlaying) Stop();

            CleanupExtractedTempFile();

            string extractedOgg = CreateExtractedOggPath(filePath, "now_playing");
            Utils.Logger.LogInfo("Play: extracting audio to '{Target}'", extractedOgg);

            ScdOggExtractor.ExtractOgg(filePath, extractedOgg);
            extractedTempOggPath = extractedOgg;

            long extractedBytes = new FileInfo(extractedOgg).Length;
            Utils.Logger.LogInfo("Play: extracted {Bytes} bytes", extractedBytes);
            if (extractedBytes == 0)
                throw new InvalidOperationException("The extracted audio was empty — the SCD may be corrupt or in an unsupported format.");

            PlayOgg(extractedOgg, onEnded);
        }

        /// <summary>
        /// Builds the path for the temporary decoded OGG.
        /// Deliberately NOT next to the source SCD: the SCD lives in the Penumbra mod
        /// directory, which may be read-only or on a drive the user can't write to (an
        /// UnauthorizedAccessException there took the whole app down), and writing into
        /// the mod folder also triggers Penumbra re-compaction.
        /// </summary>
        public static string CreateExtractedOggPath(string scdPath, string fileTag)
        {
            if (string.IsNullOrWhiteSpace(scdPath))
                throw new ArgumentException("SCD path is required.", nameof(scdPath));

            string baseName = Path.GetFileNameWithoutExtension(scdPath);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "audio";
            baseName = SanitizeForFileName(baseName);

            string suffix = string.IsNullOrWhiteSpace(fileTag) ? "preview" : fileTag;
            return Path.Combine(PlaybackTempDir, $"{baseName}_{suffix}_{Guid.NewGuid():N}.ogg");
        }

        // Matches exactly what the line above writes: "{name}_{tag}_{32 hex}.ogg". Anchored at the end
        // so a real song merely called "preview" is untouched — it takes the tag AND a full GUID in
        // the final position to qualify, which no track picks up by accident.
        private static readonly System.Text.RegularExpressions.Regex s_extractedNamePattern = new(
            @"_(?:now_playing|preview)_[0-9a-f]{32}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// True for a decoded preview this app wrote for playback.
        ///
        /// These are not songs. They are what <see cref="CreateExtractedOggPath"/> drops in the
        /// playback folder every time the user hits play, and <see cref="PurgeStalePlaybackFiles"/>
        /// clears at boot. Import has to know about them because nothing stops a user from pointing
        /// "New Playlist" at a folder that contains some — one did, and ended up with playlist entries
        /// named after a GUID. Recognized by NAME rather than by location, since by the time one has
        /// been copied somewhere else the folder no longer identifies it.
        ///
        /// The extension is deliberately not part of the test. These are written as .ogg, but once one
        /// has been imported the mod holds a .scd under the same name, and re-importing THAT — which is
        /// exactly what a second rebuild attempt does — would put the junk straight back. Nothing but
        /// this app produces a tag followed by a full GUID in the final position, whatever the
        /// extension, so matching the name alone cannot catch a real track.
        /// </summary>
        public static bool IsExtractedPlaybackFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            return s_extractedNamePattern.IsMatch(Path.GetFileNameWithoutExtension(path));
        }

        // %LOCALAPPDATA%\PicklesPlaylistEditor\playback — always writable for the current
        // user, falls back to %TEMP% if it somehow isn't.
        private static string PlaybackTempDir
        {
            get
            {
                string dir = Path.Combine(Utils.Logger.LogDirectory, "playback");
                try
                {
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                catch (Exception ex)
                {
                    Utils.Logger.LogWarn("Play: falling back to %TEMP% ('{Dir}' unusable): {Error}", dir, ex.Message);
                    string fallback = Path.Combine(Path.GetTempPath(), "pickles-playback");
                    Directory.CreateDirectory(fallback);
                    return fallback;
                }
            }
        }

        // Song names come from meta.json and routinely contain characters that are legal
        // in a playlist but not in a path.
        private static string SanitizeForFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        public static void PlayOgg(string oggPath, Action<long>? onEnded = null)
        {
            lock (_playerLock)
            {
                Utils.Logger.LogInfo("PlayOgg: opening '{File}'", oggPath);
                try { s_engine.Open(oggPath, onEnded); }
                catch (InvalidOperationException ex)
                {
                    Utils.Logger.LogError("PlayOgg: could not play '{File}': {Error}", oggPath, ex.Message);
                    throw;
                }
            }
            Utils.Logger.LogInfo("PlayOgg: playback started");
        }

        public static void Pause()
        {
            lock (_playerLock) s_engine.TogglePause();
        }

        public static void Stop()
        {
            lock (_playerLock) s_engine.Stop();
            CleanupExtractedTempFile();
        }

        private static void CleanupExtractedTempFile()
        {
            if (string.IsNullOrWhiteSpace(extractedTempOggPath) || !File.Exists(extractedTempOggPath))
            {
                extractedTempOggPath = null;
                return;
            }

            try
            {
                File.Delete(extractedTempOggPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Ignore if the file is still in use; next stop/play will attempt cleanup again.
                Utils.Logger.LogWarn("Could not delete temp playback file '{File}': {Error}",
                    extractedTempOggPath, ex.Message);
            }
            finally
            {
                extractedTempOggPath = null;
            }
        }

        /// <summary>
        /// Deletes decoded OGGs left behind by a previous crash or hard kill. Safe to call
        /// at startup; failures are non-fatal by design.
        /// </summary>
        public static void PurgeStalePlaybackFiles()
        {
            try
            {
                string dir = Path.Combine(Utils.Logger.LogDirectory, "playback");
                if (!Directory.Exists(dir)) return;

                // Snapshot our own in-use file once: it's null at startup (the usual call
                // site) but this method is safe to call mid-playback too.
                string? inUse = extractedTempOggPath;

                int removed = 0, skipped = 0;
                foreach (string file in Directory.EnumerateFiles(dir, "*.ogg"))
                {
                    if (inUse != null && string.Equals(file, inUse, StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Held open by a second instance that is playing it right now.
                        // Windows refuses the delete, which is the behaviour we want —
                        // leave it for whoever owns it to clean up.
                        skipped++;
                    }
                }

                if (removed > 0 || skipped > 0)
                    Utils.Logger.LogInfo("Playback cleanup: removed {Removed}, skipped {Skipped} in-use file(s).",
                        removed, skipped);
            }
            catch (Exception ex)
            {
                Utils.Logger.LogWarn("Stale playback cleanup failed (harmless): {Error}", ex.Message);
            }
        }

        public static TimeSpan GetPosition() => s_engine.Position;

        public static TimeSpan GetDuration() => s_engine.Duration;

        public static void Seek(TimeSpan position) => s_engine.Seek(position);

        public static int GetVolume() => s_engine.Volume;

        public static void SetVolume(int percent) => s_engine.Volume = percent;
    }
}
