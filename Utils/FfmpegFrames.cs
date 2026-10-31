using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Utils
{
    internal sealed record FfmpegFrameInfo(double? PtsSeconds, double? DurationSeconds, int Width, int Height);

    /// <summary>
    /// Runs the bundled ffmpeg to list frames (showinfo) or stream them as raw BGRA. Arguments go
    /// through ArgumentList, so no path is ever parsed by a shell, and the process has always exited
    /// before a method returns or throws.
    /// </summary>
    internal static class FfmpegFrames
    {
        internal static string ExePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>The last process started, for tests that check a kill.</summary>
        internal static int? LastProcessId { get; set; }

        private static readonly Regex Showinfo = new(
            @"\bn:\s*\d+.*?\bpts_time:(?<pts>\S+).*?(?:\bduration_time:(?<dur>\S+).*?)?\bs:(?<w>\d+)x(?<h>\d+)",
            RegexOptions.Compiled);

        public static IReadOnlyList<FfmpegFrameInfo> Probe(IReadOnlyList<string> sourceArgs, string filters, int? maxFrames,
            Action<int>? onFrame)
        {
            var args = Common(sourceArgs, filters, maxFrames).Concat(new[] { "-f", "null", "-" }).ToList();
            var frames = new List<FfmpegFrameInfo>();
            var tail = new Queue<string>();

            Run(args, process =>
            {
                string? line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    var info = ParseShowinfoLine(line);
                    if (info != null && (maxFrames == null || frames.Count < maxFrames))
                    {
                        frames.Add(info);
                        onFrame?.Invoke(frames.Count);
                    }
                    else Remember(tail, line);
                }
                return true;
            }, tail);
            return frames;
        }

        public static int Decode(IReadOnlyList<string> sourceArgs, string filters, int width, int height, int? maxFrames,
            Func<int, byte[], bool> onFrame)
        {
            var args = new List<string> { "-v", "error" };
            args.AddRange(Common(sourceArgs, filters, maxFrames));
            args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "bgra", "-" });

            int count = 0;
            var tail = new Queue<string>();
            Run(args, process =>
            {
                var stderr = Task.Run(() =>
                {
                    string? line;
                    while ((line = process.StandardError.ReadLine()) != null) lock (tail) Remember(tail, line);
                });

                var buffer = new byte[checked(width * height * 4)];
                var stdout = process.StandardOutput.BaseStream;
                bool keepGoing = true;
                while (keepGoing)
                {
                    int read = stdout.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                    if (read == 0) break;
                    if (read < buffer.Length) throw new InvalidDataException("ffmpeg stopped part-way through a frame.");
                    keepGoing = onFrame(count++, buffer);
                }
                if (!keepGoing) return false;   // early stop: the caller has what it needs
                stderr.Wait();
                return true;
            }, tail);
            return count;
        }

        internal static FfmpegFrameInfo? ParseShowinfoLine(string line)
        {
            if (!line.Contains("showinfo", StringComparison.Ordinal)) return null;
            var m = Showinfo.Match(line);
            if (!m.Success) return null;
            return new FfmpegFrameInfo(Number(m.Groups["pts"].Value),
                m.Groups["dur"].Success ? Number(m.Groups["dur"].Value) : null,
                int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture));
        }

        // ---- plumbing ----------------------------------------------------------------------------

        private static IEnumerable<string> Common(IReadOnlyList<string> sourceArgs, string filters, int? maxFrames)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-xerror" };
            args.AddRange(sourceArgs);
            if (filters.Length > 0) args.AddRange(new[] { "-vf", filters });
            args.AddRange(new[] { "-fps_mode", "passthrough" });
            if (maxFrames is { } max) args.AddRange(new[] { "-frames:v", max.ToString(CultureInfo.InvariantCulture) });
            return args;
        }

        /// <summary>Starts ffmpeg, hands it to <paramref name="work"/>, and always leaves it exited.</summary>
        private static void Run(List<string> args, Func<Process, bool> work, Queue<string> tail)
        {
            var info = new ProcessStartInfo(ExePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);

            Process process;
            try
            {
                process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"ffmpeg could not be started: {ex.Message}", ex);
            }

            LastProcessId = process.Id;
            bool timedOut = false;
            using var timer = new Timer(_ =>
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
            }, null, Timeout, System.Threading.Timeout.InfiniteTimeSpan);

            bool finished = false;
            try
            {
                finished = work(process);
            }
            catch when (timedOut)
            {
                // The kill broke the pipe; the timeout is the real error.
            }
            finally
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                catch { }
            }

            if (timedOut) throw new TimeoutException("ffmpeg took too long and was stopped.");
            if (finished && process.ExitCode != 0)
            {
                string detail;
                lock (tail) detail = string.Join(" / ", tail);
                throw new InvalidDataException($"ffmpeg could not read the file (exit {process.ExitCode}): {detail}");
            }
            process.Dispose();
        }

        private static void Remember(Queue<string> tail, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            tail.Enqueue(line.Trim());
            while (tail.Count > 4) tail.Dequeue();
        }

        private static double? Number(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    }
}
