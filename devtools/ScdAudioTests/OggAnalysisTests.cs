using Pickles_Playlist_Editor.Utils;
using Xunit;

namespace ScdAudioTests;

[Collection("ffmpeg")]
public sealed class OggAnalysisTests : IDisposable
{
    private readonly AudioFixtures _fx = new();

    public OggAnalysisTests() => Environment.CurrentDirectory = AppContext.BaseDirectory;

    public void Dispose() => _fx.Dispose();

    [Theory] // [AC-x64-naudio-5]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(150)]
    public void ClickTrack_BpmWithinOne(int bpm)
    {
        var r = OggAnalysis.Analyze(_fx.MakeClickOgg(bpm, 30));
        Assert.InRange(r.Bpm, bpm - 1, bpm + 1);
    }

    [Fact] // [AC-x64-naudio-5] [EDGE-x64-naudio-13] libZPlay reported a 180 BPM click as half that.
    public void FastClickTrack_FoldsLikeLibZPlay()
    {
        var r = OggAnalysis.Analyze(_fx.MakeClickOgg(180, 30));
        Assert.InRange(r.Bpm, 89, 91);
    }

    [Fact] // [AC-x64-naudio-5] [EDGE-x64-naudio-13] SoundTouch alone finds nothing at 200 BPM; the half-rate retry does.
    public void VeryFastClickTrack_FoldsLikeLibZPlay()
    {
        var r = OggAnalysis.Analyze(_fx.MakeClickOgg(200, 30));
        Assert.InRange(r.Bpm, 99, 101);
    }

    [Theory] // [AC-x64-naudio-5] [EDGE-x64-naudio-15]
    [InlineData(1, 22050)]
    [InlineData(1, 48000)]
    [InlineData(2, 48000)]
    public void MonoAndOtherRates_Work(int channels, int rate)
    {
        var r = OggAnalysis.Analyze(_fx.MakeClickOgg(120, 30, channels, rate));
        Assert.InRange(r.Bpm, 119, 121);
    }

    [Fact] // [AC-x64-naudio-6]
    public void Duration_IsRoundedSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), OggAnalysis.Analyze(_fx.MakeSineOgg(7.2)).Duration);
        Assert.Equal(TimeSpan.FromSeconds(8), OggAnalysis.Analyze(_fx.MakeSineOgg(7.6)).Duration);
    }

    [Fact] // [AC-x64-naudio-5] [EDGE-x64-naudio-14]
    public void Silence_GivesZeroBpm_RealDuration()
    {
        var r = OggAnalysis.Analyze(_fx.MakeSilenceOgg(10));
        Assert.Equal(0, r.Bpm);
        Assert.Equal(TimeSpan.FromSeconds(10), r.Duration);
    }

    [Fact] // [AC-x64-naudio-7] [EDGE-x64-naudio-16]
    public void UnreadableFile_NeverThrows()
    {
        string junk = Path.Combine(_fx.Dir, "junk.ogg");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5 });
        Assert.Equal(new OggAnalysis.Result(0, TimeSpan.Zero), OggAnalysis.Analyze(junk));
        Assert.Equal(new OggAnalysis.Result(0, TimeSpan.Zero), OggAnalysis.Analyze(Path.Combine(_fx.Dir, "missing.ogg")));
    }

    [Fact] // [EDGE-x64-naudio-17]
    public void DetectBpm_StopsAtMaxLength()
    {
        // Past the window the beat changes for three times as long, which would win if the window were ignored.
        double window = OggAnalysis.MaxAnalysis.TotalSeconds;
        string a = _fx.MakeClickOgg(120, window + 1);
        string b = _fx.MakeClickOgg(97, 3 * window);
        string joined = Path.Combine(_fx.Dir, "joined.ogg");
        string list = Path.Combine(_fx.Dir, "list.txt");
        File.WriteAllText(list, $"file '{a.Replace("\\", "/")}'\nfile '{b.Replace("\\", "/")}'\n");
        AdpcmScdFixture.RunFfmpeg($"-y -v error -f concat -safe 0 -i \"{list}\" -c copy \"{joined}\"");
        var r = OggAnalysis.Analyze(joined);
        Assert.InRange(r.Bpm, 119, 121);
        Assert.InRange(r.Duration.TotalSeconds, 4 * window, 4 * window + 2);
    }

    [Theory] // [AC-x64-naudio-5]
    [InlineData(0f, 0)]
    [InlineData(-3f, 0)]
    [InlineData(float.NaN, 0)]
    [InlineData(120f, 120)]
    [InlineData(170.4f, 170)]
    [InlineData(171f, 86)]
    [InlineData(180f, 90)]
    [InlineData(380f, 95)]
    [InlineData(39f, 78)]
    [InlineData(40f, 40)]
    [InlineData(25f, 50)]
    public void Fold_KnownValues(float raw, int expected) => Assert.Equal(expected, OggAnalysis.FoldToLegacyRange(raw));
}
