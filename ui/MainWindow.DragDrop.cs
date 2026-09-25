using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace Pickles_Playlist_Editor
{
    public sealed partial class MainWindow
    {
        // What the current drag is carrying: the whole selection when the grabbed row was part of
        // it, otherwise just that row. In tree order, so a multi-song drop lands the songs in the
        // order the user sees them rather than the order they were clicked.
        private List<PlaylistNodeContent> _draggedContents = new();
        // Songs resolved at drag start, before WinUI pulls the grabbed node out of ItemsSource.
        // Resolving later would have to fall back to matching on name, which picks the wrong song
        // when a playlist holds two with the same one.
        private List<(Playlist source, Option song)> _draggedSongs = new();
        private PlaylistNodeContent? _pendingDropTarget;
        private bool _dragInProgress;
        // Set by the reorder handlers to the targeted, incremental tree update to run after WinUI
        // finishes its own post-drag cleanup (applied in the deferred DragItemsCompleted callback),
        // instead of tearing down and rebuilding the whole tree via LoadPlaylists().
        private Action? _pendingTreeUpdate;

        private void PlaylistTreeView_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs e)
        {
            if (e.Items.Count != 1 || e.Items[0] is not PlaylistNodeContent grabbed) return;

            // Dragging a row that isn't selected is a drag of that row alone, and it takes the
            // selection with it — otherwise the highlight would point somewhere else the whole time.
            if (!_selection.Contains(grabbed)) SelectOnly(grabbed);

            // Only rows of the grabbed row's kind travel: a playlist and a song move by completely
            // different rules, and there is no drop that means both.
            _draggedContents = SelectedNodes().Where(n => n.Level == grabbed.Level).ToList();
            if (_draggedContents.Count == 0) _draggedContents.Add(grabbed);

            _draggedSongs = ResolveDraggedSongs(_draggedContents);

            _dragInProgress = true;
            _pendingDropTarget = null;
            e.Data.RequestedOperation = DataPackageOperation.Move;
        }

        // Pairs each dragged song node with its Option. Position identifies the song exactly —
        // song nodes are built straight from Options — which is what distinguishes two songs
        // sharing a name. Falls back to the name when the tree is a filtered subset of the playlist.
        private static List<(Playlist source, Option song)> ResolveDraggedSongs(
            IEnumerable<PlaylistNodeContent> nodes)
        {
            var resolved = new List<(Playlist, Option)>();
            var seen = new HashSet<Option>();
            foreach (var node in nodes)
            {
                if (node.Level != 2 || node.Parent == null) continue;
                if (!Playlists.TryGetValue(node.Parent.Name, out var source) || source?.Options == null) continue;

                Option? song = null;
                var siblings = node.Parent.Children;
                if (siblings.Count == source.Options.Count)
                {
                    int index = siblings.IndexOf(node);
                    if (index >= 0) song = source.Options[index];
                }
                song ??= source.Options.Find(x => x.Name == node.Name);

                if (song != null && seen.Add(song)) resolved.Add((source, song));
            }
            return resolved;
        }

        private void PlaylistTreeView_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            // Track hover target so DragItemsCompleted knows where to insert.
            // GetPosition(null) returns root/host coordinates, which is what
            // FindElementsInHostCoordinates requires.
            _pendingDropTarget = FindContentAtPosition(e.GetPosition(null));
        }

        // External file drops from Explorer
        private async void PlaylistTreeView_Drop(object sender, DragEventArgs e)
        {
            await HandleExternalDropAsync(e);
        }

        // Internal reorders — Drop doesn't fire for intra-TreeView drags with ItemsSource;
        // DragItemsCompleted always fires when a drag started here finishes.
        private async void PlaylistTreeView_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
        {
            var draggedContents = _draggedContents;
            var draggedSongs = _draggedSongs;
            var dropContent = _pendingDropTarget;
            _draggedContents = new List<PlaylistNodeContent>();
            _draggedSongs = new List<(Playlist, Option)>();
            _pendingDropTarget = null;
            _pendingTreeUpdate = null;

            Utils.Logger.LogInfo("DragCompleted: result={Result} dragged={Count} first='{Dragged}'(L{DL}) drop='{Drop}'(L{TL})",
                args.DropResult, draggedContents.Count,
                draggedContents.FirstOrDefault()?.Name ?? "(null)", draggedContents.FirstOrDefault()?.Level ?? -1,
                dropContent?.Name ?? "(null)", dropContent?.Level ?? -1);

            if (args.DropResult == DataPackageOperation.None)
            {
                // Cancelled — WinUI reverts its own visual reorder. Only repair if a dragged node
                // actually went missing, so a normal cancel doesn't flicker the whole tree.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    _dragInProgress = false;
                    RepairDraggedNodes(draggedContents);
                });
                return;
            }

            if (draggedContents.Count > 0 && dropContent != null && !draggedContents.Contains(dropContent))
                await HandleInternalReorderAsync(draggedContents, draggedSongs, dropContent);

            // Apply the targeted tree update after WinUI finishes its own post-drag cleanup (which
            // is why this is deferred to Low priority). Only touches the affected node(s); no rebuild.
            var pending = _pendingTreeUpdate;
            _pendingTreeUpdate = null;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _dragInProgress = false;
                if (pending != null) pending();
                else RepairDraggedNodes(draggedContents);
            });
        }

        private async Task HandleExternalDropAsync(DragEventArgs e)
        {
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                var items = await e.DataView.GetStorageItemsAsync();
                var files = items
                    .OfType<Windows.Storage.StorageFile>()
                    .Select(f => f.Path)
                    .ToArray();
                if (files.Length == 0) return;

                PlaylistNodeContent? targetContent = FindContentAtPosition(e.GetPosition(null));
                GetPlaylistFromTargetNode(targetContent, out Playlist? targetPlaylist);
                if (targetPlaylist != null)
                    await AddOrInsertFilesToPlaylistAsync(targetContent, targetPlaylist, files);
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorFileDrop(FormatExceptionMessage(ex)));
            }
        }

        private async Task HandleInternalReorderAsync(
            IReadOnlyList<PlaylistNodeContent> draggedContents,
            IReadOnlyList<(Playlist source, Option song)> draggedSongs,
            PlaylistNodeContent dropContent)
        {
            try
            {
                // Playlist reordering (Level 1 → Level 0, 1 or 2)
                if (draggedContents[0].Level == 1)
                {
                    await HandlePlaylistReorderAsync(draggedContents, dropContent);
                    return;
                }

                if (draggedSongs.Count == 0) return;

                // Where the songs land, and what they land after. The anchor is held as an Option
                // and not an index, because putting the run in order below removes songs from the
                // list and any index taken now would shift under it.
                Playlist? targetPlaylist;
                Option? anchor = null;
                if (dropContent.Level == 1)
                {
                    if (!Playlists.TryGetValue(dropContent.Name, out targetPlaylist) || targetPlaylist == null) return;
                }
                else if (dropContent.Level == 2 && dropContent.Parent != null)
                {
                    if (!Playlists.TryGetValue(dropContent.Parent.Name, out targetPlaylist) || targetPlaylist == null) return;
                    var resolved = ResolveDraggedSongs(new[] { dropContent });
                    if (resolved.Count == 0) return;
                    anchor = resolved[0].song;
                }
                else return;

                // Dropping a song onto itself is a no-op, not a move onto its own old position.
                var moves = draggedSongs.Where(m => !ReferenceEquals(m.song, anchor)).ToList();
                if (moves.Count == 0) return;

                var errors = new List<string>();
                var landed = new List<Option>();

                // Every source is repopulated afterwards whether or not its move succeeded. WinUI
                // pulls the grabbed node out of ItemsSource for the duration of the drag, so a song
                // that failed to move still needs its row put back — otherwise the error dialog is
                // accompanied by the song apparently vanishing from the playlist it never left.
                var touched = new HashSet<Playlist> { targetPlaylist };
                foreach (var (source, _) in moves) touched.Add(source);

                // The songs arriving from elsewhere go first, each committing on its own. A song
                // already in the target is left exactly where it is for now: the alternative — pull
                // it out, then move the others in — would have this playlist's file written by a
                // cross-playlist move while a song that belongs in it was missing from the list.
                int anchorIndex = anchor == null ? -1 : targetPlaylist.Options.IndexOf(anchor);
                int insertIndex = anchorIndex >= 0 ? anchorIndex + 1 : targetPlaylist.Options.Count;

                foreach (var (source, song) in moves)
                {
                    if (ReferenceEquals(source, targetPlaylist)) { landed.Add(song); continue; }
                    try
                    {
                        MoveSongAcrossPlaylists(source, targetPlaylist, song, insertIndex);
                        insertIndex++;
                        landed.Add(song);
                    }
                    catch (Exception ex)
                    {
                        // Every cross-playlist move rolls itself back, so a failure here costs that
                        // one song's move and nothing else. Carry on with the rest and report at the
                        // end rather than abandoning the drop halfway through.
                        errors.Add($"{source.Name}/{song.Name}: {FormatExceptionMessage(ex)}");
                    }
                }

                // Now put the whole run in order: lift out everything that landed and set it back
                // down after the anchor, so the drop reads the way the user dragged it. Nothing is
                // saved in between, so the file is only ever written with the full list. Audio files
                // are untouched here — the old code renamed a song's .scd on every reorder (e.g.
                // bpmloop -> bpmloop_1), which is what made Penumbra try to compact a freshly
                // created .scd.
                if (landed.Count > 0)
                {
                    foreach (var song in landed) targetPlaylist.Options.Remove(song);
                    int at = anchor == null ? -1 : targetPlaylist.Options.IndexOf(anchor);
                    int runStart = at >= 0 ? at + 1 : targetPlaylist.Options.Count;
                    targetPlaylist.Options.InsertRange(
                        Math.Clamp(runStart, 0, targetPlaylist.Options.Count), landed);
                    try { targetPlaylist.Save(); }
                    catch (Exception ex) { errors.Add($"{targetPlaylist.Name}: {FormatExceptionMessage(ex)}"); }
                }

                _playlistExpandedStates[targetPlaylist.Name] = true;

                // Repopulate every affected playlist after WinUI's post-drag cleanup, reveal the
                // target, and put the selection back on the songs that moved — they are what the
                // user is most likely to act on next.
                var affected = touched.ToList();
                var destination = targetPlaylist;
                var keys = landed.Select(song => SelectionKey(2, destination.Name, song.Name)).ToList();
                _pendingTreeUpdate = () =>
                {
                    foreach (var playlist in affected) SyncPlaylistNode(playlist);
                    var targetNode = FindPlaylistNode(destination.Name);
                    if (targetNode != null) targetNode.IsExpanded = true;
                    RestoreSelection(keys);
                };

                if (errors.Count > 0)
                    await ShowDialogAsync(AppStrings.Dlg_Error,
                        AppStrings.ErrorDragDrop(string.Join("\n", errors.Take(10))));
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorDragDrop(FormatExceptionMessage(ex)));
            }
        }

        /// <summary>
        /// Moves one song into another playlist, file and all.
        ///
        /// This is ordered so that no single failure can make a song disappear. Previously it removed
        /// the song from the source and saved that FIRST, then saved the target — and because Save()
        /// silently no-opped when the target's group couldn't be resolved, songs were erased from the
        /// source, never written to the target, and their .scd moved anyway. 25 songs were lost that
        /// way.
        ///
        /// Now: pre-flight both playlists, do the ADDITIVE half first, and only then the DESTRUCTIVE
        /// half. Worst case on a mid-way failure is a duplicate, never a loss. Throws with everything
        /// rolled back, so a batch can skip the song and keep going.
        /// </summary>
        private static void MoveSongAcrossPlaylists(Playlist source, Playlist target, Option song, int insertIndex)
        {
            if (!target.ExistsInManifest())
                throw new PlaylistSaveException(target.Name);
            if (!source.ExistsInManifest())
                throw new PlaylistSaveException(source.Name);

            string oldRel = Playlist.NormalizeRelativeModPath(Playlist.GetScdPath(song));
            string oldFullPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, oldRel);
            string oldSongFile = Path.GetFileName(oldRel);
            string targetScdDirectory = target.GetScdDirectoryForNewFiles();
            string newDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, targetScdDirectory);
            Directory.CreateDirectory(newDir);
            string newPath = Playlist.GetNonCollidingPath(Path.Combine(newDir, oldSongFile));

            var scdKey = Playlist.GetScdKey(song) ?? Settings.BaselineScdKey;
            string previousStoredPath = song.Files[scdKey];

            // Physical move first: if it throws, nothing has been mutated yet.
            File.Move(oldFullPath, newPath);
            // BPM/key/duration caches are keyed by full file path, so the move orphans this
            // song's entries. Migrate them (as Cleanup()/Rename() do) or the moved song reads
            // as 0:00 and the target playlist's displayed duration comes up short.
            Utils.BPMDetector.UpdateCacheForSCD(oldFullPath, newPath);
            Utils.KeyDetector.UpdateCacheForSCD(oldFullPath, newPath);
            song.Files[scdKey] = Path.Combine(targetScdDirectory, Path.GetFileName(newPath));

            int targetIndex = Math.Clamp(insertIndex, 0, target.Options.Count);
            target.Options.Insert(targetIndex, song);
            try
            {
                target.Save();
            }
            catch
            {
                // Target write failed. Undo everything — the song is still in the source
                // playlist because we haven't touched it yet, so this leaves the user exactly
                // where they started rather than one song poorer.
                target.Options.RemoveAt(targetIndex);
                try
                {
                    File.Move(newPath, oldFullPath);
                    Utils.BPMDetector.UpdateCacheForSCD(newPath, oldFullPath);
                    Utils.KeyDetector.UpdateCacheForSCD(newPath, oldFullPath);
                }
                catch (Exception moveBack)
                {
                    Utils.Logger.LogError("Move rollback: could not restore '{File}' to '{Dest}': {Error}",
                        newPath, oldFullPath, moveBack);
                }
                song.Files[scdKey] = previousStoredPath;
                throw;
            }

            // Destructive half last. The song is already committed to the target, so if this
            // throws the song exists in both playlists — recoverable, unlike losing it.
            source.Options.Remove(song);
            source.Save();
        }

        private async Task HandlePlaylistReorderAsync(
            IReadOnlyList<PlaylistNodeContent> draggedContents, PlaylistNodeContent dropContent)
        {
            try
            {
                var draggedNames = draggedContents.Where(c => c.Level == 1).Select(c => c.Name).ToList();
                if (draggedNames.Count == 0) return;
                var draggedSet = new HashSet<string>(draggedNames, StringComparer.Ordinal);

                // WinUI removes the grabbed item from ItemsSource during the drag, so Children no
                // longer contains it — and never contained the rest of a multi-selection's removal.
                // Building the list without all of them covers both.
                var root = RootPlaylistItems[0];
                var names = root.Children
                    .Where(c => c.Level == 1 && !draggedSet.Contains(c.Name))
                    .Select(c => c.Name)
                    .ToList();

                int insertIndex;
                if (dropContent.Level == 0)
                {
                    insertIndex = 0;
                }
                else if (dropContent.Level == 1)
                {
                    insertIndex = names.IndexOf(dropContent.Name);
                    if (insertIndex >= 0)
                        insertIndex++; // insert after the drop target
                    else
                        insertIndex = names.Count;
                }
                else if (dropContent.Level == 2 && dropContent.Parent != null)
                {
                    insertIndex = names.IndexOf(dropContent.Parent.Name);
                    if (insertIndex >= 0)
                        insertIndex++; // insert after the parent playlist
                    else
                        insertIndex = names.Count;
                }
                else return;

                names.InsertRange(insertIndex, draggedNames);
                Utils.Logger.LogInfo("PlaylistReorder: '{Dragged}' to index {Index}; new order: {Order}",
                    string.Join(", ", draggedNames), insertIndex, string.Join(" > ", names));

                await Task.Run(() => Playlist.ReorderAll(names));

                // Rebuild the tree from disk: WinUI's TreeView leaves the dragged node's visual out of
                // sync after a drag (it mishandles ObservableCollection.Move), so surgically moving
                // nodes rendered wrong even though the model was correct. GetAll reads the Groups
                // array in the order ReorderAll just wrote, and expansion is restored from state, so
                // a clean reload shows the right order reliably.
                var keys = draggedNames.Select(name => SelectionKey(1, root.Name, name)).ToList();
                _pendingTreeUpdate = () => { LoadPlaylists(); RestoreSelection(keys); };
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorDragDrop(FormatExceptionMessage(ex)));
            }
        }

        private PlaylistNodeContent? FindContentAtPosition(Windows.Foundation.Point position)
        {
            var elements = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(
                position, PlaylistTreeView);
            foreach (var element in elements)
            {
                DependencyObject? current = element;
                while (current != null)
                {
                    if (current is TreeViewItem tvi &&
                        PlaylistTreeView.ItemFromContainer(tvi) is PlaylistNodeContent content)
                        return content;
                    current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
                }
            }
            return null;
        }

        private async Task AddOrInsertFilesToPlaylistAsync(
            PlaylistNodeContent? targetContent, Playlist targetPlaylist, string[] files)
        {
            if (files == null || files.Length == 0) return;
            SetProgressBarText(AppStrings.Prog_ImportingSongs);
            SetProgressBarPercent(0);

            int insertIndex = -1;
            if (targetContent != null && targetContent.Level == 2 && targetContent.Parent != null)
            {
                if (Playlists.TryGetValue(targetContent.Parent.Name, out var parentPl))
                    insertIndex = parentPl.Options.FindIndex(o => o.Name == targetContent.Name) + 1;
            }

            var dispatcherQueue = _uiDispatcherQueue;
            void ReportProgress(int percent) => dispatcherQueue.TryEnqueue(() => SetProgressBarPercent(percent));

            await Task.Run(() =>
            {
                if (insertIndex >= 0)
                    targetPlaylist.Insert(files, insertIndex, ReportProgress);
                else
                    targetPlaylist.Add(files, ReportProgress);
            });

            // Repopulate just the target playlist's songs instead of rebuilding the whole tree.
            SyncPlaylistNode(targetPlaylist);
            var expandNode = FindPlaylistNode(targetPlaylist.Name);
            if (expandNode != null) expandNode.IsExpanded = true;
            ClearProgressDisplay();
        }
    }
}
