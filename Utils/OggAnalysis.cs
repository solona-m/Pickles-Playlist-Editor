using NAudio.Vorbis;
using System;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>Duration and BPM of a decoded OGG (SoundTouch.Net beat detection, NVorbis decoding).</summary>
    internal static class OggAnalysis
    {
        /// <summary>BPM is detected over at most this much audio from the start (decoding dominates the cost).</summary>
        internal static readonly TimeSpan MaxAnalysis = TimeSpan.FromSeconds(180);

        internal const int MinBpm = 40, MaxBpm = 170;

        internal readonly record struct Result(int Bpm, TimeSpan Duration);

        /// <summary>Never throws; an unreadable file gives 0 BPM and zero duration.</summary>
        public static Result Analyze(string oggPath)
        {
            TimeSpan duration = TimeSpan.Zero;
            try
            {
                float bpm;
                using (var reader = new VorbisWaveReader(oggPath))
                {
                    duration = TimeSpan.FromSeconds(Math.Round(reader.TotalTime.TotalSeconds));
                    bpm = Detect(reader, 1);
                }
                // SoundTouch finds nothing above ~190 BPM; claiming twice the rate halves the tempo into its range.
                if (!(bpm > 0))
                {
                    using var reader = new VorbisWaveReader(oggPath);
                    bpm = Detect(reader, 2);
                }
                return new Result(FoldToLegacyRange(bpm), duration);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("BPM analysis failed for '{File}': {Error}", oggPath, ex.Message);
                return new Result(0, duration);
            }
        }

        private static float Detect(VorbisWaveReader reader, int rateMultiplier)
        {
            int ch = reader.WaveFormat.Channels;
            var detector = new SoundTouch.BpmDetect(ch, reader.WaveFormat.SampleRate * rateMultiplier);
            var buf = new float[4096 * ch];
            long frames = 0, maxFrames = (long)(MaxAnalysis.TotalSeconds * reader.WaveFormat.SampleRate);
            int n;
            while (frames < maxFrames && (n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                detector.InputSamples(buf.AsSpan(0, n), n / ch);
                frames += n / ch;
            }
            return detector.GetBpm() * rateMultiplier;
        }

        /// <summary>Halves or doubles into the range libZPlay reported, so sorting and cached values stay comparable. 0 = not detected.</summary>
        internal static int FoldToLegacyRange(float bpm)
        {
            if (float.IsNaN(bpm) || float.IsInfinity(bpm) || bpm <= 0) return 0;
            double b = bpm;
            while (b > MaxBpm + 0.5) b /= 2;
            while (b < MinBpm - 0.5) b *= 2;
            return (int)Math.Clamp(Math.Round(b, MidpointRounding.AwayFromZero), MinBpm, MaxBpm);
        }
    }
}
