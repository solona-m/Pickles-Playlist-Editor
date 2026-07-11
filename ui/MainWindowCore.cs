using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Pickles_Playlist_Editor
{
    public sealed partial class MainWindow
    {
        private bool _busyOverlayVisible;
        private bool _didBackfillStatsFromCache;
        private readonly Dictionary<string, bool> _playlistExpandedStates = new();
        private Storyboard? _spinnerStoryboard;

        private void PlaylistTreeView_Expanding(Microsoft.UI.Xaml.Controls.TreeView _, Microsoft.UI.Xaml.Controls.TreeViewExpandingEventArgs e)
        {
            if (!_dragInProgress && e.Item is PlaylistNodeContent node && node.Level == 1)
                _playlistExpandedStates[node.Name] = true;
        }

        private void PlaylistTreeView_Collapsed(Microsoft.UI.Xaml.Controls.TreeView _, Microsoft.UI.Xaml.Controls.TreeViewCollapsedEventArgs e)
        {
            if (!_dragInProgress && e.Item is PlaylistNodeContent node && node.Level == 1)
                _playlistExpandedStates[node.Name] = false;
        }

        public void LoadPlaylists() => LoadPlaylists(string.Empty);

        public void LoadPlaylistsAndExpand(string playlistName) => LoadPlaylists(string.Empty, playlistName);

        public void LoadPlaylists(string filter) => LoadPlaylists(filter, null);

        private void LoadPlaylists(string filter, string? forceExpandedPlaylistName)
        {
            if (!_uiDispatcherQueue.HasThreadAccess)
            {
                _uiDispatcherQueue.TryEnqueue(() => LoadPlaylists(filter, forceExpandedPlaylistName));
                return;
            }

            try
            {
                Playlists = Playlist.GetAll();
                BackfillStatsFromCacheIfNeeded();

                RootPlaylistItems.Clear();

                var rootContent = new PlaylistNodeContent
                {
                    Name = "Playlists",
                    DisplayText = AppStrings.Tree_PlaylistsRoot,
                    Level = 0,
                    IconGlyph = PlaylistNodeContent.RootGlyph,
                    IsExpanded = true
                };

                RootPlaylistItems.Add(rootContent);

                foreach (var playlist in Playlists.Values)
                {
                    if (playlist.IsVFXGroup())
                        continue;

                    bool wasExpanded = _playlistExpandedStates.TryGetValue(playlist.Name ?? "", out bool exp) && exp;

                    var playlistContent = new PlaylistNodeContent
                    {
                        Name = playlist.Name ?? "",
                        DisplayText = playlist.Name ?? "",
                        Level = 1,
                        IconGlyph = PlaylistNodeContent.PlaylistGlyph,
                        IsExpanded = wasExpanded
                    };

                    TimeSpan playlistTime = TimeSpan.Zero;
                    bool matchFound = false;

                    if (playlist.Options == null) continue;

                    foreach (var song in playlist.Options)
                    {
                        if (song == null) continue;
                        try
                        {
                            if (!string.IsNullOrEmpty(Playlist.GetScdPath(song)))
                            {
                                TimeSpan cachedTime = BPMDetector.TryGetCachedDuration(Playlist.GetScdPath(song)) ?? TimeSpan.Zero;
                                playlistTime = playlistTime.Add(cachedTime);
                            }

                            var songContent = CreateSongNode(song);

                            if (!string.IsNullOrEmpty(filter))
                            {
                                var cmp = StringComparison.OrdinalIgnoreCase;
                                if (playlist.Name?.IndexOf(filter, cmp) >= 0 ||
                                    song.Name?.IndexOf(filter, cmp) >= 0)
                                {
                                    matchFound = true;
                                    playlistContent.AddChild(songContent);
                                }
                            }
                            else
                            {
                                playlistContent.AddChild(songContent);
                            }
                        }
                        catch (Exception ex)
                        {
                            _ = ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorLoadingSong(song.Name, playlist.Name, ex.Message));
                        }
                    }

                    if (filter.Length > 0 && playlistContent.Children.Count == 0)
                        continue;

                    // Update display text with duration
                    playlistContent.DisplayText = playlist.Name + GetTimeString(playlistTime);

                    if (matchFound)
                        playlistContent.IsExpanded = true;

                    if (!string.IsNullOrWhiteSpace(forceExpandedPlaylistName) &&
                        string.Equals(playlist.Name, forceExpandedPlaylistName, StringComparison.OrdinalIgnoreCase))
                    {
                        playlistContent.IsExpanded = true;
                    }

                    _playlistExpandedStates[playlistContent.Name] = playlistContent.IsExpanded;

                    rootContent.AddChild(playlistContent);
                }
            }
            catch (Exception ex)
            {
                _ = ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorLoadingPlaylists(ex.Message));
            }
        }

        private void RecomputePlaylistDurations(bool checkUI = true)
        {
            if (RootPlaylistItems.Count == 0) return;

            foreach (var playlist in Playlists.Values)
            {
                var playlistContent = FindPlaylistNode(playlist.Name);
                if (checkUI && playlistContent == null) continue;
                if (playlistContent == null) continue;

                TimeSpan playlistTime = TimeSpan.Zero;
                if (playlist.Options == null) continue;

                foreach (var song in playlist.Options)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(Playlist.GetScdPath(song)))
                            playlistTime = playlistTime.Add(BPMDetector.TryGetCachedDuration(Playlist.GetScdPath(song)) ?? TimeSpan.Zero);
                    }
                    catch (Exception ex)
                    {
                        _ = ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorLoadingSong(song.Name, playlist.Name, ex.Message));
                    }
                }

                SetNodeDisplayText(playlistContent, playlist.Name + GetTimeString(playlistTime));
            }

            if (!checkUI)
                LoadPlaylists();
        }

        // ─── Incremental tree update helpers ─────────────────────────────────────
        // These mutate only the affected node(s) so the whole tree isn't torn down and
        // rebuilt (RootPlaylistItems.Clear()) on every insert/reorder/etc. When a search
        // filter is active the tree is a filtered subset, so incremental paths fall back
        // to a filtered rebuild instead.
        private bool IsFilterActive => !string.IsNullOrWhiteSpace(SearchTextBox?.Text);

        private static PlaylistNodeContent CreateSongNode(Option song) => new PlaylistNodeContent
        {
            Name = song.Name,
            DisplayText = song.Name,
            Level = 2,
            IconGlyph = PlaylistNodeContent.SongGlyph
        };

        private static TimeSpan SumPlaylistDuration(Playlist playlist)
        {
            TimeSpan total = TimeSpan.Zero;
            if (playlist.Options == null) return total;
            foreach (var song in playlist.Options)
            {
                if (song == null) continue;
                var scd = Playlist.GetScdPath(song);
                if (!string.IsNullOrEmpty(scd))
                    total = total.Add(BPMDetector.TryGetCachedDuration(scd) ?? TimeSpan.Zero);
            }
            return total;
        }

        private PlaylistNodeContent CreatePlaylistNode(Playlist playlist, bool expanded)
        {
            var node = new PlaylistNodeContent
            {
                Name = playlist.Name ?? "",
                DisplayText = (playlist.Name ?? "") + GetTimeString(SumPlaylistDuration(playlist)),
                Level = 1,
                IconGlyph = PlaylistNodeContent.PlaylistGlyph,
                IsExpanded = expanded
            };
            if (playlist.Options != null)
                foreach (var song in playlist.Options)
                    if (song != null)
                        node.AddChild(CreateSongNode(song));
            return node;
        }

        // Repopulate a single playlist's song nodes and refresh its label, in place.
        private void SyncPlaylistNode(Playlist playlist)
        {
            if (IsFilterActive) { LoadPlaylists(SearchTextBox.Text); return; }
            var node = FindPlaylistNode(playlist.Name);
            if (node == null) { LoadPlaylists(); return; }

            var songNodes = new List<PlaylistNodeContent>();
            if (playlist.Options != null)
                foreach (var song in playlist.Options)
                    if (song != null)
                        songNodes.Add(CreateSongNode(song));
            node.ReplaceChildren(songNodes);
            SetNodeDisplayText(node, (playlist.Name ?? "") + GetTimeString(SumPlaylistDuration(playlist)));
        }

        // Reorder the existing Level-1 playlist nodes to match the given name order,
        // re-inserting the dragged node if WinUI removed it from ItemsSource during the drag.
        // Preserves each playlist's song subtree and expansion (no node recreation).
        private void ReorderRootChildrenToMatch(List<string> orderedNames, PlaylistNodeContent draggedNode)
        {
            if (IsFilterActive) { LoadPlaylists(SearchTextBox.Text); return; }
            if (RootPlaylistItems.Count == 0) { LoadPlaylists(); return; }
            var root = RootPlaylistItems[0];

            if (!root.Children.Contains(draggedNode))
                root.InsertChild(0, draggedNode);

            for (int target = 0; target < orderedNames.Count && target < root.Children.Count; target++)
            {
                PlaylistNodeContent? node = null;
                for (int i = target; i < root.Children.Count; i++)
                    if (root.Children[i].Name == orderedNames[target]) { node = root.Children[i]; break; }
                if (node == null) continue;
                int cur = root.Children.IndexOf(node);
                if (cur != target)
                    root.Children.Move(cur, target);
            }
        }

        // Append a newly created playlist's node (new playlists sort last by Priority).
        private void AddNewPlaylistNode(Playlist playlist)
        {
            if (IsFilterActive) { LoadPlaylists(SearchTextBox.Text); return; }
            if (RootPlaylistItems.Count == 0 || playlist.IsVFXGroup()) { LoadPlaylistsAndExpand(playlist.Name); return; }
            var node = CreatePlaylistNode(playlist, expanded: true);
            RootPlaylistItems[0].AddChild(node);
            _playlistExpandedStates[node.Name] = true;
        }

        private void RemovePlaylistNode(string name)
        {
            if (RootPlaylistItems.Count > 0)
            {
                var node = FindPlaylistNode(name);
                if (node != null) RootPlaylistItems[0].RemoveChild(node);
            }
            _playlistExpandedStates.Remove(name);
        }

        private void RemoveSongNode(Playlist playlist, string songName)
        {
            var playlistNode = FindPlaylistNode(playlist.Name);
            if (playlistNode == null) return;
            var songNode = FindSongNode(playlistNode, songName);
            if (songNode != null) playlistNode.RemoveChild(songNode);
            SetNodeDisplayText(playlistNode, (playlist.Name ?? "") + GetTimeString(SumPlaylistDuration(playlist)));
        }

        // After a cancelled or no-op drag, restore the dragged node only if WinUI actually removed it
        // from ItemsSource. A no-op when the node is still present, so normal cancels don't flicker.
        private void RepairDraggedNode(PlaylistNodeContent? dragged)
        {
            if (dragged == null || RootPlaylistItems.Count == 0) return;
            if (dragged.Level == 2 && dragged.Parent != null)
            {
                if (!dragged.Parent.Children.Contains(dragged) &&
                    Playlists.TryGetValue(dragged.Parent.Name, out var pl))
                    SyncPlaylistNode(pl);
            }
            else if (dragged.Level == 1 && !RootPlaylistItems[0].Children.Contains(dragged))
            {
                LoadPlaylists();
            }
        }

        // Update a renamed song's node in place; returns the node, or null if not found.
        private PlaylistNodeContent? RenameSongNode(Playlist playlist, string oldName, string newName)
        {
            var playlistNode = FindPlaylistNode(playlist.Name);
            var songNode = playlistNode == null ? null : FindSongNode(playlistNode, oldName);
            if (songNode == null) return null;
            songNode.Name = newName;
            SetNodeDisplayText(songNode, newName);
            return songNode;
        }

        // Update a renamed playlist's node in place and re-key the model dict + expansion map to the
        // new name; returns the node, or null if not found.
        private PlaylistNodeContent? RenamePlaylistNode(string oldName, string newName, Playlist playlist)
        {
            if (Playlists.Remove(oldName)) Playlists[newName] = playlist;
            if (_playlistExpandedStates.Remove(oldName, out var wasExpanded))
                _playlistExpandedStates[newName] = wasExpanded;
            var node = FindPlaylistNode(oldName);
            if (node == null) return null;
            node.Name = newName;
            SetNodeDisplayText(node, newName + GetTimeString(SumPlaylistDuration(playlist)));
            return node;
        }

        // Adds a just-created playlist's node without rebuilding the tree. Public so the
        // New Playlist dialog can call it after Playlist.Create.
        public void AddCreatedPlaylist(string playlistName)
        {
            if (IsFilterActive) { LoadPlaylistsAndExpand(playlistName); return; }
            // Refresh the model so the new playlist and its imported songs are present.
            Playlists = Playlist.GetAll();
            if (!Playlists.TryGetValue(playlistName, out var pl))
            {
                LoadPlaylistsAndExpand(playlistName);
                return;
            }
            AddNewPlaylistNode(pl);
            var node = FindPlaylistNode(playlistName);
            if (node != null)
            {
                node.IsExpanded = true;
                PlaylistTreeView.SelectedItems.Clear();
                PlaylistTreeView.SelectedItems.Add(node);
                _selectedNode = node;
            }
        }

        // One-time, cache-only backfill: songs whose Name has never had stats baked in
        // (e.g. imported before this feature existed) get whatever's already cached folded
        // into their in-memory Name for display. Never triggers new BPM/key/duration detection.
        //
        // This deliberately does NOT persist to disk. Writing the whole library on load is what let
        // the old GetJsonFiles wildcard bug fan out into mass corruption; the display-only names here
        // get saved naturally the next time the user actually edits that playlist.
        private void BackfillStatsFromCacheIfNeeded()
        {
            if (_didBackfillStatsFromCache) return;
            _didBackfillStatsFromCache = true;

            foreach (var playlist in Playlists.Values)
            {
                if (playlist.Options == null) continue;
                foreach (var option in playlist.Options)
                {
                    if (option == null || string.IsNullOrEmpty(option.Name)) continue;
                    if (OptionStatsNaming.StripSuffix(option.Name) != option.Name) continue;

                    string scdPath = Playlist.GetScdPath(option);
                    if (string.IsNullOrEmpty(scdPath)) continue;

                    int? bpm = BPMDetector.TryGetCachedBpm(scdPath);
                    string? key = KeyDetector.TryGetCachedKey(scdPath);
                    TimeSpan? duration = BPMDetector.TryGetCachedDuration(scdPath);
                    if (!bpm.HasValue && string.IsNullOrEmpty(key) && !duration.HasValue) continue;

                    OptionStatsNaming.UpdateName(option, bpm, key, duration);
                }
            }
        }

        private static string GetTimeString(TimeSpan time)
        {
            int hours = (int)time.TotalHours;
            return $" ({hours:D2}:{time.Minutes:D2}:{time.Seconds:D2})";
        }

        public void SetProgressBarPercent(int percent)
        {
            if (!_uiDispatcherQueue.HasThreadAccess)
            {
                _uiDispatcherQueue.TryEnqueue(() => SetProgressBarPercent(percent));
                return;
            }
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            ProgressBar1.Value = percent;
            BusyProgressBar.Value = percent;

            if (percent > 0 || !string.IsNullOrWhiteSpace(ProgressLabel.Text))
                SetBusyOverlayVisible(true);

            if (percent == 100)
            {
                ClearProgressDisplay();
            }
        }

        public void SetProgressBarText(string text)
        {
            if (!_uiDispatcherQueue.HasThreadAccess)
            {
                _uiDispatcherQueue.TryEnqueue(() => SetProgressBarText(text));
                return;
            }
            ProgressLabel.Text = text;
            BusyProgressLabel.Text = text;

            if (string.IsNullOrWhiteSpace(text))
                SetBusyOverlayVisible(false);
            else
                SetBusyOverlayVisible(true);
        }

        public void ClearProgressDisplay()
        {
            if (!_uiDispatcherQueue.HasThreadAccess)
            {
                _uiDispatcherQueue.TryEnqueue(ClearProgressDisplay);
                return;
            }

            ProgressBar1.Value = 0;
            BusyProgressBar.Value = 0;
            ProgressLabel.Text = "";
            BusyProgressLabel.Text = "";
            SetBusyOverlayVisible(false);
        }

        private Storyboard GetSpinnerStoryboard()
        {
            if (_spinnerStoryboard != null) return _spinnerStoryboard;
            var anim = new DoubleAnimation
            {
                From = 0, To = 360,
                Duration = new Duration(TimeSpan.FromSeconds(1.1)),
                RepeatBehavior = RepeatBehavior.Forever,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(anim, BusySpinnerRotate);
            Storyboard.SetTargetProperty(anim, "Angle");
            _spinnerStoryboard = new Storyboard();
            _spinnerStoryboard.Children.Add(anim);
            return _spinnerStoryboard;
        }

        private void SetBusyOverlayVisible(bool visible)
        {
            if (_busyOverlayVisible == visible) return;

            _busyOverlayVisible = visible;
            BusyOverlay.Visibility = visible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            MainContentGrid.IsHitTestVisible = !visible;

            if (visible)
            {
                BusySpinnerRotate.Angle = 0;
                GetSpinnerStoryboard().Begin();
            }
            else
            {
                GetSpinnerStoryboard().Stop();
                BusySpinnerRotate.Angle = 0;
            }
        }

        private async Task<bool> DoDeleteAsync()
        {
            try
            {
                var result = await ShowDialogAsync(
                    AppStrings.Dlg_ConfirmDelete_Title,
                    AppStrings.Dlg_ConfirmDelete_Content,
                    AppStrings.Btn_Yes, null, AppStrings.Btn_No);

                if (result != ContentDialogResult.Primary) return false;

                var selectedItems = PlaylistTreeView.SelectedItems.OfType<PlaylistNodeContent>().ToList();
                foreach (var item in selectedItems)
                {
                    if (item.Level == 1 && Playlists.TryGetValue(item.Name, out var pl))
                    {
                        pl.Delete();
                        Playlists.Remove(item.Name);
                        if (!IsFilterActive) RemovePlaylistNode(item.Name);
                    }
                    else if (item.Level == 2 && item.Parent != null)
                    {
                        if (Playlists.TryGetValue(item.Parent.Name, out var parentPl))
                        {
                            var song = parentPl.Options.Find(x => x.Name == item.Name);
                            if (song != null && !song.Name.Equals("Off", StringComparison.InvariantCultureIgnoreCase))
                            {
                                Utils.Logger.LogInfo("Deleting song '{Song}' from playlist '{Playlist}'", song.Name, parentPl.Name);
                                parentPl.Options.Remove(song);

                                // Delete the actual audio file this song referenced. Songs are stored
                                // as single .scd files inside the playlist's SCD folder — there is no
                                // per-song "<mod>/<PlaylistName>/<SongName>" directory, so the old
                                // directory delete never matched and left the .scd orphaned on disk.
                                string rel = Playlist.GetScdPath(song);
                                if (!string.IsNullOrEmpty(rel))
                                {
                                    try
                                    {
                                        string scdFull = Path.Combine(Settings.PenumbraLocation, Settings.ModName,
                                            Playlist.NormalizeRelativeModPath(rel));
                                        if (File.Exists(scdFull)) File.Delete(scdFull);
                                    }
                                    catch { /* best-effort; a leftover file is harmless and Cleanup reclaims it */ }
                                }

                                parentPl.Save();
                                if (!IsFilterActive) RemoveSongNode(parentPl, item.Name);
                            }
                        }
                    }
                }

                DeleteButton.IsEnabled = false;
                ShuffleButton.IsEnabled = false;
                SortByBPMButton.IsEnabled = false;
                if (IsFilterActive) LoadPlaylists(SearchTextBox.Text);
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorDeletion(ex.Message));
                return false;
            }
            return true;
        }

        private string ResolveTargetPlaylistForSingle()
        {
            if (_selectedNode != null)
            {
                if (_selectedNode.Level == 1) return _selectedNode.Name;
                if (_selectedNode.Level == 2 && _selectedNode.Parent != null) return _selectedNode.Parent.Name;
            }
            return null;
        }

        private static string FormatExceptionMessage(Exception ex)
        {
            var messages = new List<string>();
            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                string message = string.IsNullOrWhiteSpace(current.Message)
                    ? current.GetType().Name
                    : $"{current.GetType().Name}: {current.Message}";

                if (!messages.Contains(message))
                    messages.Add(message);
            }

            return messages.Count > 0 ? string.Join(Environment.NewLine, messages) : ex.ToString();
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                var mgr = new UpdateManager(new GithubSource("https://github.com/solona-m/Pickles-Playlist-Editor", null, false));
                var update = await mgr.CheckForUpdatesAsync();
                if (update == null) return;

                var result = await ShowDialogAsync(
                    AppStrings.Dlg_UpdateAvailable_Title,
                    AppStrings.UpdateAvailableContent(update.TargetFullRelease.Version.ToString()),
                    AppStrings.Btn_Install, null, AppStrings.Btn_Later);

                if (result != ContentDialogResult.Primary) return;

                await mgr.DownloadUpdatesAsync(update);
                mgr.ApplyUpdatesAndRestart(update);
            }
            catch
            {
                // Silently ignore update failures (no network, GitHub down, etc.)
            }
        }
    }
}
