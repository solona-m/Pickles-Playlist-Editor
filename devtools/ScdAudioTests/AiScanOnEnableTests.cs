using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pickles_Playlist_Editor;
using Pickles_Playlist_Editor.Tools;
using Xunit;

namespace ScdAudioTests;

public class AiScanOnEnableTests
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

    private static string Pass() => Method(Src("ui/MainWindow.AiScore.cs"), "private async Task RunAiScorePhaseAsync");

    private static int At(string s, string needle)
    {
        int i = s.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + needle);
        return i;
    }

    // ── Gate (behaviour) ────────────────────────────────────────────────────

    [Fact] // [AC-ai-scan-on-enable-8] [EDGE-ai-scan-on-enable-10]
    public void Gate_LibraryWhileBusy_QueuedThenRestartsOnce()
    {
        var gate = new AiPassGate(CancellationToken.None);
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.ComputeStats, out var t));
        Assert.NotEqual(0, t.Id);
        Assert.Equal(AiPassStart.Queued, gate.TryBegin(AiPass.Library, out var q1));
        Assert.Equal(AiPassStart.Queued, gate.TryBegin(AiPass.Library, out var q2));
        Assert.Equal(default, q1);
        Assert.Equal(default, q2);
        Assert.True(gate.End(t, true));
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.Library, out var t2));
        Assert.False(gate.End(t2, true)); // the queue ran once
    }

    [Fact] // [AC-ai-scan-on-enable-8] [EDGE-ai-scan-on-enable-9] [EDGE-ai-scan-on-enable-22]
    public void Gate_ComputeStatsWhileBusy_ToldAndQueued()
    {
        var gate = new AiPassGate(CancellationToken.None);
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.Library, out var t));
        Assert.Equal(AiPassStart.AlreadyRunning, gate.TryBegin(AiPass.ComputeStats, out var told));
        Assert.Equal(default, told);
        Assert.True(gate.IsQueued);
        Assert.True(gate.End(t, true));
    }

    [Fact] // [AC-ai-scan-on-enable-8] [EDGE-ai-scan-on-enable-27]
    public void Gate_ForeignTicketEnd_DoesNotReleaseRunningPass()
    {
        var gate = new AiPassGate(CancellationToken.None);
        gate.TryBegin(AiPass.Library, out var a);
        Assert.Equal(AiPassStart.AlreadyRunning, gate.TryBegin(AiPass.ComputeStats, out var none));

        Assert.False(gate.End(none, true));
        Assert.False(gate.End(new AiPassTicket(a.Id + 99, CancellationToken.None), true));
        Assert.True(gate.IsRunning);
        Assert.True(gate.IsQueued);
        Assert.False(a.Token.IsCancellationRequested);

        Assert.True(gate.End(a, true));
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.Library, out var b));
        Assert.False(gate.End(a, true)); // stale
        Assert.True(gate.IsRunning);
        Assert.False(b.Token.IsCancellationRequested);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact] // [AC-ai-scan-on-enable-6] [EDGE-ai-scan-on-enable-7]
    public void Gate_TurnOff_CancelsAndClearsQueue()
    {
        var gate = new AiPassGate(CancellationToken.None);
        gate.TryBegin(AiPass.ComputeStats, out var t);
        gate.TryBegin(AiPass.Library, out _);
        gate.TurnOff();
        Assert.True(t.Token.IsCancellationRequested);
        Assert.False(gate.IsQueued);
        Assert.False(gate.End(t, true));
    }

    [Fact] // [AC-ai-scan-on-enable-6] [EDGE-ai-scan-on-enable-7]
    public void Gate_EndDisabled_ClearsQueue()
    {
        var gate = new AiPassGate(CancellationToken.None);
        gate.TryBegin(AiPass.ComputeStats, out var t);
        Assert.Equal(AiPassStart.Queued, gate.TryBegin(AiPass.Library, out _));
        Assert.False(gate.End(t, false));
        Assert.False(gate.IsQueued);
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.ComputeStats, out var t2));
        Assert.False(gate.End(t2, true));
    }

    [Fact] // [AC-ai-scan-on-enable-7] [EDGE-ai-scan-on-enable-11]
    public void Gate_EndAfterClose_ClearsQueue()
    {
        using var closing = new CancellationTokenSource();
        var gate = new AiPassGate(closing.Token);
        gate.TryBegin(AiPass.Library, out var t);
        gate.TryBegin(AiPass.Library, out _);
        closing.Cancel();
        Assert.False(gate.End(t, true));
        Assert.False(gate.IsQueued);
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.Library, out var t2));
        Assert.True(t2.Token.IsCancellationRequested);
        Assert.Equal(AiPassStart.Queued, gate.TryBegin(AiPass.Library, out _));
        Assert.False(gate.End(t2, true));
    }

    [Fact] // [EDGE-ai-scan-on-enable-8]
    public void Gate_OffThenOnWhileWindingDown_RestartsOnce()
    {
        var gate = new AiPassGate(CancellationToken.None);
        gate.TryBegin(AiPass.Library, out var t);
        gate.TurnOff();
        Assert.Equal(AiPassStart.Queued, gate.TryBegin(AiPass.Library, out _));
        Assert.True(gate.End(t, true));
        gate.TryBegin(AiPass.Library, out var t2);
        Assert.False(gate.End(t2, true));
    }

    [Fact] // [AC-ai-scan-on-enable-7] [EDGE-ai-scan-on-enable-11]
    public void Gate_Close_CancelsRunningPass()
    {
        using var closing = new CancellationTokenSource();
        var gate = new AiPassGate(closing.Token);
        gate.TryBegin(AiPass.Library, out var t);
        closing.Cancel();
        Assert.True(t.Token.IsCancellationRequested);
        Assert.True(gate.IsRunning);
        gate.End(t, true);
        Assert.False(gate.IsRunning);
    }

    [Fact] // [AC-ai-scan-on-enable-10] [EDGE-ai-scan-on-enable-23]
    public void Gate_CancelRunning_KeepsQueue()
    {
        var gate = new AiPassGate(CancellationToken.None);
        gate.TryBegin(AiPass.Library, out var t);
        gate.CancelRunning();
        Assert.True(t.Token.IsCancellationRequested);
        Assert.Equal(AiPassStart.Queued, gate.TryBegin(AiPass.Library, out _));
        Assert.True(gate.End(t, true));
    }

    [Fact] // [EDGE-ai-scan-on-enable-8] [EDGE-ai-scan-on-enable-11]
    public void Gate_EndDisposes_LaterCallsDoNotThrow()
    {
        var gate = new AiPassGate(CancellationToken.None);
        gate.TryBegin(AiPass.Library, out var t);
        gate.End(t, true);
        Assert.Null(Record.Exception(() => { gate.TurnOff(); gate.CancelRunning(); gate.End(t, true); }));
        Assert.False(gate.IsRunning);
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.Library, out var t2));
        Assert.False(t2.Token.IsCancellationRequested);
        Assert.NotEqual(t.Id, t2.Id);
    }

    public static TheoryData<int, int> AllEndings() // ints: the enums are internal to the app
    {
        var d = new TheoryData<int, int>();
        foreach (var p in Enum.GetValues<AiPass>())
            foreach (var e in Enum.GetValues<AiPassEnd>()) d.Add((int)p, (int)e);
        return d;
    }

    [Theory] // [AC-ai-scan-on-enable-5] [AC-ai-scan-on-enable-6] [AC-ai-scan-on-enable-7] [EDGE-ai-scan-on-enable-3] [EDGE-ai-scan-on-enable-4] [EDGE-ai-scan-on-enable-12] [EDGE-ai-scan-on-enable-13]
    [MemberData(nameof(AllEndings))]
    public void ShowsDialog_Matrix(int passValue, int endValue)
    {
        var (pass, end) = ((AiPass)passValue, (AiPassEnd)endValue);
        bool expected = end is not (AiPassEnd.Cancelled or AiPassEnd.Error)
            && (pass == AiPass.ComputeStats || end == AiPassEnd.RuntimeFailed);
        Assert.Equal(expected, AiPassRules.ShowsDialog(pass, end));
        if (pass == AiPass.Library && end != AiPassEnd.RuntimeFailed) Assert.False(AiPassRules.ShowsDialog(pass, end));
        if (end is AiPassEnd.Cancelled or AiPassEnd.Error) Assert.False(AiPassRules.ShowsDialog(pass, end));
    }

    // ── Library and strings (behaviour) ─────────────────────────────────────

    [Fact] // [AC-ai-scan-on-enable-3] [EDGE-ai-scan-on-enable-5] [EDGE-ai-scan-on-enable-6]
    public void SongTargets_EveryPlaylist_SkipsSongsWithoutScd()
    {
        static Option Song(string name, params (string k, string v)[] files)
        {
            var o = new Option { Name = name };
            foreach (var (k, v) in files) o.Files[k] = v;
            return o;
        }
        var shared1 = Song("A", ("sound/bpmloop.scd", "music/a.scd"));
        var empty = Song("NoFiles");
        var shared2 = Song("A again", ("sound/bpmloop.scd", "music/a.scd"));
        var vfx = Song("Vfx", ("vfx/thing.avfx", "vfx/thing.avfx"), ("chara/x.tex", "tex/x.tex"));
        var p1 = new Playlist { Name = "One", Options = [shared1, empty] };
        var p2 = new Playlist { Name = "Two", Options = [shared2] };
        var p3 = new Playlist { Name = "Vfx", Options = [vfx] };

        var targets = Library.SongTargets([p1, p2, p3]);
        Assert.Equal(new[] { (p1, shared1), (p2, shared2) }, targets.Select(t => (t.playlist, t.option)));
    }

    private static IEnumerable<(string Locale, Dictionary<string, string> Values)> Locales() =>
        Directory.GetDirectories(Path.Combine(RuntimeTargetTests.RepoRoot(), "Strings")).Select(d =>
            (Path.GetFileName(d), XDocument.Load(Path.Combine(d, "Resources.resw")).Root!.Elements("data")
                .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "")));

    // The values before the background scan, which all told the user to run Compute Stats.
    private static readonly Dictionary<string, string[]> OldValues = new()
    {
        ["AiScore_Ready"] =
        [
            "The AI-likelihood score is on. Run Compute Stats on a playlist to score its songs.",
            "Die KI-Wahrscheinlichkeit ist eingeschaltet. Führe „Statistik berechnen“ für eine Playlist aus, um ihre Songs zu bewerten.",
            "La probabilidad de IA está activada. Ejecuta Calcular estadísticas en una lista para puntuar sus canciones.",
            "La probabilité d'IA est activée. Lancez Calculer les statistiques sur une playlist pour noter ses morceaux.",
            "De AI-waarschijnlijkheid staat aan. Voer Statistieken berekenen uit op een afspeellijst om de nummers te scoren.",
            "AI-sannolikheten är på. Kör Beräkna statistik på en spellista för att ge dess låtar poäng.",
            "AI 可能性评分已开启。对播放列表运行“计算统计”即可为其歌曲评分。",
        ],
        ["AiScore_Hint"] =
        [
            "A local machine-learning classifier (SONICS), not generative AI. Your songs never leave this computer. Turning it on downloads about {0} MB once; scores are computed by Compute Stats.",
            "Ein lokaler Machine-Learning-Klassifikator (SONICS), keine generative KI. Deine Songs verlassen diesen Computer nie. Beim Einschalten werden einmalig etwa {0} MB heruntergeladen; die Werte berechnet „Statistik berechnen“.",
            "Un clasificador local de aprendizaje automático (SONICS), no IA generativa. Tus canciones nunca salen de este equipo. Al activarlo se descargan unos {0} MB una sola vez; las puntuaciones se calculan con Calcular estadísticas.",
            "Un classifieur local d'apprentissage automatique (SONICS), pas une IA générative. Vos morceaux ne quittent jamais cet ordinateur. L'activer télécharge une seule fois environ {0} Mo ; les scores sont calculés par Calculer les statistiques.",
            "Een lokale machine-learningclassificatie (SONICS), geen generatieve AI. Je nummers verlaten deze computer nooit. Inschakelen downloadt eenmalig ongeveer {0} MB; de scores worden berekend door Statistieken berekenen.",
            "En lokal maskininlärningsklassificerare (SONICS), inte generativ AI. Dina låtar lämnar aldrig den här datorn. Att slå på den laddar ner ungefär {0} MB en gång; poängen räknas ut av Beräkna statistik.",
            "本地机器学习分类器（SONICS），不是生成式 AI。你的歌曲绝不会离开这台电脑。开启后会一次性下载约 {0} MB；评分由“计算统计”生成。",
        ],
        ["AiScore_AlreadyRunning"] =
        [
            "AI scoring is already running. Run Compute Stats again when it has finished to score these songs.",
            "Die KI-Bewertung läuft bereits. Führe „Statistik berechnen“ erneut aus, wenn sie fertig ist, um diese Songs zu bewerten.",
            "La puntuación de IA ya está en curso. Vuelve a ejecutar Calcular estadísticas cuando termine para puntuar estas canciones.",
            "Le calcul de la probabilité d'IA est déjà en cours. Relancez Calculer les statistiques quand il sera terminé pour noter ces morceaux.",
            "De AI-scoring loopt al. Voer Statistieken berekenen opnieuw uit als die klaar is om deze nummers te scoren.",
            "AI-bedömningen körs redan. Kör Beräkna statistik igen när den är klar för att ge de här låtarna poäng.",
            "AI 评分已在进行中。完成后再次运行“计算统计”即可为这些歌曲评分。",
        ],
    };

    [Fact] // [AC-ai-scan-on-enable-9] [EDGE-ai-scan-on-enable-4]
    public void Strings_DescribeBackgroundScan()
    {
        var locales = Locales().ToList();
        Assert.Equal(7, locales.Count);
        var en = locales.Single(l => l.Locale == "en-US").Values;
        Assert.Contains("background", en["AiScore_Ready"]);
        Assert.Contains("not scored yet", en["AiScore_Ready"]);
        Assert.DoesNotContain("Run Compute Stats on a playlist", en["AiScore_Ready"]);
        foreach (var must in new[] { "background", "machine-learning", "not generative AI", "never leave", "{0}" })
            Assert.Contains(must, en["AiScore_Hint"]);
        Assert.Contains("background", en["AiScore_AlreadyRunning"]);
        Assert.DoesNotContain("Compute Stats", en["AiScore_AlreadyRunning"]);
        foreach (var (loc, values) in locales)
            foreach (var (key, olds) in OldValues)
                Assert.DoesNotContain(values[key], olds);
    }

    // ── Wiring (source level) ───────────────────────────────────────────────

    [Fact] // [AC-ai-scan-on-enable-7] [EDGE-ai-scan-on-enable-11]
    public void Gate_ConstructedInCtor_LinkedToClosing()
    {
        string ai = Src("ui/MainWindow.AiScore.cs");
        Assert.Contains("private readonly AiPassGate _aiGate;", ai);
        string ctor = Method(Src("ui/MainWindow.cs"), "public MainWindow()");
        Assert.True(At(ctor, "_aiGate = new AiPassGate(_closingCts.Token);") < At(ctor, "this.InitializeComponent();"));
        Assert.Contains("_closingCts.Cancel()", Method(ai, "private void DisposeAiScore"));
    }

    [Fact] // [AC-ai-scan-on-enable-1] [EDGE-ai-scan-on-enable-1] [EDGE-ai-scan-on-enable-2]
    public void ApplySetting_MarksScanWanted_OnlyWhenTurnedOn()
    {
        string apply = Method(Src("ui/MainWindow.AiScore.cs"), "internal async Task ApplyAiScoreSettingAsync");
        Assert.True(At(apply, "AiScoreOptIn.RunAsync(") < At(apply, "if (dialog.AiScoreOptInRequested && Settings.AiScoreEnabled) _aiScanWanted = true;"));
        Assert.DoesNotContain("StartLibraryAiScan", apply);
        Assert.DoesNotContain("DispatcherQueuePriority", apply);
    }

    [Fact] // [AC-ai-scan-on-enable-1] [EDGE-ai-scan-on-enable-17] [EDGE-ai-scan-on-enable-18]
    public void ScanStartsAfterReload_OnEveryClosePath()
    {
        string open = Method(Src("ui/MainWindow.cs"), "private async Task OpenSettingsAsync");
        int cont = At(open, "ContinueAiScoreAfterSettings(libraryFresh: true);");
        Assert.True(At(open, "ApplyNamingOptionsAsync()") < cont && At(open, "LoadPlaylists();") < cont);

        string dlg = Src("ui/SettingsDialog.xaml.cs");
        const string call = "App.MainWindow.ContinueAiScoreAfterSettings(libraryFresh: NamingOptionsChanged);";
        var calls = Regex.Matches(dlg, Regex.Escape(call)).Select(m => m.Index).ToList();
        Assert.Equal(2, calls.Count);
        foreach (int c in calls)
        {
            int naming = dlg.LastIndexOf("await App.MainWindow.ApplyNamingOptionsAsync();", c, StringComparison.Ordinal);
            int methodStart = dlg.LastIndexOf("private async", c, StringComparison.Ordinal);
            Assert.True(naming > methodStart, "the scan starts after the naming reload in the same method");
        }
        Assert.Equal(2, Regex.Matches(dlg, Regex.Escape("await App.MainWindow.ApplyAiScoreSettingAsync(this);")).Count);
    }

    [Fact] // [AC-ai-scan-on-enable-1] [AC-ai-scan-on-enable-3] [EDGE-ai-scan-on-enable-16]
    public void StartLibraryAiScan_SnapshotsWholeLibrary()
    {
        string ai = Src("ui/MainWindow.AiScore.cs");
        Assert.Contains("private void StartLibraryAiScan() => _ = RunAiScorePhaseAsync(Library.SongTargets(Playlists.Values), AiPass.Library);", ai);
        string node = Method(Src("ui/MainWindow.ContextMenu.cs"), "private List<(Playlist playlist, Option option)> GetSongTargetsForNode");
        Assert.Contains("return Library.SongTargets(Playlists.Values);", node);
        string pass = Pass();
        Assert.Contains(".Select(AiScoreCache.FullPathOf).Distinct(StringComparer.OrdinalIgnoreCase)", pass);
        Assert.Contains("cache.TryGet(p) is null", pass);
        foreach (Match m in Regex.Matches(pass, @"Task\.Run\(\(\) =>"))
        {
            string body = Method(pass[m.Index..], "Task.Run(");
            Assert.DoesNotContain("Playlists", body);
            Assert.DoesNotContain("GetAll", body);
        }
    }

    [Fact] // [AC-ai-scan-on-enable-10] [EDGE-ai-scan-on-enable-23] [EDGE-ai-scan-on-enable-24]
    public void ModChange_CancelsAndRescans()
    {
        string cont = Method(Src("ui/MainWindow.AiScore.cs"), "internal void ContinueAiScoreAfterSettings");
        int key = At(cont, "AiScoreCache.Key(Utils.PenumbraMeta.ModRoot)");
        int assign = At(cont, "_aiModKey = key;");
        int enabled = At(cont, "if (!Settings.AiScoreEnabled)");
        int cancel = At(cont, "if (modChanged) _aiGate.CancelRunning();");
        int reload = At(cont, "if (modChanged && !libraryFresh) LoadPlaylists();");
        int start = At(cont, "StartLibraryAiScan();");
        Assert.True(key < assign && assign < enabled && enabled < cancel && cancel < reload && reload < start);

        string loaded = Method(Src("ui/MainWindow.cs"), "private void PlaylistTreeView_Loaded");
        Assert.True(At(loaded, "LoadPlaylists();") < At(loaded, "_aiModKey = Utils.AiScoreCache.Key(Utils.PenumbraMeta.ModRoot);"));
        Assert.DoesNotContain("StartLibraryAiScan", loaded);
    }

    [Fact] // [AC-ai-scan-on-enable-2] [EDGE-ai-scan-on-enable-20] [EDGE-ai-scan-on-enable-19]
    public void LibraryScan_Background_LowPriority_NoOverlay()
    {
        string pass = Pass();
        Assert.Matches(@"await Task\.Run\(\(\) =>\s*\{\s*using \(ThreadPriorityScope\.BelowNormal\(\)\)\s*\{[^}]*FullPathOf[^}]*cache\.TryGet\(p\) is null", pass);
        Assert.DoesNotContain("SetProgressBarText", pass);
        Assert.DoesNotContain("SetProgressBarPercent", pass);
        Assert.Single(Regex.Matches(pass, @"\bfor \("));
        Assert.Single(Regex.Matches(pass, @"ScoreSong\("));
        string ready = Method(Src("ui/MainWindow.AiScore.cs"), "private Task<bool> IsAiReadyAsync");
        Assert.Contains("var svc = AiAssetsService;", ready);
        Assert.Contains("Task.Run(() => { using (ThreadPriorityScope.BelowNormal()) return svc.IsReady(); })", ready);
    }

    [Fact] // [AC-ai-scan-on-enable-4] [EDGE-ai-scan-on-enable-14] [EDGE-ai-scan-on-enable-15]
    public void LibraryScan_UsesSharedPass_ChipsAndScoreSong()
    {
        string pass = Pass();
        Assert.Contains("AiPass pass = AiPass.ComputeStats", pass);
        Assert.Contains("% 10 == 0) _uiDispatcherQueue.TryEnqueue(RefreshAiChips)", pass);
        Assert.Matches(@"ClearBackgroundProgress\(\);\s*RefreshAiChips\(\);", pass);
        Assert.Contains("ScoreSong(", pass);
    }

    [Fact] // [AC-ai-scan-on-enable-5] [AC-ai-scan-on-enable-8] [EDGE-ai-scan-on-enable-26] [EDGE-ai-scan-on-enable-27]
    public void Pass_GateAndDialogWiring()
    {
        string pass = Pass();
        int begin = At(pass, "switch (_aiGate.TryBegin(pass, out var ticket))");
        int firstTry = Regex.Match(pass, @"\btry\s*\{").Index;
        Assert.True(begin < firstTry, "only a started pass reaches finally");
        Assert.Matches(@"case AiPassStart\.Queued: return;", pass);
        Assert.Matches(@"case AiPassStart\.AlreadyRunning:\s*await ShowDialogAsync\(AppStrings\.Summary_AiScore, AppStrings\.Get\(""AiScore_AlreadyRunning""\)\);\s*return;", pass);
        Assert.Contains("var ct = ticket.Token;", pass);

        var dialogs = Regex.Matches(pass, @"ShowDialogAsync\(AppStrings\.Summary_AiScore").Select(m => m.Index).ToList();
        Assert.Equal(4, dialogs.Count);
        foreach (int d in dialogs.Skip(1)) // the first is the AlreadyRunning one
        {
            string before = pass[Math.Max(0, d - 120)..d];
            Assert.Contains("if (AiPassRules.ShowsDialog(pass, end))", before);
        }
        Assert.DoesNotContain("new ContentDialog", pass);
        Assert.DoesNotContain(".ShowAsync()", pass);

        Assert.Matches(@"finally\s*\{\s*ClearBackgroundProgress\(\);\s*Logger\.LogInfo\([\s\S]*?\);\s*if \(_aiGate\.End\(ticket, Settings\.AiScoreEnabled\)\) StartLibraryAiScan\(\);", pass);
    }

    [Fact] // [AC-ai-scan-on-enable-6] [AC-ai-scan-on-enable-7] [EDGE-ai-scan-on-enable-7] [EDGE-ai-scan-on-enable-11]
    public void TurnOffAndClose_Wiring()
    {
        string apply = Method(Src("ui/MainWindow.AiScore.cs"), "internal async Task ApplyAiScoreSettingAsync");
        Assert.Contains("if (dialog.AiScoreChanged && !Settings.AiScoreEnabled) { _aiGate.TurnOff(); _aiScanWanted = false; }", apply);
        string pass = Pass();
        Assert.Contains("!ct.IsCancellationRequested", pass);
        Assert.Contains("r.ScoreSong(paths[i], ct);", pass);
        Assert.DoesNotContain("_closingCts", pass); // only through the ticket
        foreach (var bad in new[] { ".Save(", "UpdateName", "option.Name =" }) Assert.DoesNotContain(bad, pass);
    }

    [Fact] // [EDGE-ai-scan-on-enable-13] [EDGE-ai-scan-on-enable-21]
    public void Readiness_NoPromptForLibrary_CancelCheckedEarly()
    {
        string pass = Pass();
        int ready = At(pass, "pass == AiPass.Library ? IsAiReadyAsync() : EnsureAiReadyForStatsAsync()");
        Assert.DoesNotContain("AiScoreOptIn.RunAsync", Method(Src("ui/MainWindow.AiScore.cs"), "private Task<bool> IsAiReadyAsync"));
        int prefilter = At(pass, "cache.TryGet(p) is null");
        int check = pass.IndexOf("if (ct.IsCancellationRequested)", prefilter, StringComparison.Ordinal);
        Assert.True(check > prefilter && check < ready && ready < At(pass, "AiScoreRun.Begin("));
        Assert.Contains("i < paths.Count && !ct.IsCancellationRequested", pass);
    }

    [Fact] // [EDGE-ai-scan-on-enable-25]
    public void CountsLoggedOnEveryEnding()
    {
        string pass = Pass();
        int fin = pass.LastIndexOf("finally", StringComparison.Ordinal);
        Assert.True(fin > pass.LastIndexOf("catch (Exception", StringComparison.Ordinal));
        Assert.Contains("Logger.LogInfo(", pass[fin..]);
        Assert.Contains("catch (OperationCanceledException) { end = AiPassEnd.Cancelled; }", pass);
        Assert.Matches(@"catch \(Exception ex\) \{ end = AiPassEnd\.Error;", pass);
        Assert.Contains("AiPassEnd end = AiPassEnd.Error;", pass[..At(pass, "try")]);
    }
}
