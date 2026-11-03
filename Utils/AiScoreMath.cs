using System;
using System.IO;
using System.Threading;

namespace Pickles_Playlist_Editor.Utils
{
    internal enum AiBand { LikelyHuman, Unclear, PossiblyAi, LikelyAi }

    internal enum AiScoreOutcome { Scored, NoScore, Failed }

    internal readonly record struct AiScoreResult(AiScoreOutcome Outcome, double? Score, int Windows, int SkippedSilent, string? Error)
    {
        public static AiScoreResult Fail(string error) => new(AiScoreOutcome.Failed, null, 0, 0, error);
    }

    /// <summary>What a song's AI chip shows: only a Possibly or Likely AI score, and only while the setting is on.</summary>
    internal static class AiScoreDisplay
    {
        public static (bool Visible, AiBand Band, int Percent) For(int? stored, bool enabled)
        {
            var band = AiScoreMath.Band(stored);
            if (!enabled || band is not (AiBand.LikelyAi or AiBand.PossiblyAi)) return (false, AiBand.Unclear, 0);
            return (true, band.Value, Percent(stored!.Value));
        }

        /// <summary>The song tooltip's percent: any real score, even below the chip's threshold; null when off or unscored.</summary>
        public static int? TooltipPercent(int? stored, bool enabled) => enabled && stored is >= 0 ? Percent(stored.Value) : null;

        /// <summary>Whole percent, rounded down so it agrees with the band; 0 and 100 only for exact scores.</summary>
        public static int Percent(int stored)
        {
            int p = stored / 10;
            return stored > 0 && stored < 1000 ? Math.Clamp(p, 1, 99) : p;
        }
    }

    /// <summary>
    /// SONICS scoring rules: 120 s windows of 16 kHz mono, the mean of sigmoid(logit) over the full audible windows,
    /// one zero-padded window for a shorter track. Silent windows are left out; silence alone gets no score.
    /// </summary>
    internal static class AiScoreMath
    {
        public const int SampleRate = 16000;
        public const int WindowSamples = 1_920_000;
        public const int MinSamples = 80_000;      // 5 s
        public const double SilenceRms = 1e-3;     // -60 dBFS
        public const int AiThreshold = 700, HumanThreshold = 300, NoScore = -1;
        public const int PossibleThreshold = 510; // the first score that reads as 51%

        public static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

        /// <summary>The cached form: 0..1000.</summary>
        public static int ToStored(double probability) => (int)Math.Round(Math.Clamp(probability, 0, 1) * 1000, MidpointRounding.AwayFromZero);

        /// <summary>Null for "no chip": not scored, or scored as silence.</summary>
        public static AiBand? Band(int? stored) => stored switch
        {
            null or < 0 => null,
            >= AiThreshold => AiBand.LikelyAi,
            >= PossibleThreshold => AiBand.PossiblyAi,
            <= HumanThreshold => AiBand.LikelyHuman,
            _ => AiBand.Unclear,
        };

        /// <summary>
        /// Reads little-endian float32 samples from <paramref name="f32le"/> into one reused window buffer and scores
        /// it with <paramref name="logit"/>. Never throws for bad audio; a NaN or infinite logit is a failure.
        /// </summary>
        public static AiScoreResult Aggregate(Stream f32le, Func<float[], float> logit, CancellationToken ct, float[]? buffer = null)
        {
            var window = buffer ?? new float[WindowSamples];
            var bytes = new byte[64 * 1024];
            int carry = 0;                 // bytes of a split float kept at the start of `bytes`
            int filled = 0;                // samples in `window`
            double sumSq = 0;
            long total = 0;
            int full = 0, skipped = 0;
            double probSum = 0;

            AiScoreResult? Score(int realSamples)
            {
                double rms = Math.Sqrt(sumSq / Math.Max(1, realSamples));
                if (rms < SilenceRms) { skipped++; return null; }
                float l = logit(window);
                if (float.IsNaN(l) || float.IsInfinity(l)) return AiScoreResult.Fail($"model returned {l}");
                probSum += Sigmoid(l);
                return null;
            }

            int n;
            while ((n = f32le.Read(bytes, carry, bytes.Length - carry)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int avail = carry + n;
                int floats = avail / 4;
                for (int i = 0; i < floats; i++)
                {
                    float s = BitConverter.ToSingle(bytes, i * 4);
                    window[filled++] = s;
                    sumSq += (double)s * s;
                    total++;
                    if (filled == WindowSamples)
                    {
                        full++;
                        if (Score(WindowSamples) is { } failed) return failed;
                        filled = 0;
                        sumSq = 0;
                    }
                }
                carry = avail - floats * 4;
                if (carry > 0) Buffer.BlockCopy(bytes, floats * 4, bytes, 0, carry);
            }

            if (total < MinSamples) return new(AiScoreOutcome.NoScore, null, 0, 0, null);
            if (full == 0)
            {
                // Shorter than one window: zero-pad it, judging silence on the real samples only.
                Array.Clear(window, filled, WindowSamples - filled);
                full = 1;
                if (Score(filled) is { } failed) return failed;
            }
            int scored = full - skipped;
            if (scored == 0) return new(AiScoreOutcome.NoScore, null, full, skipped, null);
            return new(AiScoreOutcome.Scored, probSum / scored, scored, skipped, null);
        }
    }
}
