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
    // The AI-likelihood score: opt-in, the second (AI) pass of Compute Stats, chips and sort.
    public sealed partial class MainWindow
    {
        private AiAssetDownloadService? _aiAssets;
        private readonly CancellationTokenSource _closingCts = new(); // stops an AI scoring pass when the window closes
        private int _aiPhaseRunning;
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
            if (dialog.AiScoreChanged || dialog.AiScoreOptInRequested) RefreshAiScoreUi(reload: true);
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
            var (visible, band, raw) = AiScoreDisplay.For(s_showAiChips ? AiScoreCache.Default.TryGetForRel(node.ScdRel) : null, s_showAiChips);
            if (!visible)
            {
                node.AiVisibility = Visibility.Collapsed;
                return;
            }
            node.AiText = AppStrings.Get(AiScoreText.BandKey(band));
            node.AiTooltip = AppStrings.AiScoreTooltip(raw);
            node.AiAutomationName = node.AiText + ", " + node.AiTooltip;
            node.AiBrush = new SolidColorBrush(band switch
            {
                AiBand.LikelyAi => Color.FromArgb(255, 0xF2, 0xA3, 0x3A),     // amber, not red: an estimate, not an accusation
                AiBand.LikelyHuman => Color.FromArgb(255, 0x8B, 0xC9, 0x8B),
                _ => Color.FromArgb(255, 0xB8, 0xB8, 0xB8),
            });
            node.AiVisibility = Visibility.Visible;
        }

        /// <summary>Re-applies every song's AI chip from the cache, in place: no tree rebuild, selection kept.</summary>
        private void RefreshAiChips()
        {
            void Walk(IEnumerable<PlaylistNodeContent> nodes)
            {
                foreach (var n in nodes)
                {
                    if (!string.IsNullOrEmpty(n.ScdRel)) ApplyAiChip(n);
                    Walk(n.Children);
                }
            }
            Walk(RootPlaylistItems);
        }

        /// <summary>Before phase 2: are the files there? If not, offer to download them again.</summary>
        private async Task<bool> EnsureAiReadyForStatsAsync()
        {
            var svc = AiAssetsService;
            if (await Task.Run(() => { using (ThreadPriorityScope.BelowNormal()) return svc.IsReady(); })) return true;
            var outcome = await AiScoreOptIn.RunAsync(OptInUi(), svc, AiScoreOptIn.HasFreeSpace, on => { if (on) Settings.AiScoreEnabled = true; }, redownload: true);
            return outcome is AiOptInOutcome.Enabled or AiOptInOutcome.AlreadyReady;
        }

        /// <summary>
        /// Compute Stats, phase 2: scores the songs after phase 1 has saved its results. Writes only the AI cache,
        /// never playlist JSON, so closing the app mid-way loses nothing.
        /// </summary>
        private async Task RunAiScorePhaseAsync(List<(Playlist playlist, Option option)> targets)
        {
            if (Interlocked.Exchange(ref _aiPhaseRunning, 1) == 1) // one AI pass at a time
            {
                await ShowDialogAsync(AppStrings.Summary_AiScore, AppStrings.Get("AiScore_AlreadyRunning"));
                return;
            }
            try
            {
                var cache = AiScoreCache.Default;
                var all = targets.Select(t => Playlist.GetScdPath(t.option)).Where(p => !string.IsNullOrEmpty(p))
                    .Select(AiScoreCache.FullPathOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var paths = all.Where(p => cache.TryGet(p) is null).ToList();
                int alreadyCached = all.Count - paths.Count;
                if (paths.Count == 0)
                {
                    // Nothing to score: no model load and no download prompt.
                    await ShowDialogAsync(AppStrings.Summary_AiScore, AppStrings.AiScoreSummary(0, 0, 0, alreadyCached));
                    return;
                }
                if (!await EnsureAiReadyForStatsAsync())
                {
                    await ShowDialogAsync(AppStrings.Summary_AiScore, AppStrings.AiScore_SummarySkipped);
                    return;
                }
                SetBackgroundProgress(AppStrings.ProgAiScoring(0, paths.Count), 0);
                var ct = _closingCts.Token;

                var (run, unavailable) = await Task.Run(() =>
                {
                    var r = AiScoreRun.Begin(AiScoreCache.Default);
                    try
                    {
                        if (r.UnavailableReason != null) return (r, r.UnavailableReason);
                        for (int i = 0; i < paths.Count && !ct.IsCancellationRequested; i++)
                        {
                            r.ScoreSong(paths[i], ct);
                            int done = i + 1;
                            SetBackgroundProgress(AppStrings.ProgAiScoring(done, paths.Count), done * 100 / paths.Count);
                            if (done % 10 == 0) _uiDispatcherQueue.TryEnqueue(RefreshAiChips);
                        }
                        return (r, (string?)null);
                    }
                    finally { r.Dispose(); }
                });
                ClearBackgroundProgress();
                RefreshAiChips();
                await ShowDialogAsync(AppStrings.Summary_AiScore, unavailable != null
                    ? AppStrings.AiScoreRuntimeFailed(unavailable)
                    : AppStrings.AiScoreSummary(run.Scored, run.NoScore, run.Failed, run.Cached + alreadyCached));
            }
            catch (OperationCanceledException) { } // the window is closing
            catch (Exception ex) { Logger.LogError("AI score pass failed: {Error}", ex); }
            finally
            {
                ClearBackgroundProgress();
                Interlocked.Exchange(ref _aiPhaseRunning, 0);
            }
        }

        // Phase 2 reports in the status bar only: unlike SetProgressBarText it puts up no overlay, so the
        // window stays usable during a long scoring pass.
        private void SetBackgroundProgress(string text, int percent)
        {
            if (!_uiDispatcherQueue.HasThreadAccess) { _uiDispatcherQueue.TryEnqueue(() => SetBackgroundProgress(text, percent)); return; }
            ProgressLabel.Text = text;
            ProgressBar1.Value = Math.Clamp(percent, 0, 100);
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
