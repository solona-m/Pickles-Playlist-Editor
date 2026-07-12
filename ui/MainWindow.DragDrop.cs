using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace Pickles_Playlist_Editor
{
    public sealed partial class MainWindow
    {
        private PlaylistNodeContent? _draggedContent;
        private PlaylistNodeContent? _pendingDropTarget;
        private bool _dragInProgress;
        // Set by the reorder handlers to the targeted, incremental tree update to run after WinUI
        // finishes its own post-drag cleanup (applied in the deferred DragItemsCompleted callback),
        // instead of tearing down and rebuilding the whole tree via LoadPlaylists().
        private Action? _pendingTreeUpdate;

        private void PlaylistTreeView_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs e)
        {
            if (e.Items.Count == 1 && e.Items[0] is PlaylistNodeContent content)
            {
                _dragInProgress = true;
                _draggedContent = content;
                _pendingDropTarget = null;
                e.Data.RequestedOperation = DataPackageOperation.Move;
            }
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
            var draggedContent = _draggedContent;
            var dropContent = _pendingDropTarget;
            _draggedContent = null;
            _pendingDropTarget = null;
            _pendingTreeUpdate = null;

            Utils.Logger.LogInfo("DragCompleted: result={Result} dragged='{Dragged}'(L{DL}) drop='{Drop}'(L{TL})",
                args.DropResult, draggedContent?.Name ?? "(null)", draggedContent?.Level ?? -1,
                dropContent?.Name ?? "(null)", dropContent?.Level ?? -1);

            if (args.DropResult == DataPackageOperation.None)
            {
                // Cancelled — WinUI reverts its own visual reorder. Only repair if the dragged node
                // actually went missing, so a normal cancel doesn't flicker the whole tree.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    _dragInProgress = false;
                    RepairDraggedNode(draggedContent);
                });
                return;
            }

            if (draggedContent != null && dropContent != null && dropContent != draggedContent)
                await HandleInternalReorderAsync(draggedContent, dropContent);

            // Apply the targeted tree update after WinUI finishes its own post-drag cleanup (which
            // is why this is deferred to Low priority). Only touches the affected node(s); no rebuild.
            var pending = _pendingTreeUpdate;
            _pendingTreeUpdate = null;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _dragInProgress = false;
                if (pending != null) pending();
                else RepairDraggedNode(draggedContent);
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

        private async Task HandleInternalReorderAsync(PlaylistNodeContent draggedContent, PlaylistNodeContent dropContent)
        {
            try
            {
                // Playlist reordering (Level 1 → Level 0 or Level 1)
                if (draggedContent.Level == 1)
                {
                    await HandlePlaylistReorderAsync(draggedContent, dropContent);
                    return;
                }

                if (draggedContent.Level != 2 || draggedContent.Parent == null) return;

                string oldPlaylistName = draggedContent.Parent.Name;
                if (!Playlists.TryGetValue(oldPlaylistName, out var oldPlaylist)) return;
                var song = oldPlaylist.Options.Find(x => x.Name == draggedContent.Name);
                if (song == null) return;

                Playlist? targetPlaylist;
                int insertIndex;

                if (dropContent.Level == 1)
                {
                    if (!Playlists.TryGetValue(dropContent.Name, out targetPlaylist)) return;
                    insertIndex = targetPlaylist.Options.Count;
                }
                else if (dropContent.Level == 2 && dropContent.Parent != null)
                {
                    if (!Playlists.TryGetValue(dropContent.Parent.Name, out targetPlaylist)) return;
                    var ts = targetPlaylist.Options.Find(x => x.Name == dropContent.Name);
                    insertIndex = ts != null ? targetPlaylist.Options.IndexOf(ts) + 1 : targetPlaylist.Options.Count;
                }
                else return;

                // Same-playlist reorder: only the option order changes. Don't touch the audio file —
                // the old code renamed it to a non-colliding name every time (e.g. bpmloop -> bpmloop_1),
                // which was both ugly and what made Penumbra try to compact a freshly-created .scd.
                // Still Save() so the reorder is written and Penumbra picks up the new order.
                if (ReferenceEquals(oldPlaylist, targetPlaylist))
                {
                    oldPlaylist.Options.Remove(song);
                    // Recompute the insert position after removal so indices don't shift under us.
                    int idx;
                    if (dropContent.Level == 2 && dropContent.Parent != null)
                    {
                        var ts = oldPlaylist.Options.Find(x => x.Name == dropContent.Name);
                        idx = ts != null ? oldPlaylist.Options.IndexOf(ts) + 1 : oldPlaylist.Options.Count;
                    }
                    else
                    {
                        idx = oldPlaylist.Options.Count; // dropped on the playlist header -> end
                    }
                    oldPlaylist.Options.Insert(Math.Min(idx, oldPlaylist.Options.Count), song);
                    oldPlaylist.Save();
                    _playlistExpandedStates[oldPlaylist.Name] = true;
                    // Repopulate just this playlist's songs after WinUI's post-drag cleanup.
                    _pendingTreeUpdate = () => SyncPlaylistNode(oldPlaylist);
                    return;
                }

                oldPlaylist.Options.Remove(song);
                oldPlaylist.Save();

                string oldPath = Playlist.NormalizeRelativeModPath(Playlist.GetScdPath(song));
                string oldDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, Path.GetDirectoryName(oldPath) ?? string.Empty);
                string oldSongFile = Path.GetFileName(oldPath);
                string targetScdDirectory = targetPlaylist.GetScdDirectoryForNewFiles();
                string newDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, targetScdDirectory);
                Directory.CreateDirectory(newDir);
                string newPath = Playlist.GetNonCollidingPath(Path.Combine(newDir, oldSongFile));

                var scdKey = Playlist.GetScdKey(song) ?? Settings.BaselineScdKey;
                song.Files[scdKey] = Path.Combine(targetScdDirectory, Path.GetFileName(newPath));

                targetPlaylist.Options.Insert(Math.Min(insertIndex, targetPlaylist.Options.Count), song);
                targetPlaylist.Save();

                string oldFullPath = Path.Combine(oldDir, oldSongFile);
                File.Move(oldFullPath, newPath);
                // BPM/key/duration caches are keyed by full file path, so the move orphans this
                // song's entries. Migrate them (as Cleanup()/Rename() do) or the moved song reads
                // as 0:00 and the target playlist's displayed duration comes up short.
                Utils.BPMDetector.UpdateCacheForSCD(oldFullPath, newPath);
                Utils.KeyDetector.UpdateCacheForSCD(oldFullPath, newPath);
                _playlistExpandedStates[targetPlaylist.Name] = true;
                // Repopulate both affected playlists after WinUI's post-drag cleanup, and reveal the
                // moved song by expanding the target.
                var movedTo = targetPlaylist;
                var movedFrom = oldPlaylist;
                _pendingTreeUpdate = () =>
                {
                    SyncPlaylistNode(movedFrom);
                    SyncPlaylistNode(movedTo);
                    var targetNode = FindPlaylistNode(movedTo.Name);
                    if (targetNode != null) targetNode.IsExpanded = true;
                };
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorDragDrop(FormatExceptionMessage(ex)));
            }
        }

        private async Task HandlePlaylistReorderAsync(PlaylistNodeContent draggedContent, PlaylistNodeContent dropContent)
        {
            try
            {
                // WinUI removes the dragged item from ItemsSource during the drag,
                // so Children no longer contains it. Build the list without it and insert.
                var root = RootPlaylistItems[0];
                var names = root.Children
                    .Where(c => c.Level == 1 && c.Name != draggedContent.Name)
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

                names.Insert(insertIndex, draggedContent.Name);
                Utils.Logger.LogInfo("PlaylistReorder: '{Dragged}' to index {Index}; new order: {Order}",
                    draggedContent.Name, insertIndex, string.Join(" > ", names));

                await Task.Run(() => Playlist.ReorderAll(names));

                // Rebuild the tree from disk: WinUI's TreeView leaves the dragged node's visual out of
                // sync after a drag (it mishandles ObservableCollection.Move), so surgically moving
                // nodes rendered wrong even though the model was correct. GetAll now orders by the
                // group number ReorderAll just wrote, and expansion is restored from state, so a clean
                // reload shows the right order reliably.
                _pendingTreeUpdate = () => LoadPlaylists();
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
