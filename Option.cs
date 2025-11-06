using System;
using System.Collections.Generic;

namespace Pickles_Playlist_Editor
{
    public class Option
    {
        public Option()
        {
            Files = new Dictionary<string, string>();
            FileSwaps = new Dictionary<string, string>();
            Manipulations = new List<string>();
        }

        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, string> Files { get; set; }
        public Dictionary<string, string> FileSwaps { get; set; }
        public List<string> Manipulations { get; set; }
    }
}
