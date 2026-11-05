using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NAudio.Vorbis;
using Pickles_Playlist_Editor;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;
using Xunit;

namespace ScdAudioTests;

/// <summary>Builds tagged Vorbis SCDs with the bundled ffmpeg, the way ImportOggAsIs wraps an ogg (no Settings writes).</summary>
internal static class TaggedScd
{
    public static string Ogg(AdpcmScdFixture fx, string name, string extraArgs = "", double seconds = 10)
    {
        string ogg = fx.PathFor(name + ".ogg");
        string dur = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AdpcmScdFixture.RunFfmpeg($"-y -v error -f lavfi -i anoisesrc=d={dur}:a=0.3 {extraArgs} -c:a libvorbis -q:a 7 \"{ogg}\"");
        return ogg;
    }

    public static string Wrap(AdpcmScdFixture fx, string ogg, string name)
    {
        byte[] oggBytes = File.ReadAllBytes(ogg);
        var (scd, old) = AdpcmScdFixture.LoadDefault();
        int ch, rate;
        using (var r = new NVorbis.VorbisReader(new MemoryStream(oggBytes), true)) { ch = r.Channels; rate = r.SampleRate; }
        var entry = new ScdAudioEntry(old, 0, ch, rate, SscfWaveFormat.Vorbis);
        entry.Data = new ScdVorbis(oggBytes, entry);
        scd.Replace(old, entry);
        string path = fx.PathFor(name + ".scd");
        using (var bw = new BinaryWriter(File.Create(path))) scd.Write(bw);
        return path;
    }

    public const string WestCoastTags =
        "-metadata artist=\"Emotional Oranges\" -metadata album=\"West Coast Love\" -metadata title=\"West Coast Love\" " +
        "-metadata comment=\"Converted by SpotiDownloader.com\" -metadata date=1970";
}

[Collection("ffmpeg")]
public sealed class SongTagReaderTests : IDisposable
{
    private readonly AdpcmScdFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    private string Tagged() => TaggedScd.Wrap(_fx, TaggedScd.Ogg(_fx, "wcl", TaggedScd.WestCoastTags), "wcl");

    [Fact] // [AC-song-tags-tooltip-1] [AC-song-tags-tooltip-8] [EDGE-song-tags-tooltip-5]
    public void Reader_TaggedVorbisScd_ReturnsArtistAlbumTitleOnly()
    {
        var t = SongTagReader.Read(Tagged());
        Assert.Equal(new SongTags("Emotional Oranges", "West Coast Love", "West Coast Love"), t);
        string all = t!.Value.Artist + t.Value.Album + t.Value.Title;
        Assert.DoesNotContain("SpotiDownloader", all);
        Assert.DoesNotContain("1970", all);
        Assert.DoesNotContain("Lavc", all);
    }

    [Fact] // [AC-song-tags-tooltip-5]
    public void Reader_PrefixGivesSameTagsAsFullFile()
    {
        string scd = Tagged();
        Assert.True(new FileInfo(scd).Length > SongTagReader.PrefixBytes, "fixture must be bigger than the prefix");
        byte[] prefix = SongTagReader.ReadPrefix(scd, SongTagReader.PrefixBytes);
        Assert.Equal(SongTagReader.PrefixBytes, prefix.Length);
        var fromPrefix = SongTagReader.FromScdBytes(prefix);
        Assert.NotNull(fromPrefix);
        Assert.Equal(SongTagReader.FromScdBytes(File.ReadAllBytes(scd)), fromPrefix);

        string small = _fx.PathFor("small.bin");
        File.WriteAllBytes(small, new byte[1000]);
        Assert.Equal(1000, SongTagReader.ReadPrefix(small, SongTagReader.PrefixBytes).Length);
    }

