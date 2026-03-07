using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
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
        private PlaylistNodeContent? _selectedNode;

        private readonly MenuFlyout _treeContextMenu;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int attrValue, int attrSize);
        private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public MainWindow()
        {
            this.InitializeComponent();

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
            this.Title = "Pickles Playlist Editor";

            _treeContextMenu = BuildContextMenu();

            // Restore last window size
            var (w, h) = Settings.WindowSize;
            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

            this.AppWindow.Closing += (_, _) =>
            {
                var size = this.AppWindow.Size;
                Settings.WindowSize = (size.Width, size.Height);
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

        // ─── File picker helpers (unpackaged: must set HWND) ─────────────────────

        public async Task<string?> PickFolderAsync(string description = "")
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }

        public async Task<string[]?> PickFilesAsync(params string[] extensions)
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
            foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return null;
            var paths = new string[files.Count];
            for (int i = 0; i < files.Count; i++) paths[i] = files[i].Path;
            return paths;
        }

        public async Task<string?> PickSingleFileAsync(params string[] extensions)
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
            foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
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
            LoadPlaylists();
            _ = CheckForUpdatesAsync();
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

                var files = await PickFilesAsync(".ogg", ".wav", ".mp3", ".m4a", ".flac");
                if (files == null || files.Length == 0) return;

                await AddOrInsertFilesToPlaylistAsync(targetContent, targetPlaylist, files);
            }
            catch (System.Exception ex)
            {
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorAddingSongs(ex.Message));
            }
        }

        private void PlaylistTreeView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is Microsoft.UI.Xaml.FrameworkElement fe)
            {
                PlaylistNodeContent? content = null;
                DependencyObject? cur = fe;
                while (cur != null && content == null)
                {
                    if (cur is TreeViewItem tvi)
                        content = PlaylistTreeView.ItemFromContainer(tvi) as PlaylistNodeContent;
                    cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
                }
                if (content == null) return;

                int level = content.Level;
                _contextMenuNode = level >= 1 ? content : null;

                bool valid = level == 1 || level == 2;
                foreach (var item in _treeContextMenu.Items)
                    item.IsEnabled = valid;

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

        private void PlaylistTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
        {
            bool hasCheckedPlaylist = false;
            bool hasCheckedSong = false;

            foreach (var item in PlaylistTreeView.SelectedItems.OfType<PlaylistNodeContent>())
            {
                if (item.Level == 1) hasCheckedPlaylist = true;
                if (item.Level == 2) { hasCheckedSong = true; _selectedNode = item; }
            }

            if (!hasCheckedSong)
            {
                var single = PlaylistTreeView.SelectedItems.OfType<PlaylistNodeContent>().FirstOrDefault();
                if (single != null) _selectedNode = single;
            }

            DeleteButton.IsEnabled = hasCheckedPlaylist || hasCheckedSong;
            ShuffleButton.IsEnabled = hasCheckedPlaylist;
            SortByBPMButton.IsEnabled = hasCheckedPlaylist;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => _ = OpenSettingsAsync();
        private void NewButton_Click(object sender, RoutedEventArgs e) => _ = OpenNewPlaylistAsync();
        private void DeleteButton_Click(object sender, RoutedEventArgs e) => _ = DoDeleteAsync();

        private async Task OpenSettingsAsync()
        {
            var dialog = new SettingsDialog { XamlRoot = this.Content.XamlRoot };
            await dialog.ShowAsync();
            RefreshBackground();
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
            LoadPlaylists();
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in PlaylistTreeView.SelectedItems.OfType<PlaylistNodeContent>())
            {
                if (item.Level == 1 && Playlists.TryGetValue(item.Name, out var pl))
                    pl.Shuffle();
            }
            LoadPlaylists();
        }

        private SortDirection _currentSortDirection = SortDirection.Ascending;

        private void SortByBPM_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in PlaylistTreeView.SelectedItems.OfType<PlaylistNodeContent>())
            {
                if (item.Level == 1 && Playlists.TryGetValue(item.Name, out var pl))
                {
                    if (_currentSortDirection == SortDirection.Ascending)
                    {
                        pl.Sort(SortDirection.Descending);
                        _currentSortDirection = SortDirection.Descending;
                    }
                    else
                    {
                        pl.Sort(SortDirection.Ascending);
                        _currentSortDirection = SortDirection.Ascending;
                    }
                }
            }
            LoadPlaylists();
        }

        private void SortByName_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in PlaylistTreeView.SelectedItems.OfType<PlaylistNodeContent>())
            {
                if (item.Level == 1 && Playlists.TryGetValue(item.Name, out var pl))
                    pl.SortByName();
            }
            LoadPlaylists();
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
