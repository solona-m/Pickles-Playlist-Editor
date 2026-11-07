using Microsoft.UI.Dispatching;
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

namespace Pickles_Playlist_Editor
{
    // Compute Stats in the background: analysis one song at a time while the window stays usable, then song names
    // applied to the live playlists only while nothing else is going on. Also the quiet once-per-version run.
    public sealed partial class MainWindow
    {
        private readonly StatsPassGate _statsGate; // built in the constructor: it links to _closingCts
        private bool _autoStatsAgain, _namingRefreshInFlight, _restoreInFlight;
        private int _nativeModalOpen;
        private string? _autoAttemptBefore; // set while an automatic run's attempt is recorded and not yet settled
        private (string Text, int Percent)? _statsProgress, _aiProgress;
        private (string Text, int Percent)? _statusNotice;
        private DispatcherQueueTimer? _statusNoticeTimer;
        // First-load playlists and songs whose names got stats in memory only (BackfillStatsFromCacheIfNeeded).
        private readonly HashSet<Playlist> _backfilledPlaylists = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Option> _backfilledOptions = new(ReferenceEqualityComparer.Instance);

        /// <summary>Starts a stats run unless one is already going; false when it is.</summary>
        private bool StartStatsRun(List<(Playlist playlist, Option option)> targets, StatsPassKind kind)
        {
            if (!_statsGate.TryBegin(kind, out var ticket)) return false;
            _ = RunStatsPassAsync(ticket, targets);
            return true;
        }

        /// <summary>Once per app version (and on the very first launch with a mod set): a quiet run over the library. Never throws.</summary>
        internal void MaybeStartAutoStats()
        {
            try
            {
                if (_closingCts.IsCancellationRequested) return;
                Utils.VersionBackup.TryCaptureOnBoot(); // a backup exists before the first write
                string done = Settings.AutoStatsVersion, attempt = Settings.AutoStatsAttempt;
                if (!AutoStatsRules.ShouldRun(done, attempt, AppVersion.Display, modReadable: true)) return; // cheap check first
                bool modReadable = SongTagCache.ModRootOrNull(Settings.PenumbraLocation, Settings.ModName) != null
                    && PenumbraMeta.DetectFormat() != ModFormat.Unknown;
                if (!AutoStatsRules.ShouldRun(done, attempt, AppVersion.Display, modReadable)) return;
                if (_statsGate.IsRunning) { _autoStatsAgain = true; return; }
                if (StartStatsRun(Library.SongTargets(Playlists.Values), StatsPassKind.Auto))
                    Logger.LogInfo("Automatic stats run started for {Version}", AppVersion.Display);
            }
            catch (Exception ex) { Logger.LogError("Automatic stats run could not start: {Error}", ex); }
        }

