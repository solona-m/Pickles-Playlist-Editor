using System;
using System.Collections.Generic;
using System.Linq;

namespace Pickles_Playlist_Editor
{
    /// <summary>Remembers playlist expand state from before a search and puts it back when the search is cleared.</summary>
    internal sealed class SearchExpansionMemory
    {
        private Dictionary<string, bool>? _snapshot;

        /// <summary>A pre-search snapshot is held.</summary>
        internal bool Active => _snapshot != null;

        /// <summary>Call before LoadPlaylists(filter). Empty or whitespace filter = no search.</summary>
        internal void OnFilterChanged(string? filter, Dictionary<string, bool> states, IEnumerable<string> existing)
        {
            if (!string.IsNullOrWhiteSpace(filter))
            {
                if (_snapshot != null) return;
                _snapshot = new Dictionary<string, bool>(states, StringComparer.Ordinal);
                foreach (var name in existing)
                    _snapshot.TryAdd(name, false);
                return;
            }

            if (_snapshot == null) return;
            var restored = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var name in existing.ToList())
            {
                if (_snapshot.TryGetValue(name, out bool was)) restored[name] = was;
                else if (states.TryGetValue(name, out bool now)) restored[name] = now;
            }
            states.Clear();
            foreach (var kv in restored) states[kv.Key] = kv.Value;
            _snapshot = null;
        }

        /// <summary>Moves a renamed playlist's entry; a playlist with no entry drops any stale one under its new name.</summary>
        internal void OnRenamed(string oldName, string newName)
        {
            if (_snapshot == null) return;
            if (_snapshot.Remove(oldName, out bool was)) _snapshot[newName] = was;
            else _snapshot.Remove(newName);
        }

        /// <summary>Drops a name so a playlist created under it counts as new.</summary>
        internal void Forget(string name) => _snapshot?.Remove(name);
    }
}
