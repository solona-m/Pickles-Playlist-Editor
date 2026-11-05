using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pickles_Playlist_Editor;
using Xunit;

namespace ScdAudioTests;

public class AiScoreIntroTests
{
    private static string Src(string rel) => File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), rel));

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

    private static int At(string s, string needle)
    {
        int i = s.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + needle);
        return i;
    }

    private static string Ai() => Src("ui/MainWindow.AiScore.cs");

    private sealed class FakeHost : IAiScoreIntroHost
    {
        public bool AiChosenBefore { get; set; }
        public bool Answered { get; set; }
        public bool Closing { get; set; }
        public AiIntroShown Shown { get; set; } = new(true, true, true);
        public AiOptInOutcome? Outcome { get; set; } = AiOptInOutcome.Enabled;
        public bool EnableThrows { get; set; }
        public bool CloseDuringEnable { get; set; }
        public List<string> Calls { get; } = new();

        public void MarkAnswered() => Calls.Add("Mark");
        public Task<AiIntroShown> ShowAsync() { Calls.Add("Show"); return Task.FromResult(Shown); }
        public Task<AiOptInOutcome?> EnableAsync()
        {
            Calls.Add("Enable");
            if (CloseDuringEnable) Closing = true;
            if (EnableThrows) throw new InvalidOperationException("boom");
            return Task.FromResult(Outcome);
        }
    }

    // ── Pure rules ──────────────────────────────────────────────────────────

    [Fact] // [AC-ai-intro-dialog-1]
    public void ShouldShow_Table()
    {
        Assert.True(AiScoreIntro.ShouldShow(chosenBefore: false, answered: false));
        Assert.False(AiScoreIntro.ShouldShow(true, false));
        Assert.False(AiScoreIntro.ShouldShow(true, true));
        Assert.False(AiScoreIntro.ShouldShow(false, true));
    }

    [Fact] // [AC-ai-intro-dialog-9]
    public void Classify_Table()
    {
        foreach (bool ok in new[] { false, true })
            foreach (bool chk in new[] { false, true })
                Assert.Equal(AiIntroResult.NotShown, AiScoreIntro.Classify(new(false, ok, chk)));
        Assert.Equal(AiIntroResult.Dismissed, AiScoreIntro.Classify(new(true, false, true)));
        Assert.Equal(AiIntroResult.Dismissed, AiScoreIntro.Classify(new(true, false, false)));
        Assert.Equal(AiIntroResult.Accepted, AiScoreIntro.Classify(new(true, true, true)));
        Assert.Equal(AiIntroResult.Declined, AiScoreIntro.Classify(new(true, true, false)));
    }

    [Fact] // [AC-ai-intro-dialog-4] [EDGE-ai-intro-dialog-9] [EDGE-ai-intro-dialog-10] [EDGE-ai-intro-dialog-20]
    public void MarksAfterOptIn_Table()
    {
        Assert.True(AiScoreIntro.MarksAfterOptIn(null, false));
        Assert.False(AiScoreIntro.MarksAfterOptIn(AiOptInOutcome.NotFound, false));
        foreach (var o in Enum.GetValues<AiOptInOutcome>())
        {
            if (o != AiOptInOutcome.NotFound) Assert.True(AiScoreIntro.MarksAfterOptIn(o, false), o.ToString());
            Assert.False(AiScoreIntro.MarksAfterOptIn(o, true), o.ToString());
        }
        Assert.False(AiScoreIntro.MarksAfterOptIn(null, true));
    }

    // ── Flow ────────────────────────────────────────────────────────────────

    [Fact] // [AC-ai-intro-dialog-1]
    public async Task Flow_FreshUser_ShowsDialog()
    {
        var h = new FakeHost { Shown = new(true, false, true) };
        await AiScoreIntro.RunAsync(h);
        Assert.Equal(1, h.Calls.Count(c => c == "Show"));
    }

    [Theory] // [AC-ai-intro-dialog-2] [EDGE-ai-intro-dialog-8]
    [InlineData((int)AiOptInOutcome.Enabled)]
    [InlineData((int)AiOptInOutcome.AlreadyReady)]
    public async Task Flow_OkTicked_RunsEnableThenMarks(int outcome)
    {
        var h = new FakeHost { Outcome = (AiOptInOutcome)outcome };
        Assert.Equal(AiIntroResult.Accepted, await AiScoreIntro.RunAsync(h));
        Assert.Equal(new[] { "Show", "Enable", "Mark" }, h.Calls);
    }

    [Fact] // [AC-ai-intro-dialog-3] [EDGE-ai-intro-dialog-5]
    public async Task Flow_OkUnticked_MarksWithoutEnable()
    {
        var h = new FakeHost { Shown = new(true, true, false) };
        Assert.Equal(AiIntroResult.Declined, await AiScoreIntro.RunAsync(h));
        Assert.Equal(new[] { "Show", "Mark" }, h.Calls);
    }

    [Theory] // [AC-ai-intro-dialog-4] [EDGE-ai-intro-dialog-6] [EDGE-ai-intro-dialog-7]
    [InlineData((int)AiOptInOutcome.Declined)]
    [InlineData((int)AiOptInOutcome.Cancelled)]
    [InlineData((int)AiOptInOutcome.Failed)]
    [InlineData((int)AiOptInOutcome.LowDisk)]
    public async Task Flow_OkTicked_NonNotFoundOutcomes_Mark(int outcome)
    {
        var h = new FakeHost { Outcome = (AiOptInOutcome)outcome };
        await AiScoreIntro.RunAsync(h);
        Assert.Equal(new[] { "Show", "Enable", "Mark" }, h.Calls);
    }

    [Fact] // [AC-ai-intro-dialog-4] [EDGE-ai-intro-dialog-20]
    public async Task Flow_OkTicked_NotFound_DoesNotMark()
    {
        var h = new FakeHost { Outcome = AiOptInOutcome.NotFound };
        Assert.Equal(AiIntroResult.Accepted, await AiScoreIntro.RunAsync(h));
        Assert.Equal(new[] { "Show", "Enable" }, h.Calls);
    }

    [Fact] // [AC-ai-intro-dialog-4] [EDGE-ai-intro-dialog-9]
    public async Task Flow_OkTicked_EnableReturnsNullOrThrows_StillMarks()
    {
        var nul = new FakeHost { Outcome = null };
        await AiScoreIntro.RunAsync(nul);
        Assert.Contains("Mark", nul.Calls);

        var thr = new FakeHost { EnableThrows = true };
        try { await AiScoreIntro.RunAsync(thr); } catch (InvalidOperationException) { }
        Assert.Contains("Mark", thr.Calls);
    }

    [Fact] // [AC-ai-intro-dialog-4] [EDGE-ai-intro-dialog-2]
    public async Task Flow_AlreadyAnswered_Skips()
    {
        var h = new FakeHost { Answered = true };
        Assert.Equal(AiIntroResult.Skipped, await AiScoreIntro.RunAsync(h));
        Assert.Empty(h.Calls);
    }

    [Fact] // [AC-ai-intro-dialog-5] [EDGE-ai-intro-dialog-3]
    public async Task Flow_Dismissed_DoesNotMark()
    {
        var h = new FakeHost { Shown = new(true, false, true) };
        Assert.Equal(AiIntroResult.Dismissed, await AiScoreIntro.RunAsync(h));
        Assert.Equal(new[] { "Show" }, h.Calls);
    }

    [Fact] // [AC-ai-intro-dialog-5] [EDGE-ai-intro-dialog-10]
    public async Task Flow_ClosingDuringEnable_DoesNotMark()
    {
        var h = new FakeHost { CloseDuringEnable = true };
        await AiScoreIntro.RunAsync(h);
        Assert.Equal(new[] { "Show", "Enable" }, h.Calls);
    }

    [Fact] // [AC-ai-intro-dialog-6] [EDGE-ai-intro-dialog-1] [EDGE-ai-intro-dialog-13] [EDGE-ai-intro-dialog-19] [EDGE-ai-intro-dialog-22]
    public async Task Flow_ChosenBefore_SkipsAndBackfillsFlag()
    {
        var h = new FakeHost { AiChosenBefore = true };
        Assert.Equal(AiIntroResult.Skipped, await AiScoreIntro.RunAsync(h));
        Assert.Equal(new[] { "Mark" }, h.Calls);

        var answered = new FakeHost { AiChosenBefore = true, Answered = true };
        Assert.Equal(AiIntroResult.Skipped, await AiScoreIntro.RunAsync(answered));
        Assert.Empty(answered.Calls);
    }

    [Fact] // [AC-ai-intro-dialog-7] [EDGE-ai-intro-dialog-21] [AC-ai-intro-dialog-9] [EDGE-ai-intro-dialog-4]
    public async Task Flow_NotOpened_DoesNotMark()
    {
        var h = new FakeHost { Shown = new(false, false, true) };
        Assert.Equal(AiIntroResult.NotShown, await AiScoreIntro.RunAsync(h));
        Assert.Equal(new[] { "Show" }, h.Calls);
    }

    // ── Dialog and strings ──────────────────────────────────────────────────

    [Fact] // [AC-ai-intro-dialog-1] [AC-ai-intro-dialog-9]
    public void Dialog_Xaml_Shape_Pinned()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = XDocument.Parse(Src("ui/AiScoreIntroDialog.xaml")).Root!;
        Assert.Equal("ContentDialog", root.Name.LocalName);
        Assert.Equal("OK", (string?)root.Attribute("PrimaryButtonText"));
        Assert.Equal("Primary", (string?)root.Attribute("DefaultButton"));
        Assert.Null(root.Attribute("CloseButtonText"));
        Assert.Null(root.Attribute("SecondaryButtonText"));
        var all = root.Descendants().ToList();
        var box = Assert.Single(all, e => e.Name.LocalName == "CheckBox");
        Assert.Equal("AiScoreCheckBox", (string?)box.Attribute(x + "Uid"));
        Assert.Equal("True", (string?)box.Attribute("IsChecked"));
        var hint = Assert.Single(all, e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute(x + "Name") == "HintText");
        Assert.True(all.IndexOf(hint) < all.IndexOf(box));
    }

    [Fact] // [AC-ai-intro-dialog-1] [EDGE-ai-intro-dialog-17]
    public void Dialog_TitleAndHint_ComeFromSettingsStrings()
    {
        string code = Src("ui/AiScoreIntroDialog.xaml.cs");
        Assert.Contains("Title = AppStrings.Summary_AiScore", code);
        Assert.Contains("AppStrings.AiScoreHint(AiScoreText.DownloadMb)", code);
        Assert.Contains("AppStrings.AiScoreHint(AiScoreText.DownloadMb)", Src("ui/SettingsDialog.xaml.cs"));
    }

    [Fact] // [AC-ai-intro-dialog-1] [EDGE-ai-intro-dialog-17]
    public void Resw_TitleEqualsSettingsLabel_InEveryLocale()
    {
        var dirs = Directory.GetDirectories(Path.Combine(RuntimeTargetTests.RepoRoot(), "Strings"));
        Assert.Equal(7, dirs.Length);
        foreach (var dir in dirs)
        {
            var values = XDocument.Load(Path.Combine(dir, "Resources.resw")).Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
            Assert.Equal(values["AiScoreLabel.Text"], values["Summary_AiScore"]);
            Assert.False(string.IsNullOrWhiteSpace(values["AiScore_Hint"]), dir);
            Assert.False(string.IsNullOrWhiteSpace(values["AiScoreCheckBox.Content"]), dir);
        }
    }

    // ── Wiring ──────────────────────────────────────────────────────────────

    [Fact] // [AC-ai-intro-dialog-2]
    public void Wiring_EnableFromIntro_ReusesOptInAndStartsScan()
    {
        string m = Method(Ai(), "private async Task<AiOptInOutcome?> EnableAiScoreFromIntroAsync");
        int optIn = At(m, "await RunAiOptInAsync()");
        int guard = At(m, "Settings.AiScoreEnabled");
        Assert.True(optIn < guard);
        Assert.True(guard < At(m, "RefreshAiScoreUi(reload: true);"));
        Assert.True(At(m, "RefreshAiScoreUi(reload: true);") < At(m, "StartLibraryAiScan();"));
        Assert.Contains("return ", m);
    }

    [Fact] // [AC-ai-intro-dialog-2]
    public void Wiring_RunAiOptInAsync_SharedWithSettings()
    {
        string ai = Ai();
        string m = Method(ai, "private async Task<AiOptInOutcome?> RunAiOptInAsync");
        Assert.True(At(m, "try") < At(m, "AiScoreOptIn.RunAsync(OptInUi(), AiAssetsService, AiScoreOptIn.HasFreeSpace"));
        Assert.Contains("redownload: false", m);
        string catchPart = m[At(m, "catch (Exception")..];
        Assert.Contains("Logger.LogError", catchPart);
        Assert.Contains("return null;", catchPart);
        Assert.Contains("await RunAiOptInAsync()", Method(ai, "internal async Task ApplyAiScoreSettingAsync"));
        Assert.Single(Regex.Matches(ai, @"AiScoreOptIn\.RunAsync\([^;]*redownload: false"));
    }

    [Fact] // [AC-ai-intro-dialog-4] [EDGE-ai-intro-dialog-16]
    public void Wiring_SettingsFlags_Registry()
    {
        string s = Src("Settings.cs");
        string answered = Method(s, "public static bool AiScoreIntroAnswered");
        Assert.Contains("ParseFlag(", answered);
        Assert.Contains("\"AiScoreIntroAnswered\"), false)", answered);
        Assert.Contains("RegistryValueKind.DWord", answered);
        string set = answered[At(answered, "set")..];
        Assert.True(At(set, "try") < At(set, "SetValue(\"AiScoreIntroAnswered\""));
        Assert.Contains("catch", set);
        string chosen = Method(s, "public static bool AiScoreChosenBefore");
        Assert.Contains("GetValue(\"AiScoreEnabled\") != null", chosen);
        Assert.Contains("catch { return true; }", chosen);
    }

    [Fact] // [AC-ai-intro-dialog-6]
    public void Wiring_HostChosenBefore_IsValuePresence()
    {
        string host = Method(Ai(), "private sealed class IntroHost");
        Assert.Contains("AiChosenBefore => Settings.AiScoreChosenBefore", host);
        Assert.DoesNotContain("AiChosenBefore => Settings.AiScoreEnabled", host);
    }

    [Fact] // [AC-ai-intro-dialog-8] [EDGE-ai-intro-dialog-12] [EDGE-ai-intro-dialog-20]
    public void Wiring_SettingsOptIn_MarksAnswered_ExceptNotFound()
    {
        string apply = Method(Ai(), "internal async Task ApplyAiScoreSettingAsync");
        int mark = At(apply, "Settings.AiScoreIntroAnswered = true");
        Assert.True(At(apply, "await RunAiOptInAsync()") < mark);
        string line = apply[apply.LastIndexOf('\n', mark)..mark];
        Assert.Contains("dialog.AiScoreOptInRequested", line);
        Assert.Contains("AiScoreIntro.MarksAfterOptIn(", line);
    }

    [Fact] // [AC-ai-intro-dialog-8]
    public void Wiring_SettingsMark_ReachedOnEveryClosePath()
    {
        Assert.Contains("await ApplyAiScoreSettingAsync(dialog);", Method(Src("ui/MainWindow.cs"), "private async Task OpenSettingsAsync"));
        Assert.Equal(2, Regex.Matches(Src("ui/SettingsDialog.xaml.cs"), Regex.Escape("await App.MainWindow.ApplyAiScoreSettingAsync(this);")).Count);
        Assert.Contains("Settings.AiScoreIntroAnswered = true", Method(Ai(), "internal async Task ApplyAiScoreSettingAsync"));
    }

    [Fact] // [AC-ai-intro-dialog-8] [EDGE-ai-intro-dialog-18]
    public void Wiring_SettingsMark_OnlyOnAiChoice()
    {
        string apply = Method(Ai(), "internal async Task ApplyAiScoreSettingAsync");
        int mark = At(apply, "Settings.AiScoreIntroAnswered = true");
        Assert.StartsWith("if (", apply[apply.LastIndexOf('\n', mark)..mark].Trim());
        Assert.DoesNotContain("AiScoreIntroAnswered", Src("ui/SettingsDialog.xaml.cs"));
    }

    [Fact] // [AC-ai-intro-dialog-7] [EDGE-ai-intro-dialog-11]
    public void Wiring_Boot_FirstRunSettingsThenIntro()
    {
        string win = Src("ui/MainWindow.cs");
        string loaded = Method(win, "private void PlaylistTreeView_Loaded");
        int updates = At(loaded, "_ = CheckForUpdatesAsync();");
        int setup = At(loaded, "_ = SetupThenAiIntroAsync();");
        int intro = At(loaded, "_ = MaybeShowAiScoreIntroAsync();");
        Assert.True(updates < setup && setup < intro);
        Assert.True(At(loaded, "else") > setup);
        Assert.DoesNotContain("_ = OpenSettingsAsync();", loaded);
        string first = Method(win, "private async Task SetupThenAiIntroAsync");
        Assert.True(At(first, "await OpenSettingsAsync()") < At(first, "await MaybeShowAiScoreIntroAsync()"));
        Assert.DoesNotContain("StartLibraryAiScan", loaded);
        Assert.DoesNotContain("StartLibraryAiScan", first);
    }

    [Fact] // [AC-ai-intro-dialog-9] [EDGE-ai-intro-dialog-4] [EDGE-ai-intro-dialog-14]
    public void Wiring_IntroShownThroughGate_OkOnlyFromPrimary()
    {
        string m = Method(Ai(), "private async Task<AiIntroShown> ShowAiScoreIntroDialogAsync");
        Assert.Contains("ShowGatedAsync(", m);
        Assert.DoesNotContain(".ShowAsync()", m);
        Assert.Contains(".Opened +=", m);
        Assert.Contains("== ContentDialogResult.Primary", m);
    }

    [Fact] // [AC-ai-intro-dialog-9]
    public void Wiring_GateNeverHeldAcrossOptIn()
    {
        string ai = Ai();
        foreach (var sig in new[] { "internal async Task MaybeShowAiScoreIntroAsync", "private async Task<AiIntroShown> ShowAiScoreIntroDialogAsync",
                     "private async Task<AiOptInOutcome?> EnableAiScoreFromIntroAsync", "private sealed class IntroHost" })
            Assert.DoesNotContain("_dialogGate", Method(ai, sig));
    }

    [Fact] // [EDGE-ai-intro-dialog-16]
    public void Wiring_EntryPoints_CatchAndLogError()
    {
        foreach (var m in new[] { Method(Ai(), "internal async Task MaybeShowAiScoreIntroAsync"), Method(Src("ui/MainWindow.cs"), "private async Task SetupThenAiIntroAsync") })
        {
            Assert.True(At(m, "try") < At(m, "catch (Exception"));
            Assert.Contains("Logger.LogError", m[m.LastIndexOf("catch (Exception", StringComparison.Ordinal)..]);
        }
    }

    [Fact] // [EDGE-ai-intro-dialog-15]
    public void Wiring_IntroScan_UsesLibraryScanEntryPoint()
    {
        string m = Method(Ai(), "private async Task<AiOptInOutcome?> EnableAiScoreFromIntroAsync");
        Assert.Contains("StartLibraryAiScan();", m);
        Assert.DoesNotContain("RunAiScorePhaseAsync", m);
    }
}
