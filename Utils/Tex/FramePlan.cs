using System;
using System.Collections.Generic;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>Frame k shows during [StartTicks[k], StartTicks[k+1] or LoopTicks).</summary>
    internal sealed record FrameTimeline(IReadOnlyList<int> StartTicks, int LoopTicks);

    internal sealed record FramePlanResult(IReadOnlyList<int> KeptSourceIndices, FrameTimeline Timeline, bool Thinned);

    /// <summary>
    /// Which GIF frames to keep and when each one shows, in effect ticks.
    /// </summary>
    internal static class FramePlan
    {
        /// <summary>Effect ticks per second. Moon Base shows a frame every 3 ticks at about 10 fps.</summary>
        public const int TicksPerSecond = 30;

        /// <summary>The frame count DJ Moon Base proves the game handles.</summary>
        public const int MaxFrames = 48;

        public const int MaxLoopTicks = 65535;

        /// <summary>Browsers treat a 0 or 1 cs delay as 10 cs.</summary>
        public static int NormaliseDelay(int delayCs) => delayCs <= 1 ? 10 : delayCs;

        public static FramePlanResult Plan(IReadOnlyList<int> delaysCs, int maxFrames)
        {
            int m = delaysCs.Count;
            if (m == 0) return new(Array.Empty<int>(), new FrameTimeline(Array.Empty<int>(), 0), false);

            var delays = delaysCs.Select(NormaliseDelay).ToArray();
            maxFrames = Math.Max(1, maxFrames);
            bool thinned = m > maxFrames;

            var kept = thinned
                ? Enumerable.Range(0, maxFrames).Select(k => (int)((long)k * m / maxFrames)).Distinct().ToList()
                : Enumerable.Range(0, m).ToList();

            // Start of each kept frame in centiseconds; each stands for the frames up to the next.
            var startCs = new List<long>();
            long cum = 0;
            int next = 0;
            for (int i = 0; i < m; i++)
            {
                if (next < kept.Count && kept[next] == i) { startCs.Add(cum); next++; }
                cum += delays[i];
            }
            long totalCs = cum;

            double scale = TicksPerSecond / 100.0;
            long loop = (long)Math.Round(totalCs * scale);
            var starts = startCs.Select(c => (long)Math.Round(c * scale)).ToList();

            if (loop > MaxLoopTicks)
            {
                double shrink = (double)MaxLoopTicks / loop;
                starts = starts.Select(s => (long)Math.Round(s * shrink)).ToList();
                loop = MaxLoopTicks;
            }

            // Drop any frame whose window rounds to nothing; the previous frame keeps its time.
            var indices = new List<int>(kept);
            for (int k = 0; k < starts.Count;)
            {
                long end = k + 1 < starts.Count ? starts[k + 1] : loop;
                if (end <= starts[k])
                {
                    starts.RemoveAt(k);
                    indices.RemoveAt(k);
                    thinned = true;
                    if (k > 0) k--;
                    continue;
                }
                k++;
            }
            if (starts.Count > 0) starts[0] = 0;

            return new(indices, new FrameTimeline(starts.Select(s => (int)s).ToList(), (int)loop), thinned);
        }
    }
}
