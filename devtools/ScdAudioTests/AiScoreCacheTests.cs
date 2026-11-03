using Pickles_Playlist_Editor.Utils;
using Xunit;

namespace ScdAudioTests;

public sealed class AiScoreCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "aicache-" + Guid.NewGuid().ToString("N"));
    private AiScoreCache New(string name = "c.dat") => new(Path.Combine(_dir, name));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact] // [AC-ai-score-4]
    public void StoreAndGet_SurvivesReopen()
    {
        New().Store(@"C:\Mods\X\a.scd", 812);
        Assert.Equal(812, New().TryGet(@"C:\Mods\X\a.scd"));
        Assert.Null(New().TryGet(@"C:\Mods\X\b.scd"));
    }

    [Fact] // [AC-ai-score-8] [EDGE-ai-score-41] [EDGE-ai-score-49]
    public void Key_NormalisesSeparatorsCaseAndDoubles()
    {
        var c = New();
        c.Store("C:/Mods/X//pl/Song.scd", 512);
        Assert.Equal(512, c.TryGet(@"c:\mods\x\pl\song.scd"));
        c.Forget(@"C:\Mods\X\pl\Song.scd ");
        Assert.Null(c.TryGet("C:/Mods/X/pl/Song.scd"));

        c.Store(@"C:\Mods\X\pl\A.scd", 100);
        c.UpdateCacheForSCD("c:/mods/x/pl/a.scd", @"C:\Mods\X\other\A.scd");
        Assert.Equal(100, c.TryGet("C:/Mods/X/other/A.scd"));
        Assert.Null(c.TryGet(@"C:\Mods\X\pl\A.scd"));

        Assert.StartsWith(@"\\server\share", AiScoreCache.Key(@"\\server\share\a.scd"));
    }

    [Fact] // [AC-ai-score-8] [EDGE-ai-score-23]
    public void Move_FollowsTheFile_AndBack()
    {
        var c = New();
        c.Store(@"C:\M\a.scd", 900);
        c.UpdateCacheForSCD(@"C:\M\a.scd", @"C:\M\b.scd");
        Assert.Equal(900, c.TryGet(@"C:\M\b.scd"));
        c.UpdateCacheForSCD(@"C:\M\b.scd", @"C:\M\a.scd"); // rollback
        Assert.Equal(900, c.TryGet(@"C:\M\a.scd"));
        Assert.Null(c.TryGet(@"C:\M\b.scd"));
    }

    [Fact] // [AC-ai-score-8] [EDGE-ai-score-41]
    public void DeletedSongThenRenameIntoItsPath_NoStaleScore()
    {
        var c = New();
        c.Store(@"C:\M\P.scd", 950);       // song A at P
        c.Forget(@"C:\M\P.scd");           // A deleted
        c.Store(@"C:\M\Q.scd", 120);       // song B at Q
        c.UpdateCacheForSCD(@"C:\M\Q.scd", @"C:\M\P.scd");
        Assert.Equal(120, c.TryGet(@"C:\M\P.scd"));

        c.UpdateCacheForSCD(@"C:\M\R-unscored.scd", @"C:\M\P.scd"); // an unscored song moved onto P
        Assert.Null(c.TryGet(@"C:\M\P.scd"));
    }

    [Fact] // [EDGE-ai-score-24]
    public void Forget_RemovesOnlyThatPath()
    {
        var c = New();
        c.Store(@"C:\M\a.scd", 1);
        c.Store(@"C:\M\b.scd", 2);
        c.Forget(@"C:\M\a.scd");
        Assert.Null(c.TryGet(@"C:\M\a.scd"));
        Assert.Equal(2, c.TryGet(@"C:\M\b.scd"));
    }

    [Fact] // [EDGE-ai-score-26]
    public void CorruptFile_Recreated_AndStillUsable()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "bad.dat");
        File.WriteAllText(path, "{ not json");
        var c = New("bad.dat");
        c.Store(@"C:\M\a.scd", 5);
        Assert.Equal(5, c.TryGet(@"C:\M\a.scd"));
    }

    [Fact] // [AC-ai-score-1] lookups, moves and forgets never create a cache file; only a stored score does
    public void NoFile_UntilAScoreIsStored()
    {
        string path = Path.Combine(_dir, "lazy.dat");
        var c = new AiScoreCache(path);
        Assert.Null(c.TryGet(@"C:\M\a.scd"));
        c.Forget(@"C:\M\a.scd");
        c.UpdateCacheForSCD(@"C:\M\a.scd", @"C:\M\b.scd");
        Assert.False(File.Exists(path));
        c.Store(@"C:\M\a.scd", 3);
        Assert.True(File.Exists(path));
    }

    [Fact] // [EDGE-ai-score-26] a cache that can't be opened is skipped, never thrown
    public void UnopenableFile_NullSafe()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file where a folder should be");
        var c = new AiScoreCache(Path.Combine(blocker, "c.dat"));
        Assert.Null(Record.Exception(() =>
        {
            c.Store(@"C:\M\a.scd", 1);
            c.TryGet(@"C:\M\a.scd"); // may be kept in memory; nothing reaches disk
            c.Forget(@"C:\M\a.scd");
            c.UpdateCacheForSCD(@"C:\M\a.scd", @"C:\M\b.scd");
        }));
        Assert.False(File.Exists(Path.Combine(blocker, "c.dat")));
    }

    [Fact] // [EDGE-ai-score-27] two Compute Stats runs writing at once
    public void ConcurrentStores_NoCorruption()
    {
        var c = New();
        Parallel.For(0, 200, i => c.Store($@"C:\M\{i}.scd", i));
        var reopened = New();
        for (int i = 0; i < 200; i++) Assert.Equal(i, reopened.TryGet($@"C:\M\{i}.scd"));
    }

    [Fact] // [EDGE-ai-score-25] a new model tag gets its own empty cache file
    public void DefaultCache_IsNamedForTheModelTag()
    {
        string src = File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), "Utils", "AiScoreCache.cs"));
        Assert.Contains("ai_score_cache_{AiAssets.Model.Tag}.dat", src);
    }
}