        private async Task RunStatsPassAsync(StatsPassTicket ticket, List<(Playlist playlist, Option option)> targets)
        {
            var ct = ticket.Token;
            string modKey = ModKey();
            // Recorded up front, so a crash mid-run counts toward the cap; taken back on a clean finish, a cancel or an exit.
            if (ticket.Kind == StatsPassKind.Auto)
            {
                _autoAttemptBefore = Settings.AutoStatsAttempt;
                Settings.AutoStatsAttempt = AutoStatsRules.NextAttempt(_autoAttemptBefore, AppVersion.Display);
            }
            var end = StatsPassEnd.Error;
            int analysed = 0, failedRels = 0, renamed = 0, saved = 0, entryCount = 0, timedOut = 0;
            bool stoppedEarly = false;
            var failedSongs = new List<string>();
            var saveErrors = new List<string>();
            try
            {
                SetStatsProgress(AppStrings.Prog_ComputingStats, 0);
                var entries = StatsPlan.Snapshot(targets, Playlist.GetScdPath, o => _backfilledOptions.Contains(o));
                entryCount = entries.Count;
                var plan = await Task.Run(() => { using (ThreadPriorityScope.Background()) return StatsPlan.Build(entries, HasAllStats); }, ct);
                var names = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in entries) names.TryAdd(e.Rel, $"{e.PlaylistName}/{e.Name}");
                Logger.LogInfo("Stats pass {Kind} started: {ToDetect} song(s) to analyse of {Entries}", ticket.Kind, plan.ToDetect.Count, entryCount);
                var failed = await StatsDetectLoop.RunAsync(plan.ToDetect, DetectOne, () => !IsForegroundBusy(), WaitUntilIdleAsync,
                    LibraryReadGate.Shared, ct,
                    (done, total) => SetStatsProgress(AppStrings.ProgComputingStats(done, total), done * 100 / Math.Max(1, total)),
                    StatsDetectLimits.Default,
                    new StatsDetectHooks(
                        Note: (rel, elapsed, kind) => { if (kind == StatsSongNote.TimedOut) timedOut++; LogSongNote(names, rel, elapsed, kind); },
                        Stopped: s =>
                        {
                            stoppedEarly = true;
                            Logger.LogWarn("Stats: {TimedOut} song(s) timed out; analysis stopped, {Skipped} song(s) not analysed this run", s.TimedOut, s.Skipped);
                        },
                        Waiting: (i, elapsed) => Logger.LogWarn("Stats: waiting {Seconds} s to start song {Index}/{Total}: {Reason}",
                            (int)elapsed.TotalSeconds, i + 1, plan.ToDetect.Count,
                            StatsWaitReason.Describe(_busyOverlayVisible, OpenPopups().Any(p => p.Child is ContentDialog), _dialogGate.CurrentCount == 0,
                                !LibraryReadGate.Shared.IsFree, LibraryReadGate.Shared.Holder))));
                failedRels = failed.Count;
                analysed = plan.ToDetect.Count - failedRels;
                var failedSet = new HashSet<string>(failed, StringComparer.Ordinal);
                failedSongs.AddRange(entries.Where(e => failedSet.Contains(e.Rel)).Select(e => $"{e.PlaylistName}/{e.Name}"));

                (renamed, saved) = await ApplyStatsNamesAsync(plan, modKey,
                    ticket.Kind == StatsPassKind.Manual ? StatsRenameMode.Rebuild : StatsRenameMode.Additive, saveErrors, ct,
                    ticket.Kind == StatsPassKind.Manual ? targets.Select(t => t.playlist.Name ?? "").ToHashSet() : null);
                end = StatsPassEnd.Finished;
                if (ticket.Kind == StatsPassKind.Auto && AutoStatsRules.ShouldWriteMarker(end, saveErrors.Count, entryCount, Playlists.Count, stoppedEarly))
                    Settings.AutoStatsVersion = AppVersion.Display;
            }
            catch (OperationCanceledException) { end = StatsPassEnd.Cancelled; } // closing, or the mod folder changed
            catch (Exception ex) { end = StatsPassEnd.Error; Logger.LogError("Stats pass failed: {Error}", ex); }
            finally
            {
                _statsGate.End(ticket); // first, so nothing below can leave the gate held
                try { ClearStatsProgress(); } catch (Exception ex) { Logger.LogWarn("Stats: status bar not cleared: {Error}", ex.Message); }
                if (ticket.Kind == StatsPassKind.Auto) SettleAutoAttempt(AutoStatsRules.KeepsAttempt(end, saveErrors.Count, stoppedEarly));
                Logger.LogInfo("Stats pass {Kind} ended {End}: {Analysed} analysed, {Failed} failed ({TimedOut} timed out), {Renamed} renamed, {Saved} playlist(s) saved, {SaveFailed} save failure(s)",
                    ticket.Kind, end, analysed, failedRels, timedOut, renamed, saved, saveErrors.Count);
                if (_autoStatsAgain && !_closingCts.IsCancellationRequested)
                {
                    _autoStatsAgain = false;
                    MaybeStartAutoStats();
                }
            }
            if (end != StatsPassEnd.Finished) return;

            if (ticket.Kind == StatsPassKind.Manual)
            {
                var errors = failedSongs.Select(AppStrings.StatsAnalyseFailed).Concat(saveErrors).ToList();
                ShowOperationSummary(AppStrings.Summary_ComputeStats, targets.Count - failedSongs.Count, targets.Count, errors);

                // Phase 2, after phase 1's names are saved: AI scoring, in its own background pass.
                if (Settings.AiScoreEnabled) await RunAiScorePhaseAsync(targets);
            }
            else if (Settings.AiScoreEnabled && AiScoreCache.Default.HasStore)
                StartLibraryAiScan(); // quiet; never a full rescore right after a model change
        }

        private static string ModKey() => AiScoreCache.Key(PenumbraMeta.ModRoot);

        private static void CheckMod(string modKey)
        {
            if (ModKey() != modKey) throw new OperationCanceledException("mod folder changed");
        }

