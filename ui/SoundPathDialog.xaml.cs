using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    /// <summary>
    /// Everything about the mod's sound path, in one place: which path this app writes when it
    /// imports a song, and the rename that moves the whole mod — effects and songs — onto a path
    /// nobody else claims.
    ///
    /// It is its own dialog rather than four more rows in Settings for two reasons. The rename needs
    /// real estate — a preview, a live length check, and a list of companion mods — and Settings is
    /// already a long scroll of unrelated toggles. And the key and the rename are easy to confuse and
    /// dangerous to confuse: setting the key alone leaves the effects asking for a path the mod no
    /// longer supplies. Side by side, with the hint between them, they read as the two halves they
    /// are.
    ///
    /// WinUI allows one ContentDialog at a time, so Settings hides itself before showing this and
    /// reopens afterwards — the same dance <see cref="SettingsDialog.SoundCloudSignInButton_Click"/>
    /// does, for the same reason.
    /// </summary>
    public sealed partial class SoundPathDialog : ContentDialog
    {
        private bool _loading = true;
        private bool _updatingBaselineScdOptions;

        /// <summary>
        /// The mod every read here is about: the picker, the preview, the companion scan and the
        /// key check.
        ///
        /// Passed in rather than read from <see cref="Utils.PenumbraMeta.ModRoot"/>, which is the
        /// SAVED mod. Settings only writes the folder on OK, so a user who browsed to another mod
        /// and opened this editor first got a picker, a prefilled key and a rename plan all
        /// describing the mod they had just navigated away from — with nothing on screen saying so.
        ///
        /// The caller passes a folder it has already COMMITTED to Settings, because the rename half
        /// derives its target from Settings by design (see
        /// <see cref="Utils.SoundPathRename.BuildPlan"/> and
        /// <see cref="Utils.PenumbraMeta.AssertModRootUnchanged"/>) — so this is the configured mod,
        /// captured once at open. Do not weaken that to "whatever the user typed": the enable rule
        /// for the rename button is only that the folder EXISTS, so a folder with no meta.json in it
        /// would arm a rename here while the plan was built against a different mod entirely.
        /// </summary>
        private readonly string _modRoot;

        /// <summary>The key as it stands when this dialog closes, for Settings to display.</summary>
        internal string ResultKey { get; private set; } = string.Empty;

        public SoundPathDialog(string modRoot)
        {
            _modRoot = modRoot ?? string.Empty;
            this.InitializeComponent();

            BaselineScdTextBox.Text = Settings.BaselineScdKey ?? string.Empty;
            ResultKey = BaselineScdTextBox.Text;

            _loading = false;
            QueueBaselineScdOptions();
            RefreshSoundPathPreview();
            QueueCompanionScan();
        }

        // ---- the key ---------------------------------------------------------------------------

        /// <summary>
        /// Fills the picker with the mod's own .scd paths, off the UI thread.
        ///
        /// Collecting them parses the whole manifest and then visits every node of it, and under v4
        /// that manifest is the entire library — 400KB and ~11,000 lines for a working DJ pack. Doing
        /// that in the constructor is time the dialog spends not appearing, so it goes to a
        /// background thread and lands when it lands, the same shape <see cref="QueueCompanionScan"/>
        /// uses for the same reason. The picker is a convenience over the text box beside it, which
        /// is usable the instant the dialog opens.
        /// </summary>
        private void QueueBaselineScdOptions()
        {
            string root = _modRoot;
            _ = Task.Run(() =>
            {
                List<string> scdKeys;
                try
                {
                    scdKeys = Utils.PenumbraMeta.CollectScdKeys(root);
                }
                catch (Exception ex)
                {
                    // An unreadable manifest costs the picker, not the dialog.
                    Utils.Logger.LogWarn("Could not list the mod's sound paths: {Error}", ex.Message);
                    scdKeys = new List<string>();
                }

                DispatcherQueue.TryEnqueue(() => ShowBaselineScdOptions(scdKeys));
            });
        }

        private void ShowBaselineScdOptions(List<string> scdKeys)
        {
            // Read the box now rather than when the scan started: the user can type while it runs.
            var current = Utils.PenumbraMeta.NormalizeScdKey(BaselineScdTextBox.Text);

            _updatingBaselineScdOptions = true;
            BaselineScdComboBox.Items.Clear();
            foreach (var scdKey in scdKeys)
                BaselineScdComboBox.Items.Add(scdKey);
            BaselineScdComboBox.IsEnabled = scdKeys.Count > 0;
            BaselineScdComboBox.SelectedItem = scdKeys.FirstOrDefault(
                key => string.Equals(key, current, StringComparison.OrdinalIgnoreCase));
            _updatingBaselineScdOptions = false;
        }

        private void SyncBaselineScdSelection()
        {
            if (_updatingBaselineScdOptions) return;

            var current = Utils.PenumbraMeta.NormalizeScdKey(BaselineScdTextBox.Text);
            _updatingBaselineScdOptions = true;
            BaselineScdComboBox.SelectedItem = BaselineScdComboBox.Items
                .OfType<string>()
                .FirstOrDefault(key => string.Equals(key, current, StringComparison.OrdinalIgnoreCase));
            _updatingBaselineScdOptions = false;
        }

        private void BaselineScdComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingBaselineScdOptions) return;
            if (BaselineScdComboBox.SelectedItem is string scdKey)
                BaselineScdTextBox.Text = scdKey;
        }

        private void BaselineScdTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SyncBaselineScdSelection();
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(BaselineScdTextBox.Text)
                && BaselineScdTextBox.Text.Trim().EndsWith(".scd", StringComparison.OrdinalIgnoreCase);
        }

        // ---- the rename ------------------------------------------------------------------------

        // The companion scan reads every .avfx in every mod folder under the Penumbra root — tens of
        // megabytes — so it runs off the UI thread and its result is cached, keyed by the mod folder
        // and the path, because either changing invalidates it.
        private string? _companionScanKey;
        private List<Utils.CompanionMod> _companions = new();
        private bool _companionScanRunning;

        /// <summary>
        /// The path the rename moves AWAY from: the SAVED key, never the text box above.
        ///
        /// The rename rewrites files on disk to match what the mod uses today, and that is the saved
        /// key. Planning against an unsaved edit would plan against a mod that does not exist.
        /// </summary>
        private static string SavedBaselinePath =>
            Utils.PenumbraMeta.NormalizeScdKey(Settings.BaselineScdKey);

        private string CandidateSoundPath =>
            Utils.SoundPathRename.SuggestPath(DjNameTextBox.Text, SavedBaselinePath);

        private void DjNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
            => RefreshSoundPathPreview();

        private void RefreshSoundPathPreview()
        {
            if (_loading) return;

            string current = SavedBaselinePath;
            if (!Directory.Exists(_modRoot) || string.IsNullOrWhiteSpace(current))
            {
                SoundPathPreviewText.Text = AppStrings.SoundPathNoMod;
                ChangeSoundPathButton.IsEnabled = false;
                return;
            }

            // The DJ-NAME budget, not the path budget. Quoting the path length beside a box labelled
            // "your DJ name" is what made this read as a demand for a 16-character name.
            (int minName, int maxName) = Utils.SoundPathRename.NameLengthRange(current);
            string candidate = CandidateSoundPath;
            bool valid = false;

            if (string.IsNullOrWhiteSpace(DjNameTextBox.Text))
            {
                SoundPathPreviewText.Text = AppStrings.SoundPathEnterName(maxName);
            }
            else if (string.IsNullOrEmpty(candidate))
            {
                SoundPathPreviewText.Text = AppStrings.SoundPathNameUnusable;
            }
            else
            {
                string? problem = Utils.SoundPathRename.ValidateNewPath(current, candidate);
                valid = problem == null;
                SoundPathPreviewText.Text = !valid ? problem
                    : Utils.SoundPathRename.WasPadded(DjNameTextBox.Text, current)
                        // Say so rather than hand back a path with letters they never typed.
                        ? AppStrings.SoundPathPreviewPadded(candidate, minName)
                        : AppStrings.SoundPathPreview(candidate);
            }

            ChangeSoundPathButton.IsEnabled = valid;
        }

        private void QueueCompanionScan()
        {
            if (_loading) return;

            string root = _modRoot;
            string oldPath = SavedBaselinePath;
            string key = root + "|" + oldPath;
            if (key == _companionScanKey || _companionScanRunning) return;
            if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(oldPath)) return;

            _companionScanRunning = true;
            string penumbraRoot = Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? string.Empty;
            string modName = Path.GetFileName(root.TrimEnd('\\', '/'));

            _ = Task.Run(() =>
            {
                List<Utils.CompanionMod> found;
                try
                {
                    found = Utils.SoundPathRename.FindCompanions(penumbraRoot, modName, oldPath);
                }
                catch (Exception ex)
                {
                    Utils.Logger.LogWarn("Companion mod scan failed: {Error}", ex.Message);
                    found = new List<Utils.CompanionMod>();
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    _companionScanRunning = false;
                    _companionScanKey = key;
                    _companions = found;
                    ShowCompanions(oldPath);
                });
            });
        }

        private void ShowCompanions(string oldPath)
        {
            CompanionModsPanel.Children.Clear();

            if (_companions.Count == 0)
            {
                CompanionModsSection.Visibility = Visibility.Collapsed;
                return;
            }

            CompanionModsSection.Visibility = Visibility.Visible;
            CompanionModsHeader.Text = AppStrings.SoundPathCompanionHeader(oldPath);

            foreach (var companion in _companions)
            {
                // Nothing is ticked by default. Other people's mods sit in the same Penumbra root and
                // legitimately share this path; only the user knows which of these are theirs.
                CompanionModsPanel.Children.Add(new CheckBox
                {
                    Content = companion.Selectable
                        ? AppStrings.SoundPathCompanion(companion.Name, companion.RefCount)
                        : AppStrings.SoundPathCompanionHasSongs(companion.Name),
                    Tag = companion.Name,
                    IsChecked = false,
                    IsEnabled = companion.Selectable,
                    MinHeight = 0,
                });
            }
        }

        private List<string> SelectedCompanionNames() =>
            CompanionModsPanel.Children
                .OfType<CheckBox>()
                .Where(box => box.IsChecked == true)
                .Select(box => box.Tag as string ?? string.Empty)
                .Where(name => name.Length > 0)
                .ToList();

        private async void ChangeSoundPathButton_Click(object sender, RoutedEventArgs e)
        {
            string oldPath = SavedBaselinePath;
            string newPath = CandidateSoundPath;
            var selected = SelectedCompanionNames();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

            Utils.SoundPathRenamePlan plan;
            try
            {
                // Planning reads the whole mod folder, so it does not belong on the UI thread.
                plan = await Task.Run(() => Utils.SoundPathRename.BuildPlan(oldPath, newPath));
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError("Sound path rename: planning failed: {Error}", ex);
                MessageBox(hwnd, ex.Message, AppStrings.Dlg_SoundPath_Title, 0x00000010); // MB_ICONERROR
                return;
            }

            if (!plan.CanApply)
            {
                MessageBox(hwnd, string.Join("\n\n", plan.Errors), AppStrings.Dlg_SoundPath_Title,
                    0x00000030); // MB_ICONWARNING
                return;
            }

            if (MessageBox(hwnd, BuildConfirmText(plan, selected), AppStrings.Dlg_SoundPath_Title,
                    0x00000001 | 0x00000030) != 1) // MB_OKCANCEL | MB_ICONWARNING, IDOK
                return;

            Utils.SoundPathRenameResult result;
            App.MainWindow.SetProgressBarText(AppStrings.Prog_RenamingSoundPath);
            try
            {
                result = await Task.Run(() => Utils.SoundPathRename.Apply(plan, selected));
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError("Sound path rename failed: {Error}", ex);
                result = new Utils.SoundPathRenameResult { Error = ex.Message };
            }
            finally
            {
                App.MainWindow.ClearProgressDisplay();
            }

            if (!result.Succeeded)
            {
                MessageBox(hwnd, result.Error ?? string.Empty, AppStrings.Dlg_SoundPath_Title,
                    0x00000010); // MB_ICONERROR
                return;
            }

            // Apply has already persisted the key; keep the box in step so OK writes the same value.
            BaselineScdTextBox.Text = plan.NewPath;
            DjNameTextBox.Text = string.Empty;
            _companionScanKey = null;
            QueueBaselineScdOptions();
            QueueCompanionScan();

            MessageBox(hwnd, BuildResultText(plan, result), AppStrings.Dlg_SoundPath_Title,
                0x00000040); // MB_ICONINFORMATION
        }

        private static string BuildConfirmText(Utils.SoundPathRenamePlan plan, List<string> selected)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(AppStrings.SoundPathConfirmHeader(plan.OldPath, plan.NewPath));
            sb.AppendLine();
            sb.AppendLine(AppStrings.SoundPathConfirmCounts(plan.ModName, plan.RefCount, plan.OptionKeyCount));

            foreach (string name in selected)
            {
                var companion = plan.Companions.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (companion != null)
                    sb.AppendLine(AppStrings.SoundPathConfirmCompanion(companion.Name, companion.RefCount));
            }

            // A file naming the path that this app could not confirm is a Sound field may be a
            // reference it will fail to patch — the half-renamed state to avoid — so it goes in front
            // of the user rather than into the log.
            if (plan.SuspectFiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(AppStrings.SoundPathConfirmSuspects(plan.SuspectFiles.Count));
                foreach (string file in plan.SuspectFiles.Take(5))
                    sb.AppendLine("  " + Path.GetFileName(file));
            }

            sb.AppendLine();
            sb.Append(AppStrings.SoundPathConfirmFooter);
            return sb.ToString();
        }

        private static string BuildResultText(Utils.SoundPathRenamePlan plan, Utils.SoundPathRenameResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(AppStrings.SoundPathDone(plan.NewPath, result.AvfxPatched, result.OptionKeysRenamed));
            if (result.CompanionsPatched.Count > 0)
                sb.AppendLine(AppStrings.SoundPathDoneCompanions(string.Join(", ", result.CompanionsPatched)));

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine();
                foreach (string warning in result.Warnings)
                    sb.AppendLine(warning);
            }

            if (!string.IsNullOrEmpty(result.BackupFolder))
            {
                sb.AppendLine();
                sb.Append(AppStrings.SoundPathBackup(result.BackupFolder));
            }
            return sb.ToString();
        }

        // ---- OK --------------------------------------------------------------------------------

        private void OkButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Setting the key and renaming the path are different operations, and only the second
            // touches files. A hand-typed key the mod's effects do not ask for leaves the songs on a
            // path nothing requests — the music stops and the DJ hears whoever won the shared slot.
            // Warn, but honour it: a pack legitimately already on sound/dam.scd or sound/lolo.scd
            // needs exactly this box to point the app at its real key.
            if (!ConfirmBaselineKeyChange())
            {
                args.Cancel = true;
                return;
            }

            Settings.BaselineScdKey = BaselineScdTextBox.Text;
            ResultKey = Utils.PenumbraMeta.NormalizeScdKey(BaselineScdTextBox.Text);
        }

        private bool ConfirmBaselineKeyChange()
        {
            try
            {
                string typed = Utils.PenumbraMeta.NormalizeScdKey(BaselineScdTextBox.Text);
                string stored = SavedBaselinePath;
                if (string.Equals(typed, stored, StringComparison.OrdinalIgnoreCase))
                    return true;

                string modRoot = _modRoot;
                if (!Directory.Exists(modRoot))
                    return true;

                int askingForOld = Utils.SoundPathRename.CountAvfxReferences(modRoot, stored);
                if (askingForOld == 0)
                    return true;
                if (Utils.SoundPathRename.CountAvfxReferences(modRoot, typed) > 0)
                    return true;

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                return MessageBox(hwnd,
                    AppStrings.SoundPathKeyOnlyWarning(askingForOld, stored, typed),
                    AppStrings.Dlg_SoundPath_Title,
                    0x00000001 | 0x00000030) == 1; // MB_OKCANCEL | MB_ICONWARNING, IDOK
            }
            catch (Exception ex)
            {
                // A failed check must never block the user from saving.
                Utils.Logger.LogWarn("Could not check the sound path against the mod: {Error}", ex.Message);
                return true;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    }
}
