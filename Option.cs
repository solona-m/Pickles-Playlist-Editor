using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
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
    /// generated for genuinely new options. A v3 group file has no Id at all — see
    /// <see cref="ToJson(JObject, ModFormat)"/>.
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
        /// This option as a manifest object. Built by cloning <paramref name="existing"/> so keys this
        /// app doesn't model (Image, Priority, manipulations authored in Penumbra's editor) survive
        /// the round trip, then overwriting only the fields we own.
        /// </summary>
        internal JObject ToJson(JObject existing, ModFormat format)
        {
            var o = existing != null ? (JObject)existing.DeepClone() : new JObject();

            if (Id == Guid.Empty)
                Id = Guid.NewGuid();

            // v3 options have no Id. Writing one would be a key Penumbra's v3 reader doesn't consume
            // and strips on its next rewrite — so identity built on it would evaporate between
            // sessions while the code still looked like it had stable identity. It would also change
            // the file's bytes on every save, waking Penumbra's watcher for nothing.
            if (format == ModFormat.V4)
                o["Id"] = Id.ToString();

            o["Name"] = Name ?? string.Empty;

            // The two layouts differ on empty values, and each is matched exactly so that saving a
            // playlist doesn't gratuitously add or drop keys Penumbra would write differently:
            //   v4 omits them   — an option with no swaps has no "FileSwaps" key at all
            //   v3 spells them out — "Description": "", "Files": {}, "FileSwaps": {}, "Manipulations": []
            bool v3 = format == ModFormat.V3;

            SetOrRemove(o, "Description",
                string.IsNullOrEmpty(Description) ? (v3 ? new JValue(string.Empty) : null) : new JValue(Description));
            if (v3 && o["Image"] == null)
                o["Image"] = string.Empty;
            SetOrRemove(o, "Files", ToJObject(Files) ?? (v3 ? new JObject() : null));
            SetOrRemove(o, "FileSwaps", ToJObject(FileSwaps) ?? (v3 ? new JObject() : null));

            // Deep-clone: Manipulations still belongs to the JObject this option was read from, and
            // assigning a token that already has a parent grafts one document's node into another.
            // Newtonsoft does copy on assignment, but relying on that is a subtle dependency in the
            // one path whose entire job is to preserve data Penumbra's editor authored.
            SetOrRemove(o, "Manipulations",
                Manipulations is { Count: > 0 } ? (JArray)Manipulations.DeepClone() : (v3 ? new JArray() : null));

            // Emit v3 keys in Penumbra's own order too. Same data in a different shape still makes
            // Penumbra rewrite the whole file on its next load, which is the churn we're avoiding.
            if (v3)
                Playlist.Reshape(o, V3OptionKeyOrder, _ => null);

            return o;
        }

        // Penumbra's v3 option key order, taken from a file it wrote itself. "Priority" appears only
        // on options of a Multi group; Reshape skips keys that are absent and have no default.
        private static readonly string[] V3OptionKeyOrder =
            { "Name", "Description", "Image", "Priority", "Files", "FileSwaps", "Manipulations" };

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
