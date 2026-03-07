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
        private readonly Dictionary<string, bool> _playlistExpandedStates = new();
        private Storyboard? _spinnerStoryboard;

        public void LoadPlaylists() => LoadPlaylists(string.Empty);

        public void LoadPlaylistsAndExpand(string playlistName) => LoadPlaylists(string.Empty, playlistName);

        public void LoadPlaylists(string filter) => LoadPlaylists(filter, null);

        private void LoadPlaylists(string filter, string? forceExpandedPlaylistName)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => LoadPlaylists(filter, forceExpandedPlaylistName));
                return;
            }

            // Snapshot expanded state before rebuild
            if (RootPlaylistItems.Count > 0)
            {
                foreach (var child in RootPlaylistItems[0].Children)
                    _playlistExpandedStates[child.Name] = child.IsExpanded;
            }

            try
            {
                Playlists = Playlist.GetAll();

                bool skipDurationComputation = false;
                if (BPMDetector.IsFirstTimeMessage())
                {
                    BPMDetector.MarkFirstTimeMessageShown();
                    skipDurationComputation = true;
                    _ = ShowDialogAsync(AppStrings.Dlg_BPMDetection_Title, AppStrings.Dlg_BPMDetection_Content);
                    _ = RecomputePlaylistDurationsAsync();
                }

                RootPlaylistItems.Clear();

                var rootContent = new PlaylistNodeContent
                {
                    Name = "Playlists",
                    DisplayText = "Playlists",
                    Level = 0,
                    IconGlyph = PlaylistNodeContent.RootGlyph,
                    IsExpanded = true
                };

                RootPlaylistItems.Add(rootContent);

                foreach (var playlist in Playlists.Values)
                {
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
                            TimeSpan time = TimeSpan.Zero;
                            if (!skipDurationComputation && !string.IsNullOrEmpty(Playlist.GetScdPath(song)))
                            {
                                time = BPMDetector.GetDuration(Playlist.GetScdPath(song));
                                playlistTime = playlistTime.Add(time);
                            }

                            string displayText = song.Name
                                + (skipDurationComputation ? "" : GetBPMString(song))
                                + GetTimeString(time);

                            var songContent = new PlaylistNodeContent
                            {
                                Name = song.Name,
                                DisplayText = displayText,
                                Level = 2,
                                IconGlyph = PlaylistNodeContent.SongGlyph
                            };

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
                            playlistTime = playlistTime.Add(BPMDetector.GetDuration(Playlist.GetScdPath(song)));
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

        private Task RecomputePlaylistDurationsAsync()
        {
            var playlistScdPaths = new List<List<string>>();
            var playlists = Playlist.GetAll();
            foreach (var playlist in playlists.Values)
            {
                var files = new List<string>();
                if (playlist.Options != null)
                {
                    foreach (var opt in playlist.Options)
                    {
                        var scdPath = Playlist.GetScdPath(opt);
                        if (!string.IsNullOrEmpty(scdPath))
                            files.Add(Path.Combine(Settings.PenumbraLocation, Settings.ModName, scdPath));
                    }
                }
                playlistScdPaths.Add(files);
            }

            return Task.Run(() =>
            {
                try
                {
                    SetProgressBarText(AppStrings.Prog_ComputingDurations);
                    int totalFiles = playlistScdPaths.Sum(l => l.Count);
                    int filesProcessed = 0;

                    foreach (var fileList in playlistScdPaths)
                    {
                        foreach (var scd in fileList)
                        {
                            try
                            {
                                BPMDetector.GetDuration(scd);
                                filesProcessed++;
                                SetProgressBarPercent((int)(filesProcessed / (double)totalFiles * 100));
                            }
                            catch { }
                        }
                    }
                }
                finally
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        RecomputePlaylistDurations(false);
                        ClearProgressDisplay();
                    });
                }
            });
        }

        private string GetBPMString(Option song)
        {
            string scdPath = Playlist.GetScdPath(song);
            if (string.IsNullOrEmpty(scdPath)) return string.Empty;
            return " (" + BPMDetector.GetBPMFromSCD(scdPath) + " BPM)";
        }

        private static string GetTimeString(TimeSpan time)
        {
            int hours = (int)time.TotalHours;
            return $" ({hours:D2}:{time.Minutes:D2}:{time.Seconds:D2})";
        }

        public void SetProgressBarPercent(int percent)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => SetProgressBarPercent(percent));
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
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => SetProgressBarText(text));
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
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(ClearProgressDisplay);
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

                foreach (var item in PlaylistTreeView.SelectedItems.OfType<PlaylistNodeContent>().ToList())
                {
                    if (item.Level == 1 && Playlists.TryGetValue(item.Name, out var pl))
                    {
                        pl.Delete();
                    }
                    else if (item.Level == 2 && item.Parent != null)
                    {
                        if (Playlists.TryGetValue(item.Parent.Name, out var parentPl))
                        {
                            var song = parentPl.Options.Find(x => x.Name == item.Name);
                            if (song != null)
                            {
                                parentPl.Options.Remove(song);
                                parentPl.Save();
                                string songDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName, parentPl.Name, song.Name);
                                if (Directory.Exists(songDirectory))
                                    Directory.Delete(songDirectory, true);
                            }
                        }
                    }
                }

                DeleteButton.IsEnabled = false;
                ShuffleButton.IsEnabled = false;
                SortByBPMButton.IsEnabled = false;
                LoadPlaylists();
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
            return "YouTube Singles";
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                var mgr = new UpdateManager(new GithubSource("https://github.com/solona-m/Pickles-Playlist-Editor-Release", null, false));
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
