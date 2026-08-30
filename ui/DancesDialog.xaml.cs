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

        /// <summary>The staged order, which is only written when the user saves it.</summary>
        private List<int>? _pendingOrder;

        private bool _sourcesLoaded;

        public DancesDialog()
        {
            this.InitializeComponent();
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
            var groups = DanceGroupIO.ListGroups(modRoot, folder);

            // By remembered id first: a group can be renamed, and matching on the name alone would
            // quietly start editing a different group the day somebody does.
            var group = groups.FirstOrDefault(g =>
                    g.Id is { } id && id.ToString() == Settings.DanceGroupId)
                ?? groups.FirstOrDefault(g =>
                    string.Equals(g.Name, DanceMod.DancesGroupName, StringComparison.OrdinalIgnoreCase));

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
                    _candidates = found;
                    CandidateList.Items.Clear();
                    foreach (var candidate in found)
                        CandidateList.Items.Add(AppStrings.DanceModCandidate(
                            candidate.Name, candidate.DanceCount, candidate.HasDjBlock));

                    StatusText.Text = found.Count == 0
                        ? AppStrings.DanceModNoneFound
                        : AppStrings.DanceModFound(found.Count);
                });
            });
        }

        private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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

        private void RefreshDances()
        {
            if (_group == null) return;

            _dances = DanceMod.ReadDances(_group);
            _pendingOrder = null;

            DanceList.Items.Clear();
            foreach (var dance in _dances)
                DanceList.Items.Add(Describe(dance));

            DancesHeader.Text = AppStrings.DanceCount(_dances.Count);
            UpdateButtons();

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

        private static string Describe(DanceEntry dance)
        {
            string races = dance.Races.Count > 0 ? string.Join(" ", dance.Races) : "?";
            string health = dance.Health == DanceHealth.Ok ? string.Empty : "  ⚠ " + dance.Health;
            return $"{dance.Name}    {races}{health}";
        }

        private void DanceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

        private void UpdateButtons()
        {
            bool selected = DanceList.SelectedIndex >= 0;
            RenameDanceButton.IsEnabled = selected;
            RemoveDanceButton.IsEnabled = selected;
            MoveDanceUpButton.IsEnabled = selected && DanceList.SelectedIndex > 0;
            MoveDanceDownButton.IsEnabled = selected && DanceList.SelectedIndex < DanceList.Items.Count - 1;
            ApplyDanceOrderButton.IsEnabled = _pendingOrder != null;
        }

        // ---- add ---------------------------------------------------------------------------------

        private void AddDanceButton_Click(object sender, RoutedEventArgs e)
        {
            AddSection.Visibility = Visibility.Visible;
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
                    SourceList.Items.Add($"{dance.Label}    {string.Join(" ", dance.Races)}    — {mod.Name}");
                    if (_visibleSources.Count >= 300) return;
                }
            }
        }

        private readonly List<DanceSource> _visibleSources = new();

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
            ShowSources(SearchBox.Text?.Trim() ?? string.Empty);

        private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var source = SelectedSource();
            RacePanel.Children.Clear();
            if (source == null) { RefreshAddPreview(); return; }

            if (string.IsNullOrWhiteSpace(DanceNameBox.Text))
                DanceNameBox.Text = source.Label;

            foreach (string race in source.Races)
            {
                var box = new CheckBox
                {
                    Content = race,
                    Tag = race,
                    // c0101 is the midlander body every DJ pack measured is built on, and the guide
                    // treats it as the default. Anything else is opt-in.
                    IsChecked = race.Equals("c0101", StringComparison.OrdinalIgnoreCase)
                                || source.Races.Count == 1,
                };
                box.Checked += (_, _) => RefreshAddPreview();
                box.Unchecked += (_, _) => RefreshAddPreview();
                RacePanel.Children.Add(box);
            }

            IncludeStartCheckBox.IsEnabled = source.StartByRace.Count > 0;
            IncludeStartCheckBox.IsChecked = source.StartByRace.Count > 0;
            RefreshAddPreview();
        }

        private DanceSource? SelectedSource()
        {
            int at = SourceList.SelectedIndex;
            return at >= 0 && at < _visibleSources.Count ? _visibleSources[at] : null;
        }

        private List<string> SelectedRaces() => RacePanel.Children
            .OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Tag)
            .ToList();

        private void DanceNameBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAddPreview();

        private AddDancePlan? _addPlan;

        private void RefreshAddPreview()
        {
            var source = SelectedSource();
            if (source == null || _group == null || _bundle == null)
            {
                AddPreviewText.Text = string.Empty;
                ConfirmAddDanceButton.IsEnabled = false;
                return;
            }

            _addPlan = DanceModWrites.PlanAdd(_group, _dances, source, SelectedRaces(),
                DanceNameBox.Text?.Trim() ?? string.Empty,
                IncludeStartCheckBox.IsChecked == true, _bundle, _djStrings);

            AddPreviewText.Text = _addPlan.CanApply
                ? AppStrings.AddDancePreview(_addPlan.OldAnimationName, PapFile.DanceAnimationName,
                    _addPlan.SoundPathsRemoved.Count, _addPlan.Tracks, _addPlan.Effects.Count)
                  + (_addPlan.Warnings.Count > 0 ? "\n\n" + string.Join("\n", _addPlan.Warnings) : string.Empty)
                : string.Join("\n", _addPlan.Errors);

            ConfirmAddDanceButton.IsEnabled = _addPlan.CanApply;
        }

        private void ConfirmAddDanceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_group == null || _addPlan == null || _bundle == null || !_addPlan.CanApply) return;

            App.MainWindow.SetProgressBarText(AppStrings.Prog_AddingDance);
            DanceWriteResult result;
            try
            {
                result = DanceModWrites.Add(_group, _addPlan, _bundle, _djStrings);
            }
            finally
            {
                App.MainWindow.ClearProgressDisplay();
            }

            Report(result, AppStrings.AddDanceDone(_addPlan.DanceName));
            if (result.Succeeded)
            {
                AddSection.Visibility = Visibility.Collapsed;
                DanceNameBox.Text = string.Empty;
            }
            RefreshDances();
        }

        private void CancelAddDanceButton_Click(object sender, RoutedEventArgs e) =>
            AddSection.Visibility = Visibility.Collapsed;

        // ---- rename, remove, reorder -------------------------------------------------------------

        private void RenameDanceButton_Click(object sender, RoutedEventArgs e)
        {
            var dance = SelectedDance();
            if (dance == null || _group == null) return;

            // Reuses the add pane's name box rather than opening a prompt, because a second
            // ContentDialog is not possible and a message box cannot take text.
            string typed = DanceNameBox.Text?.Trim() ?? string.Empty;
            if (typed.Length == 0)
            {
                DanceNameBox.Text = dance.Name;
                AddSection.Visibility = Visibility.Visible;
                StatusText.Text = AppStrings.RenameDanceHint(dance.Name);
                return;
            }

            Report(DanceModWrites.Rename(_group, dance, typed), AppStrings.RenameDanceDone(dance.Name, typed));
            DanceNameBox.Text = string.Empty;
            RefreshDances();
        }

        private void RemoveDanceButton_Click(object sender, RoutedEventArgs e)
        {
            var dance = SelectedDance();
            if (dance == null || _group == null) return;

            IntPtr hwnd = OwnerWindow();
            if (MessageBox(hwnd, AppStrings.RemoveDanceConfirm(dance.Name), AppStrings.Dlg_Dances_Title,
                    0x00000001 | 0x00000030) != 1) // MB_OKCANCEL | MB_ICONWARNING, IDOK
                return;

            Report(DanceModWrites.Remove(_group, dance), AppStrings.RemoveDanceDone(dance.Name));
            RefreshDances();
        }

        private void MoveDanceUpButton_Click(object sender, RoutedEventArgs e) => Move(-1);

        private void MoveDanceDownButton_Click(object sender, RoutedEventArgs e) => Move(+1);

        /// <summary>
        /// Moves a dance in the list without writing anything.
        ///
        /// Staged rather than applied per click: reordering rewrites the group's option array and
        /// remaps its default selection, and doing that once per arrow press would be a write per
        /// click on a file Penumbra is watching.
        /// </summary>
        private void Move(int direction)
        {
            int at = DanceList.SelectedIndex;
            int to = at + direction;
            if (at < 0 || to < 0 || to >= DanceList.Items.Count) return;

            _pendingOrder ??= Enumerable.Range(0, _dances.Count).ToList();
            (_pendingOrder[at], _pendingOrder[to]) = (_pendingOrder[to], _pendingOrder[at]);

            object moved = DanceList.Items[at];
            DanceList.Items.RemoveAt(at);
            DanceList.Items.Insert(to, moved);
            DanceList.SelectedIndex = to;

            StatusText.Text = AppStrings.DanceOrderPending;
            UpdateButtons();
        }

        private void ApplyDanceOrderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_group == null || _pendingOrder == null) return;

            Report(DanceModWrites.Reorder(_group, _pendingOrder), AppStrings.DanceOrderDone);
            RefreshDances();
        }

        private DanceEntry? SelectedDance()
        {
            int at = DanceList.SelectedIndex;
            return at >= 0 && at < _dances.Count ? _dances[at] : null;
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

            string text = result.Warnings.Count > 0
                ? success + "\n\n" + string.Join("\n\n", result.Warnings)
                : success;

            MessageBox(hwnd, text, AppStrings.Dlg_Dances_Title,
                result.Warnings.Count > 0 ? 0x00000030u : 0x00000040u); // WARNING : INFORMATION
        }

        private static IntPtr OwnerWindow() =>
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    }
}
