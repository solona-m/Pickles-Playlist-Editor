using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Tmb;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    /// <summary>
    /// The dances in the DJ mod: what is there, and adding, renaming, removing and reordering it.
    ///
    /// A separate dialog rather than another section of Settings because it edits a DIFFERENT MOD.
    /// Everything else in this app writes to <see cref="Settings.ModName"/>, the music mod; the
    /// dances live in the DJ's dance/VFX pack, which is usually a different folder entirely. Putting
    /// that on its own surface, with the mod named at the top, is the clearest way to stop the two
    /// being confused — and confusing them is the worst thing this feature could do.
    ///
    /// WinUI allows one ContentDialog at a time, so the add pane is a section of this one rather than
    /// a second dialog, and every confirmation is a Win32 message box — the same approach
    /// <see cref="SoundPathDialog"/> takes, for the same reason.
    /// </summary>
    public sealed partial class DancesDialog : ContentDialog
    {
        private DanceGroupRef? _group;
        private List<DanceEntry> _dances = new();
        private List<DjModCandidate> _candidates = new();
        private List<DanceSourceMod> _sources = new();
        private TmbTrackBundle? _bundle;
        private HashSet<string> _djStrings = new(StringComparer.OrdinalIgnoreCase);

        private bool _sourcesLoaded;

        /// <summary>The name last filled in from a selection, so a typed-over one is not clobbered.</summary>
        private string _suggestedName = string.Empty;

        /// <summary>True while an add is in flight, so a second click cannot start another.</summary>
        private bool _adding;

        /// <summary>
        /// False until the constructor has finished wiring everything up.
        ///
        /// Guards every SelectionChanged handler. A control that raises one while the XAML is still
        /// being parsed reaches handlers whose other controls do not exist yet, and the resulting null
        /// reference is reported only as "XAML parsing failed" with no inner detail at all.
        /// </summary>
        private bool _ready;

        public DancesDialog()
        {
            this.InitializeComponent();

            DanceSortCombo.SelectedIndex = 0;   // mod order
            _ready = true;

            LoadDanceMod();
        }

        // ---- choosing the mod --------------------------------------------------------------------

        private void LoadDanceMod()
        {
            string penumbra = Settings.PenumbraLocation ?? string.Empty;
            string folder = Settings.DanceModName;

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(Path.Combine(penumbra, folder)))
            {
                ShowPicker();
                return;
            }

            string modRoot = Path.Combine(penumbra, folder);
            var group = DanceMod.FindDancesGroup(modRoot, folder, Settings.DanceGroupId);

            if (group == null)
            {
                DanceModText.Text = AppStrings.DanceModNoGroup(PenumbraOptions.DisplayName(modRoot));
                ShowPicker();
                return;
            }

            _group = group;
            DanceModText.Text = PenumbraOptions.DisplayName(modRoot);
            PickerSection.Visibility = Visibility.Collapsed;
            DancesSection.Visibility = Visibility.Visible;
            LoadGroups(modRoot, folder, group);
            RefreshDances();
            RepairBrokenDances();
        }

        /// <summary>The mod a repair pass has already been run over, so it runs once per mod.</summary>
        private string _repairedMod = string.Empty;

        /// <summary>
        /// Fixes any dance in this mod that would crash the game, without being asked to.
        ///
        /// Here, on LOAD, rather than only on the add path, because of who is actually affected. A DJ
        /// pack gets shared; the person left with a crashing dance is usually not the one who built
        /// it, and has no reason ever to add a dance. Repairing only when something is written would
        /// fix the author's copy and nobody else's.
        ///
        /// Off the UI thread and after the list has already been drawn. It reads every animation the
        /// mod's options name and may rewrite two of them, which is not work to do inside a handler
        /// that is holding the window — and in the overwhelmingly common case it finds nothing, says
        /// nothing, and the user never learns it happened.
        /// </summary>
        private void RepairBrokenDances()
        {
            if (_group == null
                || string.Equals(_repairedMod, _group.ModRoot, StringComparison.OrdinalIgnoreCase))
                return;

            _repairedMod = _group.ModRoot;
            var group = _group;

            _ = Task.Run(() =>
            {
                DanceWriteResult result;
                try
                {
                    result = DanceModWrites.RepairMod(group);
                }
                catch (Exception ex)
                {
                    // Nothing was asked for, so nothing is reported. The log is the record.
                    Logger.LogWarn("Repairing {Mod} on load failed: {Error}", group.ModName, ex.Message);
                    return;
                }

                if (result.Warnings.Count == 0) return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    // The mod on disk changed underneath the list that is already on screen, and the
                    // effect block was read from files this may have just rewritten.
                    RefreshDances();

                    // A box rather than the status line. Files in somebody's mod folder were altered
                    // without them asking, and the one thing that must not happen is that they find
                    // out later from a backup folder they do not recognise.
                    MessageBox(OwnerWindow(), string.Join("\n\n", result.Warnings),
                        AppStrings.Dlg_Dances_Title, 0x00000030); // MB_ICONWARNING
                });
            });
        }

        /// <summary>
        /// Fills the group picker, showing how many dances each group holds.
        ///
        /// Every group is listed rather than only the plausible ones: detection goes on contents, and
        /// a mod whose dances live somewhere unexpected is exactly the case the user needs to be able
        /// to correct.
        /// </summary>
        private void LoadGroups(string modRoot, string folder, DanceGroupRef selected)
        {
            _groups = DanceGroupIO.ListGroups(modRoot, folder);
            var counts = DanceMod.DanceCountsByGroup(modRoot);

            _loadingGroups = true;
            GroupCombo.Items.Clear();
            foreach (var group in _groups)
            {
                counts.TryGetValue(group.Name, out int dances);
                GroupCombo.Items.Add(AppStrings.DanceGroupOption(group.Name, dances));
            }
            GroupCombo.SelectedIndex = _groups.FindIndex(g =>
                g.Id == selected.Id && string.Equals(g.Name, selected.Name, StringComparison.Ordinal));
            _loadingGroups = false;
        }

        private List<DanceGroupRef> _groups = new();
        private bool _loadingGroups;

        private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _loadingGroups) return;
            int at = GroupCombo.SelectedIndex;
            if (at < 0 || at >= _groups.Count) return;

            _group = _groups[at];
            Settings.DanceGroupId = _group.Id?.ToString() ?? string.Empty;
            _sourcesLoaded = false;
            RefreshDances();
        }

        private void ShowPicker()
        {
            DancesSection.Visibility = Visibility.Collapsed;
            AddSection.Visibility = Visibility.Collapsed;
            PickerSection.Visibility = Visibility.Visible;
            StatusText.Text = AppStrings.DanceScanRunning;

            string penumbra = Settings.PenumbraLocation ?? string.Empty;
            string configured = Settings.ModName ?? string.Empty;

            _ = Task.Run(() =>
            {
                List<DjModCandidate> found;
                try
                {
                    found = DanceMod.FindCandidates(penumbra, configured);
                }
                catch (Exception ex)
                {
                    Logger.LogWarn("Dance mod scan failed: {Error}", ex.Message);
                    found = new List<DjModCandidate>();
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    // Only mods whose dances already carry a DJ effect block. Any mod holding a .pap
                    // technically qualifies, but that list is 41 entries long and mostly dance packs
                    // to install FROM rather than the VFX mod to install INTO — offering them invites
                    // the one mistake this feature must not make.
                    var usable = found.Where(c => c.HasDjBlock).ToList();

                    // Unless none qualify, in which case an empty list would be a dead end.
                    bool relaxed = usable.Count == 0;
                    if (relaxed) usable = found;

                    _candidates = usable;
                    CandidateList.Items.Clear();
                    foreach (var candidate in usable)
                        CandidateList.Items.Add(
                            AppStrings.DanceModCandidate(candidate.Name, candidate.DanceCount));

                    StatusText.Text = usable.Count == 0 ? AppStrings.DanceModNoneFound
                        : relaxed ? AppStrings.DanceModNonePrepped(usable.Count)
                        : AppStrings.DanceModFound(usable.Count);
                });
            });
        }

        private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            int at = CandidateList.SelectedIndex;
            if (at < 0 || at >= _candidates.Count) return;

            var chosen = _candidates[at];
            if (chosen.DancesGroup == null)
            {
                StatusText.Text = AppStrings.DanceModNoGroup(chosen.Name);
                return;
            }

            if (chosen.Format == ModFormat.V3)
                StatusText.Text = AppStrings.DanceModLegacyLayout;

            Settings.DanceModName = chosen.Folder;
            Settings.DanceGroupId = chosen.DancesGroup.Id?.ToString() ?? string.Empty;
            LoadDanceMod();
        }

        private void ChangeDanceModButton_Click(object sender, RoutedEventArgs e) => ShowPicker();

        // ---- the list ----------------------------------------------------------------------------

        /// <summary>Display row to index in <see cref="_dances"/>, under the current filter and sort.</summary>
        private List<int> _view = new();

        private void RefreshDances()
        {
            if (_group == null) return;

            _dances = DanceMod.ReadDances(_group);
            RefreshDanceList();

            // The effect block is read from the mod's own dances, so it has to be reloaded whenever
            // the list changes — and its absence is what makes Add impossible rather than merely
            // unwise.
            try
            {
                (_bundle, _djStrings) = DanceMod.LoadBundle(_dances, _group.ModRoot);
                StatusText.Text = AppStrings.DanceBundle(_bundle.TrackCount, _bundle.EffectPaths.Count);
            }
            catch (Exception ex)
            {
                _bundle = null;
                StatusText.Text = ex.Message;
            }

            AddDanceButton.IsEnabled = _bundle != null;
        }

        /// <summary>Rebuilds the visible rows from the current filter and sort.</summary>
        private void RefreshDanceList()
        {
            string filter = DanceFilterBox.Text?.Trim() ?? string.Empty;
            bool byName = DanceSortCombo.SelectedIndex == 1;

            var rows = Enumerable.Range(0, _dances.Count)
                .Where(i => filter.Length == 0
                    || _dances[i].Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

            if (byName)
                rows = rows.OrderBy(i => _dances[i].Name, StringComparer.CurrentCultureIgnoreCase);

            _view = rows.ToList();

            DanceList.Items.Clear();
            foreach (int i in _view)
                DanceList.Items.Add(Describe(_dances[i]));

            DancesHeader.Text = _view.Count == _dances.Count
                ? AppStrings.DanceCount(_dances.Count)
                : AppStrings.DanceCountFiltered(_view.Count, _dances.Count);

        }

        private void DanceFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            RefreshDanceList();
        }

        private void DanceSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            RefreshDanceList();
        }

        private static string Describe(DanceEntry dance)
        {
            string races = dance.Races.Count > 0 ? string.Join(" ", dance.Races) : "?";
            string health = dance.Health == DanceHealth.Ok ? string.Empty : "  ⚠ " + dance.Health;
            return $"{dance.Name}    {races}{health}";
        }

        // ---- add ---------------------------------------------------------------------------------

        private void AddDanceButton_Click(object sender, RoutedEventArgs e)
        {
            // The add pane takes the dialog over rather than appearing below the list. A
            // ContentDialog cannot be resized and its height is capped, so two long lists competing
            // for the same space leaves both unusable — the source list ended up a few rows tall at
            // the very bottom of the window.
            ShowAddPane(true);
            if (_sourcesLoaded) return;

            _sourcesLoaded = true;
            StatusText.Text = AppStrings.DanceScanRunning;
            string penumbra = Settings.PenumbraLocation ?? string.Empty;
            string? skip = _group == null ? null : Path.GetFileName(_group.ModRoot.TrimEnd('\\', '/'));

            _ = Task.Run(() =>
            {
                List<DanceSourceMod> found;
                try { found = DanceSourceScan.Scan(penumbra, skip); }
                catch (Exception ex)
                {
                    Logger.LogWarn("Dance source scan failed: {Error}", ex.Message);
                    found = new List<DanceSourceMod>();
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    _sources = found;
                    ShowSources(string.Empty);
                    StatusText.Text = AppStrings.DanceSourcesFound(
                        found.Count, found.Sum(m => m.Dances.Count));
                });
            });
        }

        /// <summary>
        /// The source list, flattened and filtered.
        ///
        /// Flat rather than a tree of mods: on a real Penumbra folder this is 38 mods and 517 dances,
        /// which is far too many to browse but exactly right to search.
        /// </summary>
        private void ShowSources(string filter)
        {
            SourceList.Items.Clear();
            _visibleSources.Clear();

            foreach (var mod in _sources)
            {
                foreach (var dance in mod.Dances)
                {
                    if (filter.Length > 0
                        && dance.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                        && mod.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    _visibleSources.Add(dance);
                    SourceList.Items.Add($"{dance.Label}    —  {mod.Name}");
                    if (_visibleSources.Count >= 300) return;
                }
            }
        }

        private readonly List<DanceSource> _visibleSources = new();

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            ShowSources(SearchBox.Text?.Trim() ?? string.Empty);
        }

        private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            var source = SelectedSource();
            if (source == null) { RefreshAddPreview(); return; }

            // Follow the selection unless the user has typed their own name. Only filling a blank box
            // meant picking Thriller and then Waltz installed the Waltz animation under the name
            // "Thriller" — the wrong name on the right dance, with nothing on screen to reveal it.
            if (string.IsNullOrWhiteSpace(DanceNameBox.Text)
                || string.Equals(DanceNameBox.Text, _suggestedName, StringComparison.Ordinal))
            {
                DanceNameBox.Text = source.Label;
            }
            _suggestedName = source.Label;

            IncludeStartCheckBox.IsEnabled = source.StartByRace.Count > 0;
            IncludeStartCheckBox.IsChecked = source.StartByRace.Count > 0;
            RefreshAddPreview();
        }

        /// <summary>
        /// Rebuilds the plan when the intro toggle changes.
        ///
        /// Without this the checkbox was decorative: the plan is built in RefreshAddPreview, so
        /// unticking it left the _start.pap still copied and still registered in the option.
        /// </summary>
        private void IncludeStart_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            RefreshAddPreview();
        }

        private DanceSource? SelectedSource()
        {
            int at = SourceList.SelectedIndex;
            return at >= 0 && at < _visibleSources.Count ? _visibleSources[at] : null;
        }

        private void DanceNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            RefreshAddPreview();
        }

        private AddDancePlan? _addPlan;

        private void RefreshAddPreview()
        {
            var source = SelectedSource();
            if (source == null || _group == null || _bundle == null)
            {
                AddPreviewText.Text = source != null && _bundle == null
                    ? AppStrings.DanceNoBundle
                    : string.Empty;
                    _addPlan = null;
                return;
            }

            _addPlan = DanceModWrites.PlanAdd(_group, _dances, source, source.Races,
                DanceNameBox.Text?.Trim() ?? string.Empty,
                IncludeStartCheckBox.IsChecked == true, _bundle, _djStrings);

            AddPreviewText.Text = _addPlan.CanApply
                ? AppStrings.AddDancePreview(_addPlan.OldAnimationName, PapFile.DanceAnimationName,
                      _addPlan.SoundPathsRemoved.Count, _addPlan.Tracks, _addPlan.Effects.Count)
                  + "\n" + AppStrings.AddDanceBodies(_addPlan.Races.Count)
                  + " " + AppStrings.AddDanceFiles(_addPlan.Writes.Count,
                      _addPlan.Files.Count, _addPlan.Bytes / 1048576.0)
                  + (_addPlan.Warnings.Count > 0 ? "\n\n" + string.Join("\n", _addPlan.Warnings) : string.Empty)
                : AppStrings.DanceCannotAdd + "\n" + string.Join("\n", _addPlan.Errors);

            // The footer button is never disabled: a disabled button does not raise Click, so the
            // reason would reach neither the user nor the log — which is exactly how an add came
            // to "do nothing" with no trace of why. Pressing it reports the reason instead.
            if (!_addPlan.CanApply)
                Logger.LogInfo("Dance '{Dance}' cannot be added: {Errors}",
                    _addPlan.DanceName, string.Join("; ", _addPlan.Errors));
        }

        private async Task AddSelectedDanceAsync()
        {
            // Say why nothing happened rather than returning in silence. An enabled button that does
            // nothing when clicked is the single most confusing failure this dialog can produce, and
            // it already happened once.
            if (_group == null || _addPlan == null || _bundle == null || !_addPlan.CanApply)
            {
                string why = _group == null ? "no group is selected"
                    : _bundle == null ? "this mod has no DJ effect block to copy"
                    : _addPlan == null ? "no dance is selected"
                    : string.Join(" ", _addPlan.Errors);
                Logger.LogWarn("Add dance did nothing: {Reason}", why);
                MessageBox(OwnerWindow(), why, AppStrings.Dlg_Dances_Title, 0x00000030);
                return;
            }

            Logger.LogInfo("Adding dance '{Dance}' to {Mod}/{Group}: {Files} files, {Paths} paths.",
                _addPlan.DanceName, _group.ModName, _group.Name,
                _addPlan.Writes.Count, _addPlan.Files.Count);

            // Off the UI thread. Preparing a dance reads and rewrites multi-megabyte animations
            // and then blocks on Penumbra's reload for up to five seconds; run inline it froze the
            // window for the whole operation and the progress text set on the line above could
            // never actually paint.
            var group = _group;
            var plan = _addPlan;
            var bundle = _bundle;
            var djStrings = _djStrings;

            // EVERYTHING from here is inside the try, not just the write. The caller discards this
            // task, so anything that escapes becomes an unobserved fault: no dialog, no log line, and
            // the add pane left half-dismissed. Refreshing the list afterwards does unguarded file
            // I/O, so that is not a theoretical path.
            _adding = true;
            IsPrimaryButtonEnabled = false;
            App.MainWindow.SetProgressBarText(AppStrings.Prog_AddingDance);

            try
            {
                DanceWriteResult result;
                try
                {
                    result = await Task.Run(() => DanceModWrites.Add(group, plan, bundle, djStrings));
                }
                catch (Exception ex)
                {
                    Logger.LogError("Adding dance '{Dance}' threw: {Error}", plan.DanceName, ex);
                    result = new DanceWriteResult { Error = ex.Message };
                }

                if (result.Succeeded)
                {
                    ShowAddPane(false);
                    DanceNameBox.Text = string.Empty;
                }

                // Refresh BEFORE reporting. RefreshDances claims the status line for the effect-block
                // summary, so reporting first meant the "was added" confirmation was overwritten in
                // the same frame and never rendered — and since the success popup was deliberately
                // removed, that left a successful add with no confirmation at all.
                RefreshDances();
                Report(result, AppStrings.AddDanceDone(plan.DanceName));
            }
            catch (Exception ex)
            {
                Logger.LogError("Finishing the dance add failed: {Error}", ex);
                MessageBox(OwnerWindow(), ex.Message, AppStrings.Dlg_Dances_Title, 0x00000010);
            }
            finally
            {
                App.MainWindow.ClearProgressDisplay();
                IsPrimaryButtonEnabled = true;
                _adding = false;
            }
        }

        /// <summary>
        /// Swaps between the dance list and the add pane; only one is ever on screen.
        ///
        /// The footer changes with it. While adding, the dialog offers Add and Cancel and drops
        /// Close: those are the only two ways out of a half-finished add, and leaving an accented
        /// Close sitting there was drawing the eye away from the action the pane exists for.
        /// </summary>
        private void ShowAddPane(bool adding)
        {
            AddSection.Visibility = adding ? Visibility.Visible : Visibility.Collapsed;
            DancesSection.Visibility = adding ? Visibility.Collapsed : Visibility.Visible;

            PrimaryButtonText = adding ? AppStrings.AddDanceAction : string.Empty;
            SecondaryButtonText = adding ? AppStrings.Btn_Cancel : string.Empty;
            CloseButtonText = adding ? string.Empty : AppStrings.Btn_Close;
        }

        /// <summary>
        /// The footer's primary action. Always cancels the dialog's own close: this dialog manages
        /// its own panes, and an add should leave the user looking at the updated list.
        /// </summary>
        private void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;

            // An add is no longer instantaneous, and the dialog stays open across it, so a second
            // click would start a second add from the SAME plan — whose duplicate-name and
            // folder-exists checks were both made before the first one ran. Both would succeed and
            // the group would end up with two identical options. The button is disabled for the
            // duration as well; this is the guard that does not depend on the UI keeping up.
            if (_adding) return;

            if (AddSection.Visibility == Visibility.Visible) _ = AddSelectedDanceAsync();
        }

        private void SecondaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            ShowAddPane(false);
        }

        // ---- plumbing ----------------------------------------------------------------------------

        /// <summary>
        /// Reports how a write went.
        ///
        /// Warnings are surfaced rather than logged and forgotten: the one that matters says Penumbra
        /// did not pick the change up, and a user who does not act on it can lose the edit the next
        /// time they touch the mod.
        /// </summary>
        private void Report(DanceWriteResult result, string success)
        {
            IntPtr hwnd = OwnerWindow();

            if (!result.Succeeded)
            {
                MessageBox(hwnd, result.Error ?? string.Empty, AppStrings.Dlg_Dances_Title,
                    0x00000010); // MB_ICONERROR
                return;
            }

            // Success is reported in the status line, not in a box to dismiss: the list right above it
            // already shows the dance appear, so a popup only adds a click to every single add.
            if (result.Warnings.Count == 0)
            {
                StatusText.Text = success;
                return;
            }

            // A warning still interrupts. The one that matters says Penumbra did not pick the change
            // up, and a user who scrolls past it can lose the edit the next time they touch the mod.
            MessageBox(hwnd, success + "\n\n" + string.Join("\n\n", result.Warnings),
                AppStrings.Dlg_Dances_Title, 0x00000030); // MB_ICONWARNING
        }

        private static IntPtr OwnerWindow() =>
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    }
}
