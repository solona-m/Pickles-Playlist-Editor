using System.Text.RegularExpressions;
using Pickles_Playlist_Editor.Tools;
using Xunit;

namespace ScdAudioTests;

public class BackgroundComputeStatsWiringTests
{
    private static string Root() => RuntimeTargetTests.RepoRoot();
    private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel)).Replace("\r\n", "\n");

    private static string Method(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + signature);
        int open = src.IndexOf('{', i), depth = 0;
        for (int j = open; j < src.Length; j++)
        {
            if (src[j] == '{') depth++;
            else if (src[j] == '}' && --depth == 0) return src[i..(j + 1)];
        }
        return src[i..];
    }

    private static int At(string s, string needle, int from = 0)
    {
        int i = s.IndexOf(needle, from, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + needle);
        return i;
    }

    private static int Count(string s, string needle) => Regex.Matches(s, Regex.Escape(needle)).Count;

    private static string Stats() => Src("ui/MainWindow.Stats.cs");
    private static string Run() => Method(Stats(), "private async Task RunStatsPassAsync");
    private static string Apply() => Method(Stats(), "private async Task<(int Renamed, int Saved)> ApplyStatsNamesAsync");

    private static IEnumerable<string> AppSources() =>
        Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(Path.GetRelativePath(Root(), f).Replace('\\', '/'), @"(^|/)(bin|obj|devtools)/"))
            .Select(f => File.ReadAllText(f));

    [Fact] // [AC-background-compute-stats-1] [AC-background-compute-stats-8] [EDGE-background-compute-stats-19]
    public void ComputeStatsClick_NoOverlay_StartsBackgroundRun_OrSaysRunning()
    {
        string click = Method(Src("ui/MainWindow.ContextMenu.cs"), "private async void ComputeStatsMenuItem_Click");
        Assert.Contains("if (!StartStatsRun(targetSongs, StatsPassKind.Manual))", click);
        Assert.Contains("AppStrings.Get(\"Stats_AlreadyRunning\")", click);
        foreach (var bad in new[] { "SetProgressBarText", "SetProgressBarPercent", "Task.Run", ".Save(", "UpdateName" })
            Assert.DoesNotContain(bad, click);
    }

    [Fact] // [AC-background-compute-stats-1] [AC-background-compute-stats-10]
    public void StatsRun_StatusBarOnly()
    {
        string stats = Stats();
        foreach (var bad in new[] { "SetProgressBarText", "SetProgressBarPercent", "ClearProgressDisplay(", "BusyOverlay", "IsHitTestVisible" })
            Assert.DoesNotContain(bad, stats);
        Assert.Contains("ClearStatsProgress();", Run()[At(Run(), "finally")..]);
        Assert.Contains("SetStatsProgress(AppStrings.Prog_StatsWaitingToApply, 100);", Apply());
        Assert.Contains("BackgroundStatus.Pick(_statsProgress, _aiProgress)", Method(stats, "private void RenderBackgroundProgress"));
        string ai = Method(Src("ui/MainWindow.AiScore.cs"), "private void SetBackgroundProgress");
        Assert.Contains("_aiProgress = ", ai);
        Assert.Contains("RenderBackgroundProgress();", ai);
    }

    [Fact] // [AC-background-compute-stats-2] [AC-background-compute-stats-9]
    public void StatsRun_Order_DetectApplySummaryThenPhase2()
    {
        string run = Run();
        int detect = At(run, "StatsDetectLoop.RunAsync(");
        int apply = At(run, "ApplyStatsNamesAsync(");
        int summary = At(run, "ShowOperationSummary(AppStrings.Summary_ComputeStats");
        int end = At(run, "_statsGate.End(ticket)");
        int phase2 = At(run, "RunAiScorePhaseAsync(targets)");
        Assert.True(detect < apply && apply < end && end < summary && summary < phase2);
        Assert.True(At(run, "if (end != StatsPassEnd.Finished) return;") < summary);
        Assert.True(At(run, "if (ticket.Kind == StatsPassKind.Manual)\n            {") < summary);
        Assert.Contains("if (Settings.AiScoreEnabled) await RunAiScorePhaseAsync(targets);", run);
        Assert.Contains("ticket.Kind == StatsPassKind.Manual ? StatsRenameMode.Rebuild : StatsRenameMode.Additive", run);
        Assert.Contains("ticket.Kind == StatsPassKind.Manual ? targets.Select(t => t.playlist.Name ?? \"\").ToHashSet() : null", run);
        string applySrc = Apply();
        Assert.Contains("onlyPlaylists == null || onlyPlaylists.Contains(n)", applySrc);
        Assert.Contains("requireSamePlaylist: onlyPlaylists != null", applySrc);
    }

    [Fact] // [AC-background-compute-stats-3] [EDGE-background-compute-stats-1] [EDGE-background-compute-stats-50]
    public void AutoRun_StartsOnLoadAndAfterSettings_AfterBootCapture()
    {
        string loaded = Method(Src("ui/MainWindow.cs"), "private void PlaylistTreeView_Loaded");
        Assert.True(At(loaded, "VersionBackup.TryCaptureOnBoot();") < At(loaded, "LoadPlaylists();")
            && At(loaded, "LoadPlaylists();") < At(loaded, "StartSongTagScan();")
            && At(loaded, "StartSongTagScan();") < At(loaded, "MaybeStartAutoStats();")
            && At(loaded, "MaybeStartAutoStats();") < At(loaded, "CheckForUpdatesAsync"));
        string cont = Method(Src("ui/MainWindow.AiScore.cs"), "internal void ContinueAiScoreAfterSettings");
        Assert.True(At(cont, "StartSongTagScan(modChanged);") < At(cont, "if (modChanged) _statsGate.CancelRunning();")
            && At(cont, "if (modChanged) _statsGate.CancelRunning();") < At(cont, "MaybeStartAutoStats();")
            && At(cont, "MaybeStartAutoStats();") < At(cont, "if (!Settings.AiScoreEnabled)"));
        string maybe = Method(Stats(), "internal void MaybeStartAutoStats");
        Assert.True(At(maybe, "VersionBackup.TryCaptureOnBoot()") < At(maybe, "StartStatsRun("));
    }

    [Fact] // [AC-background-compute-stats-3] [AC-background-compute-stats-5] [EDGE-background-compute-stats-2] [EDGE-background-compute-stats-20]
    public void MaybeStartAutoStats_Guards()
    {
        string maybe = Method(Stats(), "internal void MaybeStartAutoStats");
        int closing = At(maybe, "if (_closingCts.IsCancellationRequested) return;");
        int capture = At(maybe, "VersionBackup.TryCaptureOnBoot()");
        int cheap = At(maybe, "AutoStatsRules.ShouldRun(done, attempt, AppVersion.Display, modReadable: true)");
        int parse = At(maybe, "PenumbraMeta.DetectFormat() != ModFormat.Unknown");
        int rules = At(maybe, "AutoStatsRules.ShouldRun(done, attempt, AppVersion.Display, modReadable)", parse);
        int busy = At(maybe, "if (_statsGate.IsRunning) { _autoStatsAgain = true; return; }");
        int start = At(maybe, "StartStatsRun(Library.SongTargets(Playlists.Values), StatsPassKind.Auto)");
        Assert.True(closing < capture && capture < cheap && cheap < parse && parse < rules && rules < busy && busy < start);
        Assert.Contains("string done = Settings.AutoStatsVersion, attempt = Settings.AutoStatsAttempt;", maybe);
        Assert.Contains("catch (Exception ex)", maybe);
        Assert.DoesNotContain("AutoStatsAttempt =", maybe); // no attempt recorded at start
    }

    [Fact] // [AC-background-compute-stats-5] [EDGE-background-compute-stats-17] [EDGE-background-compute-stats-18] [EDGE-background-compute-stats-42]
    public void AutoRun_AttemptRecordedAtStart_TakenBackOnCleanFinishOrCancel()
    {
        // Second review: recorded before any work, so a crash during analysis counts toward the cap too.
        Assert.Equal(1, AppSources().Sum(s => Count(s, "Settings.AutoStatsAttempt = AutoStatsRules.NextAttempt(")));
        string run = Run();
        int record = At(run, "Settings.AutoStatsAttempt = AutoStatsRules.NextAttempt(attemptBefore, AppVersion.Display);");
        Assert.True(record < At(run, "try\n            {"), "recorded before the run's work starts");
        Assert.True(At(run, "if (ticket.Kind == StatsPassKind.Auto)\n            {") < record);
        string fin = run[At(run, "finally")..];
        Assert.Matches(@"if \(ticket\.Kind == StatsPassKind\.Auto && \(end == StatsPassEnd\.Cancelled \|\| \(end == StatsPassEnd\.Finished && saveErrors\.Count == 0\)\)\)\s*Settings\.AutoStatsAttempt = attemptBefore \?\? """";", fin);
        Assert.Matches(@"reachedApply = true;\s*\(renamed, saved\) = await ApplyStatsNamesAsync\(", run);
        Assert.True(At(fin, "_statsGate.End(ticket);") < At(fin, "ClearStatsProgress();"), "the gate is released first");
    }

    [Fact] // [AC-background-compute-stats-3] [AC-background-compute-stats-5] [EDGE-background-compute-stats-5] [EDGE-background-compute-stats-18] [EDGE-background-compute-stats-41]
    public void AutoRun_NoDialogs_MarkerOnlyOnCleanFinish()
    {
        Assert.Equal(1, AppSources().Sum(s => Count(s, "Settings.AutoStatsVersion = AppVersion.Display")));
        string run = Run();
        int marker = At(run, "Settings.AutoStatsVersion = AppVersion.Display");
        Assert.True(At(run, "ApplyStatsNamesAsync(") < marker && marker < At(run, "catch (OperationCanceledException)"));
        Assert.Matches(@"if \(ticket\.Kind == StatsPassKind\.Auto && AutoStatsRules\.ShouldWriteMarker\(end, saveErrors\.Count, entryCount, Playlists\.Count\)\)\s*Settings\.AutoStatsVersion", run);
        int manual = At(run, "if (ticket.Kind == StatsPassKind.Manual)\n            {");
        foreach (Match m in Regex.Matches(run, @"ShowOperationSummary\(|ShowDialogAsync\("))
            Assert.True(m.Index > manual, "every dialog in the run is under Manual");
        Assert.DoesNotContain("ShowDialogAsync(", Apply());
    }

    [Fact] // [AC-background-compute-stats-9] [EDGE-background-compute-stats-23] [EDGE-background-compute-stats-48] [EDGE-background-compute-stats-49]
    public void AutoRun_AiTail_GuardedByStore()
    {
        Assert.Contains("else if (Settings.AiScoreEnabled && AiScoreCache.Default.HasStore)\n                StartLibraryAiScan();", Run());
        Assert.DoesNotContain("no AI scan at startup", Method(Src("ui/MainWindow.cs"), "private void PlaylistTreeView_Loaded"));
        Assert.True(AiPassRules.ShowsDialog(AiPass.Library, AiPassEnd.RuntimeFailed)); // the accepted exception
        Assert.Contains("public bool HasStore => File.Exists(_path);", Src("Utils/AiScoreCache.cs"));
    }

    [Fact] // [AC-background-compute-stats-6]
    public void Detect_BackgroundPriority_SharedGate()
    {
        string loop = Src("Tools/StatsDetectLoop.cs");
        int enter = At(loop, "await gate.EnterAsync(ct);");
        Assert.True(enter < At(loop, "if (isIdle()) break;"));
        Assert.Contains("Task.Run(() => { using (ThreadPriorityScope.Background()) return detect(rel); })", loop);
        Assert.Contains("finally { gate.Exit(); }", loop);
        Assert.Contains("LibraryReadGate.Shared, ct,", Run());
        string detect = Method(Stats(), "private static bool DetectOne");
        Assert.Contains("BPMDetector.GetBPMFromSCD(rel);", detect);
        Assert.Contains("KeyDetector.GetKeyFromSCD(rel);", detect);
        Assert.Contains("BPMDetector.GetDuration(rel);", detect);
        Assert.Contains("return HasAllStats(rel);", detect);
    }

    [Fact] // [AC-background-compute-stats-6] [EDGE-background-compute-stats-21] [EDGE-background-compute-stats-22]
    public void TagScanAndAiLoop_TakeSharedGate()
    {
        string tags = Method(Src("ui/MainWindow.SongTags.cs"), "private async Task RunSongTagScanAsync");
        Assert.True(At(tags, "LibraryReadGate.Shared.Enter(ct);") < At(tags, "SongTagScan.Run("));
        Assert.Contains("finally { LibraryReadGate.Shared.Exit(); }", tags);
        Assert.True(At(tags, "await Task.Run(") < At(tags, "LibraryReadGate.Shared.Enter(ct);"), "taken on the scan thread, not awaited before it");
        string ai = Method(Src("ui/MainWindow.AiScore.cs"), "private async Task RunAiScorePhaseAsync");
        Assert.Matches(@"LibraryReadGate\.Shared\.Enter\(ct\);[^\n]*\n\s*try \{ r\.ScoreSong\(paths\[i\], ct\); \}\s*finally \{ LibraryReadGate\.Shared\.Exit\(\); \}", ai);
    }

    [Fact] // [AC-background-compute-stats-7] [EDGE-background-compute-stats-16] [EDGE-background-compute-stats-45]
    public void QuietDetection_AppOwnedSignals()
    {
        string stats = Stats();
        string dialog = Method(stats, "private bool IsDialogOpen");
        Assert.Contains("is ContentDialog", dialog);
        Assert.Contains("_dialogGate.CurrentCount == 0", dialog);
        string quiet = Method(stats, "private bool IsQuietForApply");
        foreach (var signal in new[] { "_busyOverlayVisible", "_dragInProgress", "_namingRefreshInFlight", "_restoreInFlight", "IsDialogOpen()", "IsMenuOpen()", "_nativeModalOpen", "OpenPopups()" })
            Assert.Contains(signal, quiet);
        string revert = Method(Src("ui/MainWindow.ModFolderNotices.cs"), "private async void RevertUndoButton_Click");
        Assert.True(At(revert, "_restoreInFlight = true;") < At(revert, "BackupCatalog.Restore(entry)"));
        Assert.Matches(@"finally\s*\{[^}]*LoadPlaylists\(\);\s*_restoreInFlight = false;", revert);
        Assert.Contains("GetOpenPopupsForXamlRoot", Method(stats, "private IReadOnlyList<Microsoft.UI.Xaml.Controls.Primitives.Popup> OpenPopups"));
        string menu = Method(stats, "private bool IsMenuOpen");
        foreach (var f in new[] { "_treeContextMenu.IsOpen", "_rootContextMenu.IsOpen", "AddSongsFlyout.IsOpen", "SortFlyout.IsOpen" })
            Assert.Contains(f, menu);
        string xaml = Src("ui/MainWindow.xaml");
        Assert.Contains("<MenuFlyout x:Name=\"AddSongsFlyout\">", xaml);
        Assert.Contains("<MenuFlyout x:Name=\"SortFlyout\">", xaml);
        Assert.Equal(0, AppSources().Sum(s => Count(s, "_menuOpen") + Count(s, "AppMenu_Opened")));
        string main = Src("ui/MainWindow.cs");
        Assert.Equal(3, Count(main, "_nativeModalOpen++;"));
        Assert.Equal(3, Count(main, "finally { _nativeModalOpen--; }"));
        string naming = Method(main, "internal async Task ApplyNamingOptionsAsync");
        Assert.Matches(@"_namingRefreshInFlight = true;[\s\S]*finally \{ _namingRefreshInFlight = false; \}", naming);
    }

    [Fact] // [AC-background-compute-stats-7] [EDGE-background-compute-stats-14] [EDGE-background-compute-stats-15] [EDGE-background-compute-stats-17] [EDGE-background-compute-stats-27] [EDGE-background-compute-stats-43] [EDGE-background-compute-stats-44]
    public void Apply_PerPlaylistTick_RechecksQuietModAndParts()
    {
        string apply = Apply();
        string compute = apply[At(apply, "async Task<Dictionary<StatsEntry, string>> Compute()")..At(apply, "do expected = await Compute();")];
        Assert.True(At(compute, "StatsRename.ExpectedNames(") < compute.LastIndexOf("CheckMod(modKey);", StringComparison.Ordinal));
        string loop = apply[At(apply, "foreach (string name in Playlists.Keys.Where(")..];
        int tick = At(loop, "await NextTickAsync();");
        Assert.Matches(@"await NextTickAsync\(\);\s*ct\.ThrowIfCancellationRequested\(\);", loop);
        int wait = At(loop, "await WaitUntilQuietForApplyAsync(ct);");
        int recompute = At(loop, "if (OptionStatsNaming.Parts.FromSettings() != parts) { expected = await Compute(); continue; }");
        int quiet = At(loop, "if (!IsQuietForApply()) continue;");
        int cancel = At(loop, "ct.ThrowIfCancellationRequested();", quiet);
        int mod = At(loop, "CheckMod(modKey);", quiet);
        Assert.True(cancel < mod, "closing or an exit stops the run before anything is written");
        Assert.Contains("tcs.TrySetCanceled();", Method(Stats(), "private Task NextTickAsync"));
        Assert.DoesNotContain("tcs.TrySetResult();\n", Method(Stats(), "private Task NextTickAsync").Replace("() => tcs.TrySetResult()", ""));
        int parts = At(loop, "if (OptionStatsNaming.Parts.FromSettings() != parts) continue;", mod);
        int applyOne = At(loop, "StatsRename.ApplyOne(", parts);
        int save = At(loop, "pl.Save();", applyOne);
        Assert.True(tick < wait && wait < recompute && recompute < quiet && quiet < mod && mod < parts && parts < applyOne && applyOne < save);
        Assert.DoesNotContain("await", loop[quiet..save]);
        Assert.Contains("IsQuietForApply() ? Task.CompletedTask :", Method(Stats(), "private Task WaitUntilQuietForApplyAsync"));
    }

    [Fact] // [AC-background-compute-stats-2] [EDGE-background-compute-stats-12] [EDGE-background-compute-stats-13] [EDGE-background-compute-stats-29] [EDGE-background-compute-stats-52]
    public void Apply_UpdatesNodesInPlace()
    {
        string apply = Apply();
        Assert.Contains("if (IsFilterActive) LoadPlaylists(SearchTextBox.Text.Trim());", apply);
        Assert.Contains("if (!Playlists.TryGetValue(name, out var pl)) break; // renamed meanwhile: skipped", apply);
        string nodes = Method(Stats(), "private void UpdateStatsNodes");
        Assert.Contains("n.Name == c.OldName && StatsPlan.RelKeyOf(n.ScdRel) == key", nodes);
        Assert.Contains("_nowPlayingSongName = c.NewName", nodes);
        Assert.Contains("ApplyCamelotChip(song);", nodes);
        string refresh = Method(Stats(), "private void RefreshStatsDisplay(IReadOnlyList<string> detectedRels)");
        Assert.Contains("SetNodeDisplayText(plNode,", refresh);
        Assert.Contains("detected.Contains(StatsPlan.RelKeyOf(n.ScdRel))", refresh);
        Assert.Contains("else if (plan.ToDetect.Count > 0) RefreshStatsDisplay(plan.ToDetect);", apply);
        Assert.Equal(2, Count(nodes, "SyncPlaylistNode(pl)")); // only when a node isn't found
        Assert.DoesNotContain("RootPlaylistItems.Clear", Stats());
    }

    [Fact] // [EDGE-background-compute-stats-5] [EDGE-background-compute-stats-10] [EDGE-background-compute-stats-47]
    public void Apply_SaveOncePerDirtyPlaylist_RevertOnFailure_ForgetBackfill()
    {
        string apply = Apply();
        Assert.Equal(1, Count(Stats(), ".Save("));
        Assert.Matches(@"if \(dirty\)\s*\{\s*bool ok = false;\s*try\s*\{\s*using \(snapshots\.Enter\(\)\) pl\.Save\(\);\s*_backfilledPlaylists\.Remove\(pl\);\s*ok = true;\s*\}\s*catch \(Exception ex\)\s*\{\s*StatsRename\.Revert\(changes\);", apply);
        int save = At(apply, "using (snapshots.Enter()) pl.Save();");
        int revert = At(apply, "StatsRename.Revert(changes);", save);
        Assert.True(At(apply, "if (ok)", revert) < At(apply, "UpdateStatsNodes(pl, changes)", revert), "the tree is updated only after a save that worked");
        Assert.DoesNotContain("UpdateStatsNodes(", apply[save..revert]);
        Assert.Contains("_backfilledPlaylists.Contains(pl)", apply);
        Assert.Contains("using (snapshots.Enter()) pl.Save();", apply);
        Assert.True(At(apply, "var snapshots = new SnapshotBatch();") < At(apply, "foreach (string name in Playlists.Keys.Where("));
        foreach (var f in new[] { "Utils/PenumbraMeta.cs", "Utils/V3GroupFileStore.cs" })
            Assert.Matches(@"if \(ourWrite\) ModFolderGuard\.NoteWrite\(\);\s*if \(SnapshotBatch\.SkipSnapshot\(\)\) return null;", Src(f));
    }

    [Fact] // [EDGE-background-compute-stats-18] [EDGE-background-compute-stats-38] [EDGE-background-compute-stats-39]
    public void ExitGuard_OnAllThreeExitPaths()
    {
        Assert.Matches(@"FlushPenumbraMod\(\);\s*DisposeAiScore\(\);\s*PrepareForExit\(\);", Src("ui/MainWindow.cs"));
        string update = Src("ui/MainWindowCore.cs");
        int dl = At(update, "await mgr.DownloadUpdatesAsync(update);");
        Assert.True(dl < At(update, "PrepareForExit();", dl) && At(update, "PrepareForExit();", dl) < At(update, "ApplyUpdatesAndRestart(", dl));
        Assert.Contains("catch { ResumeAfterAbortedExit(); throw; }", update);
        Assert.Matches(@"Playlist\.FlushPenumbraMod\(\);\s*PrepareForExit\(\);", update);
        string lang = Method(Src("ui/SettingsDialog.xaml.cs"), "private void PromptRestartForLanguage");
        int prep = At(lang, "App.MainWindow.PrepareForExit();");
        int restart = At(lang, "AppInstance.Restart(");
        Assert.True(prep < restart && restart < At(lang, "App.MainWindow.ResumeAfterAbortedExit();"));
        Assert.Contains("Logger.LogWarn(", Method(Stats(), "internal void ResumeAfterAbortedExit"));
        string guard = Method(Stats(), "internal void PrepareForExit");
        Assert.Contains("_closingCts.Cancel()", guard);
        Assert.Contains("StatsCacheWrites.HoldForExit();", guard);
    }

    [Fact] // [EDGE-background-compute-stats-18] [EDGE-background-compute-stats-46]
    public void CacheWrites_OnlyThroughStatsCacheWrites()
    {
        foreach (var f in new[] { "Utils/BPMDetector.cs", "Utils/KeyDetector.cs" })
        {
            foreach (string line in Src(f).Split('\n'))
            {
                bool write = Regex.IsMatch(line, @"\b(bpmCache|durationCache|keyCache)\[[^\]]+\]\s*=[^=]") || Regex.IsMatch(line, @"\b(bpmCache|durationCache|keyCache)\.Remove\(");
                if (write) Assert.Contains("StatsCacheWrites.Write(() =>", line);
            }
        }
        string writes = Src("Utils/StatsCacheWrites.cs");
        Assert.Contains("using (ThreadPriorityScope.SuspendBackground())\n                lock (Lock) action();", writes);
    }

    [Fact] // [AC-background-compute-stats-10] [EDGE-background-compute-stats-25]
    public void ClearProgressDisplay_RestoresBackgroundStatus()
    {
        string clear = Method(Src("ui/MainWindowCore.cs"), "public void ClearProgressDisplay");
        Assert.Matches(@"SetBusyOverlayVisible\(false\);\s*RenderBackgroundProgress\(\);[^\n]*\n\s*\}$", clear);
        Assert.Contains("if (_busyOverlayVisible) return;", Method(Stats(), "private void RenderBackgroundProgress"));
    }

    [Fact] // [EDGE-background-compute-stats-9] [EDGE-background-compute-stats-10]
    public void Backfill_RecordsPlaylistsAndOptions()
    {
        string backfill = Method(Src("ui/MainWindowCore.cs"), "private void BackfillStatsFromCacheIfNeeded");
        int update = At(backfill, "OptionStatsNaming.UpdateName(option, bpm, key, duration);");
        Assert.True(update < At(backfill, "_backfilledPlaylists.Add(playlist);") && update < At(backfill, "_backfilledOptions.Add(option);"));
        // only a name it actually changed: otherwise the automatic run would save playlists with nothing new
        int unchanged = At(backfill, "if (option.Name == before) continue;");
        Assert.True(At(backfill, "string before = option.Name;") < update && update < unchanged && unchanged < At(backfill, "_backfilledPlaylists.Add(playlist);"));
        string stats = Stats();
        Assert.Contains("new HashSet<Playlist> _backfilledPlaylists", stats.Replace("readonly HashSet<Playlist> _backfilledPlaylists = new(", "new HashSet<Playlist> _backfilledPlaylists"));
        Assert.Equal(2, Count(stats, "new(ReferenceEqualityComparer.Instance)"));
        Assert.Contains("StatsPlan.Snapshot(targets, Playlist.GetScdPath, o => _backfilledOptions.Contains(o))", Run());
    }

    [Fact] // [AC-background-compute-stats-7] [EDGE-background-compute-stats-17]
    public void ModChange_CancelsStatsRun_RestartsViaAgainFlag()
    {
        string fin = Run()[At(Run(), "finally")..];
        Assert.Matches(@"if \(_autoStatsAgain && !_closingCts\.IsCancellationRequested\)\s*\{\s*_autoStatsAgain = false;\s*MaybeStartAutoStats\(\);", fin);
        Assert.Contains("if (modChanged) _statsGate.CancelRunning();", Method(Src("ui/MainWindow.AiScore.cs"), "internal void ContinueAiScoreAfterSettings"));
        string ctor = Method(Src("ui/MainWindow.cs"), "public MainWindow()");
        Assert.True(At(ctor, "_statsGate = new StatsPassGate(_closingCts.Token);") < At(ctor, "this.InitializeComponent();"));
    }
}
