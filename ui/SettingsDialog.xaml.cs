using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    public sealed partial class SettingsDialog : ContentDialog
    {
        private bool _loading = true;
        private bool _updatingBaselineScdOptions;

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
                NormalizationLoudnessBox.Value = Settings.NormalizationLoudness;
                LoopSongsCheckBox.IsChecked = Settings.LoopSongs;
                NormalizeVolumeCheckBox.IsChecked = Settings.NormalizeVolume;
                ScdVersionShiftCheckBox.IsChecked = Settings.ScdVersionShift;
                FadeWithDistanceCheckBox.IsChecked = Settings.FadeWithDistance;
                AutoReloadCheckBox.IsChecked = Settings.AutoReloadMod;
                FadeBackgroundMusicCheckBox.IsChecked = Settings.FadeBackgroundMusic;
                BusNumberComboBox.SelectedIndex = BusNumberToIndex(Settings.BusNumber);
                SelectCurrentLanguage();
                UpdateCookieStatus();
            }
            catch (Exception e) {
                Console.WriteLine(e.ToString());
            }

            _loading = false;
            RefreshBaselineScdOptions(updateTextFromMod: string.IsNullOrWhiteSpace(BaselineScdTextBox.Text));
            ValidateFields();
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

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Penumbra mod directory",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(DirectoryPathTextBox.Text) ? DirectoryPathTextBox.Text : string.Empty
            };

            if (dialog.ShowDialog(GetOwnerWindow()) != System.Windows.Forms.DialogResult.OK)
                return;

            DirectoryPathTextBox.Text = dialog.SelectedPath;
            ValidateFields();
        }

        private void BaselineScdComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingBaselineScdOptions)
                return;

            if (BaselineScdComboBox.SelectedItem is string scdKey)
                BaselineScdTextBox.Text = scdKey;
        }

        private void RefreshBaselineScdOptions(bool updateTextFromMod)
        {
            var scdKeys = FindScdKeysInMod(DirectoryPathTextBox.Text);
            var current = NormalizeScdKey(BaselineScdTextBox.Text);
            var selected = scdKeys.FirstOrDefault(key => string.Equals(key, current, StringComparison.OrdinalIgnoreCase));

            _updatingBaselineScdOptions = true;
            BaselineScdComboBox.Items.Clear();
            foreach (var scdKey in scdKeys)
                BaselineScdComboBox.Items.Add(scdKey);
            BaselineScdComboBox.IsEnabled = scdKeys.Count > 0;
            BaselineScdComboBox.SelectedItem = selected;
            _updatingBaselineScdOptions = false;

            if (!updateTextFromMod || scdKeys.Count == 0)
                return;

            BaselineScdTextBox.Text = selected ?? scdKeys[0];
        }

        private void SyncBaselineScdSelection()
        {
            if (_updatingBaselineScdOptions)
                return;

            var current = NormalizeScdKey(BaselineScdTextBox.Text);
            var selected = BaselineScdComboBox.Items
                .OfType<string>()
                .FirstOrDefault(key => string.Equals(key, current, StringComparison.OrdinalIgnoreCase));

            _updatingBaselineScdOptions = true;
            BaselineScdComboBox.SelectedItem = selected;
            _updatingBaselineScdOptions = false;
        }

        // Penumbra v4 keeps every option group inside the root meta.json, so scanning loose top-level
        // *.json files for a "Files"/"Options" shape finds nothing at all. PenumbraMeta reads the
        // manifest (DefaultData.Files plus every group's options) and falls back to the v3 layout for
        // a folder Penumbra hasn't migrated yet.
        private static List<string> FindScdKeysInMod(string? modDirectory) =>
            Pickles_Playlist_Editor.Utils.PenumbraMeta.CollectScdKeys(modDirectory);

        private static string NormalizeScdKey(string? scdKey) =>
            Pickles_Playlist_Editor.Utils.PenumbraMeta.NormalizeScdKey(scdKey);

        private void BrowseBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Select background image",
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = GetExistingParentDirectory(BackgroundImageTextBox.Text)
            };

            if (dialog.ShowDialog(GetOwnerWindow()) == System.Windows.Forms.DialogResult.OK)
                BackgroundImageTextBox.Text = dialog.FileName;
        }

        private static string GetExistingDirectory(string? path) =>
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : string.Empty;

        private static string GetExistingParentDirectory(string? path)
        {
            try
            {
                return GetExistingDirectory(Path.GetDirectoryName(path ?? string.Empty));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static System.Windows.Forms.IWin32Window GetOwnerWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            return new Win32Window(hwnd);
        }

        private sealed class Win32Window(IntPtr handle) : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; } = handle;
        }

        private void DirectoryPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loading)
                RefreshBaselineScdOptions(updateTextFromMod: true);
            ValidateFields();
        }

        private void BaselineScdTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SyncBaselineScdSelection();
            ValidateFields();
        }

        // Bus 1 (Unknown) is intentionally excluded from the UI.
        // ComboBox indices: 0=BGM(0), 1=SoundEffect(2), 2=Voice(3), 3=System(4), 4=Ambient(5)
        private static readonly int[] IndexToBusMap = [16, 2, 3, 4, 5];

        private static int BusNumberToIndex(int busNumber) {
            int idx = System.Array.IndexOf(IndexToBusMap, busNumber);
            return idx >= 0 ? idx : 0;
        }

        private static int IndexToBusNumber(int index) =>
            (index >= 0 && index < IndexToBusMap.Length) ? IndexToBusMap[index] : 0;

        // Selects the ComboBox item whose Tag matches the saved language tag
        // (falls back to "System Default" at index 0).
        private void SelectCurrentLanguage()
        {
            string current = Settings.Language ?? string.Empty;
            foreach (var obj in LanguageComboBox.Items)
            {
                if (obj is ComboBoxItem item
                    && string.Equals((item.Tag as string) ?? string.Empty, current, StringComparison.OrdinalIgnoreCase))
                {
                    LanguageComboBox.SelectedItem = item;
                    return;
                }
            }
            LanguageComboBox.SelectedIndex = 0;
        }

        private string SelectedLanguageTag =>
            (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

        private void UpdateCookieStatus()
        {
            var status = Pickles_Playlist_Editor.Tools.YtDlpService.GetCookieStatus();
            CookieStatusText.Text = status switch
            {
                Pickles_Playlist_Editor.Tools.CookieStatus.Valid =>
                    "Cookies found. Age-restricted YouTube downloads are enabled.",
                Pickles_Playlist_Editor.Tools.CookieStatus.Expired =>
                    "Cookies found but they appear to be expired. Re-export cookies using the VRCVideoCacher browser extension.",
                Pickles_Playlist_Editor.Tools.CookieStatus.Invalid =>
                    "Cookies found but could not be read. Re-export cookies using the VRCVideoCacher browser extension.",
                _ => "No cookies found. Use the VRCVideoCacher browser extension to send cookies.",
            };
            ClearCookiesButton.IsEnabled = Pickles_Playlist_Editor.Tools.YtDlpService.HasCookies;
        }

        private void ClearCookiesButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            Pickles_Playlist_Editor.Tools.YtDlpService.ClearCookies();
            UpdateCookieStatus();
        }

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
            Settings.NormalizationLoudness = (int)NormalizationLoudnessBox.Value;
            Settings.LoopSongs = LoopSongsCheckBox.IsChecked == true;
            Settings.NormalizeVolume = NormalizeVolumeCheckBox.IsChecked == true;
            Settings.ScdVersionShift = ScdVersionShiftCheckBox.IsChecked == true;
            Settings.FadeWithDistance = FadeWithDistanceCheckBox.IsChecked == true;
            Settings.AutoReloadMod = AutoReloadCheckBox.IsChecked == true;
            Settings.FadeBackgroundMusic = FadeBackgroundMusicCheckBox.IsChecked == true;
            Settings.BusNumber = IndexToBusNumber(BusNumberComboBox.SelectedIndex);

            string newLanguage = SelectedLanguageTag;
            bool languageChanged = !string.Equals(newLanguage, Settings.Language ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Settings.Language = newLanguage;
            if (languageChanged)
            {
                try
                {
                    Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = newLanguage;
                }
                catch { }
                PromptRestartForLanguage();
            }
        }

        private void PromptRestartForLanguage()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            int result = MessageBox(hwnd,
                AppStrings.Dlg_RestartRequired_Content,
                AppStrings.Dlg_RestartRequired_Title,
                0x00000004 | 0x00000040); // MB_YESNO | MB_ICONINFORMATION
            if (result != 6) // not IDYES
                return;

            try
            {
                Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
            }
            catch
            {
                // Fall back to a manual relaunch if the lifecycle restart is unavailable.
                try
                {
                    string exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                        System.Diagnostics.Process.Start(exe);
                }
                catch { }
                Microsoft.UI.Xaml.Application.Current.Exit();
            }
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
                List<string> errors = null;
                await Task.Run(() => { errors = Library.ConvertToStereo(App.MainWindow.SetProgressBarPercent); });
                App.MainWindow.ClearProgressDisplay();

                if (errors != null && errors.Count > 0)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                    string msg = $"{errors.Count} file(s) could not be updated:\n\n" + string.Join("\n", errors.Take(10));
                    if (errors.Count > 10) msg += $"\n...and {errors.Count - 10} more.";
                    MessageBox(hwnd, msg, "Convert to Stereo — Errors", 0x00000030); // MB_ICONWARNING
                }
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
