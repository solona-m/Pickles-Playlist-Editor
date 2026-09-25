using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.IO;
using Windows.Storage.Pickers;

namespace Pickles_Playlist_Editor
{
    public sealed partial class MainWindow : Window
    {
        public static Dictionary<string, Playlist> Playlists { get; set; } = new();

        public ObservableCollection<PlaylistNodeContent> RootPlaylistItems { get; } = new();

        private PlaylistNodeContent? _contextMenuNode;
        // The rows the open context menu acts on — the selection, or just the right-clicked row.
        private List<PlaylistNodeContent> _contextMenuNodes = new();
        private PlaylistNodeContent? _selectedNode;

        private readonly MenuFlyout _treeContextMenu;
        private readonly MenuFlyout _rootContextMenu;
        private readonly DispatcherQueue _uiDispatcherQueue;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int attrValue, int attrSize);

        private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public MainWindow()
        {
            this.InitializeComponent();
            _uiDispatcherQueue = DispatcherQueue;
            InitializePlaybackTimer();

            // Apply system dark/light theme — WinUI 3 doesn't do this automatically
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            var bg = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            bool isDark = bg.R < 128;

            // Set content theme (controls)
            if (this.Content is FrameworkElement root)
                root.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;

            // Tell DWM to render the title bar in dark/light mode
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int darkMode = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            this.AppWindow.SetIcon("pickle.ico");
            string displayVersion = Utils.AppVersion.Display;
            this.Title = displayVersion.Length > 0
                ? $"Pickles Playlist Editor {displayVersion}"
                : "Pickles Playlist Editor";

            Utils.Logger.LogInfo("Startup: v{Version} ({Channel}) | Penumbra='{Penumbra}' Mod='{Mod}' AutoReload={AutoReload}",
                displayVersion.Length > 0 ? displayVersion : "?",
                Utils.AppVersion.IsPrerelease ? "testing channel" : "stable channel",
                Settings.PenumbraLocation ?? "(unset)", Settings.ModName ?? "(unset)", Settings.AutoReloadMod);
            Utils.Logger.LogInfo("Startup: log='{Log}' crashLog='{CrashLog}'",
                Utils.Logger.LogFilePath, Utils.Logger.CrashLogPath);

            // Penumbra's version, not ours. It owns the mod folder's layout — v3 and v4 are both live
            // steady states and it converts between them on its own schedule — so a report about
            // playlists that changed shape, or a folder that was rewritten underneath us, is not
            // answerable without knowing which build did it.
            Utils.Logger.LogInfo("Startup: Penumbra plugin {Version}", Utils.PenumbraInstall.DescribeForLog());

            // Clear decoded previews orphaned by a previous crash or hard kill.
            Player.PurgeStalePlaybackFiles();

            WireModFolderNotices();

            _treeContextMenu = BuildContextMenu();
            _rootContextMenu = BuildRootContextMenu();

            // Restore last window size
            var (w, h) = Settings.WindowSize;
            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

            this.AppWindow.Closing += (_, _) =>
            {
                var size = this.AppWindow.Size;
                Settings.WindowSize = (size.Width, size.Height);
                // Don't let a debounced Penumbra reload get dropped by closing mid-countdown.
                Playlist.FlushPenumbraMod();
            };
        }

        // ─── Dialog helper ───────────────────────────────────────────────────────

