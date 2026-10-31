using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>The WebP, APNG and video routes, all decoded by the bundled ffmpeg.</summary>
    internal static partial class PictureLoader
    {
        private static readonly string[] VideoWhitelist =
            { "-format_whitelist", "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm", "-protocol_whitelist", "file" };

        private static readonly string FpsCap = $"fps=fps='min(source_fps,{MaxClipFps})'";

        // ---- WebP --------------------------------------------------------------------------------

        private static LoadedPicture LoadWebp(string path, bool allowAnimation, int maxFrames, IProgress<string>? progress)
        {
            var webp = WebpContainer.Parse(File.ReadAllBytes(path));
            var frames = webp.Frames;
            int n = frames.Count;

            if (n == 1)
                return new LoadedPicture { Still = WebpStill(webp, frames[0]) };
            if (webp.CanvasWidth > MaxEdge || webp.CanvasHeight > MaxEdge)
                return new LoadedPicture { Still = WebpStill(webp, frames[0]), SourceFrameCount = n, Note = PictureNote.TooLarge };
            if (!allowAnimation)
                return new LoadedPicture { Still = WebpFrames(webp, new[] { 0 }, progress)[0], SourceFrameCount = n, Note = PictureNote.TableOnly };

            var starts = new List<double>();
            double t = 0;
            foreach (var frame in frames)
            {
                starts.Add(t);
                t += AnimatedFormat.BrowserDelayMs(frame.DurationMs);
            }
            return Animate(AnimatedFormat.Timing(starts, t), n, maxFrames,
                keep => WebpFrames(webp, keep, progress), trimmed: false);
        }

        /// <summary>Frame 0 (or a still file) with straight alpha, on its canvas, fitted to 2048.</summary>
        private static BgraImage WebpStill(WebpFile webp, WebpFrame frame)
        {
            var (w, h) = Fit(webp.CanvasWidth, webp.CanvasHeight, (int)MaxEdge);
            string filters = $"format=bgra,pad={webp.CanvasWidth}:{webp.CanvasHeight}:{frame.X}:{frame.Y}:color=black@0"
                + (w != webp.CanvasWidth || h != webp.CanvasHeight ? $",scale={w}:{h}:flags=area" : string.Empty);
            return WithTempFrames(new[] { frame.Still }, file =>
                DecodeOne(new[] { "-f", "webp_pipe", "-i", file }, filters, w, h));
        }

        /// <summary>Decodes frames 0..last kept in one run, compositing every one; returns the kept ones.</summary>
        private static List<BgraImage> WebpFrames(WebpFile webp, IReadOnlyList<int> keep, IProgress<string>? progress)
        {
            int last = keep.Max();
            var wanted = new HashSet<int>(keep);
            var kept = new Dictionary<int, BgraImage>();
            var canvas = new WebpCanvas(webp.CanvasWidth, webp.CanvasHeight);

            int count = WithTempFrames(webp.Frames.Take(last + 1).Select(f => f.Still), file =>
                FfmpegFrames.Decode(new[] { "-f", "webp_pipe", "-i", file },
                    $"format=bgra,pad={webp.CanvasWidth}:{webp.CanvasHeight}:0:0:color=black@0",
                    webp.CanvasWidth, webp.CanvasHeight, null, (i, buffer) =>
                    {
                        if (i > last) return false;
                        var image = canvas.Render(webp.Frames[i], buffer);
                        if (wanted.Contains(i)) kept[i] = Shrink(image);
                        progress?.Report($"{i + 1}/{last + 1}");
                        return true;
                    }));

            if (count < last + 1) throw new InvalidDataException($"Only {count} of {last + 1} WebP frames decoded.");
            return keep.Select(i => kept[i]).ToList();
        }

        /// <summary>Writes the stills back to back to one temp file, runs <paramref name="decode"/>, then cleans up.</summary>
        private static T WithTempFrames<T>(IEnumerable<byte[]> stills, Func<string, T> decode)
        {
            string folder = Path.Combine(Path.GetTempPath(), "pickles-frames-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "frames.webp");
                using (var output = File.Create(file))
                    foreach (var still in stills) output.Write(still);
                return decode(file);
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        }

        // ---- APNG --------------------------------------------------------------------------------

        private static LoadedPicture LoadApng(string path, ApngInfo apng, bool allowAnimation, int maxFrames,
            IProgress<string>? progress)
        {
            int n = apng.DelaysMs.Count;
            if (!allowAnimation)
                return new LoadedPicture { Still = ApngFrames(path, apng, new[] { 0 }, progress)[0], SourceFrameCount = n, Note = PictureNote.TableOnly };

            var starts = new List<double>();
            double t = 0;
            foreach (double delay in apng.DelaysMs)
            {
                starts.Add(t);
                t += AnimatedFormat.BrowserDelayMs(delay);
            }
            return Animate(AnimatedFormat.Timing(starts, t), n, maxFrames,
                keep => ApngFrames(path, apng, keep, progress), trimmed: false);
        }

        private static List<BgraImage> ApngFrames(string path, ApngInfo apng, IReadOnlyList<int> keep, IProgress<string>? progress)
        {
            int last = keep.Max();
            var wanted = new HashSet<int>(keep);
            var kept = new Dictionary<int, BgraImage>();
            int count = FfmpegFrames.Decode(new[] { "-f", "apng", "-i", path, "-map", "0:V:0" }, "format=bgra",
                apng.Width, apng.Height, last + 1, (i, buffer) =>
                {
                    if (wanted.Contains(i)) kept[i] = Shrink(FrameFlatten.OverBlack(buffer, apng.Width, apng.Height));
                    progress?.Report($"{i + 1}/{last + 1}");
                    return i < last;
                });
            if (count < last + 1) throw new InvalidDataException($"Only {count} of {last + 1} APNG frames decoded.");
            return keep.Select(i => kept[i]).ToList();
        }

        // ---- video -------------------------------------------------------------------------------

        private static LoadedPicture LoadVideo(string path, bool allowAnimation, int maxFrames, IProgress<string>? progress)
        {
            var source = VideoWhitelist.Concat(new[]
            {
                "-t", (MaxClipSeconds + 0.5).ToString(CultureInfo.InvariantCulture), "-i", path,
                "-map", "0:V:0", "-an", "-sn", "-dn",
            }).ToList();

            var info = FfmpegFrames.Probe(source, FpsCap + ",showinfo", allowAnimation ? null : 2,
                count => progress?.Report(count.ToString(CultureInfo.InvariantCulture)));
            if (info.Count == 0) throw new InvalidDataException("The clip has no video frames.");

            var (starts, end, usable, trimmed) = VideoTimeline(info, MaxClipSeconds * 1000.0);

            var (w, h) = (info[0].Width, info[0].Height);
            if (usable == 1)
            {
                var (sw, sh) = Fit(w, h, (int)MaxEdge);
                return new LoadedPicture { Still = DecodeOne(source, $"{FpsCap},scale={sw}:{sh}:flags=area,format=bgra", sw, sh), Trimmed = trimmed };
            }

            var (fw, fh) = Fit(w, h, FrameEdge);
            string filters = $"{FpsCap},scale={fw}:{fh}:flags=area,format=bgra";
            if (!allowAnimation)
                return new LoadedPicture { Still = DecodeOne(source, filters, fw, fh), SourceFrameCount = usable, Note = PictureNote.TableOnly };

            return Animate(AnimatedFormat.Timing(starts, end), usable, maxFrames, keep =>
            {
                int last = keep.Max();
                var wanted = new HashSet<int>(keep);
                var kept = new Dictionary<int, BgraImage>();
                int count = FfmpegFrames.Decode(source, filters, fw, fh, last + 1, (i, buffer) =>
                {
                    if (wanted.Contains(i)) kept[i] = FrameFlatten.OverBlack(buffer, fw, fh);
                    progress?.Report($"{i + 1}/{last + 1}");
                    return i < last;
                });
                if (count < last + 1) throw new InvalidDataException($"Only {count} of {last + 1} video frames decoded.");
                return keep.Select(i => kept[i]).ToList();
            }, trimmed);
        }

        /// <summary>
        /// Start times (ms, from the first frame) of the frames before the clip cap, and where the
        /// last one ends. A missing pts follows the previous delay; a missing last duration repeats it.
        /// </summary>
        internal static (List<double> Starts, double EndMs, int Usable, bool Trimmed) VideoTimeline(
            IReadOnlyList<FfmpegFrameInfo> info, double maxMs)
        {
            var starts = new List<double>();
            double first = (info[0].PtsSeconds ?? 0) * 1000;
            for (int i = 0; i < info.Count; i++)
            {
                double? pts = info[i].PtsSeconds * 1000 - first;
                if (pts == null)
                    pts = i == 0 ? 0 : starts[i - 1] + (i > 1 ? starts[i - 1] - starts[i - 2] : 100);
                starts.Add(pts.Value);
            }

            int usable = starts.FindIndex(s => s >= maxMs);
            bool trimmed = usable >= 0;
            if (!trimmed) usable = starts.Count;

            double end;
            if (trimmed) end = starts[usable];
            else
            {
                double? lastDuration = info[usable - 1].DurationSeconds * 1000;
                if (lastDuration is null or <= 0)
                    lastDuration = usable > 1 ? starts[usable - 1] - starts[usable - 2] : 100;
                end = starts[usable - 1] + lastDuration.Value;
            }
            return (starts.Take(usable).ToList(), end, usable, trimmed);
        }

        // ---- shared ------------------------------------------------------------------------------

        /// <summary>Plans the frames, decodes the kept ones, and builds the result as the GIF branch does.</summary>
        private static LoadedPicture Animate(FrameTiming timing, int sourceCount, int maxFrames,
            Func<IReadOnlyList<int>, List<BgraImage>> decode, bool trimmed)
        {
            var plan = FramePlan.Plan(timing.DelaysCs, maxFrames);
            var keep = plan.KeptSourceIndices.Select(k => timing.SourceIndices[k]).ToList();
            var frames = decode(keep);
            bool thinned = plan.Thinned || timing.SourceIndices.Count < sourceCount;

            if (frames.Count < 2)
                return new LoadedPicture { Still = frames[0], SourceFrameCount = sourceCount, Thinned = true, Note = PictureNote.Capped, Trimmed = trimmed };

            return new LoadedPicture
            {
                Still = frames[0],
                Frames = frames,
                Timeline = plan.Timeline,
                SourceFrameCount = sourceCount,
                Thinned = thinned,
                Note = thinned ? PictureNote.Capped : PictureNote.None,
                Trimmed = trimmed,
            };
        }

        private static BgraImage DecodeOne(IReadOnlyList<string> source, string filters, int w, int h)
        {
            BgraImage? image = null;
            FfmpegFrames.Decode(source, filters, w, h, 1, (_, buffer) =>
            {
                image = new BgraImage(w, h, (byte[])buffer.Clone());
                return false;
            });
            return image ?? throw new InvalidDataException("ffmpeg produced no frame.");
        }

        private static (int W, int H) Fit(int w, int h, int edge)
        {
            if (w <= edge && h <= edge) return (w, h);
            double factor = Math.Min((double)edge / w, (double)edge / h);
            return (Math.Max(1, (int)Math.Round(w * factor)), Math.Max(1, (int)Math.Round(h * factor)));
        }
    }
}
