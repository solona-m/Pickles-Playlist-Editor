using Pickles_Playlist_Editor;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace ScdAudioTests;

/// <summary>Backfill tags from folder (gh-20-backfill-tags): a temp library, a temp source folder and fake readers.</summary>
public sealed class SourceTagBackfillTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "backfill-" + Guid.NewGuid().ToString("N"));
    private string Lib => Path.Combine(_dir, "mod");
    private string Src => Path.Combine(_dir, "src");
    private readonly SongTagCache _cache;
    private readonly Dictionary<string, SourceRead> _reads = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _readCalls = new();
    private Action<string>? _onRead;

    public SourceTagBackfillTests()
    {
        Directory.CreateDirectory(Lib);
        Directory.CreateDirectory(Src);
        _cache = new SongTagCache(Path.Combine(_dir, "tags_v2.json"), Path.Combine(_dir, "source_v1.json"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly TimeSpan Len = TimeSpan.FromSeconds(180);
    private static SongTags Tags(string artist, string marker = "") =>
        new(artist, "Album", "Title", AiGeneratorMarker.DetectText(marker), marker);

    private string Scd(string rel)
    {
        string full = Path.Combine(Lib, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [1, 2, 3, (byte)rel.Length]);
        return full;
    }

    private string Source(string rel, SongTags? tags, TimeSpan? duration = null)
    {
        string full = Path.Combine(Src, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, []);
        _reads[full] = new SourceRead(tags, duration ?? Len - TimeSpan.FromSeconds(5));
        return full;
    }

    private BackfillSong Song(string rel, bool inScope = true, TimeSpan? length = null)
    {
        string full = File.Exists(Path.Combine(Lib, rel)) ? Path.Combine(Lib, rel) : Scd(rel);
        return new BackfillSong(rel, full, _cache.Generation(full), inScope, length ?? Len);
    }

    private SourceRead Read(string path)
    {
        _readCalls.Add(path);
        _onRead?.Invoke(path);
        return _reads.TryGetValue(path, out var r) ? r : new SourceRead(null, null);
    }

    private BackfillResult Run(IReadOnlyList<BackfillSong> lib, Func<string, SongTags?>? scdTags = null, CancellationToken ct = default,
        int alreadyHad = 0, string? root = null) =>
        SourceTagBackfill.Run(lib, alreadyHad, root ?? Src, _cache, Read, scdTags ?? (_ => SongTags.None), SourceTagBackfill.ScdDuration, ct);

    private SongTags? Stored(BackfillSong s) => _cache.TryGet(s.ScdFullPath);

    // ── keys ──

    [Fact] // [AC-gh-20-backfill-tags-2] [EDGE-gh-20-backfill-tags-4] [EDGE-gh-20-backfill-tags-5] [EDGE-gh-20-backfill-tags-6]
    public void Keys_BothSides_LevelsBpmloopSanitize()
    {
        Assert.Equal(["Song_1_1", "Song_1", "Song"], SourceTagBackfill.StemKeys("Song_1_1"));
        Assert.Equal(["Track_01", "Track"], SourceTagBackfill.StemKeys("Track_01"));
        Assert.Equal(["_1"], SourceTagBackfill.StemKeys("_1"));
        Assert.Empty(SourceTagBackfill.StemKeys(""));

        Assert.Equal("X", Playlist.ImportScdStem(Path.Combine("C:\\music", "X", "bpmloop.mp3")));
        Assert.Equal("", Playlist.ImportScdStem("C:\\bpmloop.mp3"));
        Assert.Equal("a_b", Playlist.ImportScdStem("C:\\m\\a\tb.mp3"));
        Assert.Equal("Song", Playlist.ImportScdStem("C:\\m\\Song. .mp3"));
        Assert.Equal(200, Playlist.ImportScdStem("C:\\m\\" + new string('x', 250) + ".mp3").Length);
        Assert.Equal("", Playlist.ImportScdStem("C:\\m\\....mp3"));

        // level order
        var s1 = Song("pl\\Song_1.scd");
        var s11 = Song("pl\\Song_1_1.scd");
        Source("Song_1.mp3", Tags("exact"));
        Source("a\\Song.mp3", Tags("stripped"));
        var t = Song("pl\\Track.scd");
        Source("Track_01.mp3", Tags("track01"));
        var holy = Song("pl\\Holy Crit.scd");
        Source("HOLY CRIT.MP3", Tags("holy"));
        var audio = Song("pl\\audio.scd");
        Source("....mp3", Tags("never"));
        var r = Run([s1, s11, t, holy, audio]);
        Assert.Equal("exact", Stored(s1)?.Artist);
        Assert.Equal("exact", Stored(s11)?.Artist); // Song_1 is the first key with a file
        Assert.Equal("track01", Stored(t)?.Artist);
        Assert.Equal("holy", Stored(holy)?.Artist);
        Assert.Equal("", Stored(audio)?.Artist ?? ""); // only the empty SCD-tag read
        Assert.Equal(1, r.NoMatch);

        // Track.mp3 present: exact wins over the source-stripped Track_01.mp3
        var t2 = Song("p2\\Tune.scd");
        Source("Tune.mp3", Tags("tune"));
        Source("Tune_01.mp3", Tags("tune01"));
        Run([t2]);
        Assert.Equal("tune", Stored(t2)?.Artist);

        // Song_1_1 reaches Song.mp3 when nothing nearer exists
        var lone = Song("p3\\Lone_1_1.scd");
        Source("x\\Lone.mp3", Tags("stripped"));
        Run([lone]);
        Assert.Equal("stripped", Stored(lone)?.Artist);
    }

    [Fact] // [AC-gh-20-backfill-tags-2] [EDGE-gh-20-backfill-tags-4]
    public void Keys_NumberedSiblings_NeverMatchEachOther()
    {
        var s = Song("pl\\Song_1.scd");
        Source("Song_2.mp3", Tags("other"));
        var r = Run([s]);
        Assert.Equal(1, r.NoMatch);
        Assert.Empty(_readCalls);
        Assert.Equal("", Stored(s)?.Artist ?? ""); // only the empty SCD-tag read
    }

    // ── candidates and lengths ──

    [Theory] // [AC-gh-20-backfill-tags-5] [EDGE-gh-20-backfill-tags-1] [EDGE-gh-20-backfill-tags-2] [EDGE-gh-20-backfill-tags-3]
    [InlineData("A", "A", "filled")]
    [InlineData("A", "B", "skipped")]
    [InlineData("A", "empty", "skipped")]
    [InlineData("A", "null", "skipped")]
    [InlineData("empty", "null", "notags")]
    [InlineData("suno1", "suno2", "skipped")]
    public void Run_CandidateRules(string first, string second, string expect)
    {
        static SongTags? T(string c) => c switch
        {
            "null" => null,
            "empty" => SongTags.None,
            "suno1" => Tags("A", "made with suno; created=1"),
            "suno2" => Tags("A", "made with suno; created=2"),
            _ => Tags(c),
        };
        var s = Song("pl\\Song.scd");
        Source("a\\Song.mp3", T(first));
        Source("b\\Song.m4a", T(second)); // mp3 and m4a side by side, in two folders
        var r = Run([s]);
        Assert.Equal(expect == "filled" ? 1 : 0, r.Filled);
        Assert.Equal(expect == "skipped" ? 1 : 0, r.Skipped);
        Assert.Equal(expect == "notags" ? 1 : 0, r.NoTags);
        Assert.Equal(expect == "filled", Stored(s) is { Artist: "A" });
    }

    [Fact] // [AC-gh-20-backfill-tags-5] [EDGE-gh-20-backfill-tags-3]
    public void Run_NineCandidates_SkippedWithoutReads()
    {
        var s = Song("pl\\Song.scd");
        for (int i = 0; i < 9; i++) Source($"d{i}\\Song.mp3", Tags("A"));
        var r = Run([s]);
        Assert.Equal(1, r.Skipped);
        Assert.Empty(_readCalls);
    }

    [Fact] // [AC-gh-20-backfill-tags-5] [EDGE-gh-20-backfill-tags-8]
    public void DurationMatches_Window_AndUnknown()
    {
        var src = TimeSpan.FromSeconds(100);
        foreach (int d in new[] { 5, -3, 7, 0 }) Assert.True(SourceTagBackfill.DurationMatches(src + TimeSpan.FromSeconds(d), src), d.ToString());
        foreach (int d in new[] { -4, 8 }) Assert.False(SourceTagBackfill.DurationMatches(src + TimeSpan.FromSeconds(d), src), d.ToString());
        Assert.False(SourceTagBackfill.DurationMatches(null, src));
        Assert.False(SourceTagBackfill.DurationMatches(src, null));

        // cached first, even over a corrupt file
        string corrupt = Scd("x\\corrupt.scd");
        Assert.Equal(TimeSpan.FromSeconds(120), SourceTagBackfill.ScdDuration(new BackfillSong("x", corrupt, 0, true, TimeSpan.FromSeconds(120))));
        Assert.Null(SourceTagBackfill.ScdDuration(new BackfillSong("x", corrupt, 0, true, null)));
        Assert.Null(SourceTagBackfill.ScdDuration(new BackfillSong("x", Path.Combine(Lib, "missing.scd"), 0, true, null)));
        Assert.Null(SourceTagBackfill.ScdDurationFromBytes([]));
        // a real Vorbis SCD and an ADPCM one: SourceTagBackfillFfmpegTests.ScdDuration_RealVorbis_InMemory_AdpcmUnknown
    }

    [Fact] // [AC-gh-20-backfill-tags-5] [EDGE-gh-20-backfill-tags-8]
    public void Run_LengthMismatchOrUnknown_Skipped()
    {
        var far = Song("pl\\Far.scd");
        Source("Far.mp3", Tags("A"), Len - TimeSpan.FromSeconds(30));
        var unknownSrc = Song("pl\\NoLen.scd");
        _reads[Source("NoLen.mp3", Tags("B"))] = new SourceRead(Tags("B"), null);
        var unknownScd = Song("pl\\Adpcm.scd") with { CachedDuration = null }; // no cache, unparseable bytes
        Source("Adpcm.mp3", Tags("C"));
        var r = Run([far, unknownSrc, unknownScd]);
        Assert.Equal(3, r.Skipped);
        Assert.Equal(0, r.Filled);
    }

    [Fact] // [AC-gh-20-backfill-tags-5] [EDGE-gh-20-backfill-tags-9]
    public void Run_LibraryWideGroup_ScopeIndependent()
    {
        // Two library songs share the name; the out-of-scope one is a different length: neither scope fills.
        var mine = Song("a\\Intro.scd");
        var other = Song("b\\Intro_1.scd", inScope: false, length: TimeSpan.FromSeconds(60));
        Source("Intro.mp3", Tags("A"));
        Assert.Equal(1, Run([mine, other]).Skipped);
        Assert.Equal(2, Run([mine, other with { InScope = true }]).Skipped);
        Assert.Equal("", Stored(mine)?.Artist ?? ""); // only the empty SCD-tag read

        // Same song in two playlists (both lengths agree): both filled.
        var p1 = Song("c\\Same.scd");
        var p2 = Song("d\\Same_1.scd");
        Source("Same.mp3", Tags("S"));
        var r = Run([p1, p2]);
        Assert.Equal(2, r.Filled);
        Assert.Equal("S", Stored(p2)?.Artist);
    }

    // ── targeting and the store guard ──

    [Fact] // [AC-gh-20-backfill-tags-4] [EDGE-gh-20-backfill-tags-10] [EDGE-gh-20-backfill-tags-11] [EDGE-gh-20-backfill-tags-13]
    public void Run_EffectiveTagTargeting_AndStoreGuard()
    {
        // SCD tags already show an artist: left alone, its file never read
        var tagged = Song("pl\\Tagged.scd");
        _cache.Store(tagged.ScdFullPath, new SongTags("Old", "", ""));
        Source("Tagged.mp3", Tags("New"));
        // never read before: its SCD carries old comments
        var oldImport = Song("pl\\OldImport.scd");
        Source("OldImport.mp3", Tags("New"));
        var r = Run([tagged, oldImport], scdTags: p => p == oldImport.ScdFullPath ? new SongTags("Embedded", "", "") : SongTags.None);
        Assert.Equal(2, r.AlreadyHad);
        Assert.Empty(_readCalls);
        Assert.Equal("Old", Stored(tagged)?.Artist);
        Assert.Equal("Embedded", Stored(oldImport)?.Artist);

        // a source entry or SCD tags appearing mid-run, or a move mid-run: refused
        var raced = Song("pl\\Raced.scd");
        var scdRaced = Song("pl\\ScdRaced.scd");
        var moved = Song("pl\\Moved.scd");
        Source("Raced.mp3", Tags("New"));
        Source("ScdRaced.mp3", Tags("New"));
        Source("Moved.mp3", Tags("New"));
        _onRead = p =>
        {
            if (p.EndsWith("Raced.mp3") && !p.EndsWith("ScdRaced.mp3")) _cache.StoreSource(raced.ScdFullPath, new SongTags("Import", "", ""));
            if (p.EndsWith("ScdRaced.mp3")) _cache.Store(scdRaced.ScdFullPath, new SongTags("Scan", "", ""));
            if (p.EndsWith("Moved.mp3")) _cache.UpdateCacheForSCD(moved.ScdFullPath, moved.ScdFullPath + ".x");
        };
        r = Run([raced, scdRaced, moved]);
        Assert.Equal(3, r.Skipped);
        Assert.Equal("Import", Stored(raced)?.Artist);
        Assert.Equal("Scan", Stored(scdRaced)?.Artist);
    }

    [Fact] // [AC-gh-20-backfill-tags-4] [EDGE-gh-20-backfill-tags-12]
    public void Cache_StoreSourceIfUntagged_AndCanLoadSource()
    {
        string a = Scd("a.scd");
        long g = _cache.Generation(a);
        Assert.True(_cache.CanLoadSource());
        Assert.False(_cache.StoreSourceIfUntagged(a, Tags("x"), g + 1)); // moved since
        Assert.True(_cache.StoreSourceIfUntagged(a, Tags("x"), g));
        Assert.False(_cache.StoreSourceIfUntagged(a, Tags("y"), g)); // already has some
        string b = Scd("b.scd");
        _cache.Store(b, new SongTags("", "", "", "Suno"));
        Assert.False(_cache.StoreSourceIfUntagged(b, Tags("y"), _cache.Generation(b))); // SCD marker shows
        _cache.Flush();

        // a source file that can't be loaded: one outcome, nothing read, the file intact
        string srcFile = Path.Combine(_dir, "locked_src.json");
        File.WriteAllText(srcFile, "{\"version\":1,\"songs\":{\"k\":[\"a\",\"b\",\"c\",\"\"]}}");
        byte[] before = File.ReadAllBytes(srcFile);
        var locked = new SongTagCache(Path.Combine(_dir, "locked_v2.json"), srcFile);
        var song = Song("pl\\L.scd");
        Source("L.mp3", Tags("A"));
        using (new FileStream(srcFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(locked.CanLoadSource());
            Assert.False(locked.StoreSourceIfUntagged(song.ScdFullPath, Tags("A"), 0));
            var r = SourceTagBackfill.Run([song], 0, Src, locked, Read, _ => SongTags.None, SourceTagBackfill.ScdDuration, default);
            Assert.Equal(BackfillOutcome.TagsFileUnavailable, r.Outcome);
            Assert.Empty(_readCalls);
        }
        locked.Flush();
        Assert.Equal(before, File.ReadAllBytes(srcFile));
    }

    [Fact] // [AC-gh-20-backfill-tags-4] [EDGE-gh-20-backfill-tags-29] [EDGE-gh-20-backfill-tags-30] [EDGE-gh-20-backfill-tags-14]
    public void Run_ScdsNeverModified_ModFolderAsRoot_SharedAndMissing()
    {
        var s = Song("pl\\Song.scd");
        var dup = s with { ScdRel = "pl/Song.scd" }; // two options on one SCD
        var missing = new BackfillSong("pl\\Gone.scd", Path.Combine(Lib, "pl", "Gone.scd"), 0, true, Len);
        Source("Song.mp3", Tags("A"));
        var before = Directory.GetFiles(Lib, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f)));
        var r = Run([s, dup, missing]);
        Assert.Equal(1, r.Targets);
        Assert.Equal(1, r.Filled);
        r = Run([Song("pl\\Other.scd")], root: Lib); // only .scd files there
        Assert.Equal(1, r.NoMatch);
        foreach (var (f, (bytes, time)) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(f));
            Assert.Equal(time, File.GetLastWriteTimeUtc(f));
        }
    }

    [Fact] // [AC-gh-20-backfill-tags-9] [EDGE-gh-20-backfill-tags-27] [EDGE-gh-20-backfill-tags-32]
    public void Run_LargeFolder_IndexesWantedOnly_ReadsMatchedOnly_Flushes()
    {
        for (int i = 0; i < 300; i++) File.WriteAllBytes(Path.Combine(Src, $"other{i}.mp3"), []);
        var lib = new List<BackfillSong>();
        for (int i = 0; i < 250; i++)
        {
            lib.Add(Song($"pl\\S{i}.scd"));
            Source($"n{i % 7}\\S{i}.mp3", Tags("A" + i));
        }
        var walk = SourceFolderWalk.Walk(Src, new HashSet<string>(lib.Select(l => Path.GetFileNameWithoutExtension(l.ScdFullPath)), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), default);
        Assert.Equal(550, walk.FilesChecked);
        Assert.Equal(250, walk.Exact.Count);

        var progress = new List<(string, int, int)>();
        var filledCalls = new List<int>();
        int seenMidRun = -1;
        _onRead = _ =>
        {
            if (_readCalls.Count != 102) return; // 101 stored by now: the 100th triggered a flush
            var mid = new SongTagCache(Path.Combine(_dir, "tags_v2.json"), Path.Combine(_dir, "source_v1.json"));
            seenMidRun = lib.Count(l => mid.TryGet(l.ScdFullPath)?.Artist.StartsWith('A') == true);
        };
        var r = SourceTagBackfill.Run(lib, 0, Src, _cache, Read, _ => SongTags.None, SourceTagBackfill.ScdDuration, default,
            (p, a, b) => progress.Add((p, a, b)), filledCalls.Add);
        Assert.InRange(seenMidRun, 100, 101);
        Assert.Equal(Enumerable.Range(1, 250), filledCalls);
        Assert.Equal(250, r.Filled);
        Assert.Equal(550, r.FilesChecked);
        Assert.Equal(250, _readCalls.Count);
        Assert.Contains((SourceTagBackfill.PhaseScanning, 550, 0), progress);
        Assert.Equal((SourceTagBackfill.PhaseReading, 250, 250), progress[^1]);
        var fresh = new SongTagCache(Path.Combine(_dir, "tags_v2.json"), Path.Combine(_dir, "source_v1.json"));
        Assert.All(lib, l => Assert.StartsWith("A", fresh.TryGet(l.ScdFullPath)?.Artist));
    }

    [Fact] // [AC-gh-20-backfill-tags-7] [EDGE-gh-20-backfill-tags-21]
    public void Run_CancelKeepsFills()
    {
        var lib = Enumerable.Range(0, 5).Select(i => Song($"pl\\C{i}.scd")).ToList();
        for (int i = 0; i < 5; i++) Source($"C{i}.mp3", Tags("A"));
        using var cts = new CancellationTokenSource();
        int n = 0;
        _onRead = _ => { if (++n == 3) cts.Cancel(); };
        var r = Run(lib, ct: cts.Token);
        Assert.Equal(3, r.Filled); // the third read finished before the next check
        var fresh = new SongTagCache(Path.Combine(_dir, "tags_v2.json"), Path.Combine(_dir, "source_v1.json"));
        Assert.Equal(3, lib.Count(l => fresh.TryGet(l.ScdFullPath)?.Artist == "A"));
    }

    [Fact] // [AC-gh-20-backfill-tags-8] [EDGE-gh-20-backfill-tags-16]
    public void Run_RootMissing_FolderUnreadable()
    {
        var r = Run([Song("pl\\A.scd")], root: Path.Combine(_dir, "nope"));
        Assert.Equal(BackfillOutcome.FolderUnreadable, r.Outcome);
        Assert.NotEmpty(r.FolderError);
    }

    [SkippableFact] // [AC-gh-20-backfill-tags-8] [EDGE-gh-20-backfill-tags-16]
    public void Run_RootAccessDenied_FolderUnreadable()
    {
        var song = Song("pl\\A.scd");
        Source("A.mp3", Tags("A"));
        var info = new DirectoryInfo(Src);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(rule);
        info.SetAccessControl(acl);
        try
        {
            bool denied;
            try { Directory.EnumerateFileSystemEntries(Src).ToList(); denied = false; } catch (UnauthorizedAccessException) { denied = true; }
            Skip.IfNot(denied, "the deny rule did not take");
            var r = Run([song]);
            Assert.Equal(BackfillOutcome.FolderUnreadable, r.Outcome);
            Assert.Empty(_readCalls);
        }
        finally
        {
            acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
    }

    [SkippableFact] // [AC-gh-20-backfill-tags-9] [EDGE-gh-20-backfill-tags-19]
    public void Run_CloudOnlyCandidate_CountedNeverRead()
    {
        var song = Song("pl\\Cloud.scd");
        string file = Source("Cloud.m4a", Tags("A"));
        File.SetAttributes(file, FileAttributes.Offline);
        Skip.IfNot((File.GetAttributes(file) & FileAttributes.Offline) != 0, "the Offline attribute did not stick");
        var r = Run([song]);
        Assert.Equal(1, r.CloudOnly);
        Assert.Equal(0, r.Filled);
        Assert.Empty(_readCalls);
    }

    [Fact] // [AC-gh-20-backfill-tags-9] [EDGE-gh-20-backfill-tags-18]
    public void Walk_DepthCapped()
    {
        string deep = Src;
        for (int i = 0; i <= SourceTagBackfill.MaxDepth; i++) deep = Path.Combine(deep, "d");
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "TooDeep.mp3"), []); // depth MaxDepth + 1
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(deep)!, "JustFits.mp3"), []); // depth MaxDepth
        var w = SourceFolderWalk.Walk(Src, new HashSet<string>(["TooDeep", "JustFits"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(), default);
        Assert.True(w.Exact.ContainsKey("JustFits"));
        Assert.False(w.Exact.ContainsKey("TooDeep"));
    }
}

