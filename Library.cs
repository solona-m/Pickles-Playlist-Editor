using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using VfxEditor.ScdFormat;

namespace Pickles_Playlist_Editor
{
    public static partial class Library
    {
        public static void Cleanup()
        {
            var failed = new List<string>();
            foreach (var playlist in MainWindow.Playlists.Values)
            {
                // One unsaveable playlist must not abort the organize pass for every other one,
                // nor escape into the void event handler that invokes this and crash the app.
                try { playlist.Cleanup(); }
                catch (Exception ex)
                {
                    Utils.Logger.LogError("Cleanup skipped '{Name}': {Error}", playlist.Name, ex);
                    failed.Add(playlist.Name);
                }
            }
            if (failed.Count > 0)
                Utils.Logger.LogWarn("Cleanup: skipped {Count} playlist(s) that could not be saved: {Names}",
                    failed.Count, string.Join(", ", failed));
        }

        // Rebuilds every library SCD's headers from the canonical default.scd plus the app's
        // current settings, keeping each file's existing audio. Copies NumChannels from
        // default.scd. Self-heals older files written with the now-fixed Bypass bug.
        public static List<string> ConvertToStereo(Action<int> progress)
        {
            var errors = new List<string>();

            var allOptions = Playlist.GetAll().Values
                .SelectMany(p => p.Options)
                .ToList();
            int total = allOptions.Count;
            int done = 0;

            byte[] defaultBytes = File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "default.scd"));

