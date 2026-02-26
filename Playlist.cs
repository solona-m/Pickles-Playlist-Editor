using Newtonsoft.Json;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using VfxEditor.ScdFormat;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;
using System.IO;
using System.Text.RegularExpressions;

namespace Pickles_Playlist_Editor
{
    enum SortDirection
    {
        Ascending,
        Descending
    }

    public class Playlist
    {
        public int Version { get { return 0; } }
        public string Name { get; set; }
        public string Description { get { return string.Empty; } }
        public string Image { get { return string.Empty; } }
        public int Page { get { return 0; } }
        public int Priority { get; set; }
        public string Type { get { return "Single"; } }
        public int DefaultSettings { get { return 0; } }
        public List<Option> Options { get; set; } = new List<Option>();


        public static void Create(string playlistName, string dir, Action<int>? callback)
        {
            if (playlistName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show("Playlist name cannot contain any of the following characters: " + string.Join(" ", Path.GetInvalidFileNameChars()));
                return;
            }

            Playlist group = new Playlist();
            group.Name = playlistName;
            group.Options = new List<Option>();

            Playlist mergedGroup = null;
            var groupFileNames = GetJsonFiles(playlistName);
            string fileName;
            if (groupFileNames.Length == 1)
            {
                fileName = groupFileNames[0];
                mergedGroup = JsonConvert.DeserializeObject<Playlist>(File.ReadAllText(fileName));
            }
            else
            {
                Option opt = new Option();
                opt.Name = "Off";
                opt.Files = new Dictionary<string, string>();
                group.Options.Add(opt);
                groupFileNames = Directory.GetFiles(Path.Combine(Settings.PenumbraLocation, Settings.ModName), "group_*");
                List<string> groupfiles = new List<string>(groupFileNames);
                groupfiles.Sort();
                mergedGroup = group;

                int groupNumber = 1;
                if (groupfiles.Count > 0)
                {
                    string lastName = groupfiles[groupfiles.Count - 1];
                    groupNumber = Int32.Parse(Path.GetFileNameWithoutExtension(lastName).Substring(6, 3)) + 1;
                    mergedGroup.Priority = mergedGroup.Priority + 1;
                }

                fileName = string.Format("group_{0}_{1}.json", string.Format("{0:D3}", groupNumber), playlistName);
            }

            if (!string.IsNullOrEmpty(dir))
            {
                int count = 0, totalCount = 0;
                foreach (string ext in Settings.SupportedFileTypes)
                { 
                    string[] fileNames = Directory.GetFiles(dir, "*" + ext, SearchOption.AllDirectories);
                    totalCount += fileNames.Length;
                }

                foreach (string ext in Settings.SupportedFileTypes)
                {
                    string[] fileNames = Directory.GetFiles(dir, "*"+ext, SearchOption.AllDirectories);
                    
                    foreach (string file in fileNames)
                    {
                        AddFiles(playlistName, mergedGroup, file);
                        if (callback != null)
                            callback((int)((float)(++count) / totalCount * 100));
                    }
                }
            }

            string json = JsonConvert.SerializeObject(mergedGroup, Formatting.Indented);


            File.WriteAllText(Path.Combine(Settings.PenumbraLocation, Settings.ModName, fileName), json);
            Directory.CreateDirectory(Path.Combine(Settings.PenumbraLocation, Settings.ModName, playlistName));

            // Notify Penumbra (if present) to refresh this mod because files/config changed.
            RefreshPenumbraMod();
        }

        static Option AddFiles(string playlistName, Playlist group, string file)
        {
            Option opt = null;
            try
            {
                ScdFile scdFile = ScdFile.Import(file);
                string filenameroot = Path.GetFileNameWithoutExtension(file);
                if (filenameroot.Equals("bpmloop", StringComparison.OrdinalIgnoreCase))
                {
                    filenameroot = Path.GetDirectoryName(file);
                    filenameroot = filenameroot.Split(Path.DirectorySeparatorChar).Last();
                }
                string cleanPlaylistName = playlistName.Replace("/", "_");
                string outDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, cleanPlaylistName);
                Directory.CreateDirectory(outDir);
                using (BinaryWriter writer = new BinaryWriter(new FileStream(Path.Combine(outDir, Path.GetFileName(filenameroot)+".scd"), FileMode.Create)))
                {
                    scdFile.Write(writer);
                }
                opt = new Option();
                opt.Name = filenameroot;
                opt.Files = new Dictionary<string, string>();
                opt.Files.Add(
                    Settings.BaselineScdKey,
                    Path.Combine(cleanPlaylistName, Path.GetFileName(filenameroot)+".scd"));
                group.Options.Add(opt);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error adding file " + file + ": " + ex.ToString());
            }
            return opt;
        }

