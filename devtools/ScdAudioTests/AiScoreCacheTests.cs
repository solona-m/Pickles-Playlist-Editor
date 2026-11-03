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

    [Fact] // [EDGE-ai-score-25] a new model tag gets its own empty cache file
    public void DefaultCache_IsNamedForTheModelTag()
    {
        string src = File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), "Utils", "AiScoreCache.cs"));
        Assert.Contains("ai_score_cache_{AiAssets.Model.Tag}.dat", src);
    }
}
