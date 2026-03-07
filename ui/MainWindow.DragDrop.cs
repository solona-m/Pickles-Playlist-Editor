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

        private void PlaylistTreeView_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs e)
        {
            if (e.Items.Count == 1 && e.Items[0] is PlaylistNodeContent content)
            {
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

            if (draggedContent == null || dropContent == null || dropContent == draggedContent) return;
            if (args.DropResult == DataPackageOperation.None) return; // cancelled

            await HandleInternalReorderAsync(draggedContent, dropContent);
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
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorFileDrop(ex.Message));
            }
        }

        private async Task HandleInternalReorderAsync(PlaylistNodeContent draggedContent, PlaylistNodeContent dropContent)
        {
            try
            {
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

                oldPlaylist.Options.Remove(song);
                oldPlaylist.Save();

                string oldPath = Playlist.GetScdPath(song);
                int lastSlash = oldPath.LastIndexOf('\\');
                string oldDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, oldPath[..lastSlash]);
                string oldSongFile = oldPath[(lastSlash + 1)..];
                var scdKey = Playlist.GetScdKey(song) ?? Settings.BaselineScdKey;
                song.Files[scdKey] = Path.Combine(targetPlaylist.Name, oldSongFile);

                targetPlaylist.Options.Insert(Math.Min(insertIndex, targetPlaylist.Options.Count), song);
                targetPlaylist.Save();

                string newDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, targetPlaylist.Name);
                Directory.CreateDirectory(newDir);
                File.Move(Path.Combine(oldDir, oldSongFile), Path.Combine(newDir, oldSongFile));

                dropContent.IsExpanded = true;
                RecomputePlaylistDurations();
                LoadPlaylists();
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorDragDrop(ex.Message));
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

            await Task.Run(() =>
            {
                if (insertIndex >= 0)
                    targetPlaylist.Insert(files, insertIndex, SetProgressBarPercent);
                else
                    targetPlaylist.Add(files, SetProgressBarPercent);
            }).ConfigureAwait(false);

            DispatcherQueue.TryEnqueue(() =>
            {
                LoadPlaylists();
                if (targetContent != null)
                {
                    string expandName = targetContent.Level == 2 && targetContent.Parent != null
                        ? targetContent.Parent.Name : targetContent.Name;
                    var expandNode = FindPlaylistNode(expandName);
                    if (expandNode != null) expandNode.IsExpanded = true;
                }
                ClearProgressDisplay();
            });
        }
    }
}
