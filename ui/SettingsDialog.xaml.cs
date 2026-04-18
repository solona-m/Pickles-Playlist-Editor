using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    public sealed partial class SettingsDialog : ContentDialog
    {
        public SettingsDialog()
        {
            this.InitializeComponent();

            try
            {
                if (!string.IsNullOrWhiteSpace(Settings.ModName))
                {
                    DirectoryPathTextBox.Text = Path.Combine(
                        Settings.PenumbraLocation ?? string.Empty,
                        Settings.ModName ?? string.Empty);
                }
                BaselineScdTextBox.Text = Settings.BaselineScdKey;
                BackgroundImageTextBox.Text = Settings.BackgroundImagePath;
                ScdVolumePercentageBox.Value = Settings.ScdVolumePercentage;
                LoopSongsCheckBox.IsChecked = Settings.LoopSongs;
                NormalizeVolumeCheckBox.IsChecked = Settings.NormalizeVolume;
                FadeWithDistanceCheckBox.IsChecked = Settings.FadeWithDistance;
                AutoReloadCheckBox.IsChecked = Settings.AutoReloadMod;
                FadeBackgroundMusicCheckBox.IsChecked = Settings.FadeBackgroundMusic;
                BusNumberComboBox.SelectedIndex = BusNumberToIndex(Settings.BusNumber);
                ValidateFields();
            }
            catch (Exception e) {
                Console.WriteLine(e.ToString());
            }
        }

        private void ValidateFields()
        {
            bool validDirectory = !string.IsNullOrEmpty(DirectoryPathTextBox.Text)
                && Directory.Exists(DirectoryPathTextBox.Text)
                && File.Exists(Path.Combine(DirectoryPathTextBox.Text, "meta.json"));
            bool validScd = !string.IsNullOrWhiteSpace(BaselineScdTextBox.Text)
                && BaselineScdTextBox.Text.Trim().EndsWith(".scd", StringComparison.OrdinalIgnoreCase);
            IsPrimaryButtonEnabled = validDirectory && validScd;
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

        private async void BrowseBaselineScdButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".scd");
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string selectedPath = file.Path;
            string baselineKey = Path.GetFileName(selectedPath);

            if (Directory.Exists(DirectoryPathTextBox.Text))
            {
                try
                {
                    string rel = Path.GetRelativePath(DirectoryPathTextBox.Text, selectedPath);
                    if (!rel.StartsWith(".."))
                        baselineKey = rel.Replace(Path.DirectorySeparatorChar, '/');
                }
                catch
                {
                    baselineKey = Path.GetFileName(selectedPath);
                }
            }

            BaselineScdTextBox.Text = baselineKey;
        }

        private async void BrowseBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".gif");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file != null)
                BackgroundImageTextBox.Text = file.Path;
        }

        private void DirectoryPathTextBox_TextChanged(object sender, TextChangedEventArgs e) => ValidateFields();

        private void BaselineScdTextBox_TextChanged(object sender, TextChangedEventArgs e) => ValidateFields();

        // Bus 1 (Unknown) is intentionally excluded from the UI.
        // ComboBox indices: 0=BGM(0), 1=SoundEffect(2), 2=Voice(3), 3=System(4), 4=Ambient(5)
        private static readonly int[] IndexToBusMap = [16, 2, 3, 4, 5];

        private static int BusNumberToIndex(int busNumber) {
            int idx = System.Array.IndexOf(IndexToBusMap, busNumber);
            return idx >= 0 ? idx : 0;
        }

        private static int IndexToBusNumber(int index) =>
            (index >= 0 && index < IndexToBusMap.Length) ? IndexToBusMap[index] : 0;

        private void OkButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            string path = DirectoryPathTextBox.Text.TrimEnd('\\', '/');
            string modName = Path.GetFileName(path);
            string penLocation = path.Length > modName.Length
                ? path[..^modName.Length]
                : path + Path.DirectorySeparatorChar;
            Settings.ModName = modName;
            Settings.PenumbraLocation = penLocation;
            Settings.BaselineScdKey = BaselineScdTextBox.Text;
            Settings.BackgroundImagePath = BackgroundImageTextBox.Text.Trim();
            Settings.ScdVolumePercentage = (int)ScdVolumePercentageBox.Value;
            Settings.LoopSongs = LoopSongsCheckBox.IsChecked == true;
            Settings.NormalizeVolume = NormalizeVolumeCheckBox.IsChecked == true;
            Settings.FadeWithDistance = FadeWithDistanceCheckBox.IsChecked == true;
            Settings.AutoReloadMod = AutoReloadCheckBox.IsChecked == true;
            Settings.FadeBackgroundMusic = FadeBackgroundMusicCheckBox.IsChecked == true;
            Settings.BusNumber = IndexToBusNumber(BusNumberComboBox.SelectedIndex);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        private void RepairLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            int result = MessageBox(hwnd,
                AppStrings.Dlg_RepairLibrary_Content,
                AppStrings.Dlg_RepairLibrary_Title,
                0x00000001 | 0x00000030); // MB_OKCANCEL | MB_ICONWARNING
            if (result != 1) // IDOK
                return;

            Library.Repair();
        }

        private void ConvertToStereoButton_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                App.MainWindow.SetProgressBarText(AppStrings.Prog_ConvertingToStereo);
                await Task.Run(() => Library.ConvertToStereo(App.MainWindow.SetProgressBarPercent));
                App.MainWindow.ClearProgressDisplay();
            });
        }

        private void OrganizeLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            int result = MessageBox(hwnd,
                AppStrings.Dlg_OrganizeLibrary_Content,
                AppStrings.Dlg_OrganizeLibrary_Title,
                0x00000001 | 0x00000030); // MB_OKCANCEL | MB_ICONWARNING
            if (result != 1) // IDOK
                return;

            Library.Cleanup();
        }
    }
}
