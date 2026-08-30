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
                RefreshBaselineScdDisplay();
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
                UpdateSoundCloudStatus();
            }
            catch (Exception e) {
                Console.WriteLine(e.ToString());
            }

            // Deliberately outside the block above. That block starts with Settings.ModName, which
            // reaches Penumbra over IPC and can throw — and its catch would then skip every
            // remaining initializer, leaving this dropdown blank. A blank dropdown that OK still
            // acts on is how a tester gets silently moved off test builds, so it is initialized on
            // its own and cannot be collateral damage from an unrelated failure.
            try
            {
                SelectCurrentUpdateChannel();
            }
            catch (Exception e)
            {
                Utils.Logger.LogWarn("Could not preselect the update channel: {Error}", e.Message);
            }

            _loading = false;
            ValidateFields();
        }

        private void ValidateFields()
        {
            // The sound path is no longer checked here: SoundPathDialog owns it, and validates it
            // far more thoroughly than an EndsWith(".scd") ever did.
            IsPrimaryButtonEnabled = !string.IsNullOrEmpty(DirectoryPathTextBox.Text)
                && Directory.Exists(DirectoryPathTextBox.Text)
                && File.Exists(Path.Combine(DirectoryPathTextBox.Text, "meta.json"));
        }

        // ---- sound path ------------------------------------------------------------------------

        private void RefreshBaselineScdDisplay() =>
            BaselineScdValueText.Text = Utils.PenumbraMeta.NormalizeScdKey(Settings.BaselineScdKey);

        /// <summary>
        /// Opens the sound-path editor.
        ///
        /// The path, the picker and the rename all moved into their own dialog: the rename needs a
        /// preview, a live length check and a companion-mod list, which is more than a Settings row
        /// can carry — and the key and the rename are dangerous to confuse, so they belong side by
        /// side rather than one of them buried in a list of toggles.
        ///
        /// WinUI allows one ContentDialog at a time, so Settings steps aside and comes back, the same
        /// dance <see cref="SoundCloudSignInButton_Click"/> does. XamlRoot is captured first because
        /// it is not reliable to read after Hide().
        /// </summary>
        private async void EditSoundPathButton_Click(object sender, RoutedEventArgs e)
        {
            var xamlRoot = this.XamlRoot;
            Hide();

            try
            {
                var dialog = new SoundPathDialog { XamlRoot = xamlRoot };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError("Could not open the sound path editor: {Error}", ex.Message);
            }

            RefreshBaselineScdDisplay();

            try
            {
                await this.ShowAsync();
            }
            catch (Exception ex)
            {
                // Reopening is a convenience; whatever the editor did is already saved.
                Utils.Logger.LogWarn("Could not reopen Settings after editing the sound path: {Error}", ex.Message);
            }

            // That reopen was a second ShowAsync, so OpenSettingsAsync already returned when Hide()
            // completed the first one. A channel change made in this second pass is ours to act on.
            if (UpdateChannelChanged)
                await App.MainWindow.CheckForUpdatesAsync();
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
            => ValidateFields();

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

        // Selects the ComboBox item whose Tag matches the saved channel. Settings.UpdateChannel
        // always answers "stable" or "testing" — never empty — so a miss can only mean the XAML
        // tags and the setting have drifted apart. The fallback matches by tag, not by position:
        // index 0 only happens to be the stable item today. If even that misses, the dropdown is
        // left unselected on purpose — SelectedUpdateChannel then reads null and OK leaves the
        // stored channel alone, which is safer than guessing on the user's behalf.
        private void SelectCurrentUpdateChannel()
        {
            UpdateChannelComboBox.SelectedItem =
                FindChannelItem(Settings.UpdateChannel) ?? FindChannelItem(Settings.UpdateChannelStable);
        }

        private ComboBoxItem? FindChannelItem(string channel) =>
            UpdateChannelComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, channel, StringComparison.OrdinalIgnoreCase));

        // Null when the dropdown holds no selection — which means initialization failed, not that
        // the user chose stable. Callers must treat null as "leave the stored channel alone";
        // defaulting it to stable here would quietly unsubscribe a tester from test builds.
        private string? SelectedUpdateChannel =>
            (UpdateChannelComboBox.SelectedItem as ComboBoxItem)?.Tag as string;

        /// <summary>
        /// True when OK changed the update channel. The caller that owns this dialog
        /// (<see cref="MainWindow.OpenSettingsAsync"/>) reads it once the dialog has actually
        /// closed and re-checks for updates then — see the comment there for why it cannot be
        /// done from inside the OK handler.
        /// </summary>
        internal bool UpdateChannelChanged { get; private set; }

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

        private void UpdateSoundCloudStatus()
        {
            bool signedIn = Pickles_Playlist_Editor.Tools.YtDlpService.HasSoundCloudSignIn;
            SoundCloudStatusText.Text = signedIn
                ? "Signed in. Tracks the artist marked as downloadable will come through at original quality instead of a 128kbps copy. Note that Go+ tracks protected with DRM still cannot be downloaded."
                : "Not signed in. Public tracks still download normally; signing in adds original-quality downloads and private tracks you have access to.";
            SoundCloudSignInButton.IsEnabled = !signedIn;
            SoundCloudSignOutButton.IsEnabled = signedIn;
        }

        private async void SoundCloudSignInButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (!SoundCloudLoginDialog.IsWebViewRuntimeAvailable())
            {
                SoundCloudStatusText.Text = "Signing in needs the Microsoft Edge WebView2 runtime, which isn't installed. Get it from https://developer.microsoft.com/microsoft-edge/webview2/ and then try again.";
                return;
            }

            // WinUI only allows one ContentDialog open at a time, so Settings has to step
            // aside for the login window and come back afterwards. Without the reopen the
            // user is dumped back to the main window with no confirmation that signing in
            // worked. Capture XamlRoot first — it isn't reliable to read after Hide().
            var xamlRoot = this.XamlRoot;
            Hide();

            try
            {
                var login = new SoundCloudLoginDialog { XamlRoot = xamlRoot };
                await login.ShowAsync();
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError("SoundCloud sign-in window failed: {Error}", ex.Message);
            }

            UpdateSoundCloudStatus();

            try
            {
                await this.ShowAsync();
            }
            catch (Exception ex)
            {
                // Reopening is a convenience; the sign-in itself already took effect.
                Utils.Logger.LogWarn("Could not reopen Settings after sign-in: {Error}", ex.Message);
            }

            // That reopen was a second ShowAsync, so OpenSettingsAsync already returned and read
            // UpdateChannelChanged back when Hide() completed the first one. A channel change made
            // in this second pass is ours to act on. The dialog is closed by now, so the update
            // prompt has the field to itself.
            if (UpdateChannelChanged)
                await App.MainWindow.CheckForUpdatesAsync();
        }

        private void SoundCloudSignOutButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            Pickles_Playlist_Editor.Tools.YtDlpService.ClearSoundCloudSignIn();
            UpdateSoundCloudStatus();
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
            // The sound path is written by SoundPathDialog, which owns both halves of that change —
            // the key this app writes AND the effect files that ask for it.
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

            // Written only on a real change. With nothing stored, Settings.UpdateChannel derives
            // the channel from the running build, and that derived state is worth preserving: a
            // tester who opens Settings for some unrelated reason and clicks OK would otherwise be
            // pinned to "testing" forever, instead of rejoining main releases on their own when the
            // matching stable version ships.
            string? newChannel = SelectedUpdateChannel;
            if (newChannel != null
                && !string.Equals(newChannel, Settings.UpdateChannel, StringComparison.OrdinalIgnoreCase))
            {
                Settings.UpdateChannel = newChannel;
                UpdateChannelChanged = true;
                Utils.Logger.LogInfo("Settings: update channel set to '{Channel}'.", Settings.UpdateChannel);
            }

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

        // Restoring an older version's config is a manual copy, so the folder has to be reachable —
        // it lives under %LOCALAPPDATA% where nobody would find it unaided.
        private void OpenBackupsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = Utils.VersionBackup.VersionsRoot;
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Utils.Logger.LogWarn("Could not open the backups folder: {Error}", ex.Message);
            }
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
