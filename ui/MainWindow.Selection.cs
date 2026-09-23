using Microsoft.UI.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;
using Windows.UI.Core;

namespace Pickles_Playlist_Editor
{
    public sealed partial class MainWindow
    {
        // The tree's selection.
        //
        // WinUI's TreeView has exactly two selection modes: Single, and a Multiple that puts a
        // checkbox on every row and cascades a playlist's check down to its songs. Neither is
        // ctrl/shift multiselect, so the selection is kept here and the row highlight is bound to
        // PlaylistNodeContent.IsSelected (SelectionMode is None in the XAML).
        //
        // Membership and IsSelected are kept in lockstep by going through Select/Deselect only.
        private readonly List<PlaylistNodeContent> _selection = new();

        // Where a Shift-range starts. Explorer semantics: a plain or Ctrl click moves it, a Shift
        // click extends from it without moving it, so repeated Shift clicks grow and shrink one range.
        private PlaylistNodeContent? _selectionAnchor;

        // The "Off" entry is Options[0], a placeholder rather than a song, and can never be deleted,
        // moved or processed. Keeping it out of the selection is simpler than filtering it out of
        // every batch operation downstream.
        private static bool IsSelectable(PlaylistNodeContent node) =>
            node.Level >= 1 &&
            !(node.Level == 2 && node.Name.Equals("Off", StringComparison.InvariantCultureIgnoreCase));

        // ─── Reading the selection ───────────────────────────────────────────────

        /// <summary>
        /// The selection in tree order, so batch operations act on songs in the order the user sees
        /// them rather than the order they happened to be clicked.
        /// </summary>
        private List<PlaylistNodeContent> SelectedNodes()
        {
            if (_selection.Count <= 1) return new List<PlaylistNodeContent>(_selection);
            var order = new Dictionary<PlaylistNodeContent, int>();
            int i = 0;
            foreach (var node in EnumerateNodes()) order[node] = i++;
            return _selection
                .OrderBy(n => order.TryGetValue(n, out int pos) ? pos : int.MaxValue)
                .ToList();
        }

        /// <summary>
        /// The selection, or — when nothing is selected — the one node given, so a right-click on an
        /// unselected row still acts on that row.
        /// </summary>
        private List<PlaylistNodeContent> SelectionOrNode(PlaylistNodeContent? node)
        {
            if (_selection.Count > 0) return SelectedNodes();
            return node == null ? new List<PlaylistNodeContent>() : new List<PlaylistNodeContent> { node };
        }

        private IEnumerable<PlaylistNodeContent> EnumerateNodes()
        {
            foreach (var root in RootPlaylistItems)
                foreach (var node in Descend(root))
                    yield return node;

            static IEnumerable<PlaylistNodeContent> Descend(PlaylistNodeContent node)
            {
                yield return node;
                foreach (var child in node.Children)
                    foreach (var descendant in Descend(child))
                        yield return descendant;
            }
        }

        // Only rows the user can actually see, which is what a Shift-range spans.
        private List<PlaylistNodeContent> VisibleNodes()
        {
            var result = new List<PlaylistNodeContent>();
            foreach (var root in RootPlaylistItems) Descend(root);
            return result;

            void Descend(PlaylistNodeContent node)
            {
                result.Add(node);
                if (!node.IsExpanded) return;
                foreach (var child in node.Children) Descend(child);
            }
        }

        // ─── Changing the selection ──────────────────────────────────────────────

        private void Select(PlaylistNodeContent node)
        {
            if (!IsSelectable(node) || _selection.Contains(node)) return;
            _selection.Add(node);
            node.IsSelected = true;
        }

        private void Deselect(PlaylistNodeContent node)
        {
            if (!_selection.Remove(node)) return;
            node.IsSelected = false;
        }

        private void ClearSelection()
        {
            foreach (var node in _selection) node.IsSelected = false;
            _selection.Clear();
        }

        private void SetSelection(IEnumerable<PlaylistNodeContent> nodes)
        {
            ClearSelection();
            foreach (var node in nodes) Select(node);
        }

        private void SelectOnly(PlaylistNodeContent node)
        {
            SetSelection(new[] { node });
            _selectionAnchor = node;
            UpdateSelectionCommands();
        }