    [Fact] // [EDGE-song-tags-tooltip-22]
    public void Reader_CommentPastPrefix_FallsBackToFullRead()
    {
        string meta = _fx.PathFor("meta.txt");
        File.WriteAllText(meta, ";FFMETADATA1\nartist=Big Artist\nalbum=Big Album\ncomment=" + new string('x', 100_000) + "\n");
        string ogg = TaggedScd.Ogg(_fx, "big", $"-i \"{meta}\" -map_metadata 1");
        string scd = TaggedScd.Wrap(_fx, ogg, "big");
        Assert.Null(SongTagReader.FromScdBytes(SongTagReader.ReadPrefix(scd, SongTagReader.PrefixBytes)));
        var t = SongTagReader.Read(scd);
        Assert.Equal("Big Artist", t?.Artist);
        Assert.Equal("Big Album", t?.Album);
    }

    [Fact] // [EDGE-song-tags-tooltip-8]
    public void ReadPrefix_ClosesHandleBeforeReturning()
    {
        string scd = Tagged();
        SongTagReader.ReadPrefix(scd, SongTagReader.PrefixBytes);
        using (new FileStream(scd, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Move(scd, scd + ".moved");
        Assert.True(File.Exists(scd + ".moved"));
    }

    [Fact] // [EDGE-song-tags-tooltip-1]
    public void Reader_AdpcmScd_ReturnsNone()
    {
        var (fmt, data) = _fx.MakeAdpcm(2, 44100);
        string scd = _fx.WriteAdpcmScd(fmt, data, 2, 44100);
        Assert.Equal(SongTags.None, SongTagReader.Read(scd));
    }

    [Fact] // [EDGE-song-tags-tooltip-6]
    public void Reader_CorruptBytes_ReturnsNullNoThrow()
    {
        var rnd = new Random(7);
        byte[] small = new byte[500]; rnd.NextBytes(small);
        byte[] big = new byte[70 * 1024]; rnd.NextBytes(big);
        Assert.Null(SongTagReader.FromScdBytes(small));
        Assert.Null(SongTagReader.FromScdBytes(big));
        string path = _fx.PathFor("garbage.scd");
        File.WriteAllBytes(path, big);
        Assert.Null(SongTagReader.Read(path));
    }

    [Fact] // [EDGE-song-tags-tooltip-7]
    public void Reader_MissingOrLockedFile_ReturnsNull()
    {
        Assert.Null(SongTagReader.Read(_fx.PathFor("missing.scd")));
        string scd = Tagged();
        using (new FileStream(scd, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(SongTagReader.Read(scd));
    }

    [Fact] // [AC-song-tags-tooltip-6] [EDGE-song-tags-tooltip-20]
    public void FromScdBytes_EqualsReadFromDisk()
    {
        string scd = Tagged();
        Assert.Equal(SongTagReader.Read(scd), SongTagReader.FromScdBytes(File.ReadAllBytes(scd)));
    }

    [Fact] // [EDGE-song-tags-tooltip-19]
    public void NormalizeAndEqualize_KeepTags()
    {
        string ogg = TaggedScd.Ogg(_fx, "edit", TaggedScd.WestCoastTags, seconds: 3);
        var before = SongTagReader.FromOgg(File.ReadAllBytes(ogg));
        Assert.Equal("Emotional Oranges", before?.Artist);
        FFMpeg.NormalizeVolume(ogg);
        Assert.Equal(before, SongTagReader.FromOgg(File.ReadAllBytes(ogg)));
        FFMpeg.Equalize(ogg, "equalizer=f=1000:t=q:w=1:g=2");
        Assert.Equal(before, SongTagReader.FromOgg(File.ReadAllBytes(ogg)));
    }
}

public sealed class SongTagsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "tags-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_dir, "song_tags_cache_v2.json");
    private SongTagCache Cache() => new(CachePath);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly SongTags Wcl = new("Emotional Oranges", "West Coast Love", "West Coast Love");

    [Fact] // [EDGE-song-tags-tooltip-2]
    public void From_MultipleValues_JoinedDistinctInOrder()
    {
        Assert.Equal("A, b", SongTags.From(["A", "b", "a", " "], [], []).Artist);
    }

    [Fact] // [EDGE-song-tags-tooltip-3] [EDGE-song-tags-tooltip-4]
    public void From_SanitizesBlankControlAndLength()
    {
        Assert.Equal("", SongTags.From(["   "], [], []).Artist);
        Assert.Equal("X Y Z", SongTags.From(["X\r\nY\tZ"], [], []).Artist);
        Assert.Equal("X Y", SongTags.From(["X\u001FY"], [], []).Artist);
        string capped = SongTags.From([new string('a', 300)], [], []).Artist;
        Assert.Equal(new string('a', 200) + "…", capped);
        // an emoji straddling the cap is not split, and the value still saves and reloads
        string emoji = SongTags.From([new string('a', 199) + string.Concat(Enumerable.Repeat("😀", 10))], [], []).Artist;
        Assert.Equal(new string('a', 199) + "…", emoji);
        var c = Cache();
        c.Store(@"C:\M\e.scd", new SongTags(emoji, "", ""));
        Assert.Equal(emoji, Cache().TryGet(@"C:\M\e.scd")?.Artist);
    }

    [Fact] // [AC-song-tags-tooltip-5] [EDGE-song-tags-tooltip-4]
    public void Cache_StoreFlushReopen_RoundTrip_EmptyIsKnown()
    {
        var odd = new SongTags("Quote \" and \\ back", "日本語のアルバム", "t");
        var c = Cache();
        Assert.Null(c.TryGet(@"C:\M\a.scd"));
        Assert.False(File.Exists(CachePath)); // a lookup creates no file
        c.Store(@"C:\M\a.scd", odd);
        c.Store(@"C:\M\b.scd", SongTags.None);
        var r = Cache();
        Assert.Equal(odd, r.TryGet(@"C:\M\a.scd"));
        Assert.Equal(SongTags.None, r.TryGet(@"C:\M\b.scd"));
    }

    [Fact] // [AC-song-tags-tooltip-5]
    public void Cache_ScanStoresBatched_SingleOpsWriteThrough()
    {
        var c = Cache();
        for (int i = 0; i < 5; i++) Assert.True(c.StoreIfUnchanged($@"C:\M\{i}.scd", Wcl, c.Generation($@"C:\M\{i}.scd")));
        Assert.Null(Cache().TryGet(@"C:\M\0.scd"));
        c.Flush();
        for (int i = 0; i < 5; i++) Assert.Equal(Wcl, Cache().TryGet($@"C:\M\{i}.scd"));

        c.Store(@"C:\M\s.scd", Wcl);
        Assert.Equal(Wcl, Cache().TryGet(@"C:\M\s.scd"));
        c.Forget(@"C:\M\s.scd");
        Assert.Null(Cache().TryGet(@"C:\M\s.scd"));
        c.UpdateCacheForSCD(@"C:\M\0.scd", @"C:\M\moved.scd");
        Assert.Equal(Wcl, Cache().TryGet(@"C:\M\moved.scd"));
        Assert.Null(Cache().TryGet(@"C:\M\0.scd"));
    }

    [Theory] // [EDGE-song-tags-tooltip-17] [EDGE-gh-20-source-tags-ai-marker-16]
    [InlineData("{ not json", ".bad")]
    [InlineData("{\"version\":99,\"songs\":{\"c:\\\\m\\\\a.scd\":[\"x\",\"y\",\"z\"]}}", ".v99.bak")]
    public void Cache_CorruptOrWrongVersion_LoadsEmpty_ThenReplaced(string content, string asideSuffix)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, content);
        var c = Cache();
        Assert.Null(c.TryGet(@"C:\M\a.scd"));
        Assert.Equal(content, File.ReadAllText(CachePath + asideSuffix)); // renamed aside, never overwritten
        c.Store(@"C:\M\b.scd", Wcl);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(CachePath));
        Assert.Equal(2, root!["version"]!.GetValue<int>());
        Assert.Equal(Wcl, Cache().TryGet(@"C:\M\b.scd"));
    }

