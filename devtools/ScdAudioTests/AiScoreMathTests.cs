using Pickles_Playlist_Editor.Utils;
using Xunit;

namespace ScdAudioTests;

public class AiScoreMathTests
{
    private const int W = AiScoreMath.WindowSamples;

    private static MemoryStream F32(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return new MemoryStream(bytes);
    }

    private static float[] Tone(int n, float amp = 0.3f) =>
        Enumerable.Range(0, n).Select(i => amp * (float)Math.Sin(2 * Math.PI * 440 * i / 16000.0)).ToArray();

    /// <summary>A fake model: the logit is the window's first sample × 10, so each window can be told apart.</summary>
    private sealed class Probe
    {
        public readonly List<float[]> Windows = new();
        public Func<float[], float> Logit => w => { Windows.Add((float[])w.Clone()); return w[0] * 10; };
    }

    [Fact] // [AC-ai-score-9] [EDGE-ai-score-5]
    public void ExactMultiple_ScoresEveryWindow_NoPaddedExtra()
    {
        var samples = Tone(2 * W);
        samples[0] = 0.1f; samples[W] = -0.1f; // window logits +1 and -1
        var probe = new Probe();
        var r = AiScoreMath.Aggregate(F32(samples), probe.Logit, CancellationToken.None);
        Assert.Equal(AiScoreOutcome.Scored, r.Outcome);
        Assert.Equal(2, probe.Windows.Count);
        Assert.Equal((AiScoreMath.Sigmoid(1) + AiScoreMath.Sigmoid(-1)) / 2, r.Score!.Value, 6);
    }

    [Fact] // [AC-ai-score-9] [EDGE-ai-score-6]
    public void TrailingPartialWindow_Ignored()
    {
        var probe = new Probe();
        var r = AiScoreMath.Aggregate(F32(Tone(W + W / 2)), probe.Logit, CancellationToken.None);
        Assert.Single(probe.Windows);
        Assert.Equal(1, r.Windows);
    }

    [Fact] // [AC-ai-score-9] [EDGE-ai-score-4]
    public void ShortTrack_OneZeroPaddedWindow()
    {
        int n = 30 * 16000;
        var probe = new Probe();
        var r = AiScoreMath.Aggregate(F32(Tone(n)), probe.Logit, CancellationToken.None);
        Assert.Equal(AiScoreOutcome.Scored, r.Outcome);
        var w = Assert.Single(probe.Windows);
        Assert.Equal(W, w.Length);
        Assert.All(w.Skip(n), s => Assert.Equal(0f, s));
        Assert.NotEqual(0f, w[n - 2]);
    }

    [Fact] // [EDGE-ai-score-4] a quiet short track is judged on its real samples, not the padding
    public void QuietShortTrack_SilenceJudgedOnRealSamplesOnly()
    {
        // RMS ~0.0021 over 10 s; over the padded 120 s it would be ~0.0006, below the -60 dBFS line.
        var r = AiScoreMath.Aggregate(F32(Tone(10 * 16000, amp: 0.003f)), _ => 0f, CancellationToken.None);
        Assert.Equal(AiScoreOutcome.Scored, r.Outcome);
    }

    [Theory] // [AC-ai-score-1] [AC-ai-score-5] [EDGE-ai-score-22] off hides any score; otherwise band + raw
    [InlineData(812, false, false, "", 0)]
    [InlineData(812, true, true, "LikelyAi", 81)]
    [InlineData(700, true, true, "LikelyAi", 70)]
    [InlineData(699, true, false, "", 0)]
    [InlineData(300, true, false, "", 0)]
    [InlineData(455, true, false, "", 0)]
    [InlineData(0, true, false, "", 0)]
    [InlineData(-1, true, false, "", 0)]
    [InlineData(null, true, false, "", 0)]
    public void Display_For(int? stored, bool enabled, bool visible, string band, int percent)
    {
        var d = AiScoreDisplay.For(stored, enabled);
        Assert.Equal(visible, d.Visible);
        if (visible) { Assert.Equal(band, d.Band.ToString()); Assert.Equal(percent, d.Percent); }
    }