        /// <summary>
        /// Drops selected nodes that are no longer in the tree. Every rebuild path
        /// (LoadPlaylists, SyncPlaylistNode) replaces node objects wholesale, so without this the
        /// selection would keep orphans alive and batch operations would act on rows nobody can see.
        /// </summary>
        private void PruneSelection()
        {
            if (_selection.Count == 0 && _selectionAnchor == null) return;

            var live = new HashSet<PlaylistNodeContent>(EnumerateNodes());
            for (int i = _selection.Count - 1; i >= 0; i--)
            {
                if (live.Contains(_selection[i])) continue;
                _selection[i].IsSelected = false;
                _selection.RemoveAt(i);
            }
            if (_selectionAnchor != null && !live.Contains(_selectionAnchor))
                _selectionAnchor = null;

            UpdateSelectionCommands();
        }

        /// <summary>
        /// Drops a collapsed playlist's songs from the selection. A batch operation whose targets
        /// the user can no longer see is how a whole playlist gets deleted by accident.
        /// </summary>
        private void DeselectHiddenChildren(PlaylistNodeContent node)
        {
            bool changed = false;
            foreach (var child in node.Children)
            {
                if (!_selection.Contains(child)) continue;
                Deselect(child);
                changed = true;
            }
            if (!changed) return;
            if (_selectionAnchor != null && _selectionAnchor.Parent == node) _selectionAnchor = node;
            UpdateSelectionCommands();
        }

        /// <summary>
        /// A selection that survives a rebuild, identified by what the user sees rather than by node
        /// object. Used across a drag-move, where every touched playlist's song nodes are recreated
        /// but the songs the user picked up should still be picked up when they land.
        /// </summary>
        private static string SelectionKey(int level, string? parentName, string name) =>
            level + "\u0001" + (parentName ?? "") + "\u0001" + name;

        private void RestoreSelection(IReadOnlyCollection<string> keys)
        {
            if (keys.Count == 0) return;
            var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
            SetSelection(EnumerateNodes()
                .Where(n => wanted.Contains(SelectionKey(n.Level, n.Parent?.Name, n.Name))));
            _selectionAnchor = _selection.LastOrDefault();
            UpdateSelectionCommands();
        }

        // ─── Click handling ──────────────────────────────────────────────────────

        private static bool IsKeyDown(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

        /// <summary>
        /// Explorer/Spotify selection: a plain click selects one row, Ctrl toggles a row, Shift
        /// extends a range from the anchor, and Ctrl+Shift adds a range to what's already selected.
        /// </summary>
        private void HandleSelectionClick(PlaylistNodeContent? node)
        {
            bool ctrl = IsKeyDown(VirtualKey.Control);
            bool shift = IsKeyDown(VirtualKey.Shift);

            // Empty space, or the root row: nothing to select.
            if (node == null || !IsSelectable(node))
            {
                if (!ctrl && !shift) { ClearSelection(); _selectionAnchor = null; }
                UpdateSelectionCommands();
                return;
            }

            // A range only makes sense between rows of the same kind — a playlist header and the
            // songs under it are never selected together, because no batch operation means both.
            if (shift && _selectionAnchor != null && _selectionAnchor.Level == node.Level)
            {
                var visible = VisibleNodes();
                int from = visible.IndexOf(_selectionAnchor);
                int to = visible.IndexOf(node);
                if (from >= 0 && to >= 0)
                {
                    if (from > to) (from, to) = (to, from);
                    var range = visible.GetRange(from, to - from + 1).Where(n => n.Level == node.Level);
                    if (ctrl) foreach (var n in range) Select(n);
                    else SetSelection(range);
                    UpdateSelectionCommands();
                    return;
                }
            }

            if (ctrl)
            {
                if (_selection.Contains(node)) Deselect(node);
                else Select(node);
                _selectionAnchor = node;
                UpdateSelectionCommands();
                return;
            }

            // Mixing levels by plain-clicking is the common case, so a plain click always starts over.
            SelectOnly(node);
        }

        private void UpdateSelectionCommands()
        {
            bool hasPlaylist = _selection.Any(n => n.Level == 1);
            bool hasSong = _selection.Any(n => n.Level == 2);

            DeleteButton.IsEnabled = hasPlaylist || hasSong;
            ShuffleButton.IsEnabled = hasPlaylist;
            SortByBPMButton.IsEnabled = hasPlaylist;
            MergeButton.IsEnabled = CanMerge(_selection);
        }
    }
}
