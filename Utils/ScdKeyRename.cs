using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Moves a Penumbra redirect from one game path to another, inside the <c>Files</c> map of an
    /// option, a group's <c>DefaultData</c>, or a v3 <c>default_mod.json</c>.
    ///
    /// Both overloads REBUILD the map rather than editing it in place. Removing the old key and
    /// adding the new one is equivalent as data and wrong as a write: it moves the renamed entry to
    /// the end, so a one-key change reads as a whole-file rewrite to any diff — and to Penumbra's own
    /// file watcher, which is the churn <see cref="PenumbraMeta.Serialize"/> goes to some trouble to
    /// avoid. A manifest here holds ~1600 song entries, so the difference is not cosmetic.
    ///
    /// Kept free of the rest of the app so it can be exercised against a real manifest on its own;
    /// <see cref="SoundPathRename"/> owns the orchestration.
    /// </summary>
    internal static class ScdKeyRename
    {
        /// <summary>A game path as Penumbra spells it. Mirrors <see cref="PenumbraMeta.NormalizeScdKey"/>.</summary>
        public static string Normalize(string? key) =>
            (key ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');

        public static bool KeyMatches(string key, string path) =>
            string.Equals(Normalize(key), Normalize(path), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Renames the key inside <c>owner["Files"]</c>, returning how many entries moved. A no-op —
        /// and no write to <c>owner</c> at all — when the old key is not there.
        /// </summary>
        public static int RenameInFiles(JObject? owner, string oldPath, string newPath)
        {
            if (owner?["Files"] is not JObject files)
                return 0;

            var replacement = new JObject();
            int renamed = 0;

            foreach (var property in files.Properties())
            {
                string name = KeyMatches(property.Name, oldPath) ? Normalize(newPath) : property.Name;
                if (name != property.Name) renamed++;

                // Deep-cloned: the value still belongs to the document being read, and assigning a
                // token that already has a parent grafts one document's node into another.
                replacement[name] = property.Value?.DeepClone() ?? JValue.CreateNull();
            }

            if (renamed > 0)
                owner["Files"] = replacement;
            return renamed;
        }

        /// <summary>
        /// Renames the key in an <see cref="Option.Files"/> map. True when something moved.
        /// </summary>
        public static bool RenameInFiles(Dictionary<string, string> files, string oldPath, string newPath)
        {
            if (files == null) return false;

            var match = files.Keys.FirstOrDefault(k => KeyMatches(k, oldPath));
            if (match == null)
                return false;

            var rebuilt = new List<KeyValuePair<string, string>>(files.Count);
            foreach (var pair in files)
                rebuilt.Add(new KeyValuePair<string, string>(
                    pair.Key == match ? Normalize(newPath) : pair.Key, pair.Value));

            files.Clear();
            foreach (var pair in rebuilt)
                files[pair.Key] = pair.Value;
            return true;
        }

        /// <summary>
        /// Renames the key across a whole v4 manifest: <c>DefaultData</c> plus every option of every
        /// group. Returns how many entries moved. Mutates <paramref name="root"/> in place, for use
        /// inside <see cref="PenumbraMeta.Mutate"/>.
        /// </summary>
        public static int RenameInManifest(JObject root, string oldPath, string newPath)
        {
            int renamed = RenameInFiles(root["DefaultData"] as JObject, oldPath, newPath);

            if (root["Groups"] is JArray groups)
            {
                foreach (var group in groups.OfType<JObject>())
                {
                    if (group["Options"] is not JArray options) continue;
                    foreach (var option in options.OfType<JObject>())
                        renamed += RenameInFiles(option, oldPath, newPath);
                }
            }

            return renamed;
        }

        /// <summary>How many entries a rename would move, without moving any.</summary>
        public static int CountInManifest(JObject? root, string path)
        {
            if (root == null) return 0;
            int found = CountInFiles(root["DefaultData"] as JObject, path);

            if (root["Groups"] is JArray groups)
            {
                foreach (var group in groups.OfType<JObject>())
                {
                    if (group["Options"] is not JArray options) continue;
                    foreach (var option in options.OfType<JObject>())
                        found += CountInFiles(option, path);
                }
            }

            return found;
        }

        public static int CountInFiles(JObject? owner, string path) =>
            owner?["Files"] is JObject files
                ? files.Properties().Count(p => KeyMatches(p.Name, path))
                : 0;
    }
}