        private static CachedStats CachedOf(string rel) =>
            new(BPMDetector.TryGetCachedBpm(rel), KeyDetector.TryGetCachedKey(rel), BPMDetector.TryGetCachedDuration(rel));

        private static bool HasAllStats(string rel) =>
            BPMDetector.TryGetCachedBpm(rel).HasValue && KeyDetector.TryGetCachedKey(rel) != null && BPMDetector.TryGetCachedDuration(rel).HasValue;

        // Which detector a song is in, for the slow/timeout log; set by the worker, so the size read stays off the UI thread.
        private sealed record DetectStep(string Rel, string Step, string SizeKb);
        private static volatile DetectStep? s_detectStep;

        // The cache-first detectors with the exact rel; true when all three stats are now cached.
        private static bool DetectOne(string rel)
        {
            string size = SizeKb(rel);
            try
            {
                s_detectStep = new DetectStep(rel, "BPM", size);
                BPMDetector.GetBPMFromSCD(rel);
                s_detectStep = new DetectStep(rel, "Key", size);
                KeyDetector.GetKeyFromSCD(rel);
                s_detectStep = new DetectStep(rel, "Duration", size);
                BPMDetector.GetDuration(rel);
                return HasAllStats(rel);
            }
            finally { if (s_detectStep?.Rel == rel) s_detectStep = null; } // a late, abandoned worker leaves the next song's step alone
        }

        private static string SizeKb(string rel)
        {
            try { return (new FileInfo(Path.Combine(Settings.PenumbraLocation, Settings.ModName, rel)).Length / 1024).ToString(); }
            catch { return "?"; }
        }

        // Only the mod-relative rel is logged: the full path can hold the Windows user name.
        private void LogSongNote(Dictionary<string, string> names, string rel, TimeSpan elapsed, StatsSongNote kind)
        {
            string song = names.TryGetValue(rel, out var n) ? n : rel;
            var step = s_detectStep;
            string stepName = step?.Rel == rel ? step.Step : "?", size = step?.Rel == rel ? step.SizeKb : "?";
            int seconds = (int)elapsed.TotalSeconds;
            switch (kind)
            {
                case StatsSongNote.StillRunning:
                    Logger.LogWarn("Stats: still analysing {Song} ({Rel}, {SizeKb} KB) at step {Step} after {Seconds} s", song, rel, size, stepName, seconds);
                    break;
                case StatsSongNote.FinishedSlow:
                    Logger.LogInfo("Stats: {Song} ({Rel}) took {Seconds} s", song, rel, seconds);
                    break;
                case StatsSongNote.TimedOut:
                    Logger.LogWarn("Stats: {Song} ({Rel}, {SizeKb} KB) timed out at step {Step} after {Seconds} s; skipped", song, rel, size, stepName, seconds);
                    break;
            }
        }

