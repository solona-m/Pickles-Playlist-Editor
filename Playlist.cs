using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VfxEditor.ScdFormat;

namespace Pickles_Playlist_Editor
{

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


        public static void Create(string playlistName, string dir)
        {
            Playlist group = new Playlist();
            group.Name = playlistName;
            group.Options = new List<Option>();

            Playlist mergedGroup = null;
            var fileNames = Directory.GetFiles(Path.Combine(Settings.PenumbraLocation, Settings.ModName), "group_*_" + playlistName + ".json");
            string fileName;
            if (fileNames.Length == 1)
            {
                fileName = fileNames[0];
                mergedGroup = JsonConvert.DeserializeObject<Playlist>(File.ReadAllText(fileName));
            }
            else
            {
                Option opt = new Option();
                opt.Name = "Off";
                opt.Files = new Dictionary<string, string>();
                group.Options.Add(opt);
                fileNames = Directory.GetFiles(Path.Combine(Settings.PenumbraLocation, Settings.ModName), "group_*");
                List<string> groupfiles = new List<string>(fileNames);
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
                foreach (string file in Directory.GetFiles(dir, "*.ogg", SearchOption.TopDirectoryOnly))
                {
                    AddFiles(playlistName, mergedGroup, file);
                }
                foreach (string file in Directory.GetFiles(dir, "*.wav", SearchOption.TopDirectoryOnly))
                {
                    AddFiles(playlistName, mergedGroup, file);
                }
                foreach (string file in Directory.GetFiles(dir, "*.scd", SearchOption.AllDirectories))
                {
                    AddFiles(playlistName, mergedGroup, file);
                }
            }
            /*
            foreach (string file in Directory.GetFiles(dir, "*.mp3", SearchOption.TopDirectoryOnly))
            {
                opt = AddFiles(Settings.PenumbraLocation, Settings.ModName, playlistName, group, file);
            }
            */

            string json = JsonConvert.SerializeObject(mergedGroup, Formatting.Indented);


            File.WriteAllText(Path.Combine(Settings.PenumbraLocation, Settings.ModName, fileName), json);
        }

        static Option AddFiles(string playlistName, Playlist group, string file)
        {
            Option opt;
            Console.WriteLine("Converting {0}", file);
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
            Console.WriteLine("done");
            opt = new Option();
            opt.Name = filenameroot;
            opt.Files = new Dictionary<string, string>();
            opt.Files.Add(
                "sound/bpmloop.scd",
                Path.Combine(playlistName, filenameroot, "bpmloop.scd"));
            group.Options.Add(opt);
            return opt;
        }

        public static Dictionary<string, Playlist> GetAll()
        {
            Dictionary<string, Playlist> playlists = new Dictionary<string, Playlist>();
            var fileNames = Directory.GetFiles(Path.Combine(Settings.PenumbraLocation, Settings.ModName), "group_*.json");
            foreach (string file in fileNames)
            {
                Playlist playlist = JsonConvert.DeserializeObject<Playlist>(File.ReadAllText(file));
                playlist.Name = playlist.Name;


                playlists.Add(playlist.Name, playlist);
            }
            return playlists;
        }

        public void Add(string[] fileNames)
        {
            foreach (string file in fileNames)
            {
                AddFiles(Name, this, file);
            }
            Save();
        }

        public void Insert(string[] fileNames, int index)
        {
            foreach (string file in fileNames)
            {
                Option opt = AddFiles(Name, this, file);
                Options.RemoveAt(Options.Count - 1);
                Options.Insert(index, opt);
                index++;
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
    }
}
