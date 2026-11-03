using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    // Song tags (artist, album) for the row tooltip and search: a quiet background read, then cache-only lookups.
    public sealed partial class MainWindow
    {
        private bool _tagScanRunning, _tagScanAgain; // UI thread only
        private CancellationTokenSource? _tagScanCts;

        // Cache-only, like the chips: tree builds never read SCDs.
        private static void ApplySongTooltip(PlaylistNodeContent node, string? modRoot = null)
        {
            if (string.IsNullOrEmpty(node.ScdRel)) return;
            string? tip = SongTooltip.Build(SongTagCache.Default.TryGetForRel(node.ScdRel, modRoot),
                s_showAiChips ? AiScoreCache.Default.TryGetForRel(node.ScdRel) : null, s_showAiChips, AppStrings.Get);
            node.SongTooltip = tip;
            node.SongHelpText = tip ?? "";
        }

        /// <summary>Re-applies every song's tooltip from the caches, in place.</summary>
        private void RefreshSongTooltips()
        {
            string? modRoot = SongTagCache.ModRootOrNull(Settings.PenumbraLocation, Settings.ModName);
            void Walk(IEnumerable<PlaylistNodeContent> nodes)
            {
                foreach (var n in nodes)
                {
                    if (!string.IsNullOrEmpty(n.ScdRel)) ApplySongTooltip(n, modRoot);
                    Walk(n.Children);
                }
            }
            Walk(RootPlaylistItems);
        }

        /// <summary>Reads tags for songs not yet known. One run at a time; a request while running reruns once after.</summary>
        private void StartSongTagScan(bool modChanged = false)
        {
            if (_closingCts.IsCancellationRequested) return;
            if (_tagScanRunning)
            {
                if (modChanged) { try { _tagScanCts?.Cancel(); } catch (ObjectDisposedException) { } }
                _tagScanAgain = true;
                return;
            }
            _tagScanRunning = true;
            _ = RunSongTagScanAsync();
        }

        private async Task RunSongTagScanAsync()
        {
            bool cancelled = false;
            try
            {
                string? modRoot = SongTagCache.ModRootOrNull(Settings.PenumbraLocation, Settings.ModName);
                var paths = modRoot == null ? new List<string>() : Library.SongTargets(Playlists.Values)
                    .Select(t => SongTagCache.FullPathOf(Playlist.GetScdPath(t.option), modRoot)).ToList();
                _tagScanCts = CancellationTokenSource.CreateLinkedTokenSource(_closingCts.Token);
                var ct = _tagScanCts.Token;
                var result = await Task.Run(() =>
                {
                    using (ThreadPriorityScope.Background())
                    {
                        LibraryReadGate.Shared.Enter(ct); // never alongside a stats run or AI scoring
                        try
                        {
                            return SongTagScan.Run(paths, SongTagCache.Default, SongTagReader.Read, ct,
                                n => { if (n % SongTagScan.FlushEvery == 0) _uiDispatcherQueue.TryEnqueue(RefreshSongTooltips); });
                        }
                        finally { LibraryReadGate.Shared.Exit(); }
                    }
                });
                Logger.LogInfo("Song tags: {Read} read ({Tagged} with artist or album), {Unreadable} unreadable, {Known} already known, {Refused} skipped",
                    result.Read, result.Tagged, result.Unreadable, result.AlreadyKnown, result.Refused);
                if (result.Read > 0)
                {
                    if (IsFilterActive) LoadPlaylists(SearchTextBox.Text.Trim());
                    else RefreshSongTooltips();
                }
            }
            catch (OperationCanceledException) { cancelled = true; } // closing, or the mod folder changed
            catch (Exception ex) { Logger.LogError("Song tag scan failed: {Error}", ex); }
            finally
            {
                _tagScanCts?.Dispose();
                _tagScanCts = null;
                _tagScanRunning = false;
                if (_tagScanAgain && !_closingCts.IsCancellationRequested)
                {
                    _tagScanAgain = false;
                    StartSongTagScan();
                }
                if (cancelled) Logger.LogInfo("Song tag scan stopped early");
            }
        }
    }
}