        public void Cleanup()
        {
            Playlist playlist = this;
            string cleanPlaylistName = playlist.Name.Replace("/", "_");
            string outDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, cleanPlaylistName);
            Directory.CreateDirectory(outDir);
            List<Option> optionsToRemove = new List<Option>();
            foreach (Option song in playlist.Options)
            {
                if (song.Name.Equals("Off", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (song.Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (song.Files != null)
                {
                    string oldPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, song.Files[song.Files.Keys.First()]);
                    if (!File.Exists(oldPath))
                    {
                        optionsToRemove.Add(song);
                        continue;
                    }

                    // Sanitize the desired filename so it is valid on Windows
                    var safeName = SanitizeFileName(song.Name);
                    if (string.IsNullOrWhiteSpace(safeName))
                        safeName = "audio";

                    // Ensure extension is .scd
                    string fileName = safeName.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) ? safeName : safeName + ".scd";

                    string newPath = Path.Combine(outDir, fileName);

                    // If target file already exists, append a numeric suffix to avoid collision
                    newPath = GetNonCollidingPath(newPath);

                    if (oldPath != newPath)
                    {
                        File.Move(oldPath, newPath);
                        BPMDetector.UpdateCacheForSCD(oldPath, newPath);
                        song.Files[song.Files.Keys.First()] = Path.Combine(cleanPlaylistName, Path.GetFileName(newPath));
                    }
                }
            }
            foreach (Option opt in optionsToRemove)
            {
                playlist.Options.Remove(opt);
            }
            this.Save();
            // Save() will refresh Penumbra; no need to call here.
        }