/// <summary>The folder walk on a real file system.</summary>
public sealed class SourceTagBackfillWalkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "bfwalk-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        try { foreach (var f in Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Touch(string rel)
    {
        string p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, []);
        return p;
    }

    private static HashSet<string> Set(params string[] k) => new(k, StringComparer.OrdinalIgnoreCase);

    [SkippableFact] // [AC-gh-20-backfill-tags-9] [EDGE-gh-20-backfill-tags-7] [EDGE-gh-20-backfill-tags-17] [EDGE-gh-20-backfill-tags-18] [EDGE-gh-20-backfill-tags-19] [EDGE-gh-20-backfill-tags-20] [EDGE-gh-20-backfill-tags-31]
    public void Walk_Skips()
    {
        Touch("a\\b\\c\\Song.flac");
        Touch("Song.ogg");
        Touch("Song.scd");
        Touch("Song.aac");
        Touch("Song_now_playing_" + Guid.NewGuid().ToString("N") + ".mp3");
        string hidden = Touch("Hidden.mp3");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        string sys = Touch("sys\\Sys.mp3");
        File.SetAttributes(Path.Combine(_dir, "sys"), FileAttributes.Directory | FileAttributes.System);
        string cloud = Touch("Cloud.m4a");
        File.SetAttributes(cloud, FileAttributes.Offline);
        var w = SourceFolderWalk.Walk(_dir, Set("Song", "Hidden", "Sys", "Cloud"), Set(), default);
        Assert.Single(w.Exact["Song"]);
        Assert.False(w.Exact.ContainsKey("Hidden"));
        Assert.False(w.Exact.ContainsKey("Sys"));
        bool offlineKept = (File.GetAttributes(cloud) & FileAttributes.Offline) != 0;
        if (offlineKept) Assert.True(w.Exact["Cloud"].Single().CloudOnly);
        Assert.Equal(2, w.FilesChecked); // Song.flac and Cloud.m4a

        // a junction back to the root is not followed
        string link = Path.Combine(_dir, "a", "loop");
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_dir}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mk.WaitForExit();
        Skip.IfNot(Directory.Exists(link), "junctions unavailable");
        w = SourceFolderWalk.Walk(_dir, Set("Song"), Set(), default);
        Assert.Single(w.Exact["Song"]);
        Directory.Delete(link);

        // an inaccessible subfolder is skipped, the rest still found
        string denied = Path.Combine(_dir, "denied");
        Touch("denied\\Song_9.mp3");
        var info = new DirectoryInfo(denied);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(rule);
        info.SetAccessControl(acl);
        try
        {
            w = SourceFolderWalk.Walk(_dir, Set("Song", "Song_9"), Set(), default);
            Assert.Single(w.Exact["Song"]);
            Assert.False(w.Exact.ContainsKey("Song_9"));
        }
        finally
        {
            acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
    }
}

