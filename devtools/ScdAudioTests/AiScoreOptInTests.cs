using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pickles_Playlist_Editor;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using Xunit;

namespace ScdAudioTests;

/// <summary>The opt-in flow against a fake UI and the real download service over a fake HTTP handler.</summary>
public sealed class AiScoreOptInTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "optin-" + Guid.NewGuid().ToString("N"));
    private readonly List<AiAsset> _pins = new();
    private readonly Dictionary<string, byte[]> _bodies = new();
    private int _requests;

    public AiScoreOptInTests()
    {
        foreach (var (name, n) in new[] { ("a.exe", 1), ("m.onnx", 2), ("m.onnx.data", 3) })
        {
            var b = new byte[200_000 + n]; new Random(n).NextBytes(b);
            _bodies[name] = b;
            _pins.Add(new AiAsset(name, "aiscan-v1", b.Length, Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant()));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private AiAssetDownloadService Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null) =>
        new(_root, _pins, tag => [$"https://m.test/{tag}/"], new Handler((req, ct) =>
        {
            Interlocked.Increment(ref _requests);
            return respond?.Invoke(req, ct) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bodies[req.RequestUri!.Segments[^1]]) });
        }), (_, _) => TimeSpan.Zero);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => f(r, ct);
    }

    private sealed class FakeUi : IAiOptInUi
    {
        public bool Confirm = true;
        public AiConsent? Consent;
        public int RequestsWhenConfirmAsked = -1;
        public Func<int>? RequestCount;
        public readonly List<AiOptInReport> Reports = new();
        public Func<CancellationTokenSource, Task>? DuringWork; // e.g. cancel mid-transfer
        public bool WorkFinishedBeforeOutcome = true;
        private Task? _work;

        public Task<bool> ConfirmAsync(AiConsent consent)
        {
            Consent = consent;
            RequestsWhenConfirmAsked = RequestCount?.Invoke() ?? -1;
            return Task.FromResult(Confirm);
        }

        public async Task<AiAssetResult> RunWithProgressAsync(Func<IProgress<AiAssetProgress>, CancellationToken, Task<AiAssetResult>> work)
        {
            using var cts = new CancellationTokenSource();
            var t = work(new Progress<AiAssetProgress>(), cts.Token);
            _work = t;
            if (DuringWork != null) await DuringWork(cts);
            return await t;
        }

        public Task ShowOutcomeAsync(AiOptInReport report)
        {
            if (_work != null && !_work.IsCompleted) WorkFinishedBeforeOutcome = false;
            Reports.Add(report);
            return Task.CompletedTask;
        }
    }

    private bool? _enabled;
    private Task<AiOptInOutcome> Run(FakeUi ui, AiAssetDownloadService svc, bool redownload = false, bool space = true)
    {
        ui.RequestCount = () => _requests;
        return AiScoreOptIn.RunAsync(ui, svc, (_, _) => space, on => _enabled = on, redownload);
    }

    [Fact] // [AC-ai-score-2]
    public async Task Consent_BeforeAnyRequest_StructuredData()
    {
        var ui = new FakeUi();
        using var svc = Service();
        await Run(ui, svc);
        Assert.Equal(0, ui.RequestsWhenConfirmAsked);
        Assert.Equal(AiAssets.TotalBytes, ui.Consent!.Bytes);
        Assert.Equal(Path.GetDirectoryName(svc.PathFor(AiAssets.Model)), ui.Consent.Folder);
        Assert.Equal(new[] { "dl.solona.info", "github.com" }, ui.Consent.Sources);
        Assert.False(ui.Consent.Redownload);
    }

    [Fact] // [AC-ai-score-2]
    public async Task Declined_NoRequest_SettingUntouched()
    {
        var ui = new FakeUi { Confirm = false };
        using var svc = Service();
        Assert.Equal(AiOptInOutcome.Declined, await Run(ui, svc));
        Assert.Equal(0, _requests);
        Assert.Null(_enabled);
    }

    [Fact] // [AC-ai-score-3]
    public async Task Success_Enabled()
    {
        var ui = new FakeUi();
        using var svc = Service();
        Assert.Equal(AiOptInOutcome.Enabled, await Run(ui, svc));
        Assert.True(_enabled);
        Assert.Equal(AiOptInOutcome.Enabled, Assert.Single(ui.Reports).Outcome);
    }

    [Fact] // [AC-ai-score-3] [EDGE-ai-score-17]
    public async Task Cancel_AwaitsTaskThenCancelled()
    {
        var ui = new FakeUi { DuringWork = async cts => { await Task.Delay(50); cts.Cancel(); } };
        using var svc = Service(async (req, ct) =>
        {
            if (req.RequestUri!.Segments[^1] != "a.exe") { await Task.Delay(Timeout.Infinite, ct); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bodies[req.RequestUri.Segments[^1]]) };
        });
        Assert.Equal(AiOptInOutcome.Cancelled, await Run(ui, svc));
        Assert.True(ui.WorkFinishedBeforeOutcome);
        Assert.Null(_enabled);
        Assert.True(File.Exists(svc.PathFor(_pins[0]))); // the finished file is kept
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "aiscan-v1"), "*.part"));
    }

    [Theory] // [AC-ai-score-3] [EDGE-ai-score-18] [EDGE-ai-score-36]
    [InlineData(404, "NotFound", "NotFound")]
    [InlineData(500, "Failed", "Http")]
    public async Task Failure_OutcomeCarriesFailureKind(int status, string outcome, string failure)
    {
        var ui = new FakeUi();
        using var svc = Service((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent([]) }));
        Assert.Equal(outcome, (await Run(ui, svc)).ToString());
        var r = Assert.Single(ui.Reports);
        Assert.Equal(failure, r.Failure.ToString());
        Assert.Null(_enabled);
    }

    [Fact] // [AC-ai-score-3] [EDGE-ai-score-18]
    public async Task ChecksumFailure_ReportsChecksum()
    {
        var ui = new FakeUi();
        using var svc = Service((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[_bodies[req.RequestUri!.Segments[^1]].Length]) }));
        Assert.Equal(AiOptInOutcome.Failed, await Run(ui, svc));
        Assert.Equal(AiAssetFailure.Checksum, Assert.Single(ui.Reports).Failure);
    }

    [Fact] // [AC-ai-score-3] [EDGE-ai-score-19]
    public async Task LowDisk_NoRequest()
    {
        var ui = new FakeUi();
        using var svc = Service();
        Assert.Equal(AiOptInOutcome.LowDisk, await Run(ui, svc, space: false));
        Assert.Equal(0, _requests);
        Assert.True(Assert.Single(ui.Reports).BytesNeeded > AiAssets.TotalBytes);
    }

    [Fact] // [EDGE-ai-score-20]
    public async Task AlreadyReady_NoConsent_Enabled()
    {
        using (var svc = Service()) await Run(new FakeUi(), svc);
        _enabled = null;
        var ui = new FakeUi();
        using var svc2 = Service();
        int before = _requests;
        Assert.Equal(AiOptInOutcome.AlreadyReady, await Run(ui, svc2));
        Assert.Null(ui.Consent);
        Assert.Equal(before, _requests);
        Assert.True(_enabled);
    }

    [Fact] // [AC-ai-score-10] [EDGE-ai-score-15]
    public async Task Redownload_FlagSet_DeclineLeavesSettingAlone()
    {
        var ui = new FakeUi { Confirm = false };
        using var svc = Service();
        Assert.Equal(AiOptInOutcome.Declined, await Run(ui, svc, redownload: true));
        Assert.True(ui.Consent!.Redownload);
        Assert.Null(_enabled); // not turned off: the setting stays on
    }

    [Fact] // [EDGE-ai-score-40]
    public void ThrottledProgress_100ms_FinalAlwaysDelivered()
    {
        long now = 0;
        var got = new List<int>();
        var p = new ThrottledProgress<int>(new SyncProgress<int>(got.Add), v => v == 999, TimeSpan.FromMilliseconds(100), () => now);
        for (int i = 0; i < 1000; i++) { now = i / 20; p.Report(i); } // 1000 reports in 50 ms
        Assert.InRange(got.Count, 1, 2);
        Assert.Equal(999, got[^1]);
        now = 1000; p.Report(5);
        Assert.Equal(5, got[^1]);
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }

    // ---------- strings ----------

    private static IEnumerable<(string Locale, Dictionary<string, string> Values)> Locales() =>
        Directory.GetDirectories(Path.Combine(RuntimeTargetTests.RepoRoot(), "Strings")).Select(d =>
            (Path.GetFileName(d), XDocument.Load(Path.Combine(d, "Resources.resw")).Root!.Elements("data")
                .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "")));

    private static IEnumerable<string> AllAiKeys()
    {
        foreach (var outcome in Enum.GetValues<AiOptInOutcome>())
            foreach (var failure in Enum.GetValues<AiAssetFailure>())
                if (AiScoreText.FailureKey(outcome, failure) is string k) yield return k;
        foreach (var band in Enum.GetValues<AiBand>()) yield return AiScoreText.BandKey(band);
        string src = File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), "ui", "AppStrings.cs"));
        foreach (Match m in Regex.Matches(src, @"GetString\(""((?:AiScore|Prog_AiScoring|Summary_AiScore)[^""]*)""\)")) yield return m.Groups[1].Value;
        yield return "SortByAiMenuItem.Text"; yield return "AiScoreLabel.Text"; yield return "AiScoreCheckBox.Content";
        // Keys fetched by name through AppStrings.Get, anywhere in the UI.
        foreach (var f in Directory.GetFiles(Path.Combine(RuntimeTargetTests.RepoRoot(), "ui"), "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"AppStrings\.Get\(""([^""]+)""\)")) yield return m.Groups[1].Value;
    }

    [Fact] // [AC-ai-score-2] [AC-ai-score-3] [EDGE-ai-score-34]
    public void AiScoreText_EveryKeyExistsInAllLocales_SamePlaceholders()
    {
        var locales = Locales().ToList();
        Assert.Equal(7, locales.Count);
        var en = locales.Single(l => l.Locale == "en-US").Values;
        foreach (var key in AllAiKeys().Distinct())
        {
            Assert.True(en.ContainsKey(key), "en-US lacks " + key);
            var holders = Regex.Matches(en[key], @"\{\d\}").Select(m => m.Value).OrderBy(x => x).ToList();
            foreach (var (loc, values) in locales)
            {
                Assert.True(values.TryGetValue(key, out var v) && v.Length > 0, $"{loc} lacks {key}");
                Assert.Equal(holders, Regex.Matches(v!, @"\{\d\}").Select(m => m.Value).OrderBy(x => x).ToList());
            }
        }
        Assert.Equal(new[] { "{0}", "{1}", "{2}" }, Regex.Matches(en["AiScore_Consent_Content"], @"\{\d\}").Select(m => m.Value).OrderBy(x => x));
    }

    [Fact] // [AC-ai-score-2] [AC-ai-score-5] framing: a local classifier, an estimate, never an accusation
    public void EnglishFraming()
    {
        var en = Locales().Single(l => l.Locale == "en-US").Values;
        foreach (var key in new[] { "AiScore_Hint", "AiScore_Consent_Content" })
        {
            Assert.Contains("machine-learning", en[key]);
            Assert.Contains("not generative AI", en[key]);
            Assert.Contains("never leave", en[key]);
        }
        foreach (var key in new[] { "AiScore_Band_Ai", "AiScore_Band_Unclear", "AiScore_Band_Human", "AiScore_Tooltip", "AiScore_Summary" })
            foreach (var bad in new[] { "fake", "detected", "caught", "cheat", "proof that" })
                Assert.DoesNotContain(bad, en[key], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("estimate, not proof", en["AiScore_Tooltip"], StringComparison.OrdinalIgnoreCase);
    }
}
