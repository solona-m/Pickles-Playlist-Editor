using System;
using System.Collections.Generic;
using System.IO;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    internal enum PictureKind { Other, WebP, Png }

    /// <summary>The frames kept from a source, and how long each shows in centiseconds.</summary>
    internal sealed record FrameTiming(IReadOnlyList<int> SourceIndices, IReadOnlyList<int> DelaysCs);

    /// <summary>Routing by content, and the one timing rule every animated format goes through.</summary>
    internal static class AnimatedFormat
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>Reads the first 12 bytes; anything short or unknown is Other.</summary>
        public static PictureKind Sniff(Stream stream)
        {
            try
            {
                var head = new byte[12];
                int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                if (read >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                    && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
                    return PictureKind.WebP;
                if (read >= 8 && head.AsSpan(0, 8).SequenceEqual(PngSignature)) return PictureKind.Png;
            }
            catch { }
            return PictureKind.Other;
        }

        /// <summary>Browsers show a frame of 10 ms or less for 100 ms.</summary>
        public static double BrowserDelayMs(double ms) => ms <= 10 ? 100 : ms;

        /// <summary>
        /// Rounds start times to centiseconds cumulatively (no drift) and merges any frame that would
        /// show for under 2 cs into the one before, so the loop keeps its length.
        /// </summary>
        public static FrameTiming Timing(IReadOnlyList<double> startMs, double endMs)
        {
            if (startMs.Count == 0) return new(Array.Empty<int>(), Array.Empty<int>());

            static long Q(double ms) => (long)Math.Round(ms / 10.0, MidpointRounding.AwayFromZero);
            var kept = new List<int> { 0 };
            var keptQ = new List<long> { Q(startMs[0]) };
            for (int i = 1; i < startMs.Count; i++)
            {
                long q = Q(startMs[i]);
                if (q - keptQ[^1] >= 2)
                {
                    kept.Add(i);
                    keptQ.Add(q);
                }
            }

            long end = Q(endMs);
            if (end - keptQ[^1] < 2 && kept.Count > 1)
            {
                kept.RemoveAt(kept.Count - 1);
                keptQ.RemoveAt(keptQ.Count - 1);
            }

            var delays = new List<int>();
            for (int k = 0; k < kept.Count; k++)
            {
                long next = k + 1 < kept.Count ? keptQ[k + 1] : end;
                long delay = next - keptQ[k];
                delays.Add((int)Math.Max(2, delay));
            }
            return new(kept, delays);
        }
    }
}
