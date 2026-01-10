using Newtonsoft.Json;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VfxEditor.ScdFormat;

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
            Playlist group = new Playlist();
            group.Name = playlistName;
            group.Options = new List<Option>();

            Playlist mergedGroup = null;
            var groupFileNames = Directory.GetFiles(Path.Combine(Settings.PenumbraLocation, Settings.ModName), "group_*_" + playlistName + ".json");
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
                string lastName = groupfiles[groupfiles.Count - 1];
                fileName = lastName;
                mergedGroup = group;
                mergedGroup.Priority = mergedGroup.Priority + 1;

                int groupNumber = Int32.Parse(Path.GetFileNameWithoutExtension(lastName).Substring(6, 3)) + 1;
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
        }

        static Option AddFiles(string playlistName, Playlist group, string file)
        {
            Option opt = null;
            try
            {
                Logger.LogInfo("Converting {0}", file);
                ScdFile scdFile = ScdFile.Import(file);
                string filenameroot = Path.GetFileNameWithoutExtension(file);
                if (filenameroot.Equals("bpmloop", StringComparison.OrdinalIgnoreCase))
                {
                    filenameroot = Path.GetDirectoryName(file);
                    filenameroot = filenameroot.Split(Path.DirectorySeparatorChar).Last();
                }
                string outDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, playlistName, filenameroot);
                Directory.CreateDirectory(outDir);
                using (BinaryWriter writer = new BinaryWriter(new FileStream(Path.Combine(outDir, "bpmloop.scd"), FileMode.Create)))
                {
                    scdFile.Write(writer);
                }
                Logger.LogInfo("done");
                opt = new Option();
                opt.Name = filenameroot;
                opt.Files = new Dictionary<string, string>();
                opt.Files.Add(
                    "sound/bpmloop.scd",
                    Path.Combine(playlistName, filenameroot, "bpmloop.scd"));
                group.Options.Add(opt);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error adding file " + file + ": " + ex.ToString());
            }
            return opt;
        }

        public static Dictionary<string, Playlist> GetAll()
        {
            Dictionary<string, Playlist> playlists = new Dictionary<string, Playlist>();
            if (Settings.PenumbraLocation == null || Settings.ModName == null)
                return playlists;
            var fileNames = Directory.GetFiles(Path.Combine(Settings.PenumbraLocation, Settings.ModName), "group_*.json");
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
                    MessageBox.Show("Error loading playlist from file " + file + ": " + ex.Message);
                    Logger.LogError("Error loading playlist from file " + file + ": " + ex.ToString());
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
            var fileNames = Directory.GetFiles(Path.Combine(Settings.PenumbraLocation, Settings.ModName), "group_*_" + Name + ".json");
            if (fileNames.Length == 0) return;
            string fileName = fileNames[0];
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(fileName, json);
        }

        public void Delete()
        {
            if (string.IsNullOrEmpty(Name))
                return;
            if (Directory.Exists(Path.Combine(Settings.PenumbraLocation, Settings.ModName, Name)))
                Directory.Delete(Path.Combine(Settings.PenumbraLocation, Settings.ModName, Name), true);
            File.Delete(Directory.GetFiles(Path.Combine(Settings.PenumbraLocation, Settings.ModName), "group_*_" + Name + ".json")[0]);
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
                otherOptions = otherOptions.OrderBy(o => BPMDetector.GetBPMFromSCD(o.Files["sound/bpmloop.scd"])).ToList();
            }
            else
            {
                otherOptions = otherOptions.OrderByDescending(o => BPMDetector.GetBPMFromSCD(o.Files["sound/bpmloop.scd"])).ToList();
            }
            Options = new List<Option>();
            if (offOption != null)
            {
                Options.Add(offOption);
            }
            Options.AddRange(otherOptions);
            Save();
        }
    }
}