        /// <summary>
        /// Applies names to the current playlists, one playlist per dispatcher tick, only while the window is quiet.
        /// The last checks and the save run with nothing awaited in between.
        /// </summary>
        private async Task<(int Renamed, int Saved)> ApplyStatsNamesAsync(StatsPlan plan, string modKey, StatsRenameMode mode,
            List<string> saveErrors, CancellationToken ct, IReadOnlyCollection<string>? onlyPlaylists = null)
        {
            SetStatsProgress(AppStrings.Prog_StatsWaitingToApply, 100);
            int renamed = 0, saved = 0;
            var parts = OptionStatsNaming.Parts.FromSettings();
            Dictionary<StatsEntry, string> expected;

            async Task<Dictionary<StatsEntry, string>> Compute()
            {
                await WaitUntilQuietForApplyAsync(ct);
                CheckMod(modKey);
                parts = OptionStatsNaming.Parts.FromSettings();
                var p = parts;
                var result = await Task.Run(() => { using (ThreadPriorityScope.Background()) return StatsRename.ExpectedNames(plan.Entries, CachedOf, p, mode); }, ct);
                CheckMod(modKey);
                return result;
            }

            do expected = await Compute(); while (OptionStatsNaming.Parts.FromSettings() != parts);
            var snapshots = new SnapshotBatch(); // one manifest snapshot for the whole step, not one per playlist

            // A manual run on a selection renames only in the playlists it was started on.
            foreach (string name in Playlists.Keys.Where(n => onlyPlaylists == null || onlyPlaylists.Contains(n)).ToList())
            {
                while (true)
                {
                    await NextTickAsync();
                    ct.ThrowIfCancellationRequested();
                    await WaitUntilQuietForApplyAsync(ct);
                    if (OptionStatsNaming.Parts.FromSettings() != parts) { expected = await Compute(); continue; }

                    // From here to the save nothing is awaited, so nothing can change underneath.
                    if (!IsQuietForApply()) continue;
                    ct.ThrowIfCancellationRequested(); // closing or an exit: nothing more is written
                    CheckMod(modKey);
                    if (OptionStatsNaming.Parts.FromSettings() != parts) continue;
                    if (!Playlists.TryGetValue(name, out var pl)) break; // renamed meanwhile: skipped
                    var (changes, dirty, _) = StatsRename.ApplyOne(pl, expected, Playlist.GetScdPath, _backfilledPlaylists.Contains(pl),
                        requireSamePlaylist: onlyPlaylists != null);
                    if (dirty)
                    {
                        bool ok = false;
                        try
                        {
                            using (snapshots.Enter()) pl.Save();
                            _backfilledPlaylists.Remove(pl);
                            ok = true;
                        }
                        catch (Exception ex)
                        {
                            StatsRename.Revert(changes);
                            saveErrors.Add($"{pl.Name}: {ex.Message}");
                            Logger.LogWarn("Stats: playlist '{Name}' could not be saved: {Error}", pl.Name, ex.Message);
                        }
                        if (ok)
                        {
                            saved++;
                            renamed += changes.Count;
                            try { UpdateStatsNodes(pl, changes); }
                            catch (Exception ex) { Logger.LogWarn("Stats: tree update failed for '{Name}': {Error}", pl.Name, ex.Message); }
                        }
                    }
                    break;
                }
            }

            if (IsFilterActive) LoadPlaylists(SearchTextBox.Text.Trim());
            else if (plan.ToDetect.Count > 0) RefreshStatsDisplay(plan.ToDetect);
            return (renamed, saved);
        }

