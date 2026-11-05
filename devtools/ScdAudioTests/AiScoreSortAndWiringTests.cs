using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Pickles_Playlist_Editor;
using Xunit;

namespace ScdAudioTests;

public class AiScoreSortAndWiringTests
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

    private static Option Song(string name) => new() { Name = name };

    [Fact] // [AC-ai-score-7] [EDGE-ai-score-28]
    public void OrderByAiScore_UnscoredLast_BothWays_TiesStable()
    {
        var a = Song("a"); var b = Song("b"); var c = Song("c"); var d = Song("d"); var e = Song("e"); var f = Song("f");
        var scores = new Dictionary<Option, int?> { [a] = 300, [b] = null, [c] = 900, [d] = -1, [e] = 300, [f] = 600 };
        var desc = Playlist.OrderByAiScore(new[] { a, b, c, d, e, f }, o => scores[o], SortDirection.Descending);
        Assert.Equal(new[] { c, f, a, e, b, d }, desc);
        var asc = Playlist.OrderByAiScore(new[] { a, b, c, d, e, f }, o => scores[o], SortDirection.Ascending);
        Assert.Equal(new[] { a, e, f, c, b, d }, asc);
    }

    [Fact] // [AC-ai-score-7] [EDGE-ai-score-46]
    public void AiSort_FirstClickDescending_IndependentDirection()
    {
        string src = Src("ui/MainWindow.AiScore.cs");
        Assert.Contains("SortDirection _aiSortDirection = SortDirection.Ascending", src);
        string click = Method(src, "private void SortByAi_Click");
        Assert.Contains("_aiSortDirection", click);
        Assert.DoesNotContain("_currentSortDirection", click);
        Assert.Contains("SortByAiLikelihood", click);
        Assert.Contains("TryGetForRel", Method(Src("Playlist.cs"), "internal void SortByAiLikelihood")); // cache only, never scores
    }

    [Fact] // [AC-ai-score-1] [AC-ai-score-4] [EDGE-ai-score-38]
    public void ComputeStats_TwoPhases()
    {
        // Retargeted for background Compute Stats: the click starts a run; the phases are ordered in RunStatsPassAsync.
        string handler = Method(Src("ui/MainWindow.ContextMenu.cs"), "private async void ComputeStatsMenuItem_Click");
        Assert.Contains("StartStatsRun(targetSongs, StatsPassKind.Manual)", handler);
        string run = Method(Src("ui/MainWindow.Stats.cs"), "private async Task RunStatsPassAsync");
        Assert.Contains("if (Settings.AiScoreEnabled) await RunAiScorePhaseAsync(targets);", run); // no AI work while off
        int summary = run.IndexOf("ShowOperationSummary(AppStrings.Summary_ComputeStats", StringComparison.Ordinal);
        int phase2 = run.IndexOf("RunAiScorePhaseAsync(", StringComparison.Ordinal);
        Assert.True(summary > 0 && phase2 > summary, "phase 2 runs after the phase 1 summary");
        int phase1Run = run.IndexOf("StatsDetectLoop.RunAsync(", StringComparison.Ordinal);
        Assert.True(phase1Run > 0 && phase1Run < summary);
        string phase = Method(Src("ui/MainWindow.AiScore.cs"), "private async Task RunAiScorePhaseAsync");
        foreach (var bad in new[] { ".Save(", "UpdateName", "option.Name =" }) Assert.DoesNotContain(bad, phase);
        Assert.Matches(@"await Task\.Run\(\(\) =>[\s\S]*ScoreSong", phase);
    }

    [Fact] // [AC-ai-score-4] phase 2 keeps the window usable: status-bar progress, no overlay, one pass at a time
    public void Phase2_NonBlockingProgress_SinglePass()
    {
        string phase = Method(Src("ui/MainWindow.AiScore.cs"), "private async Task RunAiScorePhaseAsync");
        Assert.DoesNotContain("SetProgressBarText", phase);
        Assert.DoesNotContain("SetProgressBarPercent", phase);
        Assert.Contains("SetBackgroundProgress(", phase);
        // One pass at a time through the ticketed gate (its transitions are unit-tested in AiScanOnEnableTests).
        int begin = phase.IndexOf("_aiGate.TryBegin(pass, out var ticket)", StringComparison.Ordinal);
        Assert.True(begin > 0 && begin < Regex.Match(phase, @"\btry\s*\{").Index, "only a started pass reaches finally");
        Assert.Matches(@"case AiPassStart\.Queued: return;\s*case AiPassStart\.AlreadyRunning:\s*await ShowDialogAsync\(AppStrings\.Summary_AiScore, AppStrings\.Get\(""AiScore_AlreadyRunning""\)\);\s*return;", phase);
        Assert.Matches(@"finally\s*\{\s*ClearBackgroundProgress\(\);[\s\S]*_aiGate\.End\(ticket, Settings\.AiScoreEnabled\)", phase);
        string setter = Method(Src("ui/MainWindow.AiScore.cs"), "private void SetBackgroundProgress");
        Assert.DoesNotContain("BusyOverlay", setter);
        Assert.DoesNotContain("IsHitTestVisible", setter);
    }

    [Fact] // [EDGE-ai-score-33] [EDGE-ai-score-48] songs without an SCD are skipped; all-cached loads no model and asks nothing
    public void Phase2_SkipsEmptyPaths_AndCachedBeforeLoading()
    {
        string phase = Method(Src("ui/MainWindow.AiScore.cs"), "private async Task RunAiScorePhaseAsync");
        Assert.Contains(".Where(p => !string.IsNullOrEmpty(p))", phase);
        int filter = phase.IndexOf("cache.TryGet(p) is null", StringComparison.Ordinal);
        int empty = phase.IndexOf("if (paths.Count == 0)", StringComparison.Ordinal);
        int ensure = phase.IndexOf("EnsureAiReadyForStatsAsync()", StringComparison.Ordinal);
        int begin = phase.IndexOf("AiScoreRun.Begin(", StringComparison.Ordinal);
        Assert.True(filter > 0 && filter < empty && empty < ensure && ensure < begin);
        Assert.Contains(".Select(AiScoreCache.FullPathOf)", phase); // the same spelling as chips and sort
    }

    [Fact] // [AC-ai-score-4] chips refresh in place, every 10 songs and at the end
    public void Phase2_RefreshesChipsInPlace()
    {
        string src = Src("ui/MainWindow.AiScore.cs");
        string refresh = Method(src, "private void RefreshAiChips");
        Assert.DoesNotContain("ReplaceChildren", refresh);
        Assert.DoesNotContain("LoadPlaylists", refresh);
        string phase = Method(src, "private async Task RunAiScorePhaseAsync");
        Assert.Contains("% 10 == 0) _uiDispatcherQueue.TryEnqueue(RefreshAiChips)", phase);
        Assert.Matches(@"ClearBackgroundProgress\(\);\s*RefreshAiChips\(\);", phase);
    }

    [Fact] // [AC-ai-score-8] [EDGE-ai-score-41] [EDGE-ai-score-42]
    public void DeletePaths_Forget()
    {
        string core = Src("ui/MainWindowCore.cs");
        Assert.Matches(@"File\.Delete\(scdFull\);\s*Utils\.AiScoreCache\.Default\.Forget\(scdFull\);", core);
        string delete = Method(Src("Playlist.cs"), "public void Delete()");
        Assert.True(delete.IndexOf("AiScoreCache.Default.Forget", StringComparison.Ordinal) < delete.IndexOf("ReclaimUnusedAudioDirectories", StringComparison.Ordinal));
    }

    [Fact] // [AC-ai-score-8] [EDGE-ai-score-23] [EDGE-ai-score-24] every BPM/key cache migration also moves the AI score
    public void Migrations_AtEverySite()
    {
        foreach (var f in new[] { "Playlist.cs", "Library.cs", "ui/MainWindow.DragDrop.cs" })
        {
            string src = Src(f);
            int key = Regex.Matches(src, @"KeyDetector\.UpdateCacheForSCD\(").Count;
            int ai = Regex.Matches(src, @"AiScoreCache\.Default\.UpdateCacheForSCD\(").Count;
            Assert.True(key > 0 && ai == key, $"{f}: {key} key migrations, {ai} AI migrations");
        }
        Assert.Matches(@"FileMode\.CreateNew\)\)[\s\S]{0,120}AiScoreCache\.Default\.Forget\(targetPath\)", Src("Playlist.cs"));
    }

    [Fact] // [EDGE-ai-score-39] [EDGE-ai-score-50]
    public void DialogGate_OneShowAtATime_CatchesCollision()
    {
        string src = Src("ui/MainWindow.cs");
        string gated = Method(src, "internal async Task<ContentDialogResult> ShowGatedAsync");
        Assert.Equal(1, Regex.Matches(gated, @"\.ShowAsync\(\)").Count);
        Assert.Contains("_dialogGate.WaitAsync()", gated);
        Assert.Contains("_dialogGate.Release()", gated);
        Assert.Contains("catch (COMException", gated);
        Assert.Contains("return await ShowGatedAsync(dialog);", Method(Src("ui/MainWindow.cs"), "public async Task<ContentDialogResult> ShowDialogAsync"));
        string optIn = Src("ui/AiScoreOptIn.cs");
        Assert.DoesNotContain("_dialogGate", optIn);
        Assert.DoesNotContain(".ShowAsync(", optIn); // its dialogs go through the gate
        Assert.Matches(@"dialog\.Closing \+= \(_, args\) =>\s*\{\s*if \(closing\) return;\s*args\.Cancel = true;", optIn); // Esc waits too
    }

    [Fact] // service ownership: one instance, disposed on close
    public void ServiceOwnership()
    {
        var news = Directory.GetFiles(Path.Combine(Root(), "ui"), "*.cs")
            .Sum(f => Regex.Matches(File.ReadAllText(f), @"new AiAssetDownloadService\(").Count);
        Assert.Equal(1, news);
        Assert.Contains("new AiAssetDownloadService()", Src("ui/MainWindow.AiScore.cs"));
        Assert.Matches(@"FlushPenumbraMod\(\);\s*DisposeAiScore\(\);", Src("ui/MainWindow.cs"));
        Assert.False(File.Exists(Path.Combine(Root(), "ui", "AiDownloadDialog.xaml")));
    }

    [Fact] // [AC-ai-score-1] [EDGE-ai-score-21] off by default; the sort item starts hidden; settings only ask, the download runs after close
    public void OffByDefault_AndOptInAfterClose()
    {
        Assert.Contains("ParseFlag(Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue(\"AiScoreEnabled\"), false)", Src("Settings.cs"));
        Assert.Matches(@"x:Name=""SortByAiMenuItem""[^>]*Visibility=""Collapsed""", Src("ui/MainWindow.xaml"));
        string ok = Src("ui/SettingsDialog.xaml.cs");
        Assert.Contains("AiScoreOptInRequested = aiWanted && !aiOn;", ok);
        Assert.DoesNotContain("Settings.AiScoreEnabled = true", ok);
        Assert.Equal(2, Regex.Matches(ok, @"await App\.MainWindow\.ApplyAiScoreSettingAsync\(this\);").Count);
        Assert.Contains("await ApplyAiScoreSettingAsync(dialog);", Method(Src("ui/MainWindow.cs"), "private async Task OpenSettingsAsync"));
    }

    [Fact] // [EDGE-ai-score-45] the first 315 MB verification runs off the UI thread at low priority
    public void IsReady_RunsBelowNormal_OffThread()
    {
        foreach (var src in new[] { Src("ui/MainWindow.AiScore.cs"), Src("ui/AiScoreOptIn.cs") })
            Assert.Matches(@"Task\.Run\(\(\) => \{ using \(ThreadPriorityScope\.BelowNormal\(\)\) return \w+\.IsReady\(\); \}\)", src);
    }

    [Fact] // [EDGE-ai-score-32] telemetry off before anything else touches ONNX Runtime; one file owns it
    public void TelemetryDisabledFirst_SingleOwner()
    {
        string model = Src("Tools/SonicsModel.cs");
        int off = model.IndexOf("DisableTelemetryEvents()", StringComparison.Ordinal);
        foreach (var later in new[] { "new SessionOptions", "new InferenceSession", "OrtValue." })
            Assert.True(off > 0 && off < model.IndexOf(later, StringComparison.Ordinal), later);
        var owners = Directory.EnumerateFiles(Root(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(Path.GetRelativePath(Root(), f), @"^(devtools|bin|obj|Releases[^\\/]*)[\\/]", RegexOptions.IgnoreCase))
            .Where(f => File.ReadAllText(f).Contains("Microsoft.ML.OnnxRuntime"))
            .Select(f => Path.GetRelativePath(Root(), f).Replace('\\', '/')).ToList();
        Assert.Equal(new[] { "Tools/SonicsModel.cs" }, owners);
    }

    [Fact] // [AC-ai-score-11] [EDGE-ai-score-32] [EDGE-ai-score-35] no new package; one x64 Windows ML onnxruntime with no uploader or VC++ runtime
    public void Payload_OneX64OnnxRuntime_NoUploaderNoVcRedist()
    {
        string csproj = Src("Pickles Playlist Editor.csproj");
        Assert.DoesNotContain("Microsoft.ML.OnnxRuntime", csproj);
        var ort = Directory.GetFiles(AppContext.BaseDirectory, "onnxruntime.dll", SearchOption.AllDirectories);
        string dll = Assert.Single(ort);
        Assert.Equal(ArchTargetTests.PeKind.NativeX64, ArchTargetTests.Classify(dll));
        string text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(dll)).ToLowerInvariant();
        foreach (var bad in new[] { "winhttp.dll", "msvcp140.dll", "vcruntime140.dll" }) Assert.DoesNotContain(bad, text);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.ML.OnnxRuntime.dll")));
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact] // [AC-ai-score-11]
    public void Notices_OnnxRuntimeWindowsMlTerms_AndSonics()
    {
        string n = Src("THIRD-PARTY-NOTICES.md");
        foreach (var s in new[] { "ONNX Runtime", "1.27.1", "Windows ML", "Microsoft Windows ML Runtime", "licenses/WindowsML-License.txt",
                     "licenses/WindowsML-ThirdPartyNotices.txt", "Microsoft.Windows.AI.MachineLearning", "2.4.89",
                     "SONICS", "awsaf49", "cc49422a98b5b92700876c608d0f9cdf27214a3e", "licenses/SONICS-MIT.txt", "CC BY-NC" })
            Assert.Contains(s, n);
        Assert.DoesNotContain("onnxruntime-MIT.txt", n);
    }

    [Fact] // [AC-ai-score-11]
    public void LicenceFiles_PinnedAndShipped()
    {
        string lic = Path.Combine(Root(), "licenses");
        Assert.Equal(13996, new FileInfo(Path.Combine(lic, "WindowsML-License.txt")).Length);
        Assert.Equal("66395f8cb219087fae2bd025010bd9076b736c14f03b48f20295471c0c376814", Sha(Path.Combine(lic, "WindowsML-License.txt")));
        Assert.Equal(331175, new FileInfo(Path.Combine(lic, "WindowsML-ThirdPartyNotices.txt")).Length);
        Assert.Equal("fb0af774b4d7cffc5b9d046f2aaeade2f37df2f80abf8033c95dfffcc77a8866", Sha(Path.Combine(lic, "WindowsML-ThirdPartyNotices.txt")));
        Assert.Equal("49c5a1b163d7e372b3e97a6ce2a1e3d80bc038f8dd54d516b1c0b1f1423529f9", Sha(Path.Combine(lic, "SONICS-MIT.txt")));
        var pkg = WindowsAppSdkTargetTests.Package("Microsoft.Windows.AI.MachineLearning");
        Assert.Equal(File.ReadAllBytes(Path.Combine(pkg.Dir, "license.txt")), File.ReadAllBytes(Path.Combine(lic, "WindowsML-License.txt")));
        foreach (var f in new[] { "WindowsML-License.txt", "WindowsML-ThirdPartyNotices.txt", "SONICS-MIT.txt" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "licenses", f)), f);
    }

    [Fact] // [AC-ai-score-11]
    public void CheckBuildOutput_ChecksOrtAndLicences()
    {
        string s = Src("devtools/CheckBuildOutput.ps1");
        Assert.Contains("onnxruntime.dll (one copy)", s);
        Assert.Contains("'WindowsML-License.txt', 'WindowsML-ThirdPartyNotices.txt', 'SONICS-MIT.txt'", s);
        Assert.DoesNotContain("msvcp140", s);
    }
}
