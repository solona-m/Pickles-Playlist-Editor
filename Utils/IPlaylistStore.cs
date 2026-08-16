using System.Collections.Generic;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Which on-disk layout a Penumbra mod folder is currently in.
    ///
    /// Penumbra went to v4 (everything in one root meta.json) and then reversed course, so both
    /// layouts are live steady states rather than a migration with a direction. The app reads and
    /// writes whichever it finds and never converts one to the other — converting is Penumbra's call,
    /// not ours.
    /// </summary>
    internal enum ModFormat
    {
        /// <summary>No mod folder, no meta.json, or a meta.json that doesn't parse.</summary>
        Unknown,

        /// <summary>meta.json with FileVersion &lt; 4, plus default_mod.json and group_NNN_*.json.</summary>
        V3,

        /// <summary>meta.json with FileVersion 4, holding Identifier, DefaultData and Groups.</summary>
        V4,
    }

    /// <summary>
    /// Persistence for the mod's option groups, in one specific layout.
    ///
    /// This interface is deliberately narrow: it covers only reading and writing groups. Everything
    /// format-neutral — deriving a playlist's .scd folder, moving audio, migrating the BPM/key caches,
    /// pinning "Off" first, VFX detection, the Penumbra reload debounce — stays in
    /// <see cref="Playlist"/> and is shared by both stores.
    /// </summary>
    internal interface IPlaylistStore
    {
        ModFormat Format { get; }

        /// <summary>Every playlist, in Penumbra's display order. Empty (never null) on failure.</summary>
        List<Playlist> ReadAll();

        /// <summary>
        /// Splices this playlist back into storage. Throws <see cref="PlaylistSaveException"/> when it
        /// is no longer there, so callers can roll back rather than silently drop the edit.
        /// </summary>
        void Save(Playlist playlist);

        /// <summary>Like <see cref="Save"/>, but creates the group when absent. New playlists only.</summary>
        void Upsert(Playlist playlist);

        /// <summary>Removes the group. No-op when already gone. Never touches audio files.</summary>
        void Delete(Playlist playlist);

        /// <summary>True when <see cref="Save"/> would have somewhere to write.</summary>
        bool Exists(Playlist playlist);

        /// <summary>
        /// True when some group already carries this name. Callers only ask about a name that differs
        /// from the playlist's own, so there is no "except me" parameter.
        /// </summary>
        bool NameInUse(string name);

        /// <summary>Applies this display order to every group.</summary>
        void ReorderAll(IReadOnlyList<string> orderedNames);

        /// <summary>
        /// Format-specific crash recovery, run before each load. Best-effort — must never throw, since
        /// it runs on the startup path.
        /// </summary>
        void HealOnLoad();
    }

    internal static class PlaylistStore
    {
        /// <summary>
        /// One gate for the whole mod folder, whatever layout it is in. Reorders run on a background
        /// thread (MainWindow.DragDrop) and downloads save from their own tasks, so concurrent writes
        /// are real. Penumbra is a separate process and can't be locked out — the retry loops in
        /// <see cref="PenumbraMeta.AtomicWrite"/> and the v3 writer are the mitigation there.
        /// </summary>
        internal static readonly object ModFolderGate = new();

        private static readonly V4MetaStore s_v4 = new();
        private static readonly V3GroupFileStore s_v3 = new();

        /// <summary>
        /// The store matching the folder's layout right now. Deliberately re-detected on every access
        /// rather than cached: Penumbra rewrites the folder when it reloads a mod, this app asks it to
        /// reload (<see cref="Playlist.RefreshPenumbraMod"/>), and the configured mod is changeable at
        /// runtime from the Settings dialog. Detection costs one meta.json read, and on the write path
        /// it is free — Mutate re-detects from the manifest it already parsed.
        /// </summary>
        internal static IPlaylistStore Current => For(PenumbraMeta.DetectFormat());

        internal static IPlaylistStore For(ModFormat format) => format switch
        {
            ModFormat.V3 => s_v3,

            // V4 and Unknown. An unidentifiable folder is far more likely a missing or damaged v4
            // manifest than a v3 one, and the v4 store's failure modes are the ones we want: Read()
            // returns null and Mutate throws "Nothing was written". Routing Unknown to v3 would send
            // it hunting for group files, find none, and report the wrong diagnosis.
            _ => s_v4,
        };
    }
}