/// <summary>End to end against the bundled ffmpeg: a real tagged m4a in a nested folder.</summary>
[Collection("ffmpeg")]
public sealed class SourceTagBackfillFfmpegTests : IDisposable
{
    private readonly AdpcmScdFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact] // [AC-gh-20-backfill-tags-5] [EDGE-gh-20-backfill-tags-8] [EDGE-gh-20-backfill-tags-29]
    public void ScdDuration_RealVorbis_InMemory_AdpcmUnknown()
    {
        string real = TaggedScd.Wrap(_fx, TaggedScd.Ogg(_fx, "three", seconds: 3), "three");
        var before = (File.ReadAllBytes(real), File.GetLastWriteTimeUtc(real));
        int temps = Directory.GetFiles(Path.GetTempPath(), "pickles-*").Length;
        var length = SourceTagBackfill.ScdDuration(new BackfillSong("r", real, 0, true, null));
        Assert.InRange(length!.Value.TotalSeconds, 2.9, 3.1);
        Assert.Equal(before.Item1, File.ReadAllBytes(real));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(real));
        Assert.True(Directory.GetFiles(Path.GetTempPath(), "pickles-*").Length <= temps);
        Assert.Null(SourceTagBackfill.ScdDurationFromBytes(before.Item1[..(before.Item1.Length / 8)]));
        Assert.Null(SourceTagBackfill.ScdDuration(new BackfillSong("a", _fx.MakeAdpcmScd(), 0, true, null)));
    }

    [Fact] // [AC-gh-20-backfill-tags-2] [AC-gh-20-backfill-tags-3] [EDGE-gh-20-backfill-tags-15]
    public void Ffmpeg_EndToEnd_NestedM4a_SunoComment()
    {
        string lib = _fx.PathFor("mod");
        Directory.CreateDirectory(lib);
        string scd = Path.Combine(lib, "Holy Crit_1.scd");
        File.Copy(TaggedScd.Wrap(_fx, TaggedScd.Ogg(_fx, "ten", seconds: 10), "ten"), scd); // 10 s: a 5 s source plus import's 5 s pad
        Assert.InRange(SourceTagBackfill.ScdDuration(new BackfillSong("Holy Crit_1.scd", scd, 0, true, null))!.Value.TotalSeconds, 9.5, 10.5);
        double srcSecs = 5;

        string nested = Path.Combine(_fx.Dir, "src", "a", "b");
        Directory.CreateDirectory(nested);
        string meta = _fx.PathFor("m.txt");
        File.WriteAllText(meta, ";FFMETADATA1\nartist=Pickle\nalbum=Brine\ntitle=Holy Crit\ncomment=" + SourceFiles.Esc(SourceFiles.HolyCrit) + "\n");
        string m4a = Path.Combine(nested, "Holy Crit.m4a");
        AdpcmScdFixture.RunFfmpeg($"-y -v error -f lavfi -i anoisesrc=d={srcSecs.ToString(System.Globalization.CultureInfo.InvariantCulture)}:a=0.3 -i \"{meta}\" -map_metadata 1 -c:a aac \"{m4a}\"");
        File.WriteAllBytes(Path.Combine(nested, "Broken.mp3"), [0, 1, 2, 3]);
        string broken = Path.Combine(lib, "Broken.scd");
        File.Copy(scd, broken);

        var read = SourceTagReader.ReadForBackfill(m4a);
        Assert.NotNull(read.Duration);
        Assert.Equal("Pickle", read.Tags?.Artist);
        Assert.Null(SourceTagReader.ReadForBackfill(Path.Combine(nested, "Broken.mp3")).Tags);
        Assert.Null(FFMpeg.ParseDuration("  Duration: N/A, bitrate: N/A"));
        Assert.Equal(TimeSpan.FromSeconds(83.5), FFMpeg.ParseDuration("  Duration: 00:01:23.50, start: 0"));
        // a tag value can't pose as the length: only a line that starts with "Duration:"
        Assert.Equal(TimeSpan.FromSeconds(5), FFMpeg.ParseDuration(
            "    comment         : fake Duration: 00:03:00.00\n  Duration: 00:00:05.00, start: 0"));

        var cache = new SongTagCache(_fx.PathFor("t_v2.json"), _fx.PathFor("s_v1.json"));
        var r = SourceTagBackfill.Run(
            [new BackfillSong("Holy Crit_1.scd", scd, 0, true, null), new BackfillSong("Broken.scd", broken, 0, true, null)],
            0, Path.Combine(_fx.Dir, "src"), cache, SourceTagReader.ReadForBackfill, SongTagReader.Read, SourceTagBackfill.ScdDuration, default);
        Assert.Equal(1, r.Filled);
        Assert.Equal(1, r.AiTagged);
        Assert.Equal(1, r.NoTags);
        var fresh = new SongTagCache(_fx.PathFor("t_v2.json"), _fx.PathFor("s_v1.json"));
        var t = fresh.TryGet(scd);
        Assert.Equal(("Pickle", "Brine", "Holy Crit", "Suno"), (t?.Artist, t?.Album, t?.Title, t?.AiTool));
    }
}

