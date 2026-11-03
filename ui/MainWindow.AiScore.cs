using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace Pickles_Playlist_Editor
{
    // The AI-likelihood score: opt-in, the second (AI) pass of Compute Stats, chips and sort.
    public sealed partial class MainWindow
    {
        private AiAssetDownloadService? _aiAssets;
        private readonly CancellationTokenSource _aiDownloadCts = new();
        private readonly SemaphoreSlim _dialogGate = new(1, 1);
        private SortDirection _aiSortDirection = SortDirection.Ascending; // so the first click is Descending
        private static bool s_showAiChips;

        /// <summary>One download service for the app's lifetime; disposed when the window closes.</summary>
        private AiAssetDownloadService AiAssetsService => _aiAssets ??= new AiAssetDownloadService();

        private void DisposeAiScore()
        {
            try { _aiDownloadCts.Cancel(); } catch { }
            _aiAssets?.Dispose();
        }

        /// <summary>
        /// Shows one dialog through the gate, so a summary raised while another gated dialog is open waits its turn.
        /// A collision with an ungated dialog is logged and reads as "closed".
        /// </summary>
        internal async Task<ContentDialogResult> ShowGatedAsync(ContentDialog dialog)
        {
            await _dialogGate.WaitAsync();
            try { return await dialog.ShowAsync(); }
            catch (COMException ex)
            {
                Logger.LogWarn("Dialog '{Title}' not shown: {Error}", dialog.Title as string ?? "", ex.Message);
                return ContentDialogResult.None;
            }
            finally { _dialogGate.Release(); }
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
            int? stored = s_showAiChips ? AiScoreCache.Default.TryGetForRel(node.ScdRel) : null;
            if (AiScoreMath.Band(stored) is not AiBand band)
            {
                node.AiVisibility = Visibility.Collapsed;
                return;
            }
            node.AiText = AppStrings.Get(AiScoreText.BandKey(band));
            node.AiTooltip = AppStrings.AiScoreTooltip((stored!.Value / 1000.0).ToString("0.000"));
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
            try
            {
                if (!await EnsureAiReadyForStatsAsync())
                {
                    await ShowDialogAsync(AppStrings.Summary_AiScore, AppStrings.AiScore_SummarySkipped);
                    return;
                }
                string modRoot = Path.Combine(Settings.PenumbraLocation, Settings.ModName);
                var paths = targets.Select(t => Playlist.GetScdPath(t.option)).Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => Path.Combine(modRoot, p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                SetProgressBarText(AppStrings.ProgAiScoring(0, paths.Count));
                SetProgressBarPercent(0);
                var ct = _aiDownloadCts.Token;

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
                            SetProgressBarText(AppStrings.ProgAiScoring(done, paths.Count));
                            SetProgressBarPercent(done * 100 / paths.Count);
                            if (done % 10 == 0) _uiDispatcherQueue.TryEnqueue(RefreshAiChips);
                        }
                        return (r, (string?)null);
                    }
                    finally { r.Dispose(); }
                });
                SetProgressBarPercent(100);
                RefreshAiChips();
                await ShowDialogAsync(AppStrings.Summary_AiScore, unavailable != null
                    ? AppStrings.AiScoreRuntimeFailed(unavailable)
                    : AppStrings.AiScoreSummary(run.Scored, run.NoScore, run.Failed, run.Cached));
            }
            catch (OperationCanceledException) { } // the window is closing
            catch (Exception ex) { Logger.LogError("AI score pass failed: {Error}", ex); }
        }

        private void SortByAi_Click(object sender, RoutedEventArgs e)
        {
            var direction = _aiSortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
            RunPlaylistReorder(pl => pl.SortByAiLikelihood(direction));
            _aiSortDirection = direction;
        }
    }
}
