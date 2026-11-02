using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor;
using Pickles_Playlist_Editor.Tools;
using Xunit;
using M = Pickles_Playlist_Editor.Tools.YouTubeFallbackMatcher;

namespace ScdAudioTests;

public class YouTubeFallbackTests : IDisposable
{
    private static string RepoRoot() => RuntimeTargetTests.RepoRoot();
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "yt-fallback", name));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pickles-ytfb-test-" + Guid.NewGuid().ToString("N"));
    public YouTubeFallbackTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    // The user's log, verbatim.
    private const string LogStderr =
        "ERROR: [soundcloud] 2268015032: This video is DRM protected\n" +
        "ERROR: [soundcloud] 2269478501: This video is DRM protected\n" +
        "ERROR: [soundcloud] 2249966423: This video is DRM protected\n" +
        "ERROR: [soundcloud] 2262983984: This video is DRM protected\n" +
        "ERROR: [soundcloud] 2269846133: This video is DRM protected\n" +
        "ERROR: [soundcloud] 2295265136: This video is DRM protected\n" +
        "ERROR: [soundcloud] 2243812046: This video is DRM protected\n" +
        "ERROR: [soundcloud] 2299701707: This video is DRM protected";

    private static ScTrackInfo Track(string title, string? artist, double seconds) => new("1", title, artist, seconds);
    private static YtCandidate Cand(string title, string? channel, double? seconds, int rank = 1, string id = "AAAAAAAAAAA", string? live = null) =>
        new(id, title, channel, seconds, live, rank);

    private static string ProbeJson(string title, string artist, double seconds) =>
        new JObject { ["id"] = "x", ["title"] = title, ["artist"] = artist, ["duration"] = seconds }.ToString();

    private static string SearchJson(params (string id, string title, string channel, double duration)[] entries) =>
        new JObject
        {
            ["entries"] = new JArray(entries.Select(e => new JObject
            { ["id"] = e.id, ["title"] = e.title, ["channel"] = e.channel, ["duration"] = e.duration }))
        }.ToString();

    private sealed class FakeYtDlp : IFallbackYtDlp
    {
        public List<string> Probes = new(), Searches = new(), Downloads = new();
        public Func<string, string> Probe = _ => Fixture("sc-probe-drm.json");
        public Func<string, string> Search = _ => Fixture("yt-search.json");
        public Func<string, string, Action<YtDlpProgressInfo>?, string?>? Download;

        Task<string> IFallbackYtDlp.ProbeSoundCloudTrackAsync(string url) { Probes.Add(url); return Task.FromResult(Probe(url)); }
        Task<string> IFallbackYtDlp.SearchYouTubeAsync(string query) { Searches.Add(query); return Task.FromResult(Search(query)); }

        Task<string?> IFallbackYtDlp.DownloadYouTubeAudioAsync(string youtubeUrl, string dir, Action<YtDlpProgressInfo>? onProgress)
        {
            Downloads.Add(youtubeUrl);
            if (Download != null) return Task.FromResult(Download(youtubeUrl, dir, onProgress));
            Directory.CreateDirectory(dir);
            onProgress?.Invoke(new YtDlpProgressInfo { Stage = "Downloading", Current = 1, Total = 1 });
            string file = Path.Combine(dir, "yt.ogg");
            File.WriteAllText(file, "ogg");
            return Task.FromResult<string?>(file);
        }
    }

    private static List<ProbeEntry> Entries(params string[] ids) =>
        ids.Select((id, i) => new ProbeEntry(id, $"https://api.soundcloud.com/tracks/{id}", i + 1)).ToList();

    private static string Drm(params string[] ids) =>
        string.Join("\n", ids.Select(id => $"ERROR: [soundcloud] {id}: This video is DRM protected"));

    private Task<List<FallbackTrackResult>> Run(FakeYtDlp fake, string stderr, IReadOnlyList<ProbeEntry> entries,
        bool numbered = true, MediaService service = MediaService.SoundCloud, SoundCloudLinkKind kind = SoundCloudLinkKind.Set,
        Action<YtDlpProgressInfo>? progress = null) =>
        YtDlpService.ApplyDrmFallbackAsync(service, kind, stderr, entries, _dir, numbered, fake, progress);

    // ---- trigger and parsing ----

    [Fact] // [AC-sc-youtube-fallback-1]
    public void ParseDrmTrackIds_ReadsLogLines() =>
        Assert.Equal(new[] { "2268015032", "2269478501", "2249966423", "2262983984", "2269846133", "2295265136", "2243812046", "2299701707" },
            M.ParseDrmTrackIds(LogStderr));

    [Fact] // [EDGE-sc-youtube-fallback-20]
    public void ParseDrmTrackIds_Distinct() => Assert.Single(M.ParseDrmTrackIds(Drm("5", "5")));

    [Fact] // [EDGE-sc-youtube-fallback-30]
    public void ParseDrmTrackIds_IgnoresOtherProtectionLines()
    {
        Assert.Empty(M.ParseDrmTrackIds(
            "ERROR: [soundcloud] 1: This track is only available for registered users\n" +
            "ERROR: [soundcloud] 2: This track is not available for this client\n" +
            "WARNING: [soundcloud] 3: This video is DRM protected"));
    }

    [Fact] // [AC-sc-youtube-fallback-8] [EDGE-sc-youtube-fallback-29]
    public async Task ClassifyProbe_And_UnsupportedKindNoOp()
    {
        Assert.Equal(SoundCloudLinkKind.Track, M.ClassifyProbe(JObject.Parse(Fixture("sc-probe-drm.json"))));
        Assert.Equal(SoundCloudLinkKind.Set, M.ClassifyProbe(JObject.Parse(Fixture("sc-set-flat.json"))));
        Assert.Equal(SoundCloudLinkKind.Unsupported, M.ClassifyProbe(new JObject { ["extractor_key"] = "SoundcloudUser" }));
        Assert.Equal(SoundCloudLinkKind.Unsupported, M.ClassifyProbe(new JObject()));

        var fake = new FakeYtDlp();
        Assert.Empty(await Run(fake, Drm("1"), Entries("1"), kind: SoundCloudLinkKind.Unsupported));
        Assert.Empty(fake.Probes);
    }

    [Fact] // [EDGE-sc-youtube-fallback-4]
    public void ParseProbeEntries_SingleTrack_UnnumberedName()
    {
        var entries = M.ParseProbeEntries(JObject.Parse(Fixture("sc-probe-drm.json")), "https://soundcloud.com/brianzeno-music/tonight");
        Assert.Equal(new ProbeEntry("2268015032", "https://soundcloud.com/brianzeno-music/tonight", 1), Assert.Single(entries));
    }

    [Fact] // [EDGE-sc-youtube-fallback-4]
    public async Task SingleTrack_FileHasNoPrefix()
    {
        var fake = new FakeYtDlp();
        var result = await Run(fake, Drm("1"), Entries("1"), numbered: false, kind: SoundCloudLinkKind.Track);
        Assert.True(Assert.Single(result).Substituted);
        Assert.True(File.Exists(Path.Combine(_dir, "Tonight.ogg")));
    }

    [Fact] // [EDGE-sc-youtube-fallback-5]
    public async Task SetCappedToFirst_UnnumberedIndex1()
    {
        var set = M.ParseProbeEntries(JObject.Parse(Fixture("sc-set-flat.json")), "https://soundcloud.com/a/sets/b");
        Assert.Equal(3, set.Count);
        var result = await Run(new FakeYtDlp(), Drm(set[0].Id), set, numbered: false);
        Assert.True(Assert.Single(result).Substituted);
        Assert.Equal(new[] { "Tonight.ogg" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    // ---- matcher ----

    [Fact] // [AC-sc-youtube-fallback-3]
    public void SelectCandidate_SpikeFixturePicksOfficialUpload()
    {
        var info = M.ParseTrackInfo(Fixture("sc-probe-drm.json"))!;
        Assert.Equal("Brian Zeno", info.Artist);
        var all = M.ParseSearchResults(Fixture("yt-search.json"));
        Assert.Equal(5, all.Count);
        Assert.Equal("MOWTfwrX48Y", M.SelectCandidate(info, all)!.Id);

        var parts = M.ParseScTitle(info.Title, info.Artist);
        Assert.Equal("tonight", M.CandidateCore(all[0].Title, parts));
        Assert.Equal("tonight", M.CandidateCore(all[2].Title, parts));

        Assert.Equal("Ky3sRraw9xU", M.SelectCandidate(info, all.Where(c => c.Id != "MOWTfwrX48Y").ToList())!.Id);
        Assert.Null(M.SelectCandidate(info, all.Where(c => c.Id is "6Zk32nJRdU0" or "O9cNEMWna7A" or "7OSKIWi98DA").ToList()));
    }

    [Fact] // [AC-sc-youtube-fallback-3] [EDGE-sc-youtube-fallback-12]
    public void Tolerance_And_DurationMatch()
    {
        Assert.Equal(6.99, M.Tolerance(139.888), 2);
        Assert.True(M.IsDurationMatch(180, 188));
        Assert.False(M.IsDurationMatch(180, 190));
        Assert.True(M.IsDurationMatch(60, 64.9));
        Assert.True(M.IsDurationMatch(600, 614));
        Assert.False(M.IsDurationMatch(600, 616));
        Assert.False(M.IsDurationMatch(180, 36000));
        Assert.False(M.IsDurationMatch(180, null));
    }

    [Fact] // [AC-sc-youtube-fallback-3] [EDGE-sc-youtube-fallback-26]
    public void SelectCandidate_RequiresArtistInChannelOrTitle()
    {
        var t = Track("Song", "Artist", 200);
        Assert.Null(M.SelectCandidate(t, new[] { Cand("Song", "Someone Else", 200) }));
        Assert.NotNull(M.SelectCandidate(t, new[] { Cand("Song", "Artist", 200) }));
        Assert.NotNull(M.SelectCandidate(t, new[] { Cand("Artist - Song", "Label", 200) }));
    }

    [Fact] // [EDGE-sc-youtube-fallback-34]
    public void SelectCandidate_SplitsXDashY()
    {
        var label = Track("Brian Zeno - Tonight", "Gem.", 140);
        Assert.NotNull(M.SelectCandidate(label, new[] { Cand("Tonight", "Brian Zeno - Topic", 140) }));
        Assert.Null(M.SelectCandidate(label, new[] { Cand("Tonight", "Other", 140) }));

        var partOne = Track("Part One - Song", "Artist", 200);
        Assert.Null(M.SelectCandidate(partOne, new[] { Cand("Song", "Artist", 200) }));
        Assert.NotNull(M.SelectCandidate(partOne, new[] { Cand("Part One - Song", "Artist", 200) }));
    }

    [Fact] // [EDGE-sc-youtube-fallback-36]
    public void SelectCandidate_VersionOnlySuffixIsNotASplit()
    {
        var t = Track("Song - Original Mix", "Artist", 300);
        Assert.Null(M.SelectCandidate(t, new[] { Cand("Other Song (Original Mix)", "Artist", 300) }));
        Assert.NotNull(M.SelectCandidate(t, new[] { Cand("Song (Original Mix)", "Artist", 301) }));
        Assert.Null(M.SelectCandidate(t, new[] { Cand("Song", "Artist", 300) }));

        var p = M.ParseScTitle("Song - Original Mix", "Artist");
        Assert.Equal("song", p.Song);
        Assert.Equal(new[] { "original", "mix" }, p.VersionTokens);
        Assert.Null(p.RequiredSequence);

        var r = M.ParseScTitle("Song - 2019 Remaster", "Artist");
        Assert.Equal("song", r.Song);
        Assert.Equal(new[] { "2019", "remaster" }, r.VersionTokens);
    }

    [Fact] // [EDGE-sc-youtube-fallback-35]
    public void SelectCandidate_VersionBracketsRequired()
    {
        var vip = Track("Song (Artist VIP)", "Artist", 240);
        Assert.Null(M.SelectCandidate(vip, new[] { Cand("Song", "Artist", 240) }));
        Assert.NotNull(M.SelectCandidate(vip, new[] { Cand("Song (Artist VIP)", "Artist", 240) }));

        var ext = Track("Song (Extended Mix)", "Artist", 400);
        Assert.Null(M.SelectCandidate(ext, new[] { Cand("Song", "Artist", 400) }));
        Assert.NotNull(M.SelectCandidate(ext, new[] { Cand("Song (Extended Mix)", "Artist", 400) }));
    }

    [Fact] // [EDGE-sc-youtube-fallback-10]
    public void SelectCandidate_MarkerWords()
    {
        var song = Track("Song", "Artist", 200);
        foreach (var bad in new[] { "Song (Live)", "Song cover", "Song 10 hours", "Song (Sped Up)" })
            Assert.Null(M.SelectCandidate(song, new[] { Cand(bad, "Artist", 200) }));
        Assert.NotNull(M.SelectCandidate(Track("Song (Remix)", "Artist", 200), new[] { Cand("Song (Remix)", "Artist", 200) }));
    }

    [Fact] // [EDGE-sc-youtube-fallback-11]
    public void SelectCandidate_RejectsLiveOrNoDuration()
    {
        var t = Track("Song", "Artist", 200);
        Assert.Null(M.SelectCandidate(t, new[] { Cand("Song", "Artist", 200, live: "is_live") }));
        Assert.Null(M.SelectCandidate(t, new[] { Cand("Song", "Artist", 200, live: "was_live") }));
        Assert.Null(M.SelectCandidate(t, new[] { Cand("Song", "Artist", null) }));
        Assert.NotNull(M.SelectCandidate(t, new[] { Cand("Song", "Artist", 200, live: "not_live") }));
    }

    [Fact] // [EDGE-sc-youtube-fallback-13]
    public void SelectCandidate_TieGoesToTopicThenRank()
    {
        var t = Track("Song", "Artist", 200);
        var picked = M.SelectCandidate(t, new[]
        {
            Cand("Artist - Song", "Label", 200.2, rank: 1, id: "AAAAAAAAAA1"),
            Cand("Song", "Artist - Topic", 200.5, rank: 2, id: "AAAAAAAAAA2"),
        });
        Assert.Equal("AAAAAAAAAA2", picked!.Id);

        var byRank = M.SelectCandidate(t, new[]
        {
            Cand("Artist - Song", "Label A", 200.4, rank: 1, id: "AAAAAAAAAA1"),
            Cand("Artist - Song", "Label B", 200.1, rank: 2, id: "AAAAAAAAAA2"),
        });
        Assert.Equal("AAAAAAAAAA1", byRank!.Id);

        var closer = M.SelectCandidate(t, new[]
        {
            Cand("Song", "Artist - Topic", 203, rank: 1, id: "AAAAAAAAAA1"),
            Cand("Artist - Song", "Label", 200, rank: 2, id: "AAAAAAAAAA2"),
        });
        Assert.Equal("AAAAAAAAAA2", closer!.Id);
    }

    [Fact] // [EDGE-sc-youtube-fallback-22]
    public void SelectCandidate_CjkAndAccents()
    {
        Assert.NotNull(M.SelectCandidate(Track("夜に駆ける", "YOASOBI", 260), new[] { Cand("YOASOBI「夜に駆ける」Official Music Video", "Ayase / YOASOBI", 261) }));
        Assert.NotNull(M.SelectCandidate(Track("Café", "Artist", 200), new[] { Cand("Cafe", "Artist", 200) }));
        Assert.Null(M.SelectCandidate(Track("Run", "Artist", 200), new[] { Cand("Running Up", "Artist", 200) }));
        // Glued CJK and Latin is one token, so the song rule alone rejects it (D25), artist present.
        Assert.Null(M.SelectCandidate(Track("夜に駆ける", "YOASOBI", 260), new[] { Cand("YOASOBI夜に駆ける", "YOASOBI", 261) }));
    }

    [Fact] // [EDGE-sc-youtube-fallback-34]
    public void SelectCandidate_XIsTheArtist()
    {
        var t = Track("Artist - Song", "Artist", 200);
        Assert.Null(M.ParseScTitle(t.Title, t.Artist).RequiredSequence);
        Assert.NotNull(M.SelectCandidate(t, new[] { Cand("Song", "Artist", 200) }));
        Assert.Null(M.SelectCandidate(t, new[] { Cand("Song", "Other", 200) }));
    }

    [Fact] // [AC-sc-youtube-fallback-3] [EDGE-sc-youtube-fallback-35]
    public void SelectCandidate_CandidateVersionBracketsMustBeInScTitle()
    {
        var song = Track("Song", "Artist", 200);
        foreach (var bad in new[] { "Song (VIP)", "Song (Acoustic Version)", "Song (Acapella)", "Song [Bootleg]" })
            Assert.Null(M.SelectCandidate(song, new[] { Cand(bad, "Artist", 200) }));
        foreach (var good in new[] { "Song (Radio Edit)", "Song (Official Video)", "Song (Original Mix)", "Song [2019 Remaster]" })
            Assert.NotNull(M.SelectCandidate(song, new[] { Cand(good, "Artist", 200) }));
    }

    [Fact] // [EDGE-sc-youtube-fallback-35]
    public void ScTitle_VersionWordsWinOverNoise()
    {
        var p = M.ParseScTitle("Song (Official Remix)", "Artist");
        Assert.Contains("remix", p.VersionTokens);
        Assert.DoesNotContain("official", p.VersionTokens);
        Assert.Null(M.SelectCandidate(Track("Song (Official Remix)", "Artist", 200), new[] { Cand("Song", "Artist", 200) }));
    }

    [Fact] // [EDGE-sc-youtube-fallback-34]
    public void ScTitle_FeatTailIsNotPartOfTheSong()
    {
        var t = Track("Artist - Song ft. Other", "Artist", 200);
        Assert.Equal("song", M.ParseScTitle(t.Title, t.Artist).Song);
        Assert.NotNull(M.SelectCandidate(t, new[] { Cand("Song (feat. Other)", "Artist", 200) }));
        Assert.NotNull(M.SelectCandidate(t, new[] { Cand("Artist - Song ft. Other", "Label", 200) }));
        Assert.Equal("Artist Song", M.BuildSearchQuery(t));
    }

    [Fact] // [AC-sc-youtube-fallback-3]
    public void SelectCandidate_SelfTitledAndArtistInSong()
    {
        Assert.NotNull(M.SelectCandidate(Track("Tonight", "Tonight", 200), new[] { Cand("Tonight", "Tonight", 200) }));
        Assert.NotNull(M.SelectCandidate(Track("Killer Queen", "Queen", 180), new[] { Cand("Killer Queen", "Queen Official", 181) }));
    }

    [Fact] // [EDGE-sc-youtube-fallback-23]
    public void BuildSearchQuery_StripsNoiseAndDuplicateArtist()
    {
        Assert.Equal("Brian Zeno Tonight", M.BuildSearchQuery(M.ParseTrackInfo(Fixture("sc-probe-drm.json"))!));
        Assert.Equal("Artist Song", M.BuildSearchQuery(Track("Artist - Song [FREE DL]", "Artist", 200)));
    }

    [Theory] // [EDGE-sc-youtube-fallback-14]
    [InlineData("Song \" --exec calc")]
    [InlineData("-Song \\ \u0007 tab\there")]
    [InlineData("\"\"\"\\\\")]
    public void BuildSearchQuery_Hostile(string title)
    {
        string q = M.BuildSearchQuery(Track(title + new string('x', 200), "-Artist\"", 200));
        Assert.DoesNotContain('"', q);
        Assert.DoesNotContain('\\', q);
        Assert.DoesNotContain(q, char.IsControl);
        Assert.True(q.Length <= 120);
        Assert.False(q.StartsWith("-"));
        string args = YtDlpService.BuildFallbackSearchArgs(q, "");
        Assert.True(args.IndexOf("-- \"ytsearch5:", StringComparison.Ordinal) > 0);
    }

    [Fact] // [EDGE-sc-youtube-fallback-15]
    public void IsValidYouTubeId_And_SelectSkipsBadIds()
    {
        Assert.True(M.IsValidYouTubeId("MOWTfwrX48Y"));
        Assert.False(M.IsValidYouTubeId("MOWTfwrX48Y\" --x"));
        Assert.False(M.IsValidYouTubeId("MOWTfwrX48Y\n"));
        Assert.False(M.IsValidYouTubeId("short"));
        Assert.Null(M.SelectCandidate(Track("Song", "Artist", 200), new[] { Cand("Song", "Artist", 200, id: "bad id here") }));
    }

    [Fact] // [EDGE-sc-youtube-fallback-17]
    public async Task SafeFileName_InvalidLongEmpty_And_Collision()
    {
        Assert.Equal("a_b_c", M.SafeFileName("a/b:c", "x"));
        Assert.Equal(120, M.SafeFileName(new string('a', 300), "x").Length);
        Assert.Equal("Fallback", M.SafeFileName("???", "Fallback"));

        File.WriteAllText(Path.Combine(_dir, "Tonight.ogg"), "existing");
        var result = await Run(new FakeYtDlp(), Drm("1"), Entries("1"), numbered: false, kind: SoundCloudLinkKind.Track);
        Assert.True(Assert.Single(result).Substituted);
        Assert.True(File.Exists(Path.Combine(_dir, "Tonight (2).ogg")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_dir, "Tonight.ogg")));
    }

    // ---- orchestration ----

    [Fact] // [AC-sc-youtube-fallback-1]
    public async Task ApplyFallback_SubstitutesMatchedTracks()
    {
        var fake = new FakeYtDlp();
        var result = await Run(fake, Drm("1", "2"), Entries("1", "2"));
        Assert.All(result, r => Assert.True(r.Substituted));
        Assert.All(result, r => Assert.Equal("Tonight", r.YouTubeTitle));
        Assert.Equal(2, Directory.GetFiles(_dir, "*.ogg").Length);
        Assert.Equal(new[] { "https://www.youtube.com/watch?v=MOWTfwrX48Y", "https://www.youtube.com/watch?v=MOWTfwrX48Y" }, fake.Downloads);
    }

    [Fact] // [AC-sc-youtube-fallback-2] [EDGE-sc-youtube-fallback-18]
    public async Task Fallback_FileLandsAtOriginalIndex()
    {
        File.WriteAllText(Path.Combine(_dir, "00001 - A.ogg"), "a");
        File.WriteAllText(Path.Combine(_dir, "00003 - C.ogg"), "c");
        var fake = new FakeYtDlp
        {
            Probe = _ => ProbeJson("B", "Artist", 200),
            Search = _ => SearchJson(("BBBBBBBBBBB", "B", "Artist", 200)),
        };
        var result = await Run(fake, Drm("2"), Entries("1", "2", "3"));
        Assert.True(Assert.Single(result).Substituted);
        var files = YtDlpService.CollectDownloadedFiles(_dir, numbered: true);
        Assert.Equal(new[] { "A.ogg", "B.ogg", "C.ogg" }, files.Select(Path.GetFileName));
    }

    [Fact] // [EDGE-sc-youtube-fallback-2]
    public async Task AllDrmSomeMatch_FilesCollected()
    {
        Assert.Empty(YtDlpService.CollectDownloadedFiles(_dir, true));
        await Run(new FakeYtDlp(), Drm("1"), Entries("1"));
        Assert.Single(YtDlpService.CollectDownloadedFiles(_dir, true));
    }

    [Fact] // [AC-sc-youtube-fallback-5] [EDGE-sc-youtube-fallback-31]
    public async Task ApplyFallback_ProgressWrapped()
    {
        var seen = new List<YtDlpProgressInfo>();
        await Run(new FakeYtDlp(), Drm("1", "2", "3"), Entries("1", "2", "3"), progress: seen.Add);
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.NotNull(p.FallbackTitle));
        Assert.All(seen, p => Assert.Equal(3, p.Total));
        Assert.Equal(new[] { 1, 2, 3 }, seen.Select(p => p.Current).Distinct());
        Assert.Contains(seen, p => p.FallbackTitle == "Tonight");
    }

    [Fact] // [AC-sc-youtube-fallback-6] [EDGE-sc-youtube-fallback-3]
    public void NothingDownloaded_KeepsClassification()
    {
        var outcomes = new List<FallbackTrackResult> { new("Tonight", FallbackSkipReason.NoConfidentMatch, null) };
        var ex = YtDlpService.NothingDownloaded(1, LogStderr, outcomes);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal(DownloadFailureKind.SoundCloudPaidOrProtected, YtDlpService.ClassifyFailure(ex.Message, MediaService.SoundCloud));
        Assert.NotEmpty(ex.Fallbacks);
    }

    [Fact] // [AC-sc-youtube-fallback-8] [EDGE-sc-youtube-fallback-19]
    public async Task ApplyFallback_NoOpForOtherServicesAndNoDrm()
    {
        var fake = new FakeYtDlp();
        Assert.Empty(await Run(fake, Drm("1"), Entries("1"), service: MediaService.YouTube));
        Assert.Empty(await Run(fake, Drm("1"), Entries("1"), service: MediaService.Other));
        Assert.Empty(await Run(fake, "ERROR: [soundcloud] 1: HTTP Error 404: Not Found", Entries("1")));
        Assert.Empty(await Run(fake, "", Entries("1")));
        Assert.Empty(fake.Probes);
        Assert.Empty(fake.Searches);
    }

    [Fact] // [EDGE-sc-youtube-fallback-1]
    public async Task ApplyFallback_OnlyDrmLinesFallBack()
    {
        var fake = new FakeYtDlp();
        string stderr = "ERROR: [soundcloud] 1: This track is not available from your location\n" + Drm("2") +
            "\nERROR: [soundcloud] 3: HTTP Error 429: Too Many Requests";
        var result = await Run(fake, stderr, Entries("1", "2", "3"));
        Assert.Single(result);
        Assert.Equal(new[] { "https://api.soundcloud.com/tracks/2" }, fake.Probes);
    }

    [Fact] // [EDGE-sc-youtube-fallback-6]
    public async Task ApplyFallback_UnknownIdNoDetails()
    {
        var fake = new FakeYtDlp();
        var r = Assert.Single(await Run(fake, Drm("99"), Entries("1")));
        Assert.Equal(FallbackSkipReason.NoDetails, r.Reason);
        Assert.Equal("99", r.SoundCloudTitle);
        Assert.Empty(fake.Probes);
    }

    [Fact] // [EDGE-sc-youtube-fallback-32]
    public async Task ApplyFallback_NonSoundCloudEntryUrlRejected()
    {
        var fake = new FakeYtDlp();
        var r = Assert.Single(await Run(fake, Drm("1"), new[] { new ProbeEntry("1", "https://evil.example/x", 1) }));
        Assert.Equal(FallbackSkipReason.NoDetails, r.Reason);
        Assert.Empty(fake.Probes);
    }

    [Fact] // [EDGE-sc-youtube-fallback-7]
    public async Task ApplyFallback_ProbeFailureTimeoutBadJsonOrMissingDuration()
    {
        foreach (Func<string, string> probe in new Func<string, string>[]
        {
            _ => throw new TimeoutException("yt-dlp.exe did not finish within 90 s."),
            _ => throw new InvalidOperationException("yt-dlp failed (1): boom"),
            _ => "not json",
            _ => new JObject { ["title"] = "Song" }.ToString(),
        })
        {
            var fake = new FakeYtDlp { Probe = probe };
            Assert.Equal(FallbackSkipReason.NoDetails, Assert.Single(await Run(fake, Drm("1"), Entries("1"))).Reason);
            Assert.Empty(fake.Searches);
        }
    }

    [Fact] // [EDGE-sc-youtube-fallback-8]
    public async Task ApplyFallback_RateLimitStopsRemaining()
    {
        int calls = 0;
        var fake = new FakeYtDlp
        {
            Probe = _ => ++calls == 2 ? throw new InvalidOperationException("yt-dlp failed (1): HTTP Error 429: Too Many Requests") : Fixture("sc-probe-drm.json"),
        };
        var result = await Run(fake, Drm("1", "2", "3", "4"), Entries("1", "2", "3", "4"));
        Assert.True(result[0].Substituted);
        Assert.All(result.Skip(1), r => Assert.Equal(FallbackSkipReason.SoundCloudRateLimited, r.Reason));
        Assert.Equal(2, fake.Probes.Count);
    }

    [Fact] // [EDGE-sc-youtube-fallback-9]
    public async Task ApplyFallback_SearchErrorTimeoutEmpty()
    {
        var failing = new FakeYtDlp { Search = _ => throw new TimeoutException("timeout") };
        Assert.Equal(FallbackSkipReason.SearchFailed, Assert.Single(await Run(failing, Drm("1"), Entries("1"))).Reason);
        var empty = new FakeYtDlp { Search = _ => "{\"entries\": []}" };
        Assert.Equal(FallbackSkipReason.NoConfidentMatch, Assert.Single(await Run(empty, Drm("1"), Entries("1"))).Reason);
        var garbage = new FakeYtDlp { Search = _ => "garbage" };
        Assert.Equal(FallbackSkipReason.NoConfidentMatch, Assert.Single(await Run(garbage, Drm("1"), Entries("1"))).Reason);
    }

    [Fact] // [EDGE-sc-youtube-fallback-16]
    public async Task ApplyFallback_DownloadFailureContinues()
    {
        int calls = 0;
        var fake = new FakeYtDlp();
        fake.Download = (url, dir, p) =>
        {
            if (++calls == 1) return null;
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "yt.ogg");
            File.WriteAllText(f, "ogg");
            return f;
        };
        var result = await Run(fake, Drm("1", "2"), Entries("1", "2"));
        Assert.Equal(FallbackSkipReason.DownloadFailed, result[0].Reason);
        Assert.True(result[1].Substituted);
    }

    [Fact] // [EDGE-sc-youtube-fallback-28]
    public async Task ApplyFallback_YouTubeAuthStopsRemaining()
    {
        var search = new FakeYtDlp { Search = _ => throw new InvalidOperationException("ERROR: Sign in to confirm you're not a bot") };
        var r1 = await Run(search, Drm("1", "2", "3"), Entries("1", "2", "3"));
        Assert.All(r1, r => Assert.Equal(FallbackSkipReason.YouTubeBlocked, r.Reason));
        Assert.Single(search.Searches);

        var download = new FakeYtDlp { Download = (_, _, _) => throw new InvalidOperationException("Sign in to confirm you're not a bot") };
        var r2 = await Run(download, Drm("1", "2"), Entries("1", "2"));
        Assert.All(r2, r => Assert.Equal(FallbackSkipReason.YouTubeBlocked, r.Reason));
        Assert.Single(download.Downloads);
    }

    [Fact] // [EDGE-sc-youtube-fallback-27]
    public async Task ApplyFallback_CapsAt25()
    {
        var ids = Enumerable.Range(1, 30).Select(i => i.ToString()).ToArray();
        var fake = new FakeYtDlp { Search = _ => "{\"entries\": []}" };
        var result = await Run(fake, Drm(ids), Entries(ids));
        Assert.Equal(30, result.Count);
        Assert.Equal(25, fake.Probes.Count);
        Assert.All(result.Skip(25), r => Assert.Equal(FallbackSkipReason.TooMany, r.Reason));
        Assert.Equal(new[] { "26", "27", "28", "29", "30" }, result.Skip(25).Select(r => r.SoundCloudTitle));
    }

    [Fact] // [EDGE-sc-youtube-fallback-24]
    public async Task ApplyFallback_RemovesSubdir()
    {
        await Run(new FakeYtDlp(), Drm("1", "2"), Entries("1", "2"));
        await Run(new FakeYtDlp { Download = (_, dir, _) => { Directory.CreateDirectory(dir); return null; } }, Drm("3"), Entries("3"));
        Assert.Empty(Directory.GetDirectories(_dir, "yt-fallback-*"));

        string service = File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "YtDlpService.cs"));
        int attempt = service.IndexOf("private static async Task<YtDlpDownloadResult> DownloadAudioAttemptAsync", StringComparison.Ordinal);
        Assert.True(service.IndexOf("ApplyDrmFallbackAsync(", attempt, StringComparison.Ordinal) > attempt);
        Assert.Contains("DeleteTempJar(scJar);", service);
    }

    [Fact] // [EDGE-sc-youtube-fallback-25]
    public async Task ApplyFallback_ExceptionContained()
    {
        File.WriteAllText(Path.Combine(_dir, "00009 - Existing.ogg"), "keep");
        int calls = 0;
        var fake = new FakeYtDlp();
        fake.Download = (url, dir, p) =>
        {
            if (++calls == 1) throw new IOException("disk full");
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "yt.ogg");
            File.WriteAllText(f, "ogg");
            return f;
        };
        var result = await Run(fake, Drm("1", "2"), Entries("1", "2"));
        Assert.Equal(FallbackSkipReason.DownloadFailed, result[0].Reason);
        Assert.True(result[1].Substituted);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_dir, "00009 - Existing.ogg")));
    }

    // ---- boundaries: credentials, forbidden options, limits ----

    [Fact] // [AC-sc-youtube-fallback-7]
    public void FallbackArgs_CarryNoCredentials()
    {
        string probe = YtDlpService.BuildFallbackProbeArgs("https://soundcloud.com/a/b", "--js-runtimes deno:x");
        string search = YtDlpService.BuildFallbackSearchArgs("Artist Song", "");
        Assert.DoesNotContain("--cookies", probe);
        Assert.DoesNotContain("--cookies", search);
        foreach (var flag in new[] { "--skip-download", "--ignore-no-formats-error", "--no-playlist", "-- \"" })
            Assert.Contains(flag, probe);
        Assert.DoesNotContain("--cookies", YtDlpService.BuildDownloadArgs("https://www.youtube.com/watch?v=MOWTfwrX48Y", YtDownloadMode.Single, "", "-o x", "", "", string.Empty));

        string fallback = File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "YtDlpService.Fallback.cs"));
        Assert.Contains("cookiesArg: string.Empty", fallback);
        Assert.Contains("MediaService.YouTube", fallback);
        Assert.DoesNotContain("DownloadAudioAsync(", fallback);
        Assert.DoesNotContain("CreateSoundCloudCookieJar", fallback);

        string dialog = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "YouTubeDownloadDialog.xaml.cs"));
        Assert.True(dialog.IndexOf("Fallbacks = dlResult.Fallbacks", StringComparison.Ordinal)
            < dialog.IndexOf("foreach (var file in DownloadResult.DownloadedFiles)", StringComparison.Ordinal));
    }

    [Fact] // [AC-sc-youtube-fallback-7]
    public void ForbiddenOptions_NeverBuilt()
    {
        string[] forbidden = { "--allow-unplayable-formats", "--hls-use-mpegts", "--load-info-json", "--external-downloader", "--downloader", "decrypt", "--key" };
        var outputs = new List<string>();
        foreach (var hostile in new[] { "https://soundcloud.com/a/b", "Song --allow-unplayable-formats", "-x" })
        {
            foreach (var extra in new[] { "", "--js-runtimes deno:x" })
            {
                outputs.Add(YtDlpService.BuildFallbackProbeArgs(hostile, extra));
                outputs.Add(YtDlpService.BuildFallbackSearchArgs(M.BuildSearchQuery(Track(hostile, "A", 100)), extra));
                outputs.Add(YtDlpService.BuildInfoProbeArgs("https://soundcloud.com/a/b", YtDownloadMode.Playlist, extra, "--cookies \"j\""));
                outputs.Add(YtDlpService.BuildDownloadArgs("https://soundcloud.com/a/b", YtDownloadMode.Single, "--playlist-items 1", "-o \"x\"", extra, "--ffmpeg-location \"f\"", ""));
            }
        }
        // Remote text only ever sits inside the quoted query, never as an option of its own.
        foreach (var o in outputs)
        {
            string unquoted = Regex.Replace(o, "\"[^\"]*\"", "\"\"");
            Assert.All(forbidden, f => Assert.DoesNotContain(f, unquoted, StringComparison.OrdinalIgnoreCase));
        }

        string[] exact = { "--allow-unplayable-formats", "--hls-use-mpegts", "--load-info-json", "--external-downloader", "--downloader " };
        foreach (var file in new[] { "YtDlpService.Fallback.cs", "YouTubeFallbackMatcher.cs" })
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "Tools", file));
            Assert.All(exact, f => Assert.DoesNotContain(f, src, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact] // [AC-sc-youtube-fallback-9]
    public async Task RunProcessAsync_TimesOutAndKills()
    {
        var started = DateTime.Now.AddSeconds(-1);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            YtDlpService.RunProcessAsync("cmd.exe", "/c ping -n 30 127.0.0.1", TimeSpan.FromSeconds(1)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");

        // The whole tree is killed: the ping this test started is gone too.
        await Task.Delay(500);
        var survivors = Process.GetProcessesByName("PING").Where(p =>
        {
            try { return !p.HasExited && p.StartTime >= started; } catch (Exception) { return false; }
        }).ToList();
        Assert.Empty(survivors);
    }

    [Fact] // [AC-sc-youtube-fallback-1] [EDGE-sc-youtube-fallback-4]
    public void InfoProbe_LetsALoneDrmTrackThroughOnSoundCloudOnly()
    {
        // Byte-for-byte the pre-change probe for everything that isn't SoundCloud.
        Assert.Equal("--dump-single-json --flat-playlist --no-warnings --skip-download D --no-playlist C -- \"https://www.youtube.com/watch?v=MOWTfwrX48Y\"",
            YtDlpService.BuildInfoProbeArgs("https://www.youtube.com/watch?v=MOWTfwrX48Y", YtDownloadMode.Single, "D", "C"));
        Assert.Contains("--ignore-no-formats-error",
            YtDlpService.BuildInfoProbeArgs("https://soundcloud.com/a/b", YtDownloadMode.Single, "", "", ignoreNoFormats: true));

        string service = File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "YtDlpService.cs"));
        Assert.Contains("ignoreNoFormats: service == MediaService.SoundCloud", service);
        Assert.Contains("run.ExitCode != 0 && service == MediaService.SoundCloud", service);
    }

    [Fact] // [EDGE-sc-youtube-fallback-6] [EDGE-sc-youtube-fallback-18]
    public async Task ApplyFallback_KnownIdsByIndexThenUnknownInStderrOrder()
    {
        var fake = new FakeYtDlp { Search = _ => "{\"entries\": []}" };
        var result = await Run(fake, Drm("9", "2", "8", "1"), Entries("1", "2", "3"));
        Assert.Equal(new[] { "https://api.soundcloud.com/tracks/1", "https://api.soundcloud.com/tracks/2" }, fake.Probes);
        Assert.Equal(new[] { "Tonight", "Tonight", "9", "8" }, result.Select(r => r.SoundCloudTitle));
    }

    [Fact] // [EDGE-sc-youtube-fallback-7] [EDGE-sc-youtube-fallback-9]
    public void TryParseSearchResults_FlagsUnreadableOutput()
    {
        Assert.False(M.TryParseSearchResults("{\"entries\": [", out _));
        Assert.False(M.TryParseSearchResults("garbage", out _));
        Assert.True(M.TryParseSearchResults("{\"entries\": []}", out var none));
        Assert.Empty(none);
    }

    [Fact] // [EDGE-sc-youtube-fallback-17]
    public void Truncation_NeverSplitsASurrogatePair()
    {
        string title = new string('a', 119) + "😀" + "tail";
        string name = M.SafeFileName(title, "x");
        Assert.False(char.IsHighSurrogate(name[^1]));
        string query = M.BuildSearchQuery(Track(title, null, 100));
        Assert.False(char.IsHighSurrogate(query[^1]));
    }

    [Fact] // [AC-sc-youtube-fallback-9]
    public void FallbackLimits_Constants()
    {
        Assert.Equal(25, YtDlpService.MaxFallbackTracks);
        Assert.Equal(TimeSpan.FromSeconds(90), YtDlpService.FallbackCallTimeout);
        string fallback = File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "YtDlpService.Fallback.cs"));
        Assert.Equal(2, Regex.Matches(fallback, @"FallbackCallTimeout\)").Count);
    }

    [Fact] // [EDGE-sc-youtube-fallback-33]
    public void FormatOutcomeLog_And_RedactUrls()
    {
        string log = M.FormatOutcomeLog("2268015032", new FallbackTrackResult(
            "Tonight https://api-v2.soundcloud.com/tracks/1?secret_token=s-abc", FallbackSkipReason.None, "{\"json\": 1}"));
        Assert.DoesNotContain("http", log);
        Assert.DoesNotContain("secret_token", log);
        Assert.DoesNotContain("{", log);
        Assert.Contains("2268015032", log);
        Assert.Contains("Tonight", log);
        Assert.Contains("None", log);
        Assert.True(M.RedactUrls(new string('a', 500)).Length <= 200);
    }

    // ---- report and strings ----

    private static readonly FallbackReportFormats English = new(
        "Used from YouTube:", "• {0} (YouTube: {1})", "Left out:", "• {0} ({1})", "…and {0} more",
        new Dictionary<FallbackSkipReason, string> { [FallbackSkipReason.NoConfidentMatch] = "no match", [FallbackSkipReason.SearchFailed] = "search failed" });

    [Fact] // [AC-sc-youtube-fallback-4]
    public void Report_ListsSubstitutedAndSkippedWithReasons()
    {
        string text = FallbackReportText.Compose(new[]
        {
            new FallbackTrackResult("Tonight", FallbackSkipReason.None, "Tonight (official)"),
            new FallbackTrackResult("Other", FallbackSkipReason.NoConfidentMatch, null),
            new FallbackTrackResult("Third", FallbackSkipReason.SearchFailed, null),
        }, English);
        Assert.Contains("Used from YouTube:", text);
        Assert.Contains("• Tonight (YouTube: Tonight (official))", text);
        Assert.Contains("Left out:", text);
        Assert.Contains("• Other (no match)", text);
        Assert.Contains("• Third (search failed)", text);
    }

    [Fact] // [EDGE-sc-youtube-fallback-21]
    public void Report_CapsSectionsWithMore()
    {
        var results = Enumerable.Range(1, 20).Select(i => new FallbackTrackResult($"T{i}", FallbackSkipReason.NoConfidentMatch, null)).ToList();
        string text = FallbackReportText.Compose(results, English);
        Assert.Equal(15, text.Split('\n').Count(l => l.StartsWith("• ")));
        Assert.Contains("…and 5 more", text);
    }

    private static readonly string[] Keys =
    {
        "Prog_YouTubeFallbackSearching", "Dlg_YouTubeFallback_Title", "YTFallback_SubstitutedHeader", "YTFallback_SubstitutedEntry",
        "YTFallback_SkippedHeader", "YTFallback_SkippedEntry", "YTFallback_More", "YTFallback_Reason_NoMatch",
        "YTFallback_Reason_NoDetails", "YTFallback_Reason_SearchFailed", "YTFallback_Reason_DownloadFailed",
        "YTFallback_Reason_RateLimited", "YTFallback_Reason_YouTubeBlocked", "YTFallback_Reason_TooMany", "YTFallback_NoneFound",
    };

    private static Dictionary<string, string> Resw(string locale) =>
        XDocument.Load(Path.Combine(RepoRoot(), "Strings", locale, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    private static string Placeholders(string s) =>
        string.Join(",", Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(x => x));

    [Fact] // [AC-sc-youtube-fallback-4]
    public void Resw_FallbackKeysInAllLocales()
    {
        var en = Resw("en-US");
        foreach (var locale in new[] { "en-US", "de-DE", "es-ES", "fr-FR", "nl-NL", "sv-SE", "zh-Hans" })
        {
            var strings = Resw(locale);
            foreach (var key in Keys)
            {
                Assert.True(strings.TryGetValue(key, out var value) && value.Trim().Length > 0, $"{locale}: {key}");
                Assert.Equal(Placeholders(en[key]), Placeholders(value!));
            }
        }
    }

    [Fact] // [AC-sc-youtube-fallback-4]
    public void Wording_IsNeutral()
    {
        var en = Resw("en-US");
        var texts = Keys.Select(k => en[k]).Concat(new[]
        {
            File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "YtDlpService.Fallback.cs")),
            File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "YouTubeFallbackMatcher.cs")),
            File.ReadAllText(Path.Combine(RepoRoot(), "ui", "FallbackReportText.cs")),
        });
        foreach (var text in texts)
            foreach (var word in new[] { "bypass", "circumvent", "workaround" })
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // [AC-sc-youtube-fallback-5]
    public void Dialog_UsesLocalizedFallbackStatus()
    {
        string dialog = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "YouTubeDownloadDialog.xaml.cs"));
        Assert.Contains("info.FallbackTitle", dialog);
        Assert.Contains("AppStrings.YouTubeFallbackSearching", dialog);
        Assert.Contains("AppStrings.YTFallback_NoneFound", dialog);
    }
}