/// <summary>Source-level wiring for the UI parts (the user drives UI testing).</summary>
public sealed class SourceTagBackfillWiringTests
{
    private static string Src(params string[] rel) => File.ReadAllText(Path.Combine([RuntimeTargetTests.RepoRoot(), .. rel]));

    [Fact] // [AC-gh-20-backfill-tags-2] [EDGE-gh-20-backfill-tags-10]
    public void Wiring_AddFiles_UsesImportHelpers()
    {
        string s = Src("Playlist.cs");
        int add = s.IndexOf("static Option AddFiles(");
        string body = s[add..(add + 2500)];
        Assert.Contains("ImportSongName(file)", body);
        Assert.Contains("ImportScdStem(file)", body);
        Assert.Contains("\"audio\"", body);
    }

    [Fact] // [AC-gh-20-backfill-tags-6] [AC-gh-20-backfill-tags-7] [EDGE-gh-20-backfill-tags-21] [EDGE-gh-20-backfill-tags-22] [EDGE-gh-20-backfill-tags-26] [EDGE-gh-20-backfill-tags-32] [EDGE-gh-20-backfill-tags-27]
    public void Wiring_BackgroundRun()
    {
        string ui = Src("ui", "MainWindow.Backfill.cs");
        Assert.Contains("ThreadPriorityScope.Background()", ui);
        Assert.Contains("Task.Run(", ui);
        Assert.Contains("SourceTagReader.ReadForBackfill", ui);
        Assert.Contains("CreateLinkedTokenSource(_closingCts.Token)", ui);
        Assert.Contains("if (_closingCts.IsCancellationRequested) {", ui);
        Assert.Contains("if (_backfillRunning) { await ShowDialogAsync(AppStrings.Summary_BackfillTags, AppStrings.Backfill_AlreadyRunning)", ui);
        // closing: no refresh and no dialog after the run
        string after = ui[ui.IndexOf("var result = await Task.Run(")..];
        int bail = after.IndexOf("if (_closingCts.IsCancellationRequested) return;");
        Assert.True(bail > 0 && bail < after.IndexOf("RefreshSongTooltips();") && bail < after.IndexOf("ShowDialogAsync("));
        Assert.Contains("SongTagCache.Default.Flush()", Src("ui", "MainWindow.Stats.cs")); // PrepareForExit keeps fills since the last flush
        Assert.True(ui.IndexOf("SetBackfillProgress(AppStrings.Prog_BackfillPreparing, 0)") < ui.IndexOf("Task.Run("));
        Assert.Contains("BPMDetector.TryGetCachedDuration(Playlist.GetScdPath(", ui);
        Assert.DoesNotContain("ModFolderChanged", ui);

        string core = Src("Tools", "SourceTagBackfill.cs");
        Assert.DoesNotContain("BPMDetector", core);
        Assert.Contains("is not ScdVorbis", core);
        Assert.Contains("NVorbis.VorbisReader(new MemoryStream(", core);
        Assert.DoesNotContain("WithExtracted", core);
        // the backfill's own log calls: counts and file names only (each call's arguments, continuation lines included)
        foreach (string file in new[] { core, Src("Tools", "SourceFolderWalk.cs"), ui })
            foreach (var call in System.Text.RegularExpressions.Regex.Matches(file, @"Logger\.Log\w+\((.*?)\);", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(m => m.Groups[1].Value))
            {
                foreach (string pathy in new[] { "root", "FullName", "dir.", "entry.", ".Path)", "ScdFullPath," })
                    Assert.DoesNotContain(pathy, call.Replace("Path.GetFileName(song.ScdFullPath)", "").Replace("Path.GetFileName(c.Path)", ""));
            }
        Assert.Contains("Path.GetFileName(song.ScdFullPath)", core);
        Assert.Contains("Path.GetFileName(c.Path)", core);

        string ff = Src("Utils", "FFMpeg.cs");
        Assert.Contains("if (lowPriority)", ff);
        Assert.Contains("ProcessPriorityClass.BelowNormal", ff);
        Assert.Contains("lowPriority: true", Src("Tools", "SourceTagReader.cs"));
    }

    [Fact] // [AC-gh-20-backfill-tags-6] [EDGE-gh-20-backfill-tags-28]
    public void BackgroundStatus_NestedPick_AndStatusSlot()
    {
        (string, int)? b = ("backfill", 1), s = ("stats", 2), a = ("ai", 3);
        Assert.Equal(b, BackgroundStatus.Pick(b, BackgroundStatus.Pick(s, a)));
        Assert.Equal(s, BackgroundStatus.Pick(null, BackgroundStatus.Pick(s, a)));
        Assert.Equal(a, BackgroundStatus.Pick(null, BackgroundStatus.Pick(null, a)));
        string stats = Src("ui", "MainWindow.Stats.cs");
        Assert.Contains("BackgroundStatus.Pick(_backfillProgress, BackgroundStatus.Pick(_statsProgress, _aiProgress))", stats);
        Assert.Contains("if (_busyOverlayVisible) return;", stats);
        string ui = Src("ui", "MainWindow.Backfill.cs");
        Assert.Contains("_uiDispatcherQueue.TryEnqueue(() => SetBackfillProgress(text, percent))", ui);
        Assert.Contains("RenderBackgroundProgress();", ui);
        Assert.Contains("SetBackfillProgress(\"\", 0);", ui);
    }

    [Fact] // [AC-gh-20-backfill-tags-1] [EDGE-gh-20-backfill-tags-23] [EDGE-gh-20-backfill-tags-24] [AC-gh-20-backfill-tags-3]
    public void Wiring_MenuHandlerAndRefresh()
    {
        string menu = Src("ui", "MainWindow.ContextMenu.cs");
        foreach (string build in new[] { "private MenuFlyout BuildContextMenu()", "private MenuFlyout BuildRootContextMenu()" })
        {
            int at = menu.IndexOf(build);
            string body = menu[at..menu.IndexOf("return flyout;", at)];
            Assert.True(body.IndexOf("Menu_BackfillTags") > body.IndexOf("Menu_ComputeStats"), build);
            Assert.Contains("BackfillTagsMenuItem_Click", body);
        }
        string ui = Src("ui", "MainWindow.Backfill.cs");
        int[] order =
        [
            ui.IndexOf("GetSongTargets()"), ui.IndexOf("AppStrings.Dlg_NoSongs"), ui.IndexOf("AppStrings.Backfill_NothingToFill"),
            ui.IndexOf("PickFolderAsync(AppStrings.Backfill_PickFolder)"), ui.IndexOf("if (string.IsNullOrWhiteSpace(folder)) return;"),
            ui.IndexOf("await RunBackfillAsync("),
        ];
        Assert.All(order, i => Assert.True(i > 0));
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.Contains("if (IsFilterActive) LoadPlaylists(SearchTextBox.Text.Trim());", ui);
        Assert.Contains("_uiDispatcherQueue.TryEnqueue(RefreshSongTooltips)", ui);
        Assert.Contains("ApplyAiChip(n, modRoot)", Src("ui", "MainWindow.SongTags.cs"));
        Assert.Contains("Settings.AiScoreEnabled", ui);
    }

    private static string En(string key)
    {
        var doc = System.Xml.Linq.XDocument.Load(Path.Combine(RuntimeTargetTests.RepoRoot(), "Strings", "en-US", "Resources.resw"));
        return doc.Root!.Elements("data").Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;
    }

    [Fact] // [AC-gh-20-backfill-tags-8] [EDGE-gh-20-backfill-tags-16] [EDGE-gh-20-backfill-tags-25]
    public void Summary_Text()
    {
        var r = new BackfillResult(BackfillOutcome.Done, 40, 5, 3, 2, 1, 0, 1, 0, 1);
        string text = BackfillText.Summary(r, aiOn: false, En);
        Assert.Equal(new[]
        {
            "Filled tags for 3 of 5 songs without tags, from 40 audio files searched.",
            "2 of them are tagged as made with Suno or Udio.",
            En("Backfill_AiHidden"),
            "1 already had tags and were left as they were.",
            string.Format(En("Backfill_Skipped"), 1),
            string.Format(En("Backfill_CloudOnly"), 1),
        }, text.Split('\n'));
        Assert.DoesNotContain(En("Backfill_AiHidden"), BackfillText.Summary(r, aiOn: true, En));
        Assert.Equal(En("Backfill_TagsFileUnavailable"), BackfillText.Summary(r with { Outcome = BackfillOutcome.TagsFileUnavailable }, true, En));
        Assert.Equal("The folder couldn't be read: nope", BackfillText.Summary(r with { Outcome = BackfillOutcome.FolderUnreadable, FolderError = "nope" }, true, En));
    }

    private static readonly string[] Keys =
    [
        "Menu_BackfillTags", "Summary_BackfillTags", "Backfill_PickFolder", "Prog_BackfillPreparing", "Prog_BackfillScanning",
        "Prog_BackfillReading", "Backfill_Filled", "Backfill_Ai", "Backfill_AiHidden", "Backfill_Already", "Backfill_NoMatch",
        "Backfill_Skipped", "Backfill_NoTags", "Backfill_CloudOnly", "Backfill_FolderUnreadable", "Backfill_TagsFileUnavailable",
        "Backfill_AlreadyRunning", "Backfill_NothingToFill",
    ];

    [Fact] // [AC-gh-20-backfill-tags-10]
    public void NewBackfillKeys_InAllSevenLocales_AndReadme()
    {
        var dirs = Directory.GetDirectories(Path.Combine(RuntimeTargetTests.RepoRoot(), "Strings"));
        Assert.Equal(7, dirs.Length);
        static int Holes(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Distinct().Count();
        foreach (var d in dirs)
        {
            var doc = System.Xml.Linq.XDocument.Load(Path.Combine(d, "Resources.resw"));
            foreach (var k in Keys)
            {
                string v = doc.Root!.Elements("data").Single(x => (string?)x.Attribute("name") == k).Element("value")!.Value;
                Assert.False(string.IsNullOrWhiteSpace(v), $"{d}: {k}");
                Assert.Equal(Holes(En(k)), Holes(v));
            }
        }
        Assert.Equal("Backfill tags from folder…", En("Menu_BackfillTags"));
        string readme = Src("README.md");
        Assert.Contains("Backfill tags from folder", readme);
        Assert.Contains("organized", readme);
    }
}
