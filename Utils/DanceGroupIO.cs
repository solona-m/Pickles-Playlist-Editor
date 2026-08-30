using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Which group of which mod to edit, in terms that work for both Penumbra layouts.
    ///
    /// v4 groups have a GUID; v3 groups are files identified by the <c>Name</c> INSIDE them rather
    /// than by their filename. Both are carried so neither layout has to be guessed at later.
    /// </summary>
    internal sealed class DanceGroupRef
    {
        public string ModRoot { get; init; } = string.Empty;
        public string ModName { get; init; } = string.Empty;
        public ModFormat Format { get; init; }

        public Guid? Id { get; init; }
        public string Name { get; init; } = string.Empty;

        /// <summary>Position in <c>Groups</c> (v4). Ignored for v3.</summary>
        public int Index { get; init; }

        /// <summary>The group_*.json this lives in (v3). Null for v4.</summary>
        public string? FilePath { get; init; }

        public override string ToString() => $"{ModName}/{Name}";
    }

    /// <summary>
    /// Reads and rewrites ONE option group of ANY mod, in either Penumbra layout.
    ///
    /// Narrow on purpose. The dances feature only ever edits the options of a group that already
    /// exists — it never creates, deletes, renumbers or reorders groups themselves. That restriction
    /// is what keeps the v3 path small and safe: <see cref="V3GroupFileStore"/>'s renumbering rules
    /// exist because getting a group FILE wrong condemns a mod to a permanent rewrite loop, and none
    /// of that machinery is on this path.
    ///
    /// Writes go through the same primitives the playlist library uses, so the formatting Penumbra
    /// expects — tabs and LF for v4, two spaces and CRLF for v3, no BOM either way — is not
    /// reimplemented here and cannot drift.
    /// </summary>
    internal static class DanceGroupIO
    {
        /// <summary>Every option group in a mod, whichever layout it uses.</summary>
        public static List<DanceGroupRef> ListGroups(string modRoot, string modName)
        {
            var groups = new List<DanceGroupRef>();
            if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot)) return groups;

            var root = PenumbraMeta.Read(modRoot);
            var format = PenumbraMeta.DetectFormat(root, modRoot);

            if (format == ModFormat.V3)
            {
                foreach (string file in V3GroupFileStore.GroupFilesOrdered(modRoot))
                {
                    string? name = V3GroupFileStore.TryReadContentName(file);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    groups.Add(new DanceGroupRef
                    {
                        ModRoot = modRoot,
                        ModName = modName,
                        Format = ModFormat.V3,
                        Name = name,
                        Index = groups.Count,
                        FilePath = file,
                    });
                }
                return groups;
            }

            if (root?["Groups"] is not JArray inline) return groups;

            for (int i = 0; i < inline.Count; i++)
            {
                if (inline[i] is not JObject group) continue;
                groups.Add(new DanceGroupRef
                {
                    ModRoot = modRoot,
                    ModName = modName,
                    Format = ModFormat.V4,
                    Id = Guid.TryParse(group["Id"]?.ToString(), out var id) ? id : null,
                    Name = group["Name"]?.ToString() ?? string.Empty,
                    Index = i,
                });
            }
            return groups;
        }

        /// <summary>The group's options, in order. Empty when the group cannot be read.</summary>
        public static JArray ReadOptions(DanceGroupRef group) =>
            Load(group)?["Options"] as JArray ?? new JArray();

        /// <summary>
        /// Reads the group, hands it to <paramref name="edit"/>, and writes it back.
        ///
        /// Snapshots the manifest first in both layouts, so any edit here is recoverable from the
        /// backup folder rather than only from whatever the user happens to have.
        /// </summary>
        public static void Mutate(DanceGroupRef group, Action<JObject> edit)
        {
            if (group.Format == ModFormat.V3)
            {
                MutateV3(group, edit);
                return;
            }

            PenumbraMeta.Mutate(group.ModRoot, group.ModName, root =>
            {
                var target = Resolve(root, group)
                    ?? throw new PenumbraMetaException(
                        $"'{group.Name}' is no longer in {group.ModName}; nothing was written.");
                edit(target);
            });
        }

        private static void MutateV3(DanceGroupRef group, Action<JObject> edit)
        {
            string path = group.FilePath
                ?? throw new PenumbraMetaException($"'{group.Name}' has no group file to write.");

            lock (PlaylistStore.ModFolderGate)
            {
                var json = V3GroupFileStore.TryLoadGroupQuiet(path, out string error)
                    ?? throw new PenumbraMetaException(
                        $"Could not read '{Path.GetFileName(path)}': {error}. Nothing was written.");

                // Snapshot before touching it. WriteGroupFile keeps its own .bak of the single file;
                // this keeps the dated history the rest of the app relies on for recovery.
                PenumbraMeta.TrySnapshot(group.ModRoot, group.ModName);

                edit(json);
                V3GroupFileStore.WriteGroupFile(path, json);
            }
        }

        private static JObject? Load(DanceGroupRef group)
        {
            if (group.Format == ModFormat.V3)
                return group.FilePath == null
                    ? null
                    : V3GroupFileStore.TryLoadGroupQuiet(group.FilePath, out _);

            var root = PenumbraMeta.Read(group.ModRoot);
            return root == null ? null : Resolve(root, group);
        }

        /// <summary>
        /// Finds the group again in a freshly read manifest.
        ///
        /// By Id first and by name second, never by index alone: Penumbra can rewrite the manifest
        /// between the read and the write — it did exactly that during development, silently
        /// discarding an edit — and an index that moved would then point the write at a different
        /// group entirely.
        /// </summary>
        private static JObject? Resolve(JObject root, DanceGroupRef group)
        {
            if (group.Id is { } id && PenumbraMeta.TryGetGroupById(root, id, out var byId, out _))
                return byId;

            return PenumbraMeta.FindGroupByName(root, group.Name);
        }

        // ---- DefaultSettings ---------------------------------------------------------------------

        /// <summary>
        /// Repairs the group's default selection after its options move.
        ///
        /// <c>DefaultSettings</c> on a Single group is an option INDEX in both layouts, so removing or
        /// reordering options silently changes which dance the mod defaults to unless it is remapped.
        /// <paramref name="moved"/> maps old index to new, with -1 for an option that is gone.
        /// </summary>
        public static void RemapDefaultSettings(JObject group, IReadOnlyDictionary<int, int> moved)
        {
            if (group["DefaultSettings"] is not JValue { Type: JTokenType.Integer } value) return;

            int old = (int)value;
            if (!moved.TryGetValue(old, out int now))
                return;

            // A removed default falls back to the first option, which is what Penumbra shows anyway
            // once the index it held no longer exists.
            group["DefaultSettings"] = now < 0 ? 0 : now;
        }
    }
}