    [Fact] // [AC-song-tags-tooltip-7]
    public void Cache_KeyNormalised_MovesAndForgets()
    {
        var c = Cache();
        c.Store("C:/Mods/X//pl/Song.scd", Wcl);
        Assert.Equal(Wcl, c.TryGet(@"c:\mods\x\pl\song.scd"));
        c.Store(@"C:\M\dest.scd", SongTags.None);
        c.UpdateCacheForSCD(@"C:\M\nosource.scd", @"C:\M\dest.scd");
        Assert.Null(c.TryGet(@"C:\M\dest.scd")); // a destination with no source is cleared
        c.UpdateCacheForSCD(@"c:/mods/x/pl/song.scd", @"C:\M\new.scd");
        Assert.Equal(Wcl, c.TryGet(@"C:\M\new.scd"));
        c.Forget(@"C:\M\NEW.scd");
        Assert.Null(c.TryGet(@"C:\M\new.scd"));
    }

    [Fact] // [EDGE-song-tags-tooltip-8]
    public void Cache_StoreIfUnchanged_RefusedAfterMoveOrForget()
    {
        var c = Cache();
        long g = c.Generation(@"C:\M\p.scd");
        c.UpdateCacheForSCD(@"C:\M\y.scd", @"C:\M\p.scd");
        Assert.False(c.StoreIfUnchanged(@"C:\M\p.scd", Wcl, g));
        g = c.Generation(@"C:\M\q.scd");
        c.Forget(@"C:\M\q.scd");
        Assert.False(c.StoreIfUnchanged(@"C:\M\q.scd", Wcl, g));
        Assert.Null(c.TryGet(@"C:\M\q.scd"));
    }