        public async Task<ContentDialogResult> ShowDialogAsync(
            string title, string content,
            string primary = "OK", string? secondary = null, string close = "")
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primary,
                SecondaryButtonText = secondary ?? "",
                CloseButtonText = close,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };
            return await dialog.ShowAsync();
        }

        // ─── File picker helpers
        public Task<string?> PickFolderAsync(string description = "")
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = !string.IsNullOrWhiteSpace(description)
            };

            return Task.FromResult(
                dialog.ShowDialog(GetOwnerWindow()) == System.Windows.Forms.DialogResult.OK
                    ? dialog.SelectedPath
                    : null);
        }

        public Task<string[]?> PickFilesAsync(params string[] extensions)
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = BuildFileDialogFilter(extensions),
                CheckFileExists = true,
                Multiselect = true
            };

            return Task.FromResult(
                dialog.ShowDialog(GetOwnerWindow()) == System.Windows.Forms.DialogResult.OK
                    ? dialog.FileNames
                    : null);
        }

        public Task<string?> PickSingleFileAsync(params string[] extensions)
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = BuildFileDialogFilter(extensions),
                CheckFileExists = true,
                Multiselect = false
            };

            return Task.FromResult(
                dialog.ShowDialog(GetOwnerWindow()) == System.Windows.Forms.DialogResult.OK
                    ? dialog.FileName
                    : null);
        }

        private static string BuildFileDialogFilter(params string[] extensions)
        {
            var filters = extensions
                .Where(ext => !string.IsNullOrWhiteSpace(ext))
                .Select(ext => ext.StartsWith('.') ? ext : "." + ext)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (filters.Length == 0)
                return "All files (*.*)|*.*";

            string pattern = string.Join(';', filters.Select(ext => "*" + ext));
            return $"Supported files ({pattern})|{pattern}|All files (*.*)|*.*";
        }

        private System.Windows.Forms.IWin32Window GetOwnerWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            return new Win32Window(hwnd);
        }

        private sealed class Win32Window(IntPtr handle) : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; } = handle;
        }
        // ─── Tree node helpers ───────────────────────────────────────────────────

        private static string GetNodeName(PlaylistNodeContent? node) => node?.Name ?? "";
        private static int GetNodeLevel(PlaylistNodeContent? node) => node?.Level ?? -1;
        private static string GetNodeDisplayText(PlaylistNodeContent? node) => node?.DisplayText ?? "";

        private static void SetNodeDisplayText(PlaylistNodeContent? node, string text)
        {
            if (node != null) node.DisplayText = text;
        }

        private PlaylistNodeContent? FindPlaylistNode(string name)
        {
            if (RootPlaylistItems.Count == 0) return null;
            foreach (var child in RootPlaylistItems[0].Children)
            {
                if (child.Name == name) return child;
            }
            return null;
        }

        private static PlaylistNodeContent? FindSongNode(PlaylistNodeContent playlistContent, string name)
        {
            foreach (var child in playlistContent.Children)
            {
                if (child.Name == name) return child;
            }
            return null;
        }

        private void GetPlaylistFromTargetNode(PlaylistNodeContent? target, out Playlist? targetPlaylist)
        {
            if (target == null) { targetPlaylist = null; return; }
            switch (target.Level)
            {
                case 1:
                    Playlists.TryGetValue(target.Name, out targetPlaylist);
                    break;
                case 2:
                    var parentName = target.Parent?.Name ?? "";
                    Playlists.TryGetValue(parentName, out targetPlaylist);
                    break;
                default:
                    targetPlaylist = null;
                    break;
            }
        }

        // ─── XAML event stubs ────────────────────────────────────────────────────

        private void PlaylistTreeView_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshBackground();
            var picklePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "pickle.png");
            if (System.IO.File.Exists(picklePath))
                BusyPickleImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(picklePath));
            // Before LoadPlaylists, not after: this is the earliest point where the Penumbra
            // settings are resolved, and capturing before the new build reads or heals anything is
            // what makes "the JSON as the previous version left it" true rather than approximate.
            // No-ops when nothing is configured yet — see the second call site in OpenSettingsAsync.
            Utils.VersionBackup.TryCaptureOnBoot();

            LoadPlaylists();

            _ = CheckForUpdatesAsync();
            if (string.IsNullOrWhiteSpace(Settings.PenumbraLocation) || string.IsNullOrWhiteSpace(Settings.ModName))
                _ = OpenSettingsAsync();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var filter = SearchTextBox.Text?.Trim() ?? string.Empty;
            LoadPlaylists(filter);
        }

        private void AddSongsButton_Click(object sender, RoutedEventArgs e)
        {
            // Handled by the flyout sub-items; this fires when the main button area is clicked
        }

        private void fromMyComputerToolStripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _ = AddSongsFromComputerAsync();
        }

        private async Task AddSongsFromComputerAsync()
        {
            try
            {
                Playlist? targetPlaylist = null;
                PlaylistNodeContent? targetContent = _selectedNode;

                if (_selectedNode != null)
                    GetPlaylistFromTargetNode(_selectedNode, out targetPlaylist);

                if (targetPlaylist == null)
                {
                    var name = ResolveTargetPlaylistForSingle();
                    if (name == null || !Playlists.TryGetValue(name, out targetPlaylist))
                    {
                        await ShowDialogAsync(AppStrings.Dlg_NoPlaylist_Title, AppStrings.Dlg_NoPlaylist_Content);
                        return;
                    }
                    targetContent = FindPlaylistNode(name);
                }

                var files = await PickFilesAsync(".ogg", ".wav", ".mp3", ".m4a", ".flac", ".scd");
                if (files == null || files.Length == 0) return;

                await AddOrInsertFilesToPlaylistAsync(targetContent, targetPlaylist, files);
            }
            catch (System.Exception ex)
            {
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorAddingSongs(FormatExceptionMessage(ex)));
            }
        }

        private void PlaylistTreeView_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_busyOverlayVisible) return;

            if (e.OriginalSource is not FrameworkElement fe)
                return;

            // Expanding a playlist is not selecting it, so leave the selection alone when the tap
            // landed on the chevron.
            if (IsExpandCollapseChevron(fe))
                return;

            var content = FindNodeContentFromElement(fe);
            HandleSelectionClick(content);

            if (content == null || content.Level < 1)
                return;

            _selectedNode = content;
        }

        // True when the tapped element is (or sits inside) a TreeViewItem's expand/collapse chevron,
        // which the default template names ExpandCollapseChevron.
        private static bool IsExpandCollapseChevron(FrameworkElement fe)
        {
            DependencyObject? cur = fe;
            while (cur != null)
            {
                if (cur is TreeViewItem) return false;
                if (cur is FrameworkElement named && named.Name.StartsWith("ExpandCollapse", StringComparison.Ordinal))
                    return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private async void PlaylistTreeView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_busyOverlayVisible) return;

            if (e.OriginalSource is not FrameworkElement fe)
                return;

            var node = FindNodeContentFromElement(fe);
            if (node == null)
                return;

            if (node.Level == 1)
            {
                // Options[0]/Children[0] is the "Off" placeholder entry, not a real song.
                if (!Playlists.TryGetValue(node.Name, out var playlistToStart) || playlistToStart.Options.Count <= 1)
                    return;

                var firstOption = playlistToStart.Options[1];
                var playlistContent = FindPlaylistNode(node.Name);
                if (playlistContent != null && playlistContent.Children.Count > 1)
                    _selectedNode = playlistContent.Children[1];

                PlayOption(firstOption, node.Name);
                return;
            }

            if (node.Level != 2)
                return;

            _selectedNode = node;

            if (node.Parent == null || !Playlists.TryGetValue(node.Parent.Name, out var playlist))
                return;

            var option = playlist.Options.FirstOrDefault(x => string.Equals(x.Name, node.Name, StringComparison.Ordinal));
            if (option != null)
                PlayOption(option, node.Parent.Name);
        }

        private async Task RenameNodeAsync(PlaylistNodeContent node)
        {
            if (node.Level == 1)
                await RenamePlaylistAsync(node);
            else if (node.Level == 2)
                await RenameSongAsync(node);
        }

        private async Task RenamePlaylistAsync(PlaylistNodeContent node)
        {
            if (!Playlists.TryGetValue(node.Name, out var playlist))
                return;

            var currentName = playlist.Name;
            var renameBox = new TextBox
            {
                Text = currentName,
                SelectionStart = 0,
                SelectionLength = currentName.Length,
                PlaceholderText = "Playlist name"
            };

            var dialog = new ContentDialog
            {
                Title = "Rename Playlist",
                Content = renameBox,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            var newName = renameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, currentName, StringComparison.Ordinal))
                return;

            try
            {
                playlist.Rename(newName);
                var renamed = IsFilterActive ? null : RenamePlaylistNode(currentName, newName, playlist);
                if (renamed == null)
                {
                    LoadPlaylistsAndExpand(newName);
                    renamed = FindPlaylistNode(newName);
                }
                if (renamed != null)
                {
                    SelectOnly(renamed);
                    _selectedNode = renamed;
                }
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(AppStrings.Dlg_Error, ex.Message);
            }
        }

        private async Task RenameSongAsync(PlaylistNodeContent node)
        {
            if (node.Parent == null || !Playlists.TryGetValue(node.Parent.Name, out var playlist))
                return;

            var song = playlist.Options.FirstOrDefault(x => string.Equals(x.Name, node.Name, StringComparison.Ordinal));
            if (song == null)
                return;

            var currentName = song.Name;
            var renameBox = new TextBox
            {
                Text = currentName,
                SelectionStart = 0,
                SelectionLength = currentName.Length,
                PlaceholderText = "Song name"
            };

            var dialog = new ContentDialog
            {
                Title = "Rename Song",
                Content = renameBox,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            var newName = renameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, currentName, StringComparison.Ordinal))
                return;

            var oldName = currentName;
            song.Name = newName;
            try
            {
                playlist.Save();
            }
            catch (Exception ex)
            {
                // Put the in-memory name back so the model doesn't drift from what's on disk.
                song.Name = oldName;
                await ShowDialogAsync(AppStrings.Dlg_Error, FormatExceptionMessage(ex));
                return;
            }
            var renamedSong = IsFilterActive ? null : RenameSongNode(playlist, oldName, newName);
            if (renamedSong == null)
            {
                LoadPlaylistsAndExpand(playlist.Name);
                var parentNode = FindPlaylistNode(playlist.Name);
                renamedSong = parentNode == null ? null : FindSongNode(parentNode, newName);
            }
            if (renamedSong != null)
            {
                SelectOnly(renamedSong);
                _selectedNode = renamedSong;
            }
        }

        private PlaylistNodeContent? FindNodeContentFromElement(FrameworkElement fe)
        {
            DependencyObject? cur = fe;
            while (cur != null)
            {
                if (cur is TreeViewItem tvi)
                    return PlaylistTreeView.ItemFromContainer(tvi) as PlaylistNodeContent;
                cur = VisualTreeHelper.GetParent(cur);
            }

            return null;
        }

        private void PlaylistTreeView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is Microsoft.UI.Xaml.FrameworkElement fe)
            {
                var content = FindNodeContentFromElement(fe);
                if (content == null) return;

                int level = content.Level;

                if (level == 0)
                {
                    _contextMenuNode = content;
                    _contextMenuNodes = new List<PlaylistNodeContent> { content };
                    _rootContextMenu.ShowAt(PlaylistTreeView, e.GetPosition(PlaylistTreeView));
                    return;
                }

                // Right-clicking inside the selection keeps it, so the menu acts on everything the
                // user picked. Right-clicking outside it moves the selection to that row first,
                // which is what makes "act on the selection" safe to do without looking.
                if (!_selection.Contains(content)) SelectOnly(content);

                _contextMenuNode = level >= 1 ? content : null;
                _contextMenuNodes = SelectionOrNode(_contextMenuNode);

                bool valid = level == 1 || level == 2;
                foreach (var item in _treeContextMenu.Items)
                    item.IsEnabled = valid;

                // Renaming is one node at a time by nature; there is no sensible new name to give a
                // batch, so it stays off while several rows are selected.
                _renameMenuItem.IsEnabled = valid && _contextMenuNodes.Count <= 1;

                if (valid)
                    _treeContextMenu.ShowAt(PlaylistTreeView, e.GetPosition(PlaylistTreeView));
            }
        }

        private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
        {
            var current = element;
            while (current != null)
            {
                if (current is T t) return t;
                current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>
        /// Merge needs exactly one playlist to merge INTO and at least one other to merge FROM. The
        /// target is the tree selection; the sources are picked in the dialog, so selecting several
        /// playlists is ambiguous rather than useful and disables the button.
        /// </summary>
        private bool CanMerge(List<PlaylistNodeContent> selected)
        {
            var playlistNodes = selected.Where(item => item.Level == 1).ToList();
            if (playlistNodes.Count != 1) return false;

            string target = playlistNodes[0].Name;
            return MergeCandidates(target).Count > 0;
        }

        /// <summary>
        /// Every playlist that could be folded into <paramref name="targetName"/> — that is, all of
        /// them except the target itself and the VFX groups, which are hidden from the tree
        /// (MainWindowCore.LoadPlaylists) and are not playlists in any sense the user would recognise.
        /// </summary>
        private static List<Playlist> MergeCandidates(string targetName) =>
            Playlists.Values
                .Where(p => !string.Equals(p.Name, targetName, StringComparison.Ordinal))
                .Where(p => !p.IsVFXGroup())
                .ToList();

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => _ = OpenSettingsAsync();
        private async void HelpButton_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://discord.gg/cY7eN5k7sc"));
        private void NewButton_Click(object sender, RoutedEventArgs e) => _ = OpenNewPlaylistAsync();
        private void DeleteButton_Click(object sender, RoutedEventArgs e) => _ = DoDeleteAsync();

        private void DancesButton_Click(object sender, RoutedEventArgs e) => _ = OpenDancesAsync();

        /// <summary>
        /// The dances in the DJ's dance mod.
        ///
        /// Deliberately NOT gated on first-run setup the way Settings is: most users have no separate
        /// dance mod at all, and nagging them about one on launch would be wrong. The dialog picks the
        /// mod itself the first time it is opened.
        /// </summary>
        private async Task OpenDancesAsync()
        {
            // Everything here is inside a try, because the caller discards the task. An exception
            // building or showing the dialog would otherwise be an unobserved task fault: no log
            // line, no message, and a toolbar button that simply does nothing when clicked.
            try
            {
                var dialog = new DancesDialog { XamlRoot = this.Content.XamlRoot };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError("Opening the dances dialog failed: {Error}", ex);
                try
                {
                    await new ContentDialog
                    {
                        XamlRoot = this.Content.XamlRoot,
                        Title = AppStrings.Dlg_Dances_Title,
                        Content = ex.Message,
                        CloseButtonText = "OK",
                    }.ShowAsync();
                }
                catch { /* the failure is already in the log; never fail while reporting a failure */ }
            }
        }

        private void TexturesButton_Click(object sender, RoutedEventArgs e) => _ = OpenTexturesAsync();

        /// <summary>
        /// The pictures on the DJ table and the laptop screen, which live in the VFX mod.
        ///
        /// Not gated on first-run setup, for the same reason the dances dialog is not: the dialog
        /// finds the mod itself, and most users have no VFX mod configured until they open it.
        /// </summary>
        private async Task OpenTexturesAsync()
        {
            // Inside a try because the caller discards the task. An exception building or showing the
            // dialog would otherwise be an unobserved fault: no log line, no message, and a toolbar
            // button that simply does nothing when clicked.
            try
            {
                var dialog = new TexturesDialog { XamlRoot = this.Content.XamlRoot };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError("Opening the table pictures dialog failed: {Error}", ex);
                try
                {
                    await new ContentDialog
                    {
                        XamlRoot = this.Content.XamlRoot,
                        Title = AppStrings.Dlg_Textures_Title,
                        Content = ex.Message,
                        CloseButtonText = "OK",
                    }.ShowAsync();
                }
                catch { /* the failure is already in the log; never fail while reporting a failure */ }
            }
        }

        private async Task OpenSettingsAsync()
        {
            var dialog = new SettingsDialog { XamlRoot = this.Content.XamlRoot };
            await dialog.ShowAsync();
            RefreshBackground();

            if (dialog.NamingOptionsChanged)
                await ApplyNamingOptionsAsync();
            else
                LoadPlaylists();

            // On a first run the boot capture necessarily no-opped: there was no configured mod
            // until the user filled this dialog in. This is the only point on that path where the
            // mod folder is guaranteed valid. Idempotent, so it costs nothing on every other path.
            Utils.VersionBackup.TryCaptureOnBoot();

            // Only here is the Settings ContentDialog provably closed. WinUI permits one
            // ContentDialog at a time, and a prompt raised from inside the OK handler — or from a
            // dispatcher callback that handler posts — runs while the close is still animating and
            // dies with "Only a single ContentDialog can be open at any time". CheckForUpdatesAsync
            // swallows that into the log, so the update prompt would silently never appear.
            if (dialog.UpdateChannelChanged)
                await CheckForUpdatesAsync();
        }

        /// <summary>
        /// Rewrites every song's name for the current display settings and rebuilds the tree from
        /// the new names. Shared, because Settings can be closed by Hide() rather than by OK — the
        /// sound-path and SoundCloud buttons do exactly that — and the reopened dialog's OK then
        /// lands after OpenSettingsAsync has already returned and read the flag.
        /// </summary>
        internal async Task ApplyNamingOptionsAsync()
        {
            var failed = await Task.Run(() => Library.RefreshStatNames());
            Playlists = Playlist.GetAll();
            if (failed.Count > 0)
                Utils.Logger.LogWarn("Song names: {Count} playlist(s) could not be saved: {Names}",
                    failed.Count, string.Join(", ", failed));
            LoadPlaylists();
        }

        internal void RefreshBackground()
        {
            var path = Settings.BackgroundImagePath;
            if (File.Exists(path))
                TreeViewBackgroundBrush.ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
        }

        private async Task OpenNewPlaylistAsync()
        {
            var dialog = new NewPlaylistDialog { XamlRoot = this.Content.XamlRoot };
            await dialog.ShowAsync();
            // The dialog adds the new playlist's node itself (AddCreatedPlaylist) after Create finishes.
        }

        // Shuffle/Sort surface their own error dialog and then rethrow. These are void event
        // handlers, so an escaping exception is an unhandled crash — swallow it here (already
        // reported, and nothing was written) and reload so the in-memory order can't drift from
        // what's actually on disk.
        private void RunPlaylistReorder(Action<Playlist> reorder)
        {
            foreach (var item in SelectedNodes())
            {
                if (item.Level != 1 || !Playlists.TryGetValue(item.Name, out var pl)) continue;
                try
                {
                    reorder(pl);
                    SyncPlaylistNode(pl);
                }
                catch (Exception ex)
                {
                    Utils.Logger.LogError("Reorder of '{Name}' failed: {Error}", pl.Name, ex);
                    Playlists = Playlist.GetAll();
                    LoadPlaylists();
                    return;
                }
            }
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            RunPlaylistReorder(pl => pl.Shuffle());
        }

        private void MergeButton_Click(object sender, RoutedEventArgs e) => _ = DoMergeAsync();

        /// <summary>
        /// Folds the playlists the user picks into the selected one.
        ///
        /// The merge itself runs off the UI thread: it moves a .scd per song and re-reads the whole
        /// library once per deleted source, which is far too slow to block on.
        /// </summary>
        private async Task DoMergeAsync()
        {
            try
            {
                var selected = SelectedNodes();
                if (!CanMerge(selected)) return;

                string targetName = selected.First(item => item.Level == 1).Name;
                if (!Playlists.TryGetValue(targetName, out var target)) return;

                var dialog = new MergePlaylistsDialog(target, MergeCandidates(targetName))
                {
                    XamlRoot = this.Content.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                var sources = dialog.SelectedSources;
                if (sources.Count == 0) return;

                SetProgressBarText(AppStrings.Prog_Merging);
                Playlist.MergeResult result;
                try
                {
                    result = await Task.Run(() => target.MergeFrom(sources, SetProgressBarPercent));
                }
                finally
                {
                    ClearProgressDisplay();
                }

                // Only the sources the merge actually removed leave the tree. One that failed to
                // delete is still a real playlist and must keep its node.
                foreach (var name in result.MergedSources)
                {
                    Playlists.Remove(name);
                    if (!IsFilterActive) RemovePlaylistNode(name);

                    // A song playing out of a playlist that just went away is still playing, and its
                    // entry survives under the same name in the target. Without this the lookup in
                    // MainWindow.Player fails and Next/Previous go dead with no explanation.
                    if (string.Equals(_nowPlayingPlaylistName, name, StringComparison.Ordinal))
                        _nowPlayingPlaylistName = targetName;
                }

                if (IsFilterActive) LoadPlaylists(SearchTextBox.Text);
                else SyncPlaylistNode(target);

                ClearSelection();
                UpdateSelectionCommands();

                if (result.Discarded.Count > 0)
                {
                    await ShowDialogAsync(AppStrings.Dlg_MergeComplete_Title,
                        AppStrings.MergeCompleteWithDiscards(result.Moved, targetName,
                            result.Discarded.Count));
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError("Merge failed: {Error}", ex);
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorMerge(FormatExceptionMessage(ex)));
                // The in-memory model may now disagree with disk — reload rather than guess.
                Playlists = Playlist.GetAll();
                LoadPlaylists();
            }
        }

        private SortDirection _currentSortDirection = SortDirection.Ascending;

        private void SortByBPM_Click(object sender, RoutedEventArgs e)
        {
            var direction = _currentSortDirection == SortDirection.Ascending
                ? SortDirection.Descending
                : SortDirection.Ascending;
            RunPlaylistReorder(pl => pl.Sort(direction));
            _currentSortDirection = direction;
        }

        // Kept separate from _currentSortDirection so toggling the zig-zag's starting end doesn't
        // silently flip the direction of the next plain BPM/Camelot sort.
        private SortDirection _zigZagSortDirection = SortDirection.Ascending;

        private void SortByBPMZigZag_Click(object sender, RoutedEventArgs e)
        {
            // First click opens on the fastest song; clicking again opens on the slowest.
            var direction = _zigZagSortDirection == SortDirection.Ascending
                ? SortDirection.Descending
                : SortDirection.Ascending;
            RunPlaylistReorder(pl => pl.SortByBpmZigZag(direction));
            _zigZagSortDirection = direction;
        }

        private void SortByName_Click(object sender, RoutedEventArgs e)
        {
            RunPlaylistReorder(pl => pl.SortByName());
        }

        private void SortByCamelot_Click(object sender, RoutedEventArgs e)
        {
            var direction = _currentSortDirection == SortDirection.Ascending
                ? SortDirection.Descending
                : SortDirection.Ascending;
            RunPlaylistReorder(pl => pl.SortByCamelot(direction));
            _currentSortDirection = direction;
        }

        private void ShowOperationSummary(string title, int successCount, int totalCount, System.Collections.Generic.List<string> errors)
        {
            string content = errors.Count == 0
                ? AppStrings.Processed(successCount, totalCount)
                : AppStrings.Processed(successCount, totalCount) +
                  AppStrings.ProcessedErrors(string.Join("\n", System.Linq.Enumerable.Take(errors, 10)) +
                  (errors.Count > 10 ? "\n" + AppStrings.AndMore(errors.Count - 10) : ""));

            _ = ShowDialogAsync(title, content);
        }
    }
}
