using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Persistence for Penumbra's v4 layout: every option group lives in the single root
    /// <c>meta.json</c>, keyed by a stable <c>Id</c> GUID, ordered by its index in the
    /// <c>Groups</c> array.
    ///
    /// The whole library is one ~360KB file here, so nothing is ever rebuilt from scratch: every
    /// write goes through <see cref="PenumbraMeta.Mutate"/>, which re-reads the manifest under the
    /// lock and lets this class splice in only the group it owns.
    /// </summary>
    internal sealed class V4MetaStore : IPlaylistStore
    {
        public ModFormat Format => ModFormat.V4;

        public List<Playlist> ReadAll()
        {
            var result = new List<Playlist>();

            var root = PenumbraMeta.Read();
            var groups = PenumbraMeta.TryReadGroups(root);
            if (groups == null)
            {
                // Penumbra omits the Groups key entirely for a mod with no option groups, so "no
                // Groups" is the NORMAL state of an empty mod, not evidence of damage. The only way
                // to tell an empty mod from one that lost its groups is external: a snapshot of this
                // same mod that still has some. Reporting damage for both sends a user with a
                // brand-new mod chasing a Repair that has nothing to restore.
                if (root == null)
                    Logger.LogError(
                        "'{Mod}' has no readable meta.json, so no playlists could be loaded. Nothing " +
                        "has been written. Check the mod folder in Settings.", Settings.ModName);
                else if (PenumbraMeta.NewestUsableSnapshot() != null)
                    Logger.LogError(
                        "meta.json has no Groups array, but a backup of '{Mod}' still has playlists in " +
                        "it — the manifest looks damaged. Nothing has been written. Use Repair Library " +
                        "to restore it.", Settings.ModName);
                else
                    Logger.LogInfo("'{Mod}' has no option groups yet — no playlists to load.", Settings.ModName);
                return result;
            }

            // Array order IS the order — Penumbra displays groups by their index in this array. There
            // is nothing left to sort by.
            foreach (var g in groups.OfType<JObject>())
                result.Add(Playlist.FromJson(g));

            return result;
        }

        public void Save(Playlist playlist)
        {
            PenumbraMeta.Mutate(root =>
            {
                if (!ResolveTarget(root, playlist, out var existing, out int index))
                {
                    // The whole "adding deleted my playlist" class of bug lives here: if the group is
                    // gone from the manifest, the edit has nowhere to go. Throw so callers can roll
                    // back and the user sees an error instead of silent data loss.
                    Logger.LogError("Save('{Name}'): group not found in meta.json — aborting, nothing written.",
                        playlist.Name);
                    throw new PlaylistSaveException(playlist.Name);
                }

                ((JArray)root["Groups"])[index] = playlist.ToJson(existing, ModFormat.V4);
            });

            playlist.PersistedName = playlist.Name;
            Logger.LogInfo("Saved playlist '{Name}' ({Count} options) to meta.json",
                playlist.Name, playlist.Options?.Count ?? 0);
        }

        public void Upsert(Playlist playlist)
        {
            PenumbraMeta.Mutate(root =>
            {
                // Creating the array is the NORMAL path for the first playlist in a mod, not an edge
                // case — Penumbra omits the key until there is something to put in it.
                if (root["Groups"] is not JArray groups)
                {
                    groups = new JArray();
                    root["Groups"] = groups;
                }

                if (ResolveTarget(root, playlist, out var existing, out int index))
                    groups[index] = playlist.ToJson(existing, ModFormat.V4);
                else
                    groups.Add(playlist.ToJson(null, ModFormat.V4));
            });

            playlist.PersistedName = playlist.Name;
            Logger.LogInfo("Upserted playlist '{Name}' ({Count} options) into meta.json",
                playlist.Name, playlist.Options?.Count ?? 0);
        }

        public void Delete(Playlist playlist)
        {
            PenumbraMeta.Mutate(root =>
            {
                if (root["Groups"] is not JArray groups) return;
                if (ResolveTarget(root, playlist, out _, out int index))
                    groups.RemoveAt(index);
            });
        }

        public bool Exists(Playlist playlist)
        {
            var root = PenumbraMeta.Read();
            return root != null && ResolveTarget(root, playlist, out _, out _);
        }

        public bool NameInUse(string name)
        {
            var root = PenumbraMeta.Read();
            return root != null && PenumbraMeta.FindGroupByName(root, name) != null;
        }

        /// <summary>
        /// Penumbra orders groups by their index in the manifest's Groups array, so this is a single
        /// permutation of that array — one atomic write, and no intermediate on-disk state to recover
        /// from if it fails.
        /// </summary>
        public void ReorderAll(IReadOnlyList<string> orderedNames)
        {
            PenumbraMeta.Mutate(root =>
            {
                if (root["Groups"] is not JArray groups)
                    throw new PenumbraMetaException("meta.json has no Groups array; the reorder was not applied.");

                var remaining = groups.OfType<JObject>().ToList();
                var reordered = new JArray();

                foreach (string name in orderedNames)
                {
                    var match = remaining.FirstOrDefault(g =>
                        string.Equals(g["Name"]?.ToString(), name, StringComparison.Ordinal));
                    if (match == null)
                    {
                        Logger.LogWarn("ReorderAll: no group found for '{Name}' — it keeps its old position.", name);
                        continue;
                    }
                    remaining.Remove(match);
                    reordered.Add(match);
                }

                // Anything the app didn't model (or couldn't match) still has to land in the output.
                foreach (var leftover in remaining)
                    reordered.Add(leftover);

                if (reordered.Count != groups.Count)
                    throw new PenumbraMetaException(
                        $"Reorder would have changed the group count ({groups.Count} -> {reordered.Count}); " +
                        "refusing to write.");

                root["Groups"] = reordered;
            });
        }

        /// <summary>
        /// Under v4 a <c>.reorder_tmp</c> is dead weight from a pre-v4 build — the v4 reorder is one
        /// atomic write and never stages one. Move (never delete) them out of the folder Penumbra
        /// scans. Under v3 the same file is a live playlist mid-reorder, which is why this is per
        /// store rather than shared.
        /// </summary>
        public void HealOnLoad()
        {
            try
            {
                string root = PenumbraMeta.ModRoot;
                if (!Directory.Exists(root)) return;

                foreach (var file in Directory.EnumerateFiles(root, "group_*.json.reorder_tmp").ToList())
                {
                    try
                    {
                        File.Move(file, Path.Combine(Playlist.BackupDir, Path.GetFileName(file)), overwrite: true);
                        Logger.LogInfo("Moved a stale reorder file out of the mod folder: {File}",
                            Path.GetFileName(file));
                    }
                    catch { /* locked or already gone; skip */ }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("V4 HealOnLoad failed (harmless): {Error}", ex.Message);
            }
        }

        /// <summary>
        /// Locates this playlist's group in a freshly-read manifest. Id first — that is the stable
        /// identity Penumbra keys user selections against — falling back to an exact name match for a
        /// group that has no Id yet (just created, or imported from v3). The fallback matches on the
        /// name currently ON DISK, so it still works mid-rename. A successful name match adopts the
        /// Id, so the fallback fires at most once per playlist.
        /// </summary>
        private static bool ResolveTarget(JObject root, Playlist playlist, out JObject group, out int index)
        {
            if (PenumbraMeta.TryGetGroupById(root, playlist.Id, out group, out index))
                return true;

            var byName = PenumbraMeta.FindGroupByName(root, playlist.PersistedName ?? playlist.Name, out index);
            if (byName == null)
                return false;

            group = byName;
            if (Guid.TryParse(byName["Id"]?.ToString(), out var found))
                playlist.Id = found;
            return true;
        }
    }
}