        // A dispatcher that won't take work is shutting down: stop rather than run the rest synchronously.
        private Task NextTickAsync()
        {
            var tcs = new TaskCompletionSource();
            if (!_uiDispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => tcs.TrySetResult()))
                tcs.TrySetCanceled();
            return tcs.Task;
        }

        // Renamed songs, in place: no tree rebuild, selection and expansion kept.
        private void UpdateStatsNodes(Playlist pl, List<StatsRenameChange> changes)
        {
            foreach (var c in changes)
                if (_nowPlayingPlaylistName == pl.Name && _nowPlayingSongName == c.OldName) _nowPlayingSongName = c.NewName;
            if (IsFilterActive || changes.Count == 0) return;
            var node = FindPlaylistNode(pl.Name);
            if (node == null) { SyncPlaylistNode(pl); return; }
            foreach (var c in changes)
            {
                string key = StatsPlan.RelKeyOf(Playlist.GetScdPath(c.Option));
                var song = node.Children.FirstOrDefault(n => n.Name == c.OldName && StatsPlan.RelKeyOf(n.ScdRel) == key);
                if (song == null) { SyncPlaylistNode(pl); return; }
                song.Name = c.NewName;
                song.DisplayText = c.NewName;
                ApplyCamelotChip(song);
            }
        }

        // New lengths and keys may change playlist totals and chips even where no name changed; only where analysed.
        private void RefreshStatsDisplay(IReadOnlyList<string> detectedRels)
        {
            if (RootPlaylistItems.Count == 0) return;
            var detected = new HashSet<string>(detectedRels.Select(StatsPlan.RelKeyOf));
            foreach (var plNode in RootPlaylistItems[0].Children)
            {
                if (!Playlists.TryGetValue(plNode.Name, out var pl)) continue;
                var songs = plNode.Children.Where(n => !string.IsNullOrEmpty(n.ScdRel) && detected.Contains(StatsPlan.RelKeyOf(n.ScdRel))).ToList();
                if (songs.Count == 0) continue;
                SetNodeDisplayText(plNode, (pl.Name ?? "") + GetTimeString(SumPlaylistDuration(pl)));
                foreach (var song in songs) ApplyCamelotChip(song);
            }
        }

        // ── Quiet detection: app-owned signals, never just one ──

        private IReadOnlyList<Microsoft.UI.Xaml.Controls.Primitives.Popup> OpenPopups()
        {
            try { return this.Content?.XamlRoot is { } root ? VisualTreeHelper.GetOpenPopupsForXamlRoot(root) : []; }
            catch { return []; }
        }

        private bool IsDialogOpen() => OpenPopups().Any(p => p.Child is ContentDialog) || _dialogGate.CurrentCount == 0;

        private bool IsMenuOpen() => _treeContextMenu.IsOpen || _rootContextMenu.IsOpen || AddSongsFlyout.IsOpen || SortFlyout.IsOpen;

        /// <summary>Detection pauses while a busy operation or a dialog is up: those move, rewrite and delete SCDs.</summary>
        private bool IsForegroundBusy() => _busyOverlayVisible || IsDialogOpen();

        /// <summary>Names are only applied when nothing else could be editing the playlists.</summary>
        private bool IsQuietForApply() =>
            !_busyOverlayVisible && !_dragInProgress && !_namingRefreshInFlight && !_restoreInFlight && !IsDialogOpen() && !IsMenuOpen()
            && _nativeModalOpen == 0 && OpenPopups().Count == 0;

        private Task WaitUntilIdleAsync(CancellationToken ct) => IsForegroundBusy() ? PollAsync(IsForegroundBusy, ct) : Task.CompletedTask;

        private Task WaitUntilQuietForApplyAsync(CancellationToken ct) => IsQuietForApply() ? Task.CompletedTask : PollAsync(() => !IsQuietForApply(), ct);

        private static async Task PollAsync(Func<bool> busy, CancellationToken ct)
        {
            while (busy()) await Task.Delay(1000, ct);
        }

        // ── Status bar: background slots, backfill over stats over AI ──

        private void SetStatsProgress(string text, int percent)
        {
            if (!_uiDispatcherQueue.HasThreadAccess) { _uiDispatcherQueue.TryEnqueue(() => SetStatsProgress(text, percent)); return; }
            _statsProgress = string.IsNullOrEmpty(text) ? null : (text, percent);
            RenderBackgroundProgress();
        }

        private void ClearStatsProgress() => SetStatsProgress("", 0);

        // A short, non-blocking status line; one reused timer, so a newer notice isn't cleared early.
        private void ShowStatusNotice(string text)
        {
            _statusNotice = (text, 0);
            RenderBackgroundProgress();
            if (_statusNoticeTimer == null)
            {
                _statusNoticeTimer = _uiDispatcherQueue.CreateTimer();
                _statusNoticeTimer.Interval = TimeSpan.FromSeconds(6);
                _statusNoticeTimer.IsRepeating = false;
                _statusNoticeTimer.Tick += (_, _) => { _statusNotice = null; RenderBackgroundProgress(); };
            }
            _statusNoticeTimer.Stop();
            _statusNoticeTimer.Start();
        }

        // A foreground operation owns the bar while its overlay is up; ClearProgressDisplay renders again after it.
        private void RenderBackgroundProgress()
        {
            if (!_uiDispatcherQueue.HasThreadAccess) { _uiDispatcherQueue.TryEnqueue(RenderBackgroundProgress); return; }
            if (_busyOverlayVisible) return;
            var show = BackgroundStatus.Pick(_statusNotice, BackgroundStatus.Pick(_backfillProgress, BackgroundStatus.Pick(_statsProgress, _aiProgress))); // a notice, then the job the user just started
            ProgressLabel.Text = show?.Text ?? "";
            ProgressBar1.Value = Math.Clamp(show?.Percent ?? 0, 0, 100);
            UpdateStatusBarVisibility();
        }

        // ── Exit: closing, the update restart and the language restart all pass through here ──

        /// <summary>Stops background work and waits (briefly) for a stats cache write, so the files can't be torn. Idempotent.</summary>
        internal void PrepareForExit()
        {
            try { _closingCts.Cancel(); } catch { }
            SettleAutoAttempt(keep: false); // an exit is not a failed attempt; the run's finally may never get to run
            try { SongTagCache.Default.Flush(); } catch { } // a backfill's fills since its last flush
            StatsCacheWrites.HoldForExit();
        }

        // Takes back (or keeps) the attempt recorded when the automatic run started. Idempotent.
        private void SettleAutoAttempt(bool keep)
        {
            if (_autoAttemptBefore is not string before) return;
            _autoAttemptBefore = null;
            if (!keep) Settings.AutoStatsAttempt = before;
        }

        /// <summary>The exit didn't happen after all: let cache writes through again.</summary>
        internal void ResumeAfterAbortedExit()
        {
            StatsCacheWrites.ReleaseExitHold();
            Logger.LogWarn("Exit was called off; background stats, tags and AI work stay stopped until the app restarts");
        }
    }
}
