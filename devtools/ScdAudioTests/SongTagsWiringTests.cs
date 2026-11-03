using System.Text.RegularExpressions;
using Xunit;

namespace ScdAudioTests;

public class SongTagsWiringTests
{
    private static string Root() => RuntimeTargetTests.RepoRoot();
    private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

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

    private static string Tags() => Src("ui/MainWindow.SongTags.cs");

    [Fact] // [AC-song-tags-tooltip-1] [AC-song-tags-tooltip-9]
    public void Xaml_RowTooltipBackgroundAndHelpText_Bound()
    {
        string xaml = Src("ui/MainWindow.xaml");
        Assert.Contains("<Grid Background=\"Transparent\" ToolTipService.ToolTip=\"{x:Bind SongTooltip, Mode=OneWay}\">", xaml);
        Assert.Contains("AutomationProperties.HelpText=\"{x:Bind SongHelpText, Mode=OneWay}\"", xaml);
        Assert.True(At(xaml, "AutomationProperties.HelpText=\"{x:Bind SongHelpText") < At(xaml, "ToolTipService.ToolTip=\"{x:Bind SongTooltip"));
    }

    [Fact] // [AC-song-tags-tooltip-1] [AC-song-tags-tooltip-3] [EDGE-song-tags-tooltip-18]
    public void CreateSongNode_AppliesSongTooltip_OnlyWithScdRel()
    {
        string core = Src("ui/MainWindowCore.cs");
        Assert.Contains("ApplySongTooltip(node, modRoot);", Method(core, "private static PlaylistNodeContent CreateSongNode"));
        string apply = Method(Tags(), "private static void ApplySongTooltip");
        Assert.True(At(apply, "if (string.IsNullOrEmpty(node.ScdRel)) return;") < At(apply, "SongTooltip.Build("));
        Assert.Contains("node.SongHelpText = tip ?? \"\";", apply);
        Assert.Contains("s_showAiChips ? AiScoreCache.Default.TryGetForRel(node.ScdRel) : null, s_showAiChips", apply);
        Assert.DoesNotContain("SongTagReader", apply);
        Assert.DoesNotContain("ApplySongTooltip", Method(core, "private PlaylistNodeContent CreatePlaylistNode"));
    }

    [Fact] // [AC-song-tags-tooltip-10]
    public void RefreshAiChips_ReappliesSongTooltip()
    {
        Assert.Contains("ApplyAiChip(n); ApplySongTooltip(n);", Method(Src("ui/MainWindow.AiScore.cs"), "private void RefreshAiChips"));
    }

    [Fact] // [AC-song-tags-tooltip-4] [EDGE-song-tags-tooltip-11]
    public void LoadPlaylists_ModRootOnce_LazyTagSearch()
    {
        string load = Method(Src("ui/MainWindowCore.cs"), "private void LoadPlaylists(string filter, string? forceExpandedPlaylistName)");
        Assert.Equal(1, Count(load, "SongTagCache.ModRootOrNull("));
        Assert.True(At(load, "SongTagCache.ModRootOrNull(") < At(load, "foreach (var playlist in Playlists.Values)"));
        Assert.Contains("SongSearch.Matches(filter, playlist.Name, song.Name,", load);
        Assert.Contains("() => SongTagCache.Default.TryGetForRel(Playlist.GetScdPath(song) ?? \"\", modRoot)", load);
        Assert.Contains("CreateSongNode(song, modRoot)", load);
        Assert.DoesNotContain("SongTagReader", load);
    }

    [Fact] // [AC-song-tags-tooltip-5] [EDGE-song-tags-tooltip-16] [EDGE-song-tags-tooltip-25] [EDGE-song-tags-tooltip-29]
    public void Scan_StartsOnLoadAndEverySettingsClose_ReloadFirst()
    {
        string loaded = Method(Src("ui/MainWindow.cs"), "private void PlaylistTreeView_Loaded");
        Assert.True(At(loaded, "LoadPlaylists();") < At(loaded, "StartSongTagScan();"));
        Assert.DoesNotContain("StartSongTagScan", Method(Src("ui/MainWindow.cs"), "private async Task OpenSettingsAsync"));
        string cont = Method(Src("ui/MainWindow.AiScore.cs"), "internal void ContinueAiScoreAfterSettings");
        int reload = At(cont, "if (modChanged && !libraryFresh) LoadPlaylists();");
        int scan = At(cont, "StartSongTagScan(modChanged);");
        Assert.True(reload < scan && scan < At(cont, "if (!Settings.AiScoreEnabled)"));
        // every Settings close path reaches ContinueAiScoreAfterSettings
        Assert.Equal(2, Count(Src("ui/SettingsDialog.xaml.cs"), "App.MainWindow.ContinueAiScoreAfterSettings("));
        Assert.Contains("ContinueAiScoreAfterSettings(libraryFresh: true);", Method(Src("ui/MainWindow.cs"), "private async Task OpenSettingsAsync"));
    }

    [Fact] // [AC-song-tags-tooltip-5] [EDGE-song-tags-tooltip-14]
    public void Scan_RunsInBackgroundMode_WithLinkedCts_PathsOnUiThread()
    {
        string run = Method(Tags(), "private async Task RunSongTagScanAsync");
        int paths = At(run, "SongTagCache.FullPathOf(");
        int task = At(run, "await Task.Run(");
        Assert.True(paths < task, "paths are snapshotted on the UI thread");
        Assert.Contains("using (ThreadPriorityScope.Background())", run[task..]);
        Assert.Contains("SongTagScan.Run(paths, SongTagCache.Default, SongTagReader.Read, ct,", run);
        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(_closingCts.Token)", run);
        Assert.Contains("catch (OperationCanceledException)", run);
        Assert.Contains("if (_closingCts.IsCancellationRequested) return;", Method(Tags(), "private void StartSongTagScan"));
    }

