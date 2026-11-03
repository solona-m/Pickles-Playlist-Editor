using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pickles_Playlist_Editor.Tools;
using Xunit;

namespace ScdAudioTests;

public class AiScanOnAddTests
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

    // ── Gate: the same AiPassGate the ai-scan-on-enable tests cover, here in the sequences an import produces ──

    [Fact] // [AC-ai-scan-on-add-5] [EDGE-ai-scan-on-add-2] [EDGE-ai-scan-on-add-3]
    public void Gate_ImportsDuringScan_CoalesceToOneRescan()
    {
        var gate = new AiPassGate(CancellationToken.None);
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.Library, out var t));
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(AiPassStart.Queued, gate.TryBegin(AiPass.Library, out var q));
            Assert.Equal(default, q);
        }
        Assert.True(gate.End(t, true));
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.Library, out var t2));
        Assert.False(gate.End(t2, true)); // exactly one re-scan
    }

    [Fact] // [AC-ai-scan-on-add-5] [EDGE-ai-scan-on-add-4]
    public void Gate_ImportDuringComputeStats_QueuedSilently()
    {
        var gate = new AiPassGate(CancellationToken.None);
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.ComputeStats, out var t));
        Assert.Equal(AiPassStart.Queued, gate.TryBegin(AiPass.Library, out _)); // not AlreadyRunning: no dialog
        Assert.True(gate.End(t, true));
    }

    [Fact] // [EDGE-ai-scan-on-add-10] [AC-ai-scan-on-add-6]
    public void Gate_ImportThenTurnOff_NoRescan()
    {
        var gate = new AiPassGate(CancellationToken.None);
        gate.TryBegin(AiPass.Library, out var t);
        Assert.Equal(AiPassStart.Queued, gate.TryBegin(AiPass.Library, out _));
        gate.TurnOff();
        Assert.True(t.Token.IsCancellationRequested);
        Assert.False(gate.IsQueued);
        Assert.False(gate.End(t, false));
    }

    [Fact] // [EDGE-ai-scan-on-add-9] [AC-ai-scan-on-add-7]
    public void Gate_ImportAfterClose_NeverRestarts()
    {
        using var closing = new CancellationTokenSource();
        var gate = new AiPassGate(closing.Token);
        gate.TryBegin(AiPass.Library, out var t);
        Assert.Equal(AiPassStart.Queued, gate.TryBegin(AiPass.Library, out _));
        closing.Cancel();
        Assert.False(gate.End(t, true));
    }

    [Fact] // [EDGE-ai-scan-on-add-23]
    public void Gate_ComputeStatsAfterImport_ToldAndQueued()
    {
        var gate = new AiPassGate(CancellationToken.None);
        Assert.Equal(AiPassStart.Started, gate.TryBegin(AiPass.Library, out var t)); // the import's pass
        Assert.Equal(AiPassStart.AlreadyRunning, gate.TryBegin(AiPass.ComputeStats, out var told));
        Assert.Equal(default, told);
        Assert.True(gate.IsQueued);
        Assert.True(gate.End(t, true)); // the Compute Stats songs are scored by the re-scan
        string ar = Locales().Single(l => l.Locale == "en-US").Values["AiScore_AlreadyRunning"];
        Assert.Contains("background", ar);
        Assert.Contains("after it", ar);
        Assert.DoesNotContain("Compute Stats", ar);
    }

    // ── Wiring (source level) ───────────────────────────────────────────────

    [Fact] // [AC-ai-scan-on-add-6] [AC-ai-scan-on-add-7] [EDGE-ai-scan-on-add-1] [EDGE-ai-scan-on-add-9]
    public void Hook_ScansOnlyWhenOnAndNotClosing()
    {
        string hook = Method(Src("ui/MainWindow.AiScore.cs"), "internal void RequestAiScanAfterImport");
        int tryAt = Regex.Match(hook, @"\btry\s*\{").Index;
        int guard = At(hook, "if (!Settings.AiScoreEnabled || _closingCts.IsCancellationRequested) return;");
        int log = At(hook, "Logger.LogInfo(");
        int start = At(hook, "StartLibraryAiScan();");
        int catchAt = At(hook, "catch (Exception ex)");
        Assert.True(tryAt > 0 && tryAt < guard && guard < log && log < start && start < catchAt);
        Assert.True(At(hook, "Logger.LogError(", catchAt) > catchAt);
        foreach (var bad in new[] { "LoadPlaylists", "GetAll", "RunAiScorePhaseAsync", "throw" })
            Assert.DoesNotContain(bad, hook);
    }

    [Fact] // [AC-ai-scan-on-add-1] [AC-ai-scan-on-add-4] [EDGE-ai-scan-on-add-5] [EDGE-ai-scan-on-add-6] [EDGE-ai-scan-on-add-11] [EDGE-ai-scan-on-add-16] [EDGE-ai-scan-on-add-21]
    public void FileAddAndDrop_RequestScanAfterImport()
    {
        string src = Src("ui/MainWindow.DragDrop.cs");
        string add = Method(src, "private async Task AddOrInsertFilesToPlaylistAsync");
        Assert.Equal(1, Count(add, "RequestAiScanAfterImport();"));
        int hook = At(add, "RequestAiScanAfterImport();");
        int run = At(add, "await Task.Run(");
        Assert.True(run < hook && At(add, "SyncPlaylistNode(targetPlaylist);") < hook && At(add, "ClearProgressDisplay();") < hook);
        string runBody = Method(add[run..], "await Task.Run(");
        Assert.DoesNotContain("RequestAiScanAfterImport", runBody);
        Assert.DoesNotMatch(@"\b(catch|finally)\b", add);

        string drop = Method(src, "private async Task HandleExternalDropAsync");
        int guard = At(drop, "if (targetPlaylist != null)");
        Assert.True(At(drop, "AddOrInsertFilesToPlaylistAsync(") > guard);
        Assert.Contains("AddOrInsertFilesToPlaylistAsync(", Method(Src("ui/MainWindow.cs"), "private async Task AddSongsFromComputerAsync"));
    }

    [Fact] // [AC-ai-scan-on-add-2] [AC-ai-scan-on-add-4] [EDGE-ai-scan-on-add-5] [EDGE-ai-scan-on-add-6] [EDGE-ai-scan-on-add-15] [EDGE-ai-scan-on-add-16]
    public void LinkDownload_RequestScanAfterImport()
    {
        string yt = Method(Src("ui/MainWindow.YouTube.cs"), "private async void YtDownloadButton_Click");
        int firstTry = Regex.Match(yt, @"\btry\s*\{").Index;
        Assert.True(At(yt, "bool imported = false;") < firstTry);
        int set = At(yt, "imported = true;");
        Assert.True(At(yt, "SetProgressBarPercent(100);") < set && set < At(yt, "catch (Exception"));
        Assert.Equal(1, Count(yt, "imported = true;"));
        int hook = At(yt, "if (imported) RequestAiScanAfterImport();");
        Assert.Equal(1, Count(yt, "RequestAiScanAfterImport();"));
        Assert.True(At(yt, "CleanupYtTempFiles(") < hook && hook < At(yt, "if (errorMessage != null)"));
        string catchBlock = Method(yt[At(yt, "catch (Exception")..], "catch (Exception");
        Assert.DoesNotContain("imported", catchBlock);
    }

    [Fact] // [AC-ai-scan-on-add-3] [EDGE-ai-scan-on-add-5] [EDGE-ai-scan-on-add-6] [EDGE-ai-scan-on-add-7] [EDGE-ai-scan-on-add-16]
    public void NewPlaylist_RequestScanOnlyWithFolder()
    {
        string go = Method(Src("ui/NewPlaylistDialog.xaml.cs"), "private void GoButton_Click");
        int hook = At(go, "if (!string.IsNullOrEmpty(directory)) App.MainWindow.RequestAiScanAfterImport();");
        Assert.True(At(go, "App.MainWindow.ClearProgressDisplay();") < hook && At(go, "App.MainWindow.AddCreatedPlaylist(playlistName);") < hook);
        Assert.True(hook < At(go, "catch (", hook));
        Assert.DoesNotContain("RequestAiScanAfterImport", Method(Src("ui/MainWindowCore.cs"), "public void AddCreatedPlaylist"));
    }

    [Fact] // [AC-ai-scan-on-add-1] [AC-ai-scan-on-add-2] [AC-ai-scan-on-add-3] [EDGE-ai-scan-on-add-22]
    public void EveryImportPath_IsHooked()
    {
        // The model-level backstop: Create, Add and Insert are the only callers of AddFiles.
        Assert.Equal(4, Count(Src("Playlist.cs"), "AddFiles("));

        var methods = new (string File, string Signature)[]
        {
            ("ui/MainWindow.DragDrop.cs", "private async Task AddOrInsertFilesToPlaylistAsync"),
            ("ui/MainWindow.YouTube.cs", "private async void YtDownloadButton_Click"),
            ("ui/NewPlaylistDialog.xaml.cs", "private void GoButton_Click"),
        };
        var spans = methods.Select(m =>
        {
            string src = Src(m.File);
            string body = Method(src, m.Signature);
            int start = src.IndexOf(body, StringComparison.Ordinal);
            return (m.File, Start: start, End: start + body.Length, Body: body);
        }).ToList();

        // Stated limit: a progress-less Playlist.Add/Insert from the UI is not caught here; the AddFiles count above is the backstop.
        var import = new Regex(@"Playlist\.Create\(|\.(Add|Insert)\([^;]*(ReportProgress|SetProgressBarPercent|progress|callback)", RegexOptions.IgnoreCase); // onProgress, reportProgress too
        var hits = new Dictionary<string, int>();
        foreach (var file in Directory.GetFiles(Path.Combine(Root(), "ui"), "*.cs"))
        {
            string rel = "ui/" + Path.GetFileName(file);
            string src = File.ReadAllText(file);
            foreach (Match m in import.Matches(src))
            {
                var span = spans.FirstOrDefault(s => s.File == rel && m.Index >= s.Start && m.Index < s.End);
                Assert.True(span.Body != null, $"import call outside the hooked methods: {rel} at {m.Index}: {m.Value}");
                hits[rel] = hits.GetValueOrDefault(rel) + 1;
            }
        }
        foreach (var s in spans)
        {
            Assert.True(hits.GetValueOrDefault(s.File) > 0, "no import call found in " + s.File);
            int last = import.Matches(s.Body).Last().Index;
            Assert.True(s.Body.IndexOf("RequestAiScanAfterImport()", last, StringComparison.Ordinal) > last, "hook after the import in " + s.File);
        }

        // Non-test sources: every *.cs under the repo root except bin/, obj/ and devtools/.
        var creators = Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root(), f).Replace('\\', '/'))
            .Where(r => !Regex.IsMatch(r, @"(^|/)(bin|obj|devtools)/"))
            .Where(r => Src(r).Contains("Playlist.Create(", StringComparison.Ordinal))
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "ui/MainWindow.YouTube.cs", "ui/NewPlaylistDialog.xaml.cs" }, creators);
    }

    [Fact] // [AC-ai-scan-on-add-6] [EDGE-ai-scan-on-add-17] [EDGE-ai-scan-on-add-18] [EDGE-ai-scan-on-add-20]
    public void NoScanOutsideImports()
    {
        var ui = Directory.GetFiles(Path.Combine(Root(), "ui"), "*.cs").ToDictionary(f => "ui/" + Path.GetFileName(f), File.ReadAllText);
        Assert.Equal(4, ui.Values.Sum(s => Count(s, "RequestAiScanAfterImport(")));

        string ai = ui["ui/MainWindow.AiScore.cs"];
        int callers = ui.Values.Sum(s => Count(s, "StartLibraryAiScan();"));
        Assert.Equal(3, callers);
        Assert.Contains("StartLibraryAiScan();", Method(ai, "internal void ContinueAiScoreAfterSettings"));
        Assert.Contains("StartLibraryAiScan();", Method(ai, "internal void RequestAiScanAfterImport"));
        Assert.Contains("if (_aiGate.End(ticket, Settings.AiScoreEnabled)) StartLibraryAiScan();", Method(ai, "private async Task RunAiScorePhaseAsync"));

        var quiet = new (string File, string Signature)[]
        {
            ("ui/MainWindowCore.cs", "private void LoadPlaylists(string filter, string? forceExpandedPlaylistName)"),
            ("ui/MainWindowCore.cs", "private void SyncPlaylistNode"),
            ("ui/MainWindowCore.cs", "private void AddNewPlaylistNode"),
            ("ui/MainWindowCore.cs", "public void AddCreatedPlaylist"),
            ("ui/MainWindow.cs", "private void PlaylistTreeView_Loaded"),
            ("ui/MainWindow.cs", "private async Task DoMergeAsync"),
            ("ui/MainWindowCore.cs", "private async Task<bool> DoDeleteAsync"),
            ("ui/MainWindow.ContextMenu.cs", "private async void NormalizeAudioMenuItem_Click"),
            ("ui/MainWindow.ContextMenu.cs", "private async Task ApplyEqualizerSettingsToTargetsAsync"),
        };
        foreach (var (file, sig) in quiet)
        {
            string body = Method(ui[file], sig);
            Assert.DoesNotContain("RequestAiScanAfterImport", body);
            Assert.DoesNotContain("StartLibraryAiScan", body);
        }
    }

    [Fact] // [AC-ai-scan-on-add-4] [EDGE-ai-scan-on-add-8] [EDGE-ai-scan-on-add-12] [EDGE-ai-scan-on-add-19]
    public void ImportScan_IsTheCachedSkippingLibraryPass()
    {
        string ai = Src("ui/MainWindow.AiScore.cs");
        Assert.Contains("private void StartLibraryAiScan() => _ = RunAiScorePhaseAsync(Library.SongTargets(Playlists.Values), AiPass.Library);", ai);
        string pass = Method(ai, "private async Task RunAiScorePhaseAsync");
        Assert.Matches(@"Task\.Run\(\(\) =>\s*\{\s*using \(ThreadPriorityScope\.BelowNormal\(\)\)\s*\{[^}]*\.Select\(AiScoreCache\.FullPathOf\)\.Distinct\(StringComparer\.OrdinalIgnoreCase\)[^}]*cache\.TryGet\(p\) is null", pass);

        string pl = Src("Playlist.cs");
        int create = At(pl, "FileMode.CreateNew");
        int forget = At(pl, "AiScoreCache.Default.Forget(targetPath)");
        Assert.True(forget > create);
        Assert.Equal(1, Count(pl, "AiScoreCache.Default.Forget(targetPath)"));
        int reused = pl.LastIndexOf("if (reused)", create, StringComparison.Ordinal);
        Assert.True(reused > 0);
        string reusedBranch = Method(pl[reused..], "if (reused)");
        Assert.DoesNotContain("Forget(", reusedBranch);
    }

    [Fact] // [AC-ai-scan-on-add-1] [EDGE-ai-scan-on-add-13] [EDGE-ai-scan-on-add-14]
    public void ImportScan_DialogPolicyUnchanged()
    {
        foreach (var e in Enum.GetValues<AiPassEnd>())
            Assert.Equal(e == AiPassEnd.RuntimeFailed, AiPassRules.ShowsDialog(AiPass.Library, e));
        Assert.Contains("pass == AiPass.Library ? IsAiReadyAsync() : EnsureAiReadyForStatsAsync()",
            Method(Src("ui/MainWindow.AiScore.cs"), "private async Task RunAiScorePhaseAsync"));
    }

    // ── Strings and README ──────────────────────────────────────────────────

    private static IEnumerable<(string Locale, Dictionary<string, string> Values)> Locales() =>
        Directory.GetDirectories(Path.Combine(Root(), "Strings")).Select(d =>
            (Path.GetFileName(d), XDocument.Load(Path.Combine(d, "Resources.resw")).Root!.Elements("data")
                .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "")));

    private static readonly Dictionary<string, string> ComputeStatsName = new()
    {
        ["en-US"] = "Compute Stats", ["de-DE"] = "Statistik", ["es-ES"] = "Calcular estadísticas", ["fr-FR"] = "Calculer les statistiques",
        ["nl-NL"] = "Statistieken berekenen", ["sv-SE"] = "Beräkna statistik", ["zh-Hans"] = "计算统计",
    };

    // The values that sent added songs to Compute Stats.
    private static readonly string[] OldValues =
    [
        "The AI-likelihood score is on. Any songs in your library that are not scored yet are now scored in the background, at low priority; tags appear as songs are scored. Compute Stats scores songs you add later.",
        "A local machine-learning classifier (SONICS), not generative AI. Your songs never leave this computer. Turning it on downloads about {0} MB once; your library is then scored in the background, and Compute Stats scores songs you add later.",
        "Die KI-Wahrscheinlichkeit ist eingeschaltet. Alle noch nicht bewerteten Songs deiner Bibliothek werden jetzt im Hintergrund mit niedriger Priorität bewertet; die Markierungen erscheinen nach und nach. „Statistik berechnen“ bewertet später hinzugefügte Songs.",
        "Ein lokaler Machine-Learning-Klassifikator (SONICS), keine generative KI. Deine Songs verlassen diesen Computer nie. Beim Einschalten werden einmalig etwa {0} MB heruntergeladen; danach wird deine Bibliothek im Hintergrund bewertet, und „Statistik berechnen“ bewertet später hinzugefügte Songs.",
        "La probabilidad de IA está activada. Las canciones de tu biblioteca que aún no tienen puntuación se puntúan ahora en segundo plano, con prioridad baja; las etiquetas aparecen a medida que se puntúan. Calcular estadísticas puntúa las canciones que añadas más tarde.",
        "Un clasificador local de aprendizaje automático (SONICS), no IA generativa. Tus canciones nunca salen de este equipo. Al activarlo se descargan unos {0} MB una sola vez; después tu biblioteca se puntúa en segundo plano, y Calcular estadísticas puntúa las canciones que añadas más tarde.",
        "La probabilité d'IA est activée. Les morceaux de votre bibliothèque qui ne sont pas encore notés le sont maintenant en arrière-plan, en priorité basse ; les étiquettes apparaissent au fur et à mesure. Calculer les statistiques note les morceaux ajoutés plus tard.",
        "Un classifieur local d'apprentissage automatique (SONICS), pas une IA générative. Vos morceaux ne quittent jamais cet ordinateur. L'activer télécharge une seule fois environ {0} Mo ; votre bibliothèque est ensuite notée en arrière-plan, et Calculer les statistiques note les morceaux ajoutés plus tard.",
        "De AI-waarschijnlijkheid staat aan. Nummers in je bibliotheek die nog geen score hebben, worden nu op de achtergrond met lage prioriteit gescoord; de labels verschijnen zodra ze klaar zijn. Statistieken berekenen scoort nummers die je later toevoegt.",
        "Een lokale machine-learningclassificatie (SONICS), geen generatieve AI. Je nummers verlaten deze computer nooit. Inschakelen downloadt eenmalig ongeveer {0} MB; daarna wordt je bibliotheek op de achtergrond gescoord, en Statistieken berekenen scoort nummers die je later toevoegt.",
        "AI-sannolikheten är på. Låtar i ditt bibliotek som inte har bedömts än bedöms nu i bakgrunden med låg prioritet; etiketterna visas allt eftersom. Beräkna statistik bedömer låtar du lägger till senare.",
        "En lokal maskininlärningsklassificerare (SONICS), inte generativ AI. Dina låtar lämnar aldrig den här datorn. Att slå på den laddar ner ungefär {0} MB en gång; sedan bedöms ditt bibliotek i bakgrunden, och Beräkna statistik bedömer låtar du lägger till senare.",
        "AI 可能性评分已开启。曲库中尚未评分的歌曲现在会在后台以低优先级评分；评分完成后标签会陆续出现。之后添加的歌曲由“计算统计”评分。",
        "本地机器学习分类器（SONICS），不是生成式 AI。你的歌曲绝不会离开这台电脑。开启后会一次性下载约 {0} MB；之后会在后台为你的曲库评分，之后添加的歌曲由“计算统计”评分。",
    ];

    [Fact] // [AC-ai-scan-on-add-8]
    public void Strings_AddedSongsScoredInBackground()
    {
        var locales = Locales().ToList();
        Assert.Equal(7, locales.Count);
        foreach (var (loc, values) in locales)
            foreach (var key in new[] { "AiScore_Ready", "AiScore_Hint" })
            {
                Assert.DoesNotContain(ComputeStatsName[loc], values[key]);
                Assert.DoesNotContain(values[key], OldValues);
            }
        var en = locales.Single(l => l.Locale == "en-US").Values;
        Assert.Contains("Songs you add later", en["AiScore_Ready"]);
        Assert.Contains("songs you add later", en["AiScore_Hint"]);
        Assert.Contains("background", en["AiScore_Hint"]);
    }

    [Fact] // [AC-ai-scan-on-add-8]
    public void Readme_AddedSongsScoredInBackground()
    {
        string readme = Src("README.md");
        Assert.DoesNotContain("for songs added later)", readme);
        string para = readme.Split('\n').Single(l => l.StartsWith("AI-likelihood score", StringComparison.Ordinal));
        Assert.Contains("whenever you add", para);
        Assert.Contains("low priority", para);
    }
}
