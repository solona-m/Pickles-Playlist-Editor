using Newtonsoft.Json.Linq;
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
        private static readonly Regex GroupJsonPattern = new(@"^group_(\d+)_(.+)\.json$", RegexOptions.IgnoreCase);
        private static readonly Regex GroupBakPattern  = new(@"^group_\d+_(.+)\.json\.bak$", RegexOptions.IgnoreCase);

        public static void Cleanup()
        {
            foreach (var playlist in MainWindow.Playlists.Values)
                playlist.Cleanup();
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

        private static List<string> RepairCore()
        {
            var log = new List<string>();

            string base_ = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(base_))
            {
                log.Add($"ERROR: mod directory not found: {base_}");
                return log;
            }

            log.AddRange(FixNameMismatches(base_));

            // Find existing group JSON files
            var existing = Directory.GetFiles(base_, "group_*.json")
                .Select(Path.GetFileName)
                .Where(f => GroupJsonPattern.IsMatch(f))
                .ToList();

            var nums = existing
                .Select(f => int.Parse(GroupJsonPattern.Match(f).Groups[1].Value))
                .ToList();

            var existingNames = existing
                .Select(f => GroupJsonPattern.Match(f).Groups[2].Value.ToLowerInvariant())
                .ToHashSet();

            int nextNum = nums.Count > 0 ? nums.Max() + 1 : 1;
            log.Add($"Highest existing group JSON: {nums.Max():D3}, starting at {nextNum:D3}");

            // Get all bak files
            var baks = Directory.GetFiles(base_, "group_*.json.bak")
                .Select(Path.GetFileName)
                .Where(f => GroupBakPattern.IsMatch(f))
                .OrderBy(f => f)
                .ToList();

            log.Add($"Found {baks.Count} .bak files\n");

            var copied = new List<string>();
            foreach (var bak in baks)
            {
                string bakPath = Path.Combine(base_, bak);

                JObject data = TryLoadJson(bakPath);
                if (data == null)
                {
                    log.Add($"SKIP (parse error): {bak}");
                    continue;
                }

                string playlistName = GroupBakPattern.Match(bak).Groups[1].Value;
                if (existingNames.Contains(playlistName.ToLowerInvariant()))
                {
                    log.Add($"SKIP (exists):    {bak}");
                    continue;
                }

                if (data["Options"] is not JArray options)
                {
                    log.Add($"SKIP (no songs):  {bak}");
                    continue;
                }

                // Remove options whose song file is missing
                int omitted = 0;
                foreach (var opt in options.OfType<JObject>().ToList())
                {
                    if (opt["Files"] is not JObject files) continue;
                    var songProp = files.Properties().FirstOrDefault(p => p.Name.Contains("bpmloop.scd"));
                    if (songProp == null) continue;
                    string songPath = songProp.Value.ToString().Replace('\\', '/');
                    if (!File.Exists(Path.Combine(base_, songPath)))
                    {
                        opt.Remove();
                        omitted++;
                    }
                }

                int songCount = options.OfType<JObject>()
                    .Count(o => (o["Files"] as JObject)?.Properties()
                        .Any(p => p.Name.Contains("bpmloop.scd")) == true);

                if (songCount == 0)
                {
                    log.Add($"SKIP (no songs):  {bak}");
                    continue;
                }

                string newName = $"group_{nextNum:D3}_{playlistName}.json";
                File.WriteAllText(Path.Combine(base_, newName), data.ToString(), System.Text.Encoding.UTF8);
                string omittedNote = omitted > 0 ? $", {omitted} omitted" : "";
                log.Add($"COPIED ({songCount,3} songs{omittedNote}): {bak} -> {newName}");
                copied.Add(newName);
                nextNum++;
            }
            Pickles_Playlist_Editor.Playlist.RefreshPenumbraMod();
            log.Add($"\nDone. {copied.Count} files copied.");
            return log;
        }

        private static bool _nameMismatchHealAttempted;

        // Auto-heals filename/content name mismatches once per session, the first time the library is
        // loaded against a real mod directory. Only the safe rename pass runs here — never the .bak
        // restore, which stays behind the manual "Repair Library" button. Idempotent: a healthy
        // library moves nothing. Failures never block loading.
        internal static void HealNameMismatchesOnce()
        {
            if (_nameMismatchHealAttempted)
                return;
            if (Settings.PenumbraLocation == null || Settings.ModName == null)
                return;
            string base_ = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(base_))
                return;

            // Only mark done once we actually have a directory to heal, so a session that starts
            // before Penumbra is configured still heals after the user sets it up.
            _nameMismatchHealAttempted = true;
            try
            {
                foreach (var line in FixNameMismatches(base_))
                {
                    if (line.StartsWith("RENAMED") || line.StartsWith("WARNING"))
                        Utils.Logger.LogInfo("Auto-heal: {Line}", line);
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError("Auto-heal failed: {Error}", ex);
            }
        }

        // Repairs the fallout of the old GetJsonFiles wildcard bug: because "group_*_<name>.json"
        // also matched longer names ending in "_<name>", Save() could write one playlist's data into
        // another playlist's file. That leaves files whose on-disk name no longer matches the "Name"
        // stored inside them, so GetAll() (keyed by content Name) silently drops or merges playlists.
        // This pass renames each such file so its filename matches its content again, which both makes
        // the playlist visible and lets the now-exact GetJsonFiles find it. Duplicate names (the true
        // symptom of an overwrite) are surfaced in the log so the .bak restore phase / the user can
        // recover the clobbered playlist.
        private static List<string> FixNameMismatches(string base_)
        {
            var log = new List<string>();

            // Track every group_*.json filename so renames never collide with an existing file.
            var used = new HashSet<string>(
                Directory.GetFiles(base_, "group_*.json").Select(Path.GetFileName),
                StringComparer.OrdinalIgnoreCase);

            int renamed = 0;
            foreach (var path in Directory.GetFiles(base_, "group_*.json").OrderBy(f => f))
            {
                string file = Path.GetFileName(path);
                var m = GroupJsonPattern.Match(file);
                if (!m.Success) continue;

                JObject data = TryLoadJson(path);
                string contentName = data?["Name"]?.ToString();
                if (string.IsNullOrEmpty(contentName)) continue;

                string cleanName = contentName.Replace("/", "_");
                if (string.Equals(m.Groups[2].Value, cleanName, StringComparison.OrdinalIgnoreCase))
                    continue; // filename already matches content — nothing to fix

                // Find a free group number for the corrected filename. Bump the number (never a
                // filename suffix) so the result still parses as group_<digits>_<name>.json.
                int num = int.Parse(m.Groups[1].Value);
                string target;
                do
                {
                    target = $"group_{num:D3}_{cleanName}.json";
                    num++;
                } while (used.Contains(target));

                File.Move(path, Path.Combine(base_, target));
                used.Remove(file);
                used.Add(target);
                log.Add($"RENAMED (name mismatch): {file} -> {target}");
                renamed++;
            }

            // Warn about playlists that now have more than one file — the hallmark of an overwrite,
            // where one file holds legitimate data and the other(s) are a clobbered copy.
            var dupes = used
                .Select(f => GroupJsonPattern.Match(f))
                .Where(mm => mm.Success)
                .GroupBy(mm => mm.Groups[2].Value, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);
            foreach (var g in dupes)
                log.Add($"WARNING (duplicate playlist '{g.Key}'): {string.Join(", ", g.Select(mm => mm.Value))}");

            log.Add(renamed > 0 ? $"Fixed {renamed} name mismatch(es).\n" : "No name mismatches found.\n");
            return log;
        }

        private static JObject TryLoadJson(string path)
        {
            foreach (var enc in new[] { System.Text.Encoding.UTF8, System.Text.Encoding.Default })
            {
                try
                {
                    string text = File.ReadAllText(path, enc);
                    return JObject.Parse(text);
                }
                catch { }
            }
            return null;
        }
    }
}