    [Fact] // [EDGE-song-tags-tooltip-27]
    public void Cache_ModRootKeys_MatchAndNullRootNeverThrows()
    {
        const string root = @"E:\Penumbra\Mod";
        foreach (var rel in new[] { "pl/a.scd", @"pl\a.scd" })
            Assert.Equal(AiScoreCache.Key(Path.Combine(root, Playlist.NormalizeRelativeModPath(rel))),
                AiScoreCache.Key(SongTagCache.FullPathOf(rel, root)));
        var c = Cache();
        c.Store(SongTagCache.FullPathOf("pl/a.scd", root), Wcl);
        Assert.Equal(Wcl, c.TryGetForRel(@"pl\a.scd", root));
        Assert.Null(SongTagCache.ModRootOrNull(null, "Mod"));
        Assert.Null(SongTagCache.ModRootOrNull(@"E:\Penumbra", " "));
        Assert.Equal(root, SongTagCache.ModRootOrNull(@"E:\Penumbra", "Mod"));
        Assert.Null(Record.Exception(() => c.TryGetForRel("pl/a.scd", null)));
        Assert.Null(c.TryGetForRel("", root));
    }

    [Fact] // [EDGE-song-tags-tooltip-28]
    public void Cache_IoFailureOnLoad_NoWriteThenRetries()
    {
        var seed = Cache();
        seed.Store(@"C:\M\a.scd", Wcl);
        byte[] before = File.ReadAllBytes(CachePath);
        var c = Cache();
        using (new FileStream(CachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(c.TryGet(@"C:\M\a.scd"));
            Assert.Null(Record.Exception(() => c.Store(@"C:\M\b.scd", SongTags.None)));
        }
        Assert.Equal(before, File.ReadAllBytes(CachePath));
        Assert.Equal(Wcl, c.TryGet(@"C:\M\a.scd"));
        Assert.Equal(SongTags.None, c.TryGet(@"C:\M\b.scd"));
        c.Flush();
        var f = Cache();
        Assert.Equal(Wcl, f.TryGet(@"C:\M\a.scd"));
        Assert.Equal(SongTags.None, f.TryGet(@"C:\M\b.scd"));
    }

    [Fact] // [EDGE-song-tags-tooltip-28] a delete or move made while the file was locked is not undone by the later load
    public void Cache_ForgetOrMoveWhileUnloaded_NotUndoneByLoad()
    {
        var seed = Cache();
        seed.Store(@"C:\M\gone.scd", Wcl);
        seed.Store(@"C:\M\from.scd", Wcl);
        var c = Cache();
        using (new FileStream(CachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            c.Forget(@"C:\M\gone.scd");
            c.UpdateCacheForSCD(@"C:\M\from.scd", @"C:\M\to.scd");
        }
        Assert.Null(c.TryGet(@"C:\M\gone.scd"));
        Assert.Null(c.TryGet(@"C:\M\from.scd")); // no stale tags at the old path
        Assert.Null(c.TryGet(@"C:\M\to.scd"));   // read again by the next scan
        c.Flush();
        Assert.Null(Cache().TryGet(@"C:\M\gone.scd"));
    }

    // ── Scan ──

    [Fact] // [AC-song-tags-tooltip-5] [EDGE-song-tags-tooltip-9]
    public void Scan_ReadsOnlyUnknown_DistinctPaths_StoresAll()
    {
        var c = Cache();
        c.Store(@"C:\M\known.scd", Wcl);
        var calls = new List<string>();
        var r = SongTagScan.Run([@"C:\M\known.scd", @"C:\M\a.scd", "c:/m/A.scd", @"C:\M\b.scd"], c,
            p => { calls.Add(p); return Wcl; }, CancellationToken.None);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new SongTagScanResult(2, 2, 0, 1, 0), r);
        Assert.Equal(Wcl, Cache().TryGet(@"C:\M\b.scd")); // flushed at the end
    }

    [Fact] // [AC-song-tags-tooltip-5]
    public void Scan_FlushesEvery100()
    {
        var c = Cache();
        var paths = Enumerable.Range(0, 250).Select(i => $@"C:\M\{i}.scd").ToList();
        var seen = new Dictionary<int, int>();
        SongTagScan.Run(paths, c, _ => Wcl, CancellationToken.None, n =>
        {
            if (n is 101 or 201) seen[n] = paths.Count(p => Cache().TryGet(p) != null);
        });
        Assert.True(seen[101] >= 100);
        Assert.True(seen[201] >= 200);
    }

    [Fact] // [EDGE-song-tags-tooltip-7]
    public void Scan_UnreadableNotCached_ContinuesWithRest()
    {
        var c = Cache();
        var r = SongTagScan.Run([@"C:\M\a.scd", @"C:\M\bad.scd", @"C:\M\c.scd"], c,
            p => p.Contains("bad") ? null : Wcl, CancellationToken.None);
        Assert.Equal(1, r.Unreadable);
        Assert.Null(c.TryGet(@"C:\M\bad.scd"));
        Assert.Equal(Wcl, c.TryGet(@"C:\M\c.scd"));
    }

    [Fact] // [EDGE-song-tags-tooltip-23]
    public void Scan_ReadThrows_CountedUnreadable_Continues()
    {
        var c = Cache();
        var r = SongTagScan.Run([@"C:\M\a.scd", @"C:\M\boom.scd", @"C:\M\c.scd"], c,
            p => p.Contains("boom") ? throw new InvalidOperationException("odd") : Wcl, CancellationToken.None);
        Assert.Equal(1, r.Unreadable);
        Assert.Equal(2, r.Read);
        Assert.Null(c.TryGet(@"C:\M\boom.scd"));
        Assert.Equal(Wcl, c.TryGet(@"C:\M\c.scd"));
    }

    [Fact] // [EDGE-song-tags-tooltip-14] [EDGE-song-tags-tooltip-24]
    public void Scan_Cancelled_ThrowsAndKeepsFlushedStores()
    {
        var c = Cache();
        using var cts = new CancellationTokenSource();
        var paths = Enumerable.Range(0, 5).Select(i => $@"C:\M\{i}.scd").ToList();
        Assert.Throws<OperationCanceledException>(() =>
            SongTagScan.Run(paths, c, _ => Wcl, cts.Token, n => { if (n == 2) cts.Cancel(); }));
        var fresh = Cache();
        Assert.Equal(2, paths.Count(p => fresh.TryGet(p) != null));
    }

    [Fact] // [EDGE-song-tags-tooltip-8]
    public void Scan_PathForgottenDuringRead_Refused()
    {
        var c = Cache();
        var r = SongTagScan.Run([@"C:\M\a.scd"], c, p => { c.Forget(p); return Wcl; }, CancellationToken.None);
        Assert.Equal(1, r.Refused);
        Assert.Null(c.TryGet(@"C:\M\a.scd"));
    }

    [Fact] // [AC-song-tags-tooltip-5]
    public void ThreadPriorityScope_Background_RestoresAndRepeats()
    {
        Exception? error = null;
        ThreadPriority after1 = default, after2 = default;
        var t = new Thread(() =>
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
                using (ThreadPriorityScope.Background()) { }
                after1 = Thread.CurrentThread.Priority;
                using (ThreadPriorityScope.Background()) { }
                after2 = Thread.CurrentThread.Priority;
            }
            catch (Exception ex) { error = ex; }
        });
        t.Start();
        t.Join();
        Assert.Null(error);
        Assert.Equal(ThreadPriority.AboveNormal, after1);
        Assert.Equal(ThreadPriority.AboveNormal, after2);
        string src = File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), "Utils", "ThreadPriorityScope.cs"));
        string dispose = src[src.IndexOf("public void Dispose()", StringComparison.Ordinal)..];
        Assert.True(dispose.IndexOf("ThreadModeBackgroundEnd", StringComparison.Ordinal) < dispose.IndexOf("Thread.CurrentThread.Priority = _saved", StringComparison.Ordinal));
    }

    // ── Tooltip and search ──

    private static Dictionary<string, string> En() =>
        XDocument.Load(Path.Combine(RuntimeTargetTests.RepoRoot(), "Strings", "en-US", "Resources.resw")).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");

    private static readonly Dictionary<string, string> s_en = En();
    private static string Get(string key) => s_en[key];

    [Fact] // [AC-song-tags-tooltip-1]
    public void Tooltip_ArtistAndAlbumLines()
    {
        Assert.Equal("Artist: Emotional Oranges\nAlbum: West Coast Love", SongTooltip.Build(Wcl, null, false, Get));
        Assert.Equal("Artist: A", SongTooltip.Build(new SongTags("A", "", ""), null, false, Get));
        Assert.Equal("Album: B", SongTooltip.Build(new SongTags("", "B", ""), null, false, Get));
    }

    [Fact] // [AC-song-tags-tooltip-8]
    public void Tooltip_AndSearch_IgnoreTitle()
    {
        var titleOnly = new SongTags("", "", "Secret Title");
        Assert.Null(SongTooltip.Build(titleOnly, null, false, Get));
        Assert.False(SongSearch.Matches("secret", "pl", "song", () => titleOnly));
    }

    [Theory] // [AC-song-tags-tooltip-2] [EDGE-song-tags-tooltip-13]
    [InlineData(230, 23)]
    [InlineData(0, 0)]
    [InlineData(509, 50)]
    [InlineData(510, 51)]
    [InlineData(1000, 100)]
    public void Tooltip_AiLineForEveryScore(int stored, int pct)
    {
        string tip = SongTooltip.Build(null, stored, true, Get)!;
        Assert.Equal($"AI likelihood: {pct}%. An estimate, not proof.", tip);
    }

    [Fact] // [AC-song-tags-tooltip-2] [EDGE-song-tags-tooltip-13]
    public void Tooltip_AiPercent_AgreesWithChip()
    {
        for (int s = 510; s <= 1000; s++)
            Assert.Equal(AiScoreDisplay.For(s, true).Percent, AiScoreDisplay.TooltipPercent(s, true));
    }

    [Fact] // [AC-song-tags-tooltip-3] [EDGE-song-tags-tooltip-12]
    public void Tooltip_NoAiLine_WhenOffUnscoredOrNoScore_NullWhenEmpty()
    {
        Assert.Equal("Artist: Emotional Oranges\nAlbum: West Coast Love", SongTooltip.Build(Wcl, 900, false, Get));
        Assert.Null(SongTooltip.Build(null, null, true, Get));
        Assert.Null(SongTooltip.Build(null, -1, true, Get));
        Assert.Null(SongTooltip.Build(SongTags.None, null, false, Get));
    }

    [Fact] // [EDGE-song-tags-tooltip-21]
    public void Tooltip_NoTags_WithScore_OnlyAiLine()
    {
        Assert.Equal("AI likelihood: 12%. An estimate, not proof.", SongTooltip.Build(SongTags.None, 120, true, Get));
    }

    [Fact] // [AC-song-tags-tooltip-4]
    public void Search_MatchesArtistOrAlbum_CaseInsensitive()
    {
        Assert.True(SongSearch.Matches("emotional", "Next 1", "west coast love_1", () => Wcl));
        Assert.True(SongSearch.Matches("WEST COAST", "Next 1", "x", () => new SongTags("", "West Coast Love", "")));
        Assert.False(SongSearch.Matches("zzz", "Next 1", "west coast love_1", () => Wcl));
    }

    [Fact] // [EDGE-song-tags-tooltip-11]
    public void Search_NameOrPlaylistMatch_SkipsTagLookup()
    {
        int lookups = 0;
        SongTags? Count() { lookups++; return null; }
        Assert.True(SongSearch.Matches("next", "Next 1", "x", Count));
        Assert.True(SongSearch.Matches("coast", "pl", "west coast love", Count));
        Assert.Equal(0, lookups);
        Assert.False(SongSearch.Matches("emotional", "pl", "west coast love", Count));
        Assert.Equal(1, lookups);
    }

    [Fact] // [EDGE-song-tags-tooltip-26]
    public void NodeContent_RaisesOnlyOnChange()
    {
        // PlaylistNodeContent needs the WinUI runtime for its brushes, so the equality guard is checked in source.
        string src = File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), "ui", "PlaylistNodeContent.cs"));
        foreach (var (prop, field) in new[] { ("SongTooltip", "_songTooltip"), ("SongHelpText", "_songHelpText") })
            Assert.Matches($@"public string\??\s+{prop}[\s\S]*?set \{{ if \(string\.Equals\({field}, value, StringComparison\.Ordinal\)\) return; {field} = value; PropertyChanged\?\.Invoke", src);
    }

    [Fact] // [AC-song-tags-tooltip-9]
    public void SongTipKeys_InAllSevenLocales_OnePlaceholder()
    {
        var dirs = Directory.GetDirectories(Path.Combine(RuntimeTargetTests.RepoRoot(), "Strings"));
        Assert.Equal(7, dirs.Length);
        foreach (var d in dirs)
        {
            var values = XDocument.Load(Path.Combine(d, "Resources.resw")).Root!.Elements("data")
                .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");
            foreach (var key in new[] { SongTooltip.ArtistKey, SongTooltip.AlbumKey })
            {
                Assert.True(values.TryGetValue(key, out var v) && v.Trim().Length > 3, $"{Path.GetFileName(d)} lacks {key}");
                Assert.Single(Regex.Matches(v!, @"\{\d\}"));
                Assert.Contains("{0}", v);
            }
        }
        Assert.Equal("Artist: {0}", s_en["SongTip_Artist"]);
        Assert.Equal("Album: {0}", s_en["SongTip_Album"]);
    }
}
