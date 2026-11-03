using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using Xunit;

namespace ScdAudioTests;

[Collection("ffmpeg")]
public sealed class AiScoreDecoderTests : IDisposable
{
    private readonly AudioFixtures _fx = new();

    public AiScoreDecoderTests() => Environment.CurrentDirectory = AppContext.BaseDirectory;

    public void Dispose() => _fx.Dispose();

    private static string BundledFfmpeg => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

    [Fact] // [AC-ai-score-9] [EDGE-ai-score-11] [EDGE-ai-score-47]
    public void ArgumentList_Pinned()
    {
        string odd = "C:\\Users\\Zoë \"Q\"\\a b\\song é.ogg";
        Assert.Equal(new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-i", odd, "-vn", "-ac", "1",
            "-af", "aresample=16000:resampler=soxr:precision=20", "-f", "f32le", "-",
        }, AiAudioDecoder.Arguments(odd));
    }

    [Fact] // [EDGE-ai-score-11] the essentials build has no soxr: fail loudly, never fall back to another resampler
    public void EssentialsFfmpeg_NoSoxr_Failed()
    {
        var r = AiAudioDecoder.DecodeAndScore(BundledFfmpeg, _fx.MakeSineOgg(8), _ => 0f, CancellationToken.None);
        Assert.Equal(AiScoreOutcome.Failed, r.Outcome);
        Assert.Contains("ffmpeg exited", r.Error);
    }

    [Fact] // [EDGE-ai-score-9]
    public void CorruptInput_FailedWithStderr()
    {
        string junk = Path.Combine(_fx.Dir, "junk.ogg");
        File.WriteAllBytes(junk, Enumerable.Repeat((byte)7, 4000).ToArray());
        var r = AiAudioDecoder.Run(BundledFfmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-i", junk, "-f", "f32le", "-"],
            _ => 0f, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(AiScoreOutcome.Failed, r.Outcome);
        Assert.Contains("ffmpeg exited", r.Error);
    }

    [Fact] // [EDGE-ai-score-10] [EDGE-ai-score-47] a path with spaces and non-ASCII reaches the process; the bundled
           // ffmpeg decodes without soxr so the plumbing (pipe, aggregate) is exercised end to end
    public void PipeAndOddPath_DecodeAndAggregate()
    {
        string odd = Path.Combine(_fx.Dir, "a b é (q)"); // Windows allows no quotes in names; the argument test covers them
        Directory.CreateDirectory(odd);
        string ogg = Path.Combine(odd, "tone é.ogg");
        File.Copy(_fx.MakeSineOgg(8), ogg);
        int calls = 0;
        var r = AiAudioDecoder.Run(BundledFfmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-i", ogg, "-ac", "1", "-ar", "16000", "-f", "f32le", "-"],
            _ => { calls++; return 2f; }, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(AiScoreOutcome.Scored, r.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(AiScoreMath.Sigmoid(2), r.Score!.Value, 6);
    }

    [Fact] // [EDGE-ai-score-10]
    public void Inactivity_KillsSilentProcess()
    {
        var started = DateTime.UtcNow;
        var r = AiAudioDecoder.Run("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep 60"], _ => 0f, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(AiScoreOutcome.Failed, r.Outcome);
        Assert.Contains("wrote nothing", r.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }

    [Fact] // [EDGE-ai-score-10] a slow but steady stream is never killed
    public void Streaming_NotKilled()
    {
        const string script = "$o=[Console]::OpenStandardOutput(); for($i=0;$i -lt 6;$i++){ $o.Write([byte[]](0,0,0,0),0,4); $o.Flush(); Start-Sleep -Milliseconds 900 }";
        var r = AiAudioDecoder.Run("powershell.exe", ["-NoProfile", "-Command", script], _ => 0f, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotEqual(AiScoreOutcome.Failed, r.Outcome); // finishes on its own: 6 samples is NoScore
        Assert.Equal(AiScoreOutcome.NoScore, r.Outcome);
    }

    [Fact] // [AC-ai-score-4]
    public void Cancel_KillsTheDecoder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            AiAudioDecoder.Run("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep 60"], _ => 0f, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact] // [AC-ai-score-4] the decoder child runs below normal priority
    public void Decoder_RunsBelowNormal()
    {
        string src = File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), "Tools", "AiAudioDecoder.cs"));
        Assert.Contains("PriorityClass = ProcessPriorityClass.BelowNormal", src);
        Assert.Contains("psi.ArgumentList.Add", src);
        Assert.DoesNotContain("FFMpeg.", src);
    }
}