        private static string GetNonCollidingPath(string path)
        {
            if (!File.Exists(path)) return path;

            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int idx = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name}_{idx}{ext}");
                idx++;
            } while (File.Exists(candidate));
            return candidate;
        }

        public static string? GetScdKey(Option opt)
        {
            if (opt?.Files == null || opt.Files.Count == 0)
                return null;

            if (opt.Files.ContainsKey(Settings.BaselineScdKey))
                return Settings.BaselineScdKey;

            if (opt.Files.ContainsKey("sound/bpmloop.scd"))
                return "sound/bpmloop.scd";

            return opt.Files.Keys.FirstOrDefault(k => k.EndsWith(".scd", StringComparison.OrdinalIgnoreCase));
        }

        public static string GetScdPath(Option opt)
        {
            string? key = GetScdKey(opt);
            if (key == null)
                return string.Empty;
            return opt.Files[key];
        }

        public static string GetBaselineScdFileName()
        {
            string key = Settings.BaselineScdKey.Replace('/', Path.DirectorySeparatorChar);
            string fileName = Path.GetFileName(key);
            if (string.IsNullOrWhiteSpace(fileName))
                return "bpmloop.scd";
            return fileName;
        }

        public static Dictionary<string, Playlist> GetAll()
        {
            Dictionary<string, Playlist> playlists = new Dictionary<string, Playlist>();
            if (Settings.PenumbraLocation == null || Settings.ModName == null)
                return playlists;

            string modDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(modDirectory))
                return playlists;

            var fileNames = Directory.GetFiles(modDirectory, "group_*.json");
            foreach (string file in fileNames)
            {
                try
                {
                    Playlist playlist = JsonConvert.DeserializeObject<Playlist>(File.ReadAllText(file));

                    if (playlist == null)
                    {
                        MessageBox.Show("Error loading playlist from file " + file);
                    }
                    else
                    {
                        playlist.Name = playlist.Name;


                        playlists.Add(playlist.Name, playlist);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error loading playlist from file " + file + ": " + ex.ToString());
                }
            }
            return playlists;
        }

        public void Add(string[] fileNames, Action<int>? callback = null)
        {
            int count = 0;
            foreach (string file in fileNames)
            {
                if (Settings.SupportedFileTypes.Contains(Path.GetExtension(file).ToLower()))
                    AddFiles(Name, this, file);
                if (callback != null)
                    callback((int)((float)(++count)/fileNames.Length*100));
            }
            Save();
        }

        public void Insert(string[] fileNames, int index, Action<int>? callback = null)
        {
            int count = 0;
            foreach (string file in fileNames)
            {
                Option opt = AddFiles(Name, this, file);
                Options.RemoveAt(Options.Count - 1);
                Options.Insert(index, opt);
                index++;
                if (callback != null)
                    callback((int)((float)(++count) / fileNames.Length * 100));
            }
            Save();
        }

        public void Save()
        {
            var fileNames = GetJsonFiles(Name);
            if (fileNames.Length == 0) return;
            string fileName = fileNames[0];
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(fileName, json);

            // Notify Penumbra (if present) that the mod directory changed so it can refresh.
            RefreshPenumbraMod();
        }

        public void Delete()
        {
            if (string.IsNullOrEmpty(Name))
                return;
            if (Directory.Exists(Path.Combine(Settings.PenumbraLocation, Settings.ModName, Name)))
                Directory.Delete(Path.Combine(Settings.PenumbraLocation, Settings.ModName, Name), true);
            File.Delete(GetJsonFiles(Name)[0]);

            // Notify Penumbra after removing files
            RefreshPenumbraMod();
        }

        private static string[] GetJsonFiles(string name)
        {
            if (Settings.PenumbraLocation == null || Settings.ModName == null)
                return Array.Empty<string>();

            string modDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
            if (!Directory.Exists(modDirectory))
                return Array.Empty<string>();

            return Directory.GetFiles(Path.Combine(Settings.PenumbraLocation, Settings.ModName), "group_*_" + name.Replace("/","_") + ".json");
        }

        internal void Shuffle()
        {
            int n = Options.Count;
            List<Option> shuffledOptions = new List<Option>(n);
            shuffledOptions.Add(Options[0]); // Keep the "Off" option in place
            Options.RemoveAt(0);
            Random rng = new Random();
            while (Options.Count > 0)
            {
                int k = rng.Next(Options.Count);
                shuffledOptions.Add(Options[k]);
                Options.RemoveAt(k);
            }
            Options = shuffledOptions;
            Save();
        }

        internal void Sort(SortDirection direction)
        {
            Option offOption = Options.FirstOrDefault(o => o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase));
            List<Option> otherOptions = Options.Where(o => !o.Name.Equals("Off", StringComparison.OrdinalIgnoreCase)).ToList();
            if (direction == SortDirection.Ascending)
            {
                otherOptions = otherOptions.OrderBy(o => BPMDetector.GetBPMFromSCD(GetScdPath(o))).ToList();
            }
            else
            {
                otherOptions = otherOptions.OrderByDescending(o => BPMDetector.GetBPMFromSCD(GetScdPath(o))).ToList();
            }
            Options = new List<Option>();
            if (offOption != null)
            {
                Options.Add(offOption);
            }
            Options.AddRange(otherOptions);
            Save();
        }

        /// <summary>
        /// Replace invalid characters for Windows filenames, trim trailing dots/spaces and limit length.
        /// </summary>
        private static string SanitizeFileName(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            // Replace directory separators and invalid filename characters with underscore
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                if (invalid.Contains(ch) || char.IsControl(ch))
                    sb.Append('_');
                else
                    sb.Append(ch);
            }

            var result = sb.ToString();

            // Trim trailing spaces and dots (Windows does not allow names that end with dot/space)
            result = result.TrimEnd(' ', '.');

            // Limit filename length (reserve space for extension .scd)
            const int maxFileName = 200;
            if (result.Length > maxFileName)
                result = result.Substring(0, maxFileName);

            // As an extra safeguard remove any remaining invalid subsequences
            result = Regex.Replace(result, @"[\\\/:\*\?""<>\|]", "_");

            return result;
        }

        /// <summary>
        /// Attempt to notify Penumbra (FFXIV Dalamud plugin) that the mod folder changed so Penumbra can refresh its cache.
        /// This is a best-effort notification: Penumbra watches file changes; touching meta.json or creating a transient marker file
        /// commonly triggers a refresh. This function does not interact with Dalamud directly.
        /// </summary>
        private static void RefreshPenumbraMod()
        {
            try
            {
                PenumbraApi.ReloadMod(Settings.ModName, Settings.ModName);
            }
            catch
            {
                // best-effort only; swallow any errors to avoid breaking the UI
            }
        }
    }
}
