using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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