    [Theory] // near-certain scores never read as 0% or 100%
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(994, 99)]
    [InlineData(997, 99)]
    [InlineData(999, 99)]
    [InlineData(1000, 100)]
    public void Display_Percent_Clamps(int stored, int percent) => Assert.Equal(percent, AiScoreDisplay.Percent(stored));

    [Theory] // [AC-ai-score-1] the stored flag's forms
    [InlineData(null, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(1L, true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("junk", false)]
    public void ParseFlag_Forms(object? value, bool expected) => Assert.Equal(expected, Pickles_Playlist_Editor.Settings.ParseFlag(value, false));

    [Fact] // [AC-ai-score-6] [EDGE-ai-score-1]
    public void AllZero_NoScore()
    {
        var probe = new Probe();
        var r = AiScoreMath.Aggregate(F32(new float[2 * W]), probe.Logit, CancellationToken.None);
        Assert.Equal(AiScoreOutcome.NoScore, r.Outcome);
        Assert.Empty(probe.Windows);
        Assert.Equal(2, r.SkippedSilent);
    }

    [Fact] // [AC-ai-score-6] [EDGE-ai-score-2]
    public void BelowMinus60dBFS_NoScore()
    {
        var r = AiScoreMath.Aggregate(F32(Tone(W, amp: 0.0012f)), new Probe().Logit, CancellationToken.None); // RMS ~8.5e-4
        Assert.Equal(AiScoreOutcome.NoScore, r.Outcome);
    }

    [Fact] // [EDGE-ai-score-3]
    public void SilentWindowInsideTrack_LeftOutOfTheMean()
    {
        var samples = Tone(3 * W);
        Array.Clear(samples, W, W);
        samples[0] = 0.2f; samples[2 * W] = 0.2f;
        var probe = new Probe();
        var r = AiScoreMath.Aggregate(F32(samples), probe.Logit, CancellationToken.None);
        Assert.Equal(2, probe.Windows.Count);
        Assert.Equal(1, r.SkippedSilent);
        Assert.Equal(AiScoreMath.Sigmoid(2), r.Score!.Value, 6);
    }

    [Theory] // [AC-ai-score-6] [EDGE-ai-score-7]
    [InlineData(0)]
    [InlineData(79_999)]
    public void FewerThanFiveSeconds_NoScore(int n)
    {
        var r = AiScoreMath.Aggregate(F32(Tone(n)), new Probe().Logit, CancellationToken.None);
        Assert.Equal(AiScoreOutcome.NoScore, r.Outcome);
    }

    [Fact] // [EDGE-ai-score-8]
    public void ReadsSplittingAFloat_SameResultAsOneRead()
    {
        var samples = Tone(W + 12345);
        double whole = AiScoreMath.Aggregate(F32(samples), w => w[100] * 7, CancellationToken.None).Score!.Value;
        double split = AiScoreMath.Aggregate(new ChunkedStream(F32(samples), 7), w => w[100] * 7, CancellationToken.None).Score!.Value;
        Assert.Equal(whole, split);
    }

    private sealed class ChunkedStream(Stream inner, int chunk) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, chunk));
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Theory] // [EDGE-ai-score-13]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NonFiniteLogit_Failed(float logit)
    {
        var r = AiScoreMath.Aggregate(F32(Tone(W)), _ => logit, CancellationToken.None);
        Assert.Equal(AiScoreOutcome.Failed, r.Outcome);
    }

    [Theory] // [AC-ai-score-5] [EDGE-ai-score-14]
    [InlineData(700, "LikelyAi")]
    [InlineData(1000, "LikelyAi")]
    [InlineData(699, "Unclear")]
    [InlineData(301, "Unclear")]
    [InlineData(300, "LikelyHuman")]
    [InlineData(0, "LikelyHuman")]
    public void Bands_AtTheBoundaries(int stored, string band) => Assert.Equal(band, AiScoreMath.Band(stored).ToString());

    [Fact] // [AC-ai-score-6] [EDGE-ai-score-14]
    public void NoScoreAndUnscored_NoBand()
    {
        Assert.Null(AiScoreMath.Band(null));
        Assert.Null(AiScoreMath.Band(AiScoreMath.NoScore));
        Assert.Equal(700, AiScoreMath.ToStored(0.6995));
        Assert.Equal(1000, AiScoreMath.ToStored(1.5));
        Assert.Equal(0, AiScoreMath.ToStored(-0.2));
    }

    [Fact] // [EDGE-ai-score-29] a long mix reuses one window buffer
    public void LongTrack_AllWindowsScored_OneBuffer()
    {
        var buffer = new float[W];
        var seen = new HashSet<float[]>(ReferenceEqualityComparer.Instance);
        var r = AiScoreMath.Aggregate(F32(Tone(5 * W)), w => { seen.Add(w); return 0; }, CancellationToken.None, buffer);
        Assert.Equal(5, r.Windows);
        Assert.Same(buffer, Assert.Single(seen));
    }

    [Fact] // [EDGE-ai-score-31]
    public void ThreadPriorityScope_RestoresEvenOnThrow()
    {
        var t = new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Normal;
            try
            {
                using (ThreadPriorityScope.BelowNormal())
                {
                    Assert.Equal(ThreadPriority.BelowNormal, Thread.CurrentThread.Priority);
                    throw new InvalidOperationException();
                }
            }
            catch (InvalidOperationException) { }
            Assert.Equal(ThreadPriority.Normal, Thread.CurrentThread.Priority);
        });
        t.Start();
        t.Join();
    }
}
