using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>
    /// Decodes a track with the downloaded full-build ffmpeg (soxr, the resampler SONICS was trained with) to
    /// 16 kHz mono float32 and scores it as it streams. Never the bundled ffmpeg: it has no soxr.
    /// </summary>
    internal static class AiAudioDecoder
    {
        internal static readonly TimeSpan DefaultInactivity = TimeSpan.FromSeconds(60);

        /// <summary>One item per argument, so any path reaches ffmpeg intact.</summary>
        internal static IReadOnlyList<string> Arguments(string inputPath) =>
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-i", inputPath, "-vn", "-ac", "1",
            "-af", "aresample=16000:resampler=soxr:precision=20", "-f", "f32le", "-",
        ];

        public static AiScoreResult DecodeAndScore(string ffmpegExe, string inputPath, Func<float[], float> logit, CancellationToken ct, float[]? buffer = null) =>
            Run(ffmpegExe, Arguments(inputPath), logit, DefaultInactivity, ct, buffer);

        /// <summary>
        /// Runs <paramref name="exe"/> at below-normal priority and scores its stdout. Killed when it writes nothing for
        /// <paramref name="inactivity"/>, on cancel, or when scoring fails.
        /// </summary>
        internal static AiScoreResult Run(string exe, IReadOnlyList<string> args, Func<float[], float> logit, TimeSpan inactivity,
            CancellationToken ct, float[]? buffer = null)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            Process process;
            try { process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start"); }
            catch (Exception ex) { return AiScoreResult.Fail("could not start the decoder: " + ex.Message); }

            using (process)
            {
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } // already exited

                var stderr = process.StandardError.ReadToEndAsync();
                using var watch = new InactivityStream(process.StandardOutput.BaseStream);
                using var timer = new Timer(_ =>
                {
                    if (!watch.Busy && DateTime.UtcNow - watch.LastData > inactivity) { watch.TimedOut = true; Kill(process); }
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                using var onCancel = ct.Register(() => Kill(process));

                AiScoreResult result;
                // The decoder waits while a window is classified; that is not silence.
                float Timed(float[] w)
                {
                    watch.Busy = true;
                    try { return logit(w); }
                    finally { watch.Busy = false; watch.Touch(); }
                }
                try { result = AiScoreMath.Aggregate(watch, Timed, ct, buffer); }
                catch (OperationCanceledException) { Kill(process); throw; }
                catch (Exception ex) { Kill(process); result = AiScoreResult.Fail("scoring failed: " + ex.Message); }

                if (result.Outcome == AiScoreOutcome.Failed) Kill(process); // it may be blocked on a full pipe
                if (!process.WaitForExit(10_000)) Kill(process);
                ct.ThrowIfCancellationRequested();
                if (watch.TimedOut) return AiScoreResult.Fail($"the decoder wrote nothing for {inactivity.TotalSeconds:0} s");
                if (result.Outcome == AiScoreOutcome.Failed) return result;
                if (process.HasExited && process.ExitCode != 0)
                    return AiScoreResult.Fail($"ffmpeg exited {process.ExitCode}: {Tail(stderr)}");
                return result;
            }
        }

        private static string Tail(Task<string> stderr)
        {
            string text = stderr.Wait(2000) ? stderr.Result : "";
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(" | ", lines.TakeLast(3));
        }

        private static void Kill(Process p)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }

        /// <summary>Notes when data last arrived, for the watchdog.</summary>
        private sealed class InactivityStream(Stream inner) : Stream
        {
            private long _lastTicks = DateTime.UtcNow.Ticks;
            public volatile bool TimedOut;
            public volatile bool Busy; // a window is being classified: the decoder is waiting on us
            public DateTime LastData => new(Interlocked.Read(ref _lastTicks), DateTimeKind.Utc);
            public void Touch() => Interlocked.Exchange(ref _lastTicks, DateTime.UtcNow.Ticks);

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = inner.Read(buffer, offset, count);
                if (n > 0) Interlocked.Exchange(ref _lastTicks, DateTime.UtcNow.Ticks);
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
