using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace Pickles_Playlist_Editor
{
    // The AI-likelihood score: opt-in, the library scan (on turn-on and after imports), the AI pass of Compute Stats, chips and sort.
    public sealed partial class MainWindow
    {
        private AiAssetDownloadService? _aiAssets;
        private readonly CancellationTokenSource _closingCts = new(); // stops an AI scoring pass when the window closes
        private readonly AiPassGate _aiGate; // built in the constructor: it links to _closingCts
        private bool _aiScanWanted;
        private string? _aiModKey;
        private SortDirection _aiSortDirection = SortDirection.Ascending; // so the first click is Descending
        private static bool s_showAiChips;

        /// <summary>One download service for the app's lifetime; disposed when the window closes.</summary>
        private AiAssetDownloadService AiAssetsService => _aiAssets ??= new AiAssetDownloadService();

        private void DisposeAiScore()
        {
            try { _closingCts.Cancel(); } catch { }
            _aiAssets?.Dispose();
        }

        private ContentDialogOptInUi OptInUi() => new(() => this.Content.XamlRoot, ShowGatedAsync);

        /// <summary>After the Settings dialog has closed: run the opt-in if it was ticked, then show or hide the AI UI.</summary>
        internal async Task ApplyAiScoreSettingAsync(SettingsDialog dialog)
        {
            if (dialog.AiScoreOptInRequested)
            {
                try
                {
                    await AiScoreOptIn.RunAsync(OptInUi(), AiAssetsService, AiScoreOptIn.HasFreeSpace, on => Settings.AiScoreEnabled = on, redownload: false);
                }
                catch (Exception ex) { Logger.LogError("AI score opt-in failed: {Error}", ex); }
            }
            if (dialog.AiScoreChanged && !Settings.AiScoreEnabled) { _aiGate.TurnOff(); _aiScanWanted = false; }
            if (dialog.AiScoreChanged || dialog.AiScoreOptInRequested) RefreshAiScoreUi(reload: true);
            if (dialog.AiScoreOptInRequested && Settings.AiScoreEnabled) _aiScanWanted = true; // started after the reload
        }

        /// <summary>
        /// After Settings has closed and the library has been reloaded: read new songs' tags; scan for AI if it was just
        /// turned on, or rescan if the mod folder changed while AI is on.
        /// </summary>
        internal void ContinueAiScoreAfterSettings(bool libraryFresh)
        {
            string key = AiScoreCache.Key(Utils.PenumbraMeta.ModRoot);
            bool modChanged = _aiModKey != null && key != _aiModKey;
            _aiModKey = key;
            // Reloaded first, whatever the AI setting, so both scans snapshot the new mod. A running AI pass keeps
            // its own snapshot, and RefreshAiChips walks the rebuilt tree.
            if (modChanged && !libraryFresh) LoadPlaylists();
            StartSongTagScan(modChanged);
            if (modChanged) _statsGate.CancelRunning();
            MaybeStartAutoStats();
            if (!Settings.AiScoreEnabled) { _aiScanWanted = false; return; }
            if (modChanged) _aiGate.CancelRunning();
            if (_aiScanWanted || modChanged)
            {
                _aiScanWanted = false;
                StartLibraryAiScan();
            }
        }

        private void StartLibraryAiScan() => _ = RunAiScorePhaseAsync(Library.SongTargets(Playlists.Values), AiPass.Library);

        /// <summary>After an import: score the new songs in the background; scored songs are skipped. Never throws.</summary>
        internal void RequestAiScanAfterImport()
        {
            try
            {
                if (!Settings.AiScoreEnabled || _closingCts.IsCancellationRequested) return;
                Logger.LogInfo("AI scan requested after an import");
                StartLibraryAiScan();
            }
            catch (Exception ex) { Logger.LogError("AI scan after an import could not start: {Error}", ex); }
        }

        /// <summary>Shows or hides the AI sort item and chips to match the setting.</summary>
        internal void RefreshAiScoreUi(bool reload)
        {
            bool on = Settings.AiScoreEnabled;
            SortByAiMenuItem.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            bool changed = on != s_showAiChips;
            s_showAiChips = on;
            if (reload && changed) RefreshAiChips();
        }

        // Cache-only, like the Camelot chip: tree builds never score.
        private static void ApplyAiChip(PlaylistNodeContent node)
        {
            var (visible, band, percent) = AiScoreDisplay.For(s_showAiChips ? AiScoreCache.Default.TryGetForRel(node.ScdRel) : null, s_showAiChips);
            if (!visible)
            {
                node.AiVisibility = Visibility.Collapsed;
                return;
            }
            node.AiText = AppStrings.Get(AiScoreText.BandKey(band));
            node.AiTooltip = AppStrings.AiScoreTooltip(percent);
            node.AiAutomationName = node.AiText + ", " + node.AiTooltip;
            // amber, not red: an estimate, not an accusation; paler for Possibly AI
            node.AiBrush = new SolidColorBrush(band == AiBand.LikelyAi ? Color.FromArgb(255, 0xF2, 0xA3, 0x3A) : Color.FromArgb(255, 0xF5, 0xD0, 0x8A));
            node.AiVisibility = Visibility.Visible;
        }

        /// <summary>Re-applies every song's AI chip from the cache, in place: no tree rebuild, selection kept.</summary>
        private void RefreshAiChips()
        {
            void Walk(IEnumerable<PlaylistNodeContent> nodes)
            {
                foreach (var n in nodes)
                {
                    if (!string.IsNullOrEmpty(n.ScdRel)) { ApplyAiChip(n); ApplySongTooltip(n); }
                    Walk(n.Children);
                }
            }
            Walk(RootPlaylistItems);
        }

        /// <summary>Are the files there? Checked off the UI thread; never prompts.</summary>
        private Task<bool> IsAiReadyAsync()
        {
            var svc = AiAssetsService;
            return Task.Run(() => { using (ThreadPriorityScope.BelowNormal()) return svc.IsReady(); });
        }

        /// <summary>Before phase 2: are the files there? If not, offer to download them again.</summary>
        private async Task<bool> EnsureAiReadyForStatsAsync()
        {
            if (await IsAiReadyAsync()) return true;
            var svc = AiAssetsService;
            var outcome = await AiScoreOptIn.RunAsync(OptInUi(), svc, AiScoreOptIn.HasFreeSpace, on => { if (on) Settings.AiScoreEnabled = true; }, redownload: true);
            return outcome is AiOptInOutcome.Enabled or AiOptInOutcome.AlreadyReady;
        }

        /// <summary>
        /// One AI pass: Compute Stats phase 2, or the background library scan. Writes only the AI cache, never
        /// playlist JSON, so closing the app mid-way loses nothing. A library scan shows no summary.
        /// </summary>
        private async Task RunAiScorePhaseAsync(List<(Playlist playlist, Option option)> targets, AiPass pass = AiPass.ComputeStats)
        {
            switch (_aiGate.TryBegin(pass, out var ticket)) // one AI pass at a time; a busy gate queues a library scan
            {
                case AiPassStart.Queued: return;
                case AiPassStart.AlreadyRunning:
                    await ShowDialogAsync(AppStrings.Summary_AiScore, AppStrings.Get("AiScore_AlreadyRunning"));
                    return;
            }
            var ct = ticket.Token;
            AiScoreRun? run = null;
            int alreadyCached = 0, toScore = 0;
            AiPassEnd end = AiPassEnd.Error;
            try
            {
                var cache = AiScoreCache.Default;
                var rels = targets.Select(t => Playlist.GetScdPath(t.option)).Where(p => !string.IsNullOrEmpty(p)).ToList();
                var (paths, cached) = await Task.Run(() =>
                {
                    using (ThreadPriorityScope.BelowNormal())
                    {
                        var all = rels.Select(AiScoreCache.FullPathOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var todo = all.Where(p => cache.TryGet(p) is null).ToList();
                        return (todo, all.Count - todo.Count);
                    }
                });
                alreadyCached = cached;
                toScore = paths.Count;
                if (ct.IsCancellationRequested) { end = AiPassEnd.Cancelled; return; }
                if (paths.Count == 0)
                {
                    // Nothing to score: no model load and no download prompt.
                    end = AiPassEnd.NothingToScore;
                    if (AiPassRules.ShowsDialog(pass, end))
                        await ShowDialogAsync(AppStrings.Summary_AiScore, AppStrings.AiScoreSummary(0, 0, 0, alreadyCached));
                    return;
                }
                if (!await (pass == AiPass.Library ? IsAiReadyAsync() : EnsureAiReadyForStatsAsync()))
                {
                    end = AiPassEnd.NotReady;
                    if (AiPassRules.ShowsDialog(pass, end))
                        await ShowDialogAsync(AppStrings.Summary_AiScore, AppStrings.AiScore_SummarySkipped);
                    return;
                }
                if (ct.IsCancellationRequested) { end = AiPassEnd.Cancelled; return; }
                SetBackgroundProgress(AppStrings.ProgAiScoring(0, paths.Count), 0);

                string? unavailable = await Task.Run(() =>
                {
                    var r = run = AiScoreRun.Begin(AiScoreCache.Default);
                    try
                    {
                        if (r.UnavailableReason != null) return r.UnavailableReason;
                        for (int i = 0; i < paths.Count && !ct.IsCancellationRequested; i++)
                        {
                            LibraryReadGate.Shared.Enter(ct); // one background reader of the library at a time
                            try { r.ScoreSong(paths[i], ct); }
                            finally { LibraryReadGate.Shared.Exit(); }
                            int done = i + 1;
                            SetBackgroundProgress(AppStrings.ProgAiScoring(done, paths.Count), done * 100 / paths.Count);
                            if (done % 10 == 0) _uiDispatcherQueue.TryEnqueue(RefreshAiChips);
                        }
                        return (string?)null;
                    }
                    finally { r.Dispose(); }
                });
                ClearBackgroundProgress();
                RefreshAiChips();
                end = ct.IsCancellationRequested ? AiPassEnd.Cancelled : unavailable != null ? AiPassEnd.RuntimeFailed : AiPassEnd.Finished;
                if (AiPassRules.ShowsDialog(pass, end))
                    await ShowDialogAsync(AppStrings.Summary_AiScore, unavailable != null
                        ? AppStrings.AiScoreRuntimeFailed(unavailable)
                        : AppStrings.AiScoreSummary(run!.Scored, run.NoScore, run.Failed, run.Cached + alreadyCached));
            }
            catch (OperationCanceledException) { end = AiPassEnd.Cancelled; } // closing, or the score was turned off
            catch (Exception ex) { end = AiPassEnd.Error; Logger.LogError("AI score pass failed: {Error}", ex); }
            finally
            {
                ClearBackgroundProgress();
                Logger.LogInfo("AI score pass {Pass} ended {End}: {Scored} scored, {NoScore} no score, {Failed} failed, {Cached} already scored, {ToScore} to score",
                    pass, end, run?.Scored ?? 0, run?.NoScore ?? 0, run?.Failed ?? 0, (run?.Cached ?? 0) + alreadyCached, toScore);
                if (_aiGate.End(ticket, Settings.AiScoreEnabled)) StartLibraryAiScan();
            }
        }

        // Phase 2 reports in the status bar only: unlike SetProgressBarText it puts up no overlay, so the
        // window stays usable during a long scoring pass.
        private void SetBackgroundProgress(string text, int percent)
        {
            if (!_uiDispatcherQueue.HasThreadAccess) { _uiDispatcherQueue.TryEnqueue(() => SetBackgroundProgress(text, percent)); return; }
            _aiProgress = string.IsNullOrEmpty(text) ? null : (text, percent); // shown unless a stats run is
            RenderBackgroundProgress();
        }

        private void ClearBackgroundProgress() => SetBackgroundProgress("", 0);

        private void SortByAi_Click(object sender, RoutedEventArgs e)
        {
            var direction = _aiSortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
            RunPlaylistReorder(pl => pl.SortByAiLikelihood(direction));
            _aiSortDirection = direction;
        }
    }
}
