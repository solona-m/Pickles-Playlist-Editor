using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pickles_Playlist_Editor
{
    /// <summary>
    /// One selectable entry in a Penumbra option group — for this app, one song.
    ///
    /// In Penumbra's v4 manifest an option carries an <c>Id</c> GUID, and Penumbra stores the user's
    /// current selection for a group against that Id. Minting fresh Ids on save would silently reset
    /// every playlist to a different song, so <see cref="Id"/> is read from the manifest and only
    /// generated for genuinely new options.
    /// </summary>
    public class Option
    {
        public Option()
        {
            Files = new Dictionary<string, string>();
            FileSwaps = new Dictionary<string, string>();
        }

        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, string> Files { get; set; }
        public Dictionary<string, string> FileSwaps { get; set; }

        // v4 manipulations are objects ({Type, Manipulation:{...}}), not strings. This app never
        // authors them; it exists only so options written by Penumbra's editor round-trip intact.
        [JsonIgnore]
        public JArray Manipulations { get; set; }

        public static Option FromJson(JObject o)
        {
            var opt = new Option
            {
                Name = o["Name"]?.ToString() ?? string.Empty,
                Description = o["Description"]?.ToString(),
                Manipulations = o["Manipulations"] as JArray,
            };

            if (Guid.TryParse(o["Id"]?.ToString(), out var id))
                opt.Id = id;

            if (o["Files"] is JObject files)
            {
                foreach (var p in files.Properties())
                    opt.Files[p.Name] = p.Value?.ToString() ?? string.Empty;
            }

            // v3 called this "Swaps"; v4 renamed it to "FileSwaps". Read both, write only the latter.
            if ((o["FileSwaps"] ?? o["Swaps"]) is JObject swaps)
            {
                foreach (var p in swaps.Properties())
                    opt.FileSwaps[p.Name] = p.Value?.ToString() ?? string.Empty;
            }

            return opt;
        }

        /// <summary>
        /// This option as a v4 manifest object. Built by cloning <paramref name="existing"/> so keys
        /// this app doesn't model (Image, Priority, manipulations authored in Penumbra's editor)
        /// survive the round trip, then overwriting only the fields we own.
        /// </summary>
        internal JObject ToJson(JObject existing)
        {
            var o = existing != null ? (JObject)existing.DeepClone() : new JObject();

            if (Id == Guid.Empty)
                Id = Guid.NewGuid();
            o["Id"] = Id.ToString();
            o["Name"] = Name ?? string.Empty;

            SetOrRemove(o, "Description", string.IsNullOrEmpty(Description) ? null : new JValue(Description));
            SetOrRemove(o, "Files", ToJObject(Files));
            SetOrRemove(o, "FileSwaps", ToJObject(FileSwaps));

            // Deep-clone: Manipulations still belongs to the JObject this option was read from, and
            // assigning a token that already has a parent grafts one document's node into another.
            // Newtonsoft does copy on assignment, but relying on that is a subtle dependency in the
            // one path whose entire job is to preserve data Penumbra's editor authored.
            SetOrRemove(o, "Manipulations",
                Manipulations is { Count: > 0 } ? (JArray)Manipulations.DeepClone() : null);

            return o;
        }

        // Penumbra omits empty collections rather than writing "Files": {}. Match that, so saving a
        // playlist doesn't add a key to every "Off" option in the manifest.
        private static void SetOrRemove(JObject target, string key, JToken value)
        {
            if (value == null)
                target.Remove(key);
            else
                target[key] = value;
        }

        private static JObject ToJObject(Dictionary<string, string> map)
        {
            if (map == null || map.Count == 0)
                return null;
            var o = new JObject();
            foreach (var kvp in map)
                o[kvp.Key] = kvp.Value;
            return o;
        }
    }
}
