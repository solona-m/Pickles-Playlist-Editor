using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    public sealed partial class NewPlaylistDialog : ContentDialog
    {
        public NewPlaylistDialog()
        {
            this.InitializeComponent();
            IsPrimaryButtonEnabled = false;
        }

        private void ValidateFields()
        {
            IsPrimaryButtonEnabled = !string.IsNullOrEmpty(PlaylistNameTextBox.Text)
                && (string.IsNullOrEmpty(DirectoryPathTextBox.Text)
                    || Directory.Exists(DirectoryPathTextBox.Text));
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                DirectoryPathTextBox.Text = folder.Path;
                ValidateFields();
            }
        }

        private void PlaylistNameTextBox_TextChanged(object sender, TextChangedEventArgs e) => ValidateFields();

        private void DirectoryPathTextBox_TextChanged(object sender, TextChangedEventArgs e) => ValidateFields();

        private void GoButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            string playlistName = PlaylistNameTextBox.Text;
            string directory = DirectoryPathTextBox.Text;

            App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    App.MainWindow.SetProgressBarText(AppStrings.Prog_ImportingSongs);
                    await Task.Run(() => Playlist.Create(playlistName, directory,
                        App.MainWindow.SetProgressBarPercent));
                    App.MainWindow.ClearProgressDisplay();
                    App.MainWindow.LoadPlaylistsAndExpand(playlistName);
                }
                catch (Exception ex)
                {
                    await App.MainWindow.ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.ErrorAddingSongs(ex.Message));
                }
            });
        }
    }
}