    [Fact] // [EDGE-song-tags-tooltip-24]
    public void Scan_ModChangeCancelsRunningAndQueuesRerun()
    {
        string start = Method(Tags(), "private void StartSongTagScan");
        int running = At(start, "if (_tagScanRunning)");
        Assert.True(At(start, "if (modChanged) { try { _tagScanCts?.Cancel(); }") > running);
        Assert.True(At(start, "_tagScanAgain = true;") > running);
    }

    [Fact] // [AC-song-tags-tooltip-5] [EDGE-song-tags-tooltip-10]
    public void ScanEnd_RefiltersTrimmedWhenSearchActive_ElseRefreshes()
    {
        string run = Method(Tags(), "private async Task RunSongTagScanAsync");
        int guard = At(run, "if (result.Read > 0)");
        Assert.True(At(run, "if (IsFilterActive) LoadPlaylists(SearchTextBox.Text.Trim());") > guard);
        Assert.True(At(run, "else RefreshSongTooltips();") > guard);
    }

    [Fact] // [EDGE-song-tags-tooltip-15]
    public void Scan_CoalescesRequests()
    {
        string run = Method(Tags(), "private async Task RunSongTagScanAsync");
        string fin = run[At(run, "finally")..];
        Assert.Matches(@"_tagScanRunning = false;[\s\S]*if \(_tagScanAgain && !_closingCts\.IsCancellationRequested\)\s*\{\s*_tagScanAgain = false;\s*StartSongTagScan\(\);", fin);
        Assert.Contains("_tagScanCts?.Dispose();", fin);
    }

    [Fact] // [EDGE-song-tags-tooltip-30]
    public void Scan_RunningFlagSetBeforeAnyAwait()
    {
        string start = Method(Tags(), "private void StartSongTagScan");
        Assert.True(At(start, "_tagScanRunning = true;") < At(start, "RunSongTagScanAsync()"));
        Assert.DoesNotContain("await", start);
        string run = Method(Tags(), "private async Task RunSongTagScanAsync");
        int task = At(run, "await Task.Run(");
        Assert.Equal(0, Count(run[..task], "await")); // nothing awaited before the scan starts
        Assert.DoesNotContain("_tagScanRunning = true", run);
    }

    [Fact] // [EDGE-song-tags-tooltip-27]
    public void NullModRoot_Wiring()
    {
        string load = Method(Src("ui/MainWindowCore.cs"), "private void LoadPlaylists(string filter, string? forceExpandedPlaylistName)");
        Assert.Contains("SongTagCache.ModRootOrNull(Settings.PenumbraLocation, Settings.ModName)", load);
        Assert.DoesNotContain("Path.Combine(Settings.PenumbraLocation", load);
        string run = Method(Tags(), "private async Task RunSongTagScanAsync");
        int tryAt = Regex.Match(run, @"\btry\s*\{").Index;
        Assert.True(tryAt > 0 && tryAt < At(run, "SongTagCache.FullPathOf("));
        Assert.True(At(run, "SongTagCache.ModRootOrNull(") > tryAt);
    }

    [Fact] // [AC-song-tags-tooltip-7]
    public void EveryAiCacheMoveAndForget_HasTagCacheTwin()
    {
        foreach (var file in new[] { "Library.cs", "Playlist.cs", "ui/MainWindow.DragDrop.cs", "ui/MainWindowCore.cs" })
        {
            string src = Src(file);
            Assert.Equal(Count(src, "AiScoreCache.Default.UpdateCacheForSCD("), Count(src, "SongTagCache.Default.UpdateCacheForSCD("));
            Assert.Equal(Count(src, "AiScoreCache.Default.Forget("), Count(src, "SongTagCache.Default.Forget("));
            // each twin is the next line, with the same arguments
            foreach (Match m in Regex.Matches(src, @"AiScoreCache\.Default\.(UpdateCacheForSCD|Forget)\(([^;]*)\);[^\r\n]*\r?\n\s*(Utils\.)?SongTagCache\.Default\.(\w+)\(([^;]*)\);"))
            {
                Assert.Equal(m.Groups[1].Value, m.Groups[4].Value);
                Assert.Equal(m.Groups[2].Value, m.Groups[5].Value);
            }
            Assert.Equal(Count(src, "AiScoreCache.Default.UpdateCacheForSCD(") + Count(src, "AiScoreCache.Default.Forget("),
                Regex.Matches(src, @"AiScoreCache\.Default\.(UpdateCacheForSCD|Forget)\(([^;]*)\);[^\r\n]*\r?\n\s*(Utils\.)?SongTagCache\.Default\.\1\(\2\);").Count);
        }
    }

    [Fact] // [AC-song-tags-tooltip-6] [EDGE-song-tags-tooltip-20]
    public void AddFiles_StoresTagsFromEncodedBytes_BothBranches()
    {
        string pl = Src("Playlist.cs");
        int reused = At(pl, "if (reused)\r\n                {");
        int aiForget = At(pl, "AiScoreCache.Default.Forget(targetPath);", reused);
        int store = At(pl, "try { if (Pickles_Playlist_Editor.Tools.SongTagReader.FromScdBytes(encoded) is { } tags) SongTagCache.Default.Store(targetPath, tags); } catch { }", reused);
        Assert.True(aiForget < store);
        // after the else branch closes, so both branches store
        string between = pl[aiForget..store];
        Assert.True(Count(between, "}") >= 1);
        Assert.True(store < At(pl, "string targetRelative = Path.Combine(playlistScdDirectory", reused));
    }
}
