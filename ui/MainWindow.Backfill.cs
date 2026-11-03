using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    // Backfill tags from folder: source tags for songs imported before #20, from the original files the user still has.
    public sealed partial class MainWindow
    {
        private bool _backfillRunning; // UI thread only
        private (string Text, int Percent)? _backfillProgress;

        private async void BackfillTagsMenuItem_Click(object sender, object e)
        {
            if (_closingCts.IsCancellationRequested) { Logger.LogInfo("Backfill: not started, the app is closing"); return; }
            if (_backfillRunning) { await ShowDialogAsync(AppStrings.Summary_BackfillTags, AppStrings.Backfill_AlreadyRunning); return; }
            var targets = GetSongTargets();
            if (targets.Count == 0) { await ShowDialogAsync(AppStrings.Summary_BackfillTags, AppStrings.Dlg_NoSongs); return; }
            string? modRoot = SongTagCache.ModRootOrNull(Settings.PenumbraLocation, Settings.ModName);
            if (modRoot == null) { await ShowDialogAsync(AppStrings.Summary_BackfillTags, AppStrings.Dlg_NoSongs); return; }

            // Snapshot on the UI thread: the whole library (name clashes are judged library-wide), scoped untagged songs marked.
            var scoped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in targets)
                scoped.Add(AiScoreCache.Key(SongTagCache.FullPathOf(Playlist.GetScdPath(t.option), modRoot)));
            var library = new List<BackfillSong>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int alreadyHad = 0;
            foreach (var (_, opt) in Library.SongTargets(Playlists.Values))
            {
                string rel = Playlist.GetScdPath(opt);
                string full = SongTagCache.FullPathOf(rel, modRoot);
                string key = AiScoreCache.Key(full);
                if (!seen.Add(key)) continue;
                bool inScope = scoped.Contains(key);
                if (inScope && SongTagCache.Default.TryGet(full) is { } t && (t.HasArtistOrAlbum || t.IsAiTagged))
                {
                    alreadyHad++;
                    inScope = false;
                }
                library.Add(new BackfillSong(rel, full, SongTagCache.Default.Generation(full), inScope,
                    BPMDetector.TryGetCachedDuration(Playlist.GetScdPath(opt))));
            }
            if (!library.Exists(s => s.InScope)) { await ShowDialogAsync(AppStrings.Summary_BackfillTags, AppStrings.Backfill_NothingToFill); return; }

            string? folder = await PickFolderAsync(AppStrings.Backfill_PickFolder);
            if (string.IsNullOrWhiteSpace(folder)) return;
            await RunBackfillAsync(library, alreadyHad, folder);
        }

        private async Task RunBackfillAsync(List<BackfillSong> library, int alreadyHad, string folder)
        {
            _backfillRunning = true;
            SetBackfillProgress(AppStrings.Prog_BackfillPreparing, 0);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closingCts.Token);
            var ct = cts.Token;
            BackfillResult result;
            try
            {
                result = await Task.Run(() =>
                {
                    using (ThreadPriorityScope.Background())
                        return SourceTagBackfill.Run(library, alreadyHad, folder, SongTagCache.Default,
                            SourceTagReader.ReadForBackfill, SongTagReader.Read, SourceTagBackfill.ScdDuration, ct,
                            (phase, a, b) => SetBackfillProgress(phase == SourceTagBackfill.PhaseScanning
                                ? AppStrings.ProgBackfillScanning(a) : AppStrings.ProgBackfillReading(a, b), b > 0 ? a * 100 / b : 0),
                            n => { if (n % SourceTagBackfill.FlushEvery == 0) _uiDispatcherQueue.TryEnqueue(RefreshSongTooltips); });
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("Backfill tags failed: {Error}", ex);
                return;
            }
            finally
            {
                SetBackfillProgress("", 0);
                _backfillRunning = false;
            }
            Logger.LogInfo("Backfill: {Outcome}; {Filled} of {Targets} filled ({Ai} AI-tagged), {Already} already had tags, {NoMatch} no match, {Skipped} skipped, {NoTags} no tags, {Cloud} cloud-only",
                result.Outcome, result.Filled, result.Targets, result.AiTagged, result.AlreadyHad, result.NoMatch, result.Skipped, result.NoTags, result.CloudOnly);
            if (result.Filled > 0)
            {
                if (IsFilterActive) LoadPlaylists(SearchTextBox.Text.Trim());
                else RefreshSongTooltips();
            }
            if (!_closingCts.IsCancellationRequested)
                await ShowDialogAsync(AppStrings.Summary_BackfillTags, BackfillText.Summary(result, Settings.AiScoreEnabled, AppStrings.Get));
        }

        private void SetBackfillProgress(string text, int percent)
        {
            if (!_uiDispatcherQueue.HasThreadAccess) { _uiDispatcherQueue.TryEnqueue(() => SetBackfillProgress(text, percent)); return; }
            _backfillProgress = string.IsNullOrEmpty(text) ? null : (text, percent);
            RenderBackgroundProgress();
        }
    }
}
