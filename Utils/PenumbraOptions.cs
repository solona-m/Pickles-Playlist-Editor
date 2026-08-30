using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// One selectable option in some mod, flattened out of whichever layout that mod uses.
    ///
    /// <see cref="Files"/> maps a game path — forward slashes, as Penumbra spells them — to a path
    /// relative to the mod folder, with backslashes.
    /// </summary>
    internal sealed class ModOption
    {
        /// <summary>v4 options carry a GUID; v3 options have none.</summary>
        public Guid? Id { get; init; }

        public string Name { get; init; } = string.Empty;

        /// <summary>The group this belongs to, or empty when it came from the mod's default files.</summary>
        public string GroupName { get; init; } = string.Empty;

        /// <summary>Position within the group, which is what <c>DefaultSettings</c> indexes.</summary>
        public int Index { get; init; }

        /// <summary>
        /// True when this is not really an option but the mod's always-on files.
        ///
        /// Worth distinguishing: nine dance mods in a real Penumbra folder have no option groups at
        /// all and ship their dance here, so a reader that only walked groups would report them as
        /// containing nothing.
        /// </summary>
        public bool IsDefaultData { get; init; }

        public IReadOnlyDictionary<string, string> Files { get; init; } =
            new Dictionary<string, string>();

        public override string ToString() =>
            $"{(GroupName.Length > 0 ? GroupName + "/" : "")}{Name} ({Files.Count} files)";
    }

    /// <summary>
    /// Reads the options of ANY mod in the Penumbra folder, whichever layout it is in.
    ///
    /// Deliberately separate from <see cref="PenumbraMeta"/>, which is about the one mod this app
    /// edits: this walks a thousand folders belonging to other people, so it is read-only, forgiving
    /// of anything it does not recognise, and never retries — an unreadable stranger's mod is a mod to
    /// skip, not a failure to report. Modelled on <see cref="PenumbraMeta.CollectScdKeys"/>, which
    /// solves the same problem for a narrower question.
    /// </summary>
    internal static class PenumbraOptions
    {
        // Spelled out here rather than taken from PenumbraMeta so this file depends on nothing.
        // PenumbraMeta reaches Playlist and Settings, which reach the whole application; keeping
        // this leaf-level is what lets the scan be exercised offline against a real Penumbra
        // folder, and that has already earned its keep twice this feature.
        private const string MetaFile = "meta.json";
        private const string LegacyDefaultMod = "default_mod.json";

        /// <summary>The mod's display name, which is often not its folder name.</summary>
        public static string DisplayName(string modDirectory)
        {
            var root = TryParse(Path.Combine(modDirectory, MetaFile));
            string? name = root?["Name"]?.ToString();
            return string.IsNullOrWhiteSpace(name) ? Path.GetFileName(modDirectory) : name;
        }

        /// <summary>The identifier Penumbra keys the mod by, used to spot duplicate folders.</summary>
        public static string? Identifier(string modDirectory) =>
            TryParse(Path.Combine(modDirectory, MetaFile))?["Identifier"]?.ToString();

        /// <summary>
        /// Every option in the mod, including its default files as one synthetic entry.
        ///
        /// Returns an empty list rather than throwing for anything unreadable.
        /// </summary>
        public static List<ModOption> Read(string modDirectory)
        {
            var options = new List<ModOption>();
            if (string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory))
                return options;

            var root = TryParse(Path.Combine(modDirectory, MetaFile));

            if (root != null)
            {
                AddDefaultData(root["DefaultData"] as JObject, options);

                if (root["Groups"] is JArray groups)
                {
                    foreach (var group in groups.OfType<JObject>())
                        AddGroup(group, options);
                }
            }

            // v3 keeps its groups in sibling files and its defaults in default_mod.json. Branch on the
            // detected FORMAT rather than on whether a Groups array is present: Penumbra omits that key
            // for any v4 mod with no groups, so keying off it would send hundreds of healthy v4 mods
            // down this path for nothing.
            if (IsLegacyLayout(root, modDirectory))
            {
                AddDefaultData(TryParse(Path.Combine(modDirectory, LegacyDefaultMod)), options);

                foreach (string file in EnumerateSafely(modDirectory, "group_*.json"))
                    AddGroup(TryParse(file), options);
            }

            return options;
        }

        private static void AddGroup(JObject? group, List<ModOption> into)
        {
            if (group?["Options"] is not JArray entries) return;
            string groupName = group["Name"]?.ToString() ?? string.Empty;

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] is not JObject option) continue;
                into.Add(new ModOption
                {
                    Id = Guid.TryParse(option["Id"]?.ToString(), out var id) ? id : null,
                    Name = option["Name"]?.ToString() ?? string.Empty,
                    GroupName = groupName,
                    Index = i,
                    Files = ReadFiles(option["Files"] as JObject),
                });
            }
        }

        private static void AddDefaultData(JObject? node, List<ModOption> into)
        {
            var files = ReadFiles(node?["Files"] as JObject);
            if (files.Count == 0) return;

            into.Add(new ModOption
            {
                Name = string.Empty,
                GroupName = string.Empty,
                Index = -1,
                IsDefaultData = true,
                Files = files,
            });
        }

        private static Dictionary<string, string> ReadFiles(JObject? files)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (files == null) return map;

            foreach (var property in files.Properties())
            {
                string value = property.Value?.ToString() ?? string.Empty;
                if (value.Length > 0) map[NormalizeGamePath(property.Name)] = value;
            }
            return map;
        }

        /// <summary>A game path as Penumbra spells it: trimmed, forward slashes, no leading slash.</summary>
        private static string NormalizeGamePath(string? path) =>
            (path ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');

        /// <summary>
        /// Whether the mod keeps its groups in sibling group_*.json files rather than inline.
        ///
        /// Discriminates on FileVersion, not on shape: Penumbra omits the Groups key entirely for a
        /// v4 mod that has no option groups, so a healthy modern mod and a legacy one look alike
        /// from their contents. Mirrors PenumbraMeta.DetectFormat, which is the authority for the
        /// mod this app edits.
        /// </summary>
        private static bool IsLegacyLayout(JObject? root, string modDirectory)
        {
            if (root?["FileVersion"] is JValue { Type: JTokenType.Integer } version)
                return (int)version is >= 1 and < 4;

            if (root?["Groups"] is JArray || root?["DefaultData"] is JObject) return false;

            return EnumerateSafely(modDirectory, "group_*.json").Any()
                || File.Exists(Path.Combine(modDirectory, LegacyDefaultMod));
        }

        private static JObject? TryParse(string path)
        {
            try
            {
                return File.Exists(path)
                    ? JObject.Parse(File.ReadAllText(path, Encoding.UTF8))
                    : null;
            }
            catch
            {
                // A stranger's mod that will not parse is one to walk past.
                return null;
            }
        }

        private static IEnumerable<string> EnumerateSafely(string directory, string pattern)
        {
            try { return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly); }
            catch { return Array.Empty<string>(); }
        }
    }
}
