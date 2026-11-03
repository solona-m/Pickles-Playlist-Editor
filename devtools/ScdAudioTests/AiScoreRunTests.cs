using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;
using Xunit;

namespace ScdAudioTests;

[Collection("ffmpeg")]
public sealed class AiScoreRunTests : IDisposable
{
    private readonly AdpcmScdFixture _fx = new();
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "airun-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _fx.Dispose();
        try { Directory.Delete(_cacheDir, true); } catch { }
    }

    private AiScoreCache Cache() => new(Path.Combine(_cacheDir, "c.dat"));

    private string VorbisScd()
    {
        string path = _fx.PathFor("vorbis.scd");
        Directory.CreateDirectory(_fx.Dir);
        File.Copy(AdpcmScdFixture.DefaultScd, path, true);
        return path;
    }

    [Fact] // [AC-ai-score-4] [AC-ai-score-9]
    public void Scored_IsCachedAsTimesThousand_ThenServedFromCache()
    {
        var cache = Cache();
        int calls = 0;
        using var run = new AiScoreRun(cache, (_, _) => { calls++; return new(AiScoreOutcome.Scored, 0.8126, 2, 0, null); }, null);
        string scd = VorbisScd();
        Assert.Equal(AiSongOutcome.Scored, run.ScoreSong(scd));
        Assert.Equal(813, cache.TryGet(scd));
        Assert.Equal(AiSongOutcome.Cached, run.ScoreSong(scd));
        Assert.Equal(1, calls);
        Assert.Equal((1, 1, 0, 0), (run.Scored, run.Cached, run.Failed, run.NoScore));
    }

    [Fact] // [AC-ai-score-6] [EDGE-ai-score-1]
    public void NoScore_CachedAsMinusOne()
    {
        var cache = Cache();
        using var run = new AiScoreRun(cache, (_, _) => new(AiScoreOutcome.NoScore, null, 0, 0, null), null);
        string scd = VorbisScd();
        Assert.Equal(AiSongOutcome.NoScore, run.ScoreSong(scd));
        Assert.Equal(AiScoreMath.NoScore, cache.TryGet(scd));
    }

    [Fact] // [EDGE-ai-score-9] [EDGE-ai-score-13]
    public void Failed_NotCached_SoItRetries()
    {
        var cache = Cache();
        using var run = new AiScoreRun(cache, (_, _) => AiScoreResult.Fail("boom"), null);
        string scd = VorbisScd();
        Assert.Equal(AiSongOutcome.Failed, run.ScoreSong(scd));
        Assert.Null(cache.TryGet(scd));
        Assert.Equal(1, run.Failed);
    }

    [Fact] // [EDGE-ai-score-12] [EDGE-ai-score-43] unreadable, or moved away mid-pass: Failed, nothing thrown
    public void UnreadableScd_Failed_NeverThrows()
    {
        string bad = _fx.PathFor("bad.scd");
        Directory.CreateDirectory(_fx.Dir);
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });
        using var run = new AiScoreRun(Cache(), (_, _) => throw new Exception("not reached"), null);
        Assert.Equal(AiSongOutcome.Failed, run.ScoreSong(bad));
        Assert.Equal(AiSongOutcome.Failed, run.ScoreSong(_fx.PathFor("missing.scd")));
        Assert.Equal(AiSongOutcome.Failed, run.ScoreSong(""));
    }

    [Fact] // [AC-ai-score-9] [EDGE-ai-score-12]
    public void Vorbis_PassesRawOggBytes_TempFilesDeleted()
    {
        string scd = VorbisScd();
        var stored = ((ScdVorbis)ScdFile.Import(AdpcmScdFixture.DefaultScdCopy()).Audio[0].Data).Data;
        string? seen = null;
        byte[]? bytes = null;
        using var run = new AiScoreRun(Cache(), (path, _) => { seen = path; bytes = File.ReadAllBytes(path); return new(AiScoreOutcome.Scored, 0.5, 1, 0, null); }, null);
        run.ScoreSong(scd);
        Assert.EndsWith(".ogg", seen);
        Assert.Equal(stored, bytes);
        Assert.False(File.Exists(seen));
    }

    [Fact] // [AC-ai-score-9] [EDGE-ai-score-12] ADPCM is scored from a WAV of its own data, not a Vorbis re-encode
    public void Adpcm_DecodedFromWav_NoBundledFfmpeg()
    {
        var (fmt, data) = _fx.MakeAdpcm(2, 44100);
        string scd = _fx.WriteAdpcmScd(fmt, data, 2, 44100);
        var entry = (ScdAdpcm)ScdFile.Import(AdpcmScdFixture.CopyOf(scd)).Audio[0].Data;
        string? seen = null;
        byte[]? bytes = null;
        using var run = new AiScoreRun(Cache(), (path, _) => { seen = path; bytes = File.ReadAllBytes(path); return new(AiScoreOutcome.Scored, 0.5, 1, 0, null); }, null);
        Assert.Equal(AiSongOutcome.Scored, run.ScoreSong(scd));
        Assert.EndsWith(".wav", seen);
        Assert.Equal(AdpcmWav.Build(entry.WaveHeader, entry.Data), bytes);
        Assert.False(File.Exists(seen));

        foreach (var f in new[] { "Tools/AiScoreRun.cs", "Tools/AiAudioDecoder.cs" })
        {
            string src = File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), f));
            Assert.DoesNotContain("FFMpeg.", src);
            Assert.DoesNotContain("WithExtractedOgg", src);
        }
    }

    [Fact] // [EDGE-ai-score-16]
    public void ModelLoadFailure_UnavailableNotThrown()
    {
        using var run = AiScoreRun.Begin(Cache(), () => throw new DllNotFoundException("onnxruntime.dll"));
        Assert.Contains("DllNotFoundException", run.UnavailableReason);
        Assert.Equal(AiSongOutcome.Unavailable, run.ScoreSong(VorbisScd()));
        Assert.Equal(0, SonicsModel.ActiveLeases);
    }

    [Fact] // [AC-ai-score-8] [EDGE-ai-score-43] the song is moved and another takes its path while it is scored
    public void PathChangesHandsMidScore_ResultDropped()
    {
        var cache = Cache();
        string scd = VorbisScd();
        string other = _fx.PathFor("other.scd");
        cache.Store(other, 120);
        using var run = new AiScoreRun(cache, (_, _) =>
        {
            cache.UpdateCacheForSCD(scd, _fx.PathFor("moved.scd")); // X moves away
            cache.UpdateCacheForSCD(other, scd);                    // Y moves into X's old path
            return new(AiScoreOutcome.Scored, 0.97, 1, 0, null);
        }, null);
        Assert.Equal(AiSongOutcome.Failed, run.ScoreSong(scd));
        Assert.Equal(120, cache.TryGet(scd)); // still Y's score, not X's 970
    }

    [Fact] // [EDGE-ai-score-48]
    public void AllCached_NoDecoderCalls()
    {
        var cache = Cache();
        string scd = VorbisScd();
        cache.Store(scd, 42);
        using var run = new AiScoreRun(cache, (_, _) => throw new Exception("must not decode"), null);
        Assert.Equal(AiSongOutcome.Cached, run.ScoreSong(scd));
    }

    [Fact] // [EDGE-ai-score-31] scoring runs below normal priority and restores it
    public void ScoreSong_RunsBelowNormal_ThenRestores()
    {
        ThreadPriority during = ThreadPriority.Highest, after = ThreadPriority.Highest;
        var t = new Thread(() =>
        {
            using var run = new AiScoreRun(Cache(), (_, _) => { during = Thread.CurrentThread.Priority; return AiScoreResult.Fail("x"); }, null);
            run.ScoreSong(VorbisScd());
            after = Thread.CurrentThread.Priority;
        });
        t.Start();
        t.Join();
        Assert.Equal(ThreadPriority.BelowNormal, during);
        Assert.Equal(ThreadPriority.Normal, after);
    }

    [SkippableFact] // [AC-ai-score-9] the real pipeline on an ADPCM SCD (full ffmpeg decodes the WAV)
    public void RealModel_AdpcmScd_Scores()
    {
        string? dir = Environment.GetEnvironmentVariable("PICKLES_AISCAN_DIR");
        Skip.If(string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir!, AiAssets.Model.Name)), "PICKLES_AISCAN_DIR not set");
        var (fmt, data) = _fx.MakeAdpcm(2, 44100, seconds: 8, noise: true);
        string scd = _fx.WriteAdpcmScd(fmt, data, 2, 44100);
        using var run = AiScoreRun.Begin(Cache(), () => SonicsModel.Load(Path.Combine(dir!, AiAssets.Model.Name)), Path.Combine(dir!, AiAssets.Ffmpeg.Name));
        Assert.Null(run.UnavailableReason);
        Assert.Equal(AiSongOutcome.Scored, run.ScoreSong(scd));
    }
}