            foreach (var option in allOptions)
            {
                string rel = Playlist.GetScdPath(option);
                if (!string.IsNullOrEmpty(rel))
                {
                    string scdPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, rel);
                    if (File.Exists(scdPath))
                    {
                        try
                        {
                            // Read the existing file purely to recover its audio (read by offset,
                            // so it survives any header misalignment from the old Bypass bug).
                            ScdFile existing;
                            using (var reader = new BinaryReader(File.Open(scdPath, FileMode.Open, FileAccess.Read, FileShare.Read)))
                                existing = new ScdFile(reader, false);

                            if (existing.Audio.Count == 0)
                            {
                                progress?.Invoke((int)(++done / (double)total * 100));
                                continue;
                            }

                            // Fresh canonical skeleton from default.scd.
                            ScdFile rebuilt;
                            using (var defReader = new BinaryReader(new MemoryStream(defaultBytes)))
                                rebuilt = new ScdFile(defReader, false);

                            int defChannels = rebuilt.Audio[0].NumChannels;
                            rebuilt.Audio.Clear();
                            rebuilt.Audio.Add(existing.Audio[0]);
                            rebuilt.Audio[0].NumChannels = defChannels;
                            rebuilt.ApplyCurrentSettings();

                            using (var writer = new BinaryWriter(File.Open(scdPath, FileMode.Create)))
                                rebuilt.Write(writer);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{Path.GetFileName(scdPath)}: {ex.Message}");
                        }
                    }
                }
                progress?.Invoke((int)(++done / (double)total * 100));
            }

            return errors;
        }

        [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        public static List<string> Repair()
        {
            try
            {
                return RepairCore();
            }
            catch (Exception ex)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                MessageBox(hwnd,
                    "Repair failed, please screenshot and report this error.\n\n" + ex.ToString(),
                    "Error",
                    0x00000010); // MB_ICONERROR
                return null;
            }
        }

        // In Penumbra's v4 format the whole library lives in one meta.json, so most of the old repair
        // passes are gone along with the thing they repaired: there are no group_NNN_*.json filenames
        // left to renumber, to drift out of sync with their contents, or to strand as .reorder_tmp
        // sidecars. What remains is the damage that is still possible - a broken manifest, playlists
        // pointing at renamed audio, and playlists pointing at audio that is gone.
        private static List<string> RepairCore()
        {
            var log = new List<string>();

            string base_ = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(base_))
            {
                log.Add($"ERROR: mod directory not found: {base_}");
                return log;
            }

            // 1. meta.json is the whole library now, so a broken one is the catastrophic case. Check
            //    it before touching anything else.
            log.AddRange(VerifyOrRestoreManifest());

            // 2. Repoint playlists at .scd files whose names drifted.
            log.AddRange(StripRedundantScdSuffixes(base_));

            // 3. Drop songs whose audio no longer exists on disk.
            log.AddRange(DropMissingSongs(base_));

            PenumbraMeta.CleanLegacyFiles();
            Playlist.RefreshPenumbraMod();
            log.Add("\nDone.");
            return log;
        }

        // meta.json holds every playlist now. If it is unreadable or has lost its Groups array, the
        // library is gone - fall back to the newest pre-write snapshot that still has groups in it.
        private static List<string> VerifyOrRestoreManifest()
        {
            var log = new List<string>();

            var groups = PenumbraMeta.TryReadGroups();
            if (groups != null && groups.Count > 0)
            {
                log.Add($"meta.json OK: {groups.Count} playlist(s).\n");
                return log;
            }

            log.Add(groups == null
                ? "PROBLEM: meta.json is missing, unreadable, or has no Groups array."
                : "PROBLEM: meta.json parses but contains no playlists.");

            string snapshot = PenumbraMeta.NewestUsableSnapshot();
            if (snapshot == null)
            {
                log.Add("No usable backup found - nothing to restore from.\n");
                return log;
            }

            try
            {
                // Snapshot the broken file first: if restoring turns out to be the wrong call, the
                // current state is still recoverable.
                PenumbraMeta.TrySnapshot();
                PenumbraMeta.AtomicWrite(PenumbraMeta.MetaPath,
                    File.ReadAllText(snapshot, System.Text.Encoding.UTF8));
                int restored = PenumbraMeta.TryReadGroups()?.Count ?? 0;
                log.Add($"RESTORED meta.json from {Path.GetFileName(snapshot)} ({restored} playlist(s)).\n");
            }
            catch (Exception ex)
            {
                log.Add($"ERROR: restore from {Path.GetFileName(snapshot)} failed: {ex.Message}\n");
            }

            return log;
        }

        // Removes options whose referenced audio file is gone. These are dead entries: selecting one
        // in Penumbra silently does nothing.
        private static List<string> DropMissingSongs(string base_)
        {
            var log = new List<string>();
            int removed = 0, touched = 0;

            foreach (var playlist in Playlist.GetAll().Values)
            {
                if (playlist.Options == null) continue;

                var dead = playlist.Options
                    .Where(o => o?.Files != null && o.Files.Count > 0)
                    .Where(o =>
                    {
                        string rel = Playlist.GetScdPath(o);
                        if (string.IsNullOrEmpty(rel)) return false;
                        return !File.Exists(Path.Combine(base_, Playlist.NormalizeRelativeModPath(rel)));
                    })
                    .ToList();

                if (dead.Count == 0) continue;

                foreach (var o in dead)
                {
                    playlist.Options.Remove(o);
                    log.Add($"REMOVED (audio missing): {playlist.Name} / {o.Name}");
                }

                try
                {
                    playlist.Save();
                    removed += dead.Count;
                    touched++;
                }
                catch (Exception ex)
                {
                    log.Add($"SKIPPED (could not save): {playlist.Name} - {ex.Message}");
                }
            }

            log.Add(removed > 0
                ? $"Removed {removed} dead song entr(ies) across {touched} playlist(s).\n"
                : "No dead song entries found.\n");
            return log;
        }

        // Reverses the leftover "_1" (or "_2", ...) suffixes the old same-playlist song-reorder bug
        // appended to .scd filenames (Creep.scd -> Creep_1.scd) via GetNonCollidingPath. That bug
        // renamed base -> base_1, so the base name is now free and we can safely rename each file
        // back and repoint the playlist at it. Only strips when the shorter name is actually free on
        // disk, so distinct files never collide and genuinely-suffixed names are left alone.
        private static List<string> StripRedundantScdSuffixes(string base_)
        {
            var log = new List<string>();
            var suffix = new Regex(@"^(.*)_\d+$");
            int renamed = 0, playlistsTouched = 0;

            foreach (var playlist in Playlist.GetAll().Values)
            {
                if (playlist.Options == null) continue;
                bool changed = false;

                foreach (var option in playlist.Options)
                {
                    if (option?.Files == null) continue;
                    foreach (var key in option.Files.Keys.ToList())
                    {
                        string rel = option.Files[key];
                        if (string.IsNullOrEmpty(rel) || !rel.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Peel off one numeric suffix at a time while the shorter name is free.
                        string currentRel = rel;
                        while (true)
                        {
                            var m = suffix.Match(Path.GetFileNameWithoutExtension(currentRel));
                            if (!m.Success || m.Groups[1].Value.Length == 0) break;

                            string dirPrefix = currentRel.Substring(0, currentRel.Length - Path.GetFileName(currentRel).Length);
                            string candidateRel = dirPrefix + m.Groups[1].Value + Path.GetExtension(currentRel);

                            string currentFull = Path.Combine(base_, Playlist.NormalizeRelativeModPath(currentRel));
                            string candidateFull = Path.Combine(base_, Playlist.NormalizeRelativeModPath(candidateRel));

                            if (!File.Exists(currentFull)) break;   // referenced file missing
                            if (File.Exists(candidateFull)) break;  // shorter name taken - keep suffix

                            try { File.Move(currentFull, candidateFull); }
                            catch (Exception ex)
                            {
                                log.Add($"SKIP (rename failed {Path.GetFileName(currentFull)}): {ex.Message}");
                                break;
                            }

                            Utils.BPMDetector.UpdateCacheForSCD(currentFull, candidateFull);
                            Utils.KeyDetector.UpdateCacheForSCD(currentFull, candidateFull);
                            currentRel = candidateRel;
                        }

                        if (currentRel != rel)
                        {
                            option.Files[key] = currentRel;
                            changed = true;
                            renamed++;
                            log.Add($"UNSUFFIXED: {rel} -> {currentRel}");
                        }
                    }
                }

                if (changed)
                {
                    // A playlist whose group can't be resolved must not abort the whole heal pass -
                    // this runs on the load path, so throwing here would break startup.
                    try
                    {
                        playlist.Save();
                        playlistsTouched++;
                    }
                    catch (Exception ex)
                    {
                        log.Add($"SKIPPED (could not save): {playlist.Name} - {ex.Message}");
                    }
                }
            }

            log.Add(renamed > 0
                ? $"Removed {renamed} redundant _N suffix(es) across {playlistsTouched} playlist(s).\n"
                : "No redundant _N .scd suffixes found.\n");
            return log;
        }
    }
}
