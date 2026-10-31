using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Tex;
using System.Globalization;
using Windows.Graphics.Imaging;
using Windows.Storage;
using F = AnimHarness.MediaFixtures;

namespace AnimHarness;

internal static partial class Program
{
    private const string Fmt = "gh11-animated-formats";

    private static bool Near((int R, int G, int B) a, (int R, int G, int B) b, int tol = 8) =>
        Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    private static (int, int, int) C(int k) { var c = F.Colour(k); return (c.R, c.G, c.B); }

    private static byte Alpha(BgraImage img, int x, int y) => img.Pixels[(y * img.Width + x) * 4 + 3];

    private static void Throws(Func<object?> act, string what, Func<Exception, bool>? match = null)
    {
        try { act(); Check(false, what + " (did not throw)"); }
        catch (Exception ex) { var inner = ex is AggregateException a ? a.InnerException! : ex; Check(match?.Invoke(inner) ?? true, $"{what} ({inner.GetType().Name}: {inner.Message})"); }
    }

    private static T WithFfmpeg<T>(Func<T> act, string? exe = null, TimeSpan? timeout = null)
    {
        string oldExe = FfmpegFrames.ExePath;
        var oldTimeout = FfmpegFrames.Timeout;
        try
        {
            if (exe != null) FfmpegFrames.ExePath = exe;
            if (timeout != null) FfmpegFrames.Timeout = timeout.Value;
            return act();
        }
        finally
        {
            FfmpegFrames.ExePath = oldExe;
            FfmpegFrames.Timeout = oldTimeout;
        }
    }

    private static int TempFolders() =>
        Directory.GetDirectories(Path.GetTempPath(), "pickles-frames-*").Length;

    /// <summary>The 4-frame AC-1 WebP: 50/100/150/50 ms blend frames in the colours of frames 0..3.</summary>
    private static string Webp4(string name = "a4.webp", byte[]? trailing = null)
    {
        var w = new WebpWriter(64, 48);
        int[] ms = { 50, 100, 150, 50 };
        for (int k = 0; k < 4; k++) { var c = F.Colour(k); w.Frame(0, 0, F.Solid(64, 48, c.R, c.G, c.B), ms[k]); }
        return w.Save(name, trailing);
    }

    private static string Apng4(string name = "a4.png")
    {
        var a = new ApngWriter(64, 48);
        (ushort, ushort)[] d = { (5, 100), (1, 10), (15, 100), (1, 20) };
        for (int k = 0; k < 4; k++) { var c = F.Colour(k); a.Frame(F.Solid(64, 48, c.R, c.G, c.B), d[k].Item1, d[k].Item2); }
        return a.Save(name);
    }

    private static string Mp4N(int n, double fps, string name, int w = 64, int h = 48) => F.Mp4(F.ColourFrames(n, w, h), fps, name);

    private static void RegisterMedia()
    {
        var plan4 = FramePlan.Plan(new[] { 5, 10, 15, 5 }, 48).Timeline;

        Add("WebpAnimates", $"AC-{Fmt}-1", () =>
        {
            var pic = Load(Webp4())!;
            Check(pic.Frames?.Count == 4 && pic.Frames[0].Width == 64 && pic.Frames[0].Height == 48, "4 frames 64x48");
            Check(pic.Timeline!.StartTicks.SequenceEqual(plan4.StartTicks) && pic.Timeline.LoopTicks == plan4.LoopTicks, "timeline");
            Check(Enumerable.Range(0, 4).All(k => Near(At(pic.Frames![k], 32, 24), C(k), 2)), "colours");
            Check(!pic.Thinned, "not thinned");
        });

        Add("WebpPreviewFramesMatchFit", $"AC-{Fmt}-1", () =>
        {
            using var mod = Fixtures.Pickles();
            var pic = Load(Webp4())!;
            var previews = DjTableAnimationCore.PreviewFrames(mod.Read(mod.Table.Relative), mod.Table.Surface, pic.Frames!, FitMode.Fill);
            Check(previews.Count == 4, "one preview per frame");
        });

        Add("ApngAnimates", $"AC-{Fmt}-2", () =>
        {
            string png = Apng4();
            string apng = F.PathFor("a4.apng");
            File.Copy(png, apng, true);
            foreach (var path in new[] { png, apng })
            {
                var pic = Load(path)!;
                Check(pic.Frames?.Count == 4 && pic.Timeline!.StartTicks.SequenceEqual(plan4.StartTicks), $"{Path.GetFileName(path)} 4 frames, timeline");
                Check(Enumerable.Range(0, 4).All(k => Near(At(pic.Frames![k], 32, 24), C(k), 0)), $"{Path.GetFileName(path)} colours");
            }
        });

        Add("Mp4Animates", $"AC-{Fmt}-3", () =>
        {
            foreach (var (path, label) in new[] { (Mp4N(20, 10, "v20.mp4"), "mp4"), (F.Webm(F.ColourFrames(20), 10, "v20.webm"), "webm") })
            {
                var pic = Load(path)!;
                Check(pic.Frames?.Count == 20, $"{label} 20 frames ({pic.Frames?.Count})");
                Check(pic.Timeline!.StartTicks.SequenceEqual(Enumerable.Range(0, 20).Select(i => i * 3)) && pic.Timeline.LoopTicks == 60, $"{label} timeline");
                Check(!pic.Trimmed && Enumerable.Range(0, 20).All(k => Near(At(pic.Frames![k], 32, 24), C(k))), $"{label} colours, not trimmed");
            }
        });

        Add("VideoThinnedWithNote", $"AC-{Fmt}-3", () =>
        {
            var pic = Load(Mp4N(100, 25, "v100.mp4"))!;
            Check(pic.Frames?.Count == 48 && pic.Thinned && pic.Note == PictureNote.Capped && pic.SourceFrameCount == 100
                && pic.Timeline!.LoopTicks == 120, $"48 frames, capped, loop 120 ({pic.Frames?.Count} {pic.SourceFrameCount} {pic.Timeline?.LoopTicks})");
        });

        Add("VideoTrimmedFromFrames", $"AC-{Fmt}-3 EDGE-{Fmt}-15", () =>
        {
            var p12 = Load(Mp4N(120, 10, "v12s.mp4"))!;
            Check(p12.Trimmed && p12.Timeline!.LoopTicks == 300 && p12.SourceFrameCount == 100, $"12 s mp4 trimmed ({p12.Timeline?.LoopTicks} {p12.SourceFrameCount})");

            string raw = F.WriteRaw(F.ColourFrames(120), "pipe.bgra");
            string piped = F.PathFor("piped.webm");
            File.WriteAllBytes(piped, FfmpegStdout("-f", "rawvideo", "-pix_fmt", "bgra", "-s", "64x48", "-r", "10", "-i", raw,
                "-c:v", "libvpx-vp9", "-deadline", "realtime", "-f", "webm", "pipe:1"));
            var pw = Load(piped)!;
            Check(pw.Trimmed && pw.Timeline!.LoopTicks == 300, $"piped webm trimmed ({pw.Timeline?.LoopTicks})");

            var p10 = Load(Mp4N(100, 10, "v10s.mp4"))!;
            Check(!p10.Trimmed, "10 s not trimmed");

            string v3 = Mp4N(30, 10, "v3s.mp4");
            string la = F.PathFor("longaudio.mp4");
            F.Ffmpeg("-i", v3, "-f", "lavfi", "-i", "sine=d=12", "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", la);
            var pla = Load(la)!;
            Check(!pla.Trimmed && pla.Timeline!.LoopTicks == 90, $"long audio not trimmed ({pla.Timeline?.LoopTicks})");
        });

        Add("FormatsApplyAndRestorePickles", $"AC-{Fmt}-4", () => ApplyRestoreFormats(() => Fixtures.Pickles(), Fixtures.PicklesAvfx));
        Add("FormatsApplyAndRestoreMoon", $"AC-{Fmt}-4", () => ApplyRestoreFormats(() => Fixtures.Moon(), MoonAvfx));

        Add("FormatsFirstFrameForStillCards", $"AC-{Fmt}-5", () =>
        {
            var tw = new WebpWriter(64, 48);
            tw.Frame(0, 0, F.Solid(64, 48, 200, 10, 10, 0), 100).Frame(0, 0, F.Solid(64, 48, 10, 200, 10), 100);
            string twebp = tw.Save("transparent.webp");
            foreach (var (path, colour) in new[] { (Webp4("s.webp"), C(0)), (Apng4("s.png"), C(0)), (Mp4N(5, 10, "s.mp4"), C(0)),
                (F.Webm(F.ColourFrames(5), 10, "s.webm"), C(0)), (twebp, (0, 0, 0)) })
            {
                var pic = Load(path, animate: false)!;
                Check(pic.Frames == null && pic.Note == PictureNote.TableOnly && Near(At(pic.Still, 10, 10), colour)
                    && Alpha(pic.Still, 10, 10) == 255 && Math.Max(pic.Still.Width, pic.Still.Height) <= 1024, $"{Path.GetFileName(path)} first frame");
            }
            int reports = 0;
            var still = Load(Mp4N(20, 10, "probe.mp4"), animate: false, progress: new SyncProgress(_ => reports++))!;
            Check(still.SourceFrameCount == 2 && reports <= 2, $"still card probes at most 2 frames ({still.SourceFrameCount}, {reports} reports)");
        });

        Add("PickerFilterListsFormats", $"AC-{Fmt}-6", () =>
        {
            var parts = PictureLoader.PickerFilter.Split('|');
            foreach (var ext in new[] { "png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "webp", "apng", "mp4", "webm" })
                Check(parts[0].Contains("*." + ext) && parts[1].Contains("*." + ext), "lists ." + ext);
            Check(PictureLoader.PickerFilter.Contains("All files"), "All files");
        });

        Add("ContentBeatsExtension", $"AC-{Fmt}-6 EDGE-{Fmt}-11", () =>
        {
            string webpAsPng = F.PathFor("webp.png"); File.Copy(Webp4(), webpAsPng, true);
            string gifAsWebp = F.PathFor("gif.webp");
            new GifWriter(8, 8, Palette).Solid(1).Solid(2).Save(gifAsWebp);
            string mp4AsBin = F.PathFor("clip.bin"); File.Copy(Mp4N(5, 10, "c.mp4"), mp4AsBin, true);
            Check(Load(webpAsPng)!.Frames?.Count == 4, "WebP named .png");
            Check(Load(gifAsWebp)!.Frames?.Count == 2, "GIF named .webp");
            Check(Load(mp4AsBin)!.Frames?.Count == 5, "MP4 named .bin");
        });

        Add("StillWebpLoads", $"AC-{Fmt}-7", () =>
        {
            var img = new BgraImage(40, 30);
            for (int y = 0; y < 30; y++) for (int x = 0; x < 40; x++) { int i = (y * 40 + x) * 4; img.Pixels[i] = (byte)(x * 6); img.Pixels[i + 1] = (byte)(y * 8); img.Pixels[i + 2] = 120; img.Pixels[i + 3] = (byte)(x < 20 ? 255 : 0); }
            var opaque = img.Clone(); for (int i = 3; i < opaque.Pixels.Length; i += 4) opaque.Pixels[i] = 255;

            string lossless = F.PathFor("still-ll.webp"); File.WriteAllBytes(lossless, F.StillWebp(opaque, true, "still-ll"));
            string lossy = F.PathFor("still-ly.webp"); File.WriteAllBytes(lossy, F.StillWebp(opaque, false, "still-ly"));
            string alpha = F.PathFor("still-a.webp"); File.WriteAllBytes(alpha, F.StillWebp(img, false, "still-a"));
            string trail = F.PathFor("still-trail.webp"); File.WriteAllBytes(trail, File.ReadAllBytes(lossless).Concat(new byte[30]).ToArray());

            var pl = Load(lossless)!;
            Check(pl.Frames == null && pl.Note == PictureNote.None && pl.Still.Pixels.AsSpan().SequenceEqual(opaque.Pixels), "lossless exact");
            Check(Fixtures.Psnr(Load(lossy)!.Still, opaque) >= 35, "lossy PSNR");
            var pa = Load(alpha)!;
            Check(Alpha(pa.Still, 30, 10) == 0 && Alpha(pa.Still, 5, 10) == 255, "alpha kept");
            Check(Load(trail)!.Still.Pixels.AsSpan().SequenceEqual(pl.Still.Pixels), "trailing bytes ignored");
        });

        Add("WebpTooLargeStill", $"AC-{Fmt}-8 EDGE-{Fmt}-12", () =>
        {
            var w = new WebpWriter(2050, 10);
            w.Frame(0, 0, F.Solid(2050, 10, 200, 0, 0), 100).Frame(0, 0, F.Solid(2050, 10, 0, 200, 0), 100);
            var pic = Load(w.Save("big.webp"))!;
            Check(pic.Frames == null && pic.Note == PictureNote.TooLarge && pic.Still.Width <= 2048 && Near(At(pic.Still, 100, 4), (200, 0, 0)), "too-large still");

            var w2 = new WebpWriter(2050, 10);
            w2.Frame(4, 2, F.Solid(17, 7, 0, 0, 220), 100, lossless: false).Frame(0, 0, F.Solid(2050, 10, 0, 200, 0), 100);
            var p2 = Load(w2.Save("big-sub.webp"))!;
            Check(p2.Note == PictureNote.TooLarge && Alpha(p2.Still, 200, 4) == 0, "lossy sub-rect frame 0: transparent outside");
        });

        Add("ApngTooLargeStill", $"AC-{Fmt}-8 EDGE-{Fmt}-12", () =>
        {
            var a = new ApngWriter(2050, 10);
            a.Frame(F.Solid(2050, 10, 200, 0, 0), 1, 10).Frame(F.Solid(2050, 10, 0, 200, 0), 1, 10);
            var pic = Load(a.Save("big.png"))!;
            Check(pic.Frames == null && pic.Note == PictureNote.TooLarge && pic.Still.Width <= 2048, "too-large APNG still");
        });

        Add("FormatsBoundedMemory", $"AC-{Fmt}-8 EDGE-{Fmt}-22", () =>
        {
            string webp = F.PathFor("many.webp");
            F.Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=512x512:r=25:d=24", "-c:v", "libwebp_anim", "-lossless", "0", "-q:v", "30", "-loop", "0", webp);
            string apng = F.PathFor("many.png");
            F.Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=512x512:r=25:d=12", "-plays", "0", "-f", "apng", apng);
            string mp4 = F.PathFor("big.mp4");
            F.Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=1024x1024:r=30:d=10", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", mp4);

            foreach (var (path, decoded, canvas) in new[] { (webp, 600, 512 * 512 * 4L), (apng, 300, 512 * 512 * 4L), (mp4, 300, 1024 * 1024 * 4L) })
            {
                GC.Collect();
                long baseline = GC.GetTotalMemory(true), peak = 0;
                int reports = 0;
                // Decode progress reads "i/n"; video probe progress is a bare count.
                var progress = new SyncProgress(s => { if (!s.Contains('/')) return; reports++; peak = Math.Max(peak, GC.GetTotalMemory(false) - baseline); });
                var pic = Load(path, progress: progress)!;
                long bound = 48 * canvas + 4 * canvas + 64L * 1024 * 1024;
                Check(pic.Frames?.Count == 48, $"{Path.GetFileName(path)} 48 frames ({pic.Frames?.Count})");
                int lastKept = 47 * decoded / 48;   // decoding stops after the last kept frame
                Check(reports >= lastKept + 1, $"{Path.GetFileName(path)} progress per decoded frame ({reports})");
                Check(peak <= bound, $"{Path.GetFileName(path)} peak {peak / 1048576} MB <= {bound / 1048576} MB");
            }
        });

        Add("UnreadableFormatsThrow", $"AC-{Fmt}-9 EDGE-{Fmt}-19", () =>
        {
            var good = File.ReadAllBytes(Webp4());
            string truncated = F.PathFor("trunc.webp"); File.WriteAllBytes(truncated, good[..(good.Length - 40)]);
            string m4a = F.PathFor("audio.m4a"); F.Ffmpeg("-f", "lavfi", "-i", "sine=d=1", "-c:a", "aac", m4a);
            string wav = F.PathFor("audio.wav"); F.Ffmpeg("-f", "lavfi", "-i", "sine=d=1", wav);
            string rnd = F.PathFor("random.mp4"); File.WriteAllBytes(rnd, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7919 % 251)).ToArray());
            string txt = F.PathFor("readme.txt"); File.WriteAllText(txt, string.Concat(Enumerable.Repeat("Pickles Playlist Editor manages DJ songs and tables.\n", 60)));
            string cover = F.PathFor("cover.png");
            F.Ffmpeg("-f", "lavfi", "-i", "color=c=red:s=16x16:d=1", "-frames:v", "1", cover);
            string mp3 = F.PathFor("cover.mp3");
            F.Ffmpeg("-f", "lavfi", "-i", "sine=d=1", "-i", cover, "-map", "0", "-map", "1", "-c:a", "libmp3lame", "-c:v", "png", "-disposition:v", "attached_pic", mp3);
            foreach (var path in new[] { truncated, m4a, wav, rnd, txt, mp3 })
                Throws(() => Load(path), Path.GetFileName(path) + " unreadable");
        });

        Add("CorruptApngAsToday", $"AC-{Fmt}-9 EDGE-{Fmt}-34", () =>
        {
            var a = new ApngWriter(64, 48);
            for (int k = 0; k < 4; k++) { var c = F.Colour(k); a.Frame(F.Solid(64, 48, c.R, c.G, c.B), 1, 10); }
            var pic = Load(a.Save("corrupt.png", corruptFdat: true))!;
            Check(pic.Frames == null && Near(At(pic.Still, 5, 5), C(0), 0), "WIC still of the default image");
        });

        Add("StringsCoverAllLocales", $"AC-{Fmt}-10", () =>
        {
            string root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "Pickles Playlist Editor.csproj"))) root = Path.GetDirectoryName(root)!;
            Check(root != null, "repo root found");
            foreach (var resw in Directory.GetFiles(Path.Combine(root!, "Strings"), "Resources.resw", SearchOption.AllDirectories))
            {
                var doc = System.Xml.Linq.XDocument.Load(resw);
                string? V(string key) => doc.Root!.Elements("data").FirstOrDefault(d => (string?)d.Attribute("name") == key)?.Element("value")?.Value;
                string lang = Path.GetFileName(Path.GetDirectoryName(resw))!;
                Check(V("Textures_AnimTrimmed")?.Contains("{0}") == true, lang + " trimmed key");
                Check(V("TexturesHint.Text")?.Contains("WebP") == true && V("Textures_AuthorAnimation")?.Contains("WebP") == true, lang + " hint/author mention WebP");
                if (lang == "en-US")
                    Check(V("Textures_AnimTooLarge")?.Contains("GIF") == false && V("Textures_AnimTableOnly")?.Contains("GIF") == false, "en-US no GIF-only text");
            }
            string readme = File.ReadAllText(Path.Combine(root!, "README.md"));
            int at = readme.IndexOf("Animated GIFs work on the table panel", StringComparison.Ordinal);
            string para = at >= 0 ? readme[at..Math.Min(readme.Length, at + 2000)] : "";
            Check(para.Contains("WebP") && para.Contains("APNG") && para.Contains("MP4"), "README names the formats");
        });

        // ---- WebP compositing ---------------------------------------------------------------------

        Add("WebpFlagBits", $"EDGE-{Fmt}-1", () =>
        {
            var hole = F.Solid(2, 2, 0, 0, 0, 0);
            var w = new WebpWriter(8, 8);
            w.Frame(0, 0, F.Solid(8, 8, 200, 0, 0), 100, flags: 0x02)            // no blend, keep
             .Frame(0, 0, F.Solid(2, 2, 0, 0, 200), 100, flags: 0x01)            // blend, dispose
             .Frame(4, 4, hole, 100, flags: 0x00)                                 // blend: transparent pixels keep red
             .Frame(4, 4, hole, 100, flags: 0x03)                                 // no blend + dispose: replaces with transparent
             .Frame(6, 6, F.Solid(2, 2, 0, 200, 0), 100, flags: 0x00);
            var f = Load(w.Save("flags.webp"))!.Frames!;
            Check(Near(At(f[1], 0, 0), (0, 0, 200), 0), "blend frame shows");
            Check(Near(At(f[2], 0, 0), (0, 0, 0), 0) && Near(At(f[2], 4, 4), (200, 0, 0), 0), "bit 0 cleared the rect; blend keeps red under alpha 0");
            Check(Near(At(f[3], 4, 4), (0, 0, 0), 0), "bit 1 replaces with transparent");
            Check(Near(At(f[4], 4, 4), (0, 0, 0), 0) && Near(At(f[4], 6, 6), (0, 200, 0), 0), "disposed after showing");
        });

        Add("WebpAlphaBlend", $"EDGE-{Fmt}-2", () =>
        {
            var w = new WebpWriter(8, 8);
            w.Frame(0, 0, F.Solid(8, 8, 0, 0, 255), 100, flags: 0x02).Frame(0, 0, F.Solid(8, 8, 255, 0, 0, 128), 100);
            var px = At(Load(w.Save("blend.webp"))!.Frames![1], 4, 4);
            Check(Near(px, (128, 0, 127), 2), $"over result {px}");
        });

        Add("WebpNoBlendReplaces", $"EDGE-{Fmt}-3", () =>
        {
            var w = new WebpWriter(8, 8);
            w.Frame(0, 0, F.Solid(8, 8, 200, 0, 0), 100, flags: 0x02).Frame(0, 0, F.Solid(8, 8, 0, 200, 0, 0), 100, flags: 0x02);
            Check(Near(At(Load(w.Save("noblend.webp"))!.Frames![1], 4, 4), (0, 0, 0), 0), "alpha-0 replace shows black");
        });

        Add("WebpDisposeRectOnly", $"EDGE-{Fmt}-4", () =>
        {
            var w = new WebpWriter(8, 8) { Background = 0xFFFFFFFF };
            w.Frame(0, 0, F.Solid(8, 8, 200, 0, 0), 100, flags: 0x02).Frame(0, 0, F.Solid(2, 2, 0, 0, 200), 100, flags: 0x01)
             .Frame(6, 6, F.Solid(2, 2, 0, 200, 0), 100);
            var f = Load(w.Save("dispose.webp"))!.Frames!;
            Check(Near(At(f[2], 0, 0), (0, 0, 0), 0) && Near(At(f[2], 4, 4), (200, 0, 0), 0), "only the rect cleared, ANIM background ignored");
        });

        Add("WebpLossyAlphaFrames", $"EDGE-{Fmt}-5", () =>
        {
            var half = new BgraImage(16, 8);
            for (int y = 0; y < 8; y++) for (int x = 0; x < 16; x++) { int i = (y * 16 + x) * 4; half.Pixels[i + 2] = 220; half.Pixels[i + 3] = (byte)(x < 8 ? 255 : 128); }
            var w = new WebpWriter(16, 8);
            w.Frame(0, 0, F.Solid(16, 8, 0, 0, 220), 100, flags: 0x02).Frame(0, 0, half, 100, lossless: false);
            var f = Load(w.Save("lossyalpha.webp"))!.Frames![1];
            Check(Near(At(f, 2, 4), (220, 0, 0), 12) && Near(At(f, 13, 4), (110, 0, 110), 14), $"left opaque, right ~50% ({At(f, 2, 4)} {At(f, 13, 4)})");
        });

        Add("WebpSubRectsNotRescaled", $"EDGE-{Fmt}-6", () =>
        {
            var w = new WebpWriter(64, 48);
            w.Frame(0, 0, F.Solid(64, 48, 0, 0, 0), 100, flags: 0x02).Frame(4, 4, F.Solid(17, 7, 200, 0, 0), 100).Frame(58, 42, F.Solid(6, 6, 0, 200, 0), 100);
            var f = Load(w.Save("subrects.webp"))!.Frames!;
            Check(Near(At(f[1], 4, 4), (200, 0, 0), 0) && Near(At(f[1], 20, 10), (200, 0, 0), 0) && Near(At(f[1], 21, 10), (0, 0, 0), 0)
                && Near(At(f[1], 20, 11), (0, 0, 0), 0), "17x7 at (4,4)");
            Check(Near(At(f[2], 58, 42), (0, 200, 0), 0) && Near(At(f[2], 63, 47), (0, 200, 0), 0) && Near(At(f[2], 57, 41), (0, 0, 0), 0), "6x6 at (58,42)");
        });

        Add("ShortDelaysAreTenCs", $"EDGE-{Fmt}-7", () =>
        {
            var want = FramePlan.Plan(new[] { 10, 10, 4 }, 48).Timeline;
            var w = new WebpWriter(8, 8);
            int[] ms = { 0, 10, 40 };
            for (int k = 0; k < 3; k++) { var c = F.Colour(k); w.Frame(0, 0, F.Solid(8, 8, c.R, c.G, c.B), ms[k], flags: 0x02); }
            Check(Load(w.Save("short.webp"))!.Timeline!.StartTicks.SequenceEqual(want.StartTicks), "WebP 0/10/40 ms");
            foreach (var (name, d) in new[] { ("short.png", new (ushort, ushort)[] { (0, 100), (1, 100), (4, 100) }), ("short0.png", new (ushort, ushort)[] { (0, 0), (0, 0), (4, 100) }) })
            {
                var a = new ApngWriter(8, 8);
                for (int k = 0; k < 3; k++) { var c = F.Colour(k); a.Frame(F.Solid(8, 8, c.R, c.G, c.B), d[k].Item1, d[k].Item2); }
                var t = Load(a.Save(name))!.Timeline!;
                Check(t.StartTicks.SequenceEqual(want.StartTicks) && t.LoopTicks == want.LoopTicks, name);
            }
        });

        Add("MalformedWebpRejected", $"EDGE-{Fmt}-8", () =>
        {
            var good = File.ReadAllBytes(Webp4("m.webp"));
            var cases = new Dictionary<string, byte[]>
            {
                ["truncated"] = good[..(good.Length - 10)],
                ["no image"] = new WebpWriter(64, 48).Frame(0, 0, F.Solid(64, 48, 1, 1, 1), 100, rawChunks: Array.Empty<byte>()).Frame(0, 0, F.Solid(64, 48, 1, 1, 1), 100).ToBytes(),
                ["no VP8X"] = RemoveChunk(good, "VP8X"),
                ["rect outside"] = new WebpWriter(64, 48).Frame(60, 44, F.Solid(17, 7, 1, 1, 1), 100).Frame(0, 0, F.Solid(64, 48, 1, 1, 1), 100).ToBytes(),
                ["frame larger"] = new WebpWriter(64, 48).Frame(0, 0, F.Solid(70, 50, 1, 1, 1), 100).Frame(0, 0, F.Solid(64, 48, 1, 1, 1), 100).ToBytes(),
                ["size mismatch"] = new WebpWriter(64, 48).Frame(0, 0, F.Solid(17, 7, 1, 1, 1), 100,
                    rawChunks: F.ImageChunks(F.StillWebp(F.Solid(64, 48, 1, 1, 1), true, "mm"))).Frame(0, 0, F.Solid(64, 48, 1, 1, 1), 100).ToBytes(),
                ["chunk past RIFF end"] = GrowLastChunk(good.Concat(new byte[40]).ToArray(), 20),
            };
            foreach (var (name, bytes) in cases)
            {
                Throws(() => WebpContainer.Parse(bytes), name + ": Parse", e => e is InvalidDataException);
                string path = F.PathFor("bad.webp");
                File.WriteAllBytes(path, bytes);
                int before = TempFolders();
                Throws(() => Load(path), name + ": load");
                Check(TempFolders() == before, name + ": no temp folder");
            }
        });

        Add("WebpTrailingBytesLoads", $"EDGE-{Fmt}-8", () =>
        {
            var plain = Load(Webp4("t1.webp"))!;
            string trail = Webp4("t2.webp", trailing: System.Text.Encoding.ASCII.GetBytes("XMP-junk-after-the-riff-end-30"));
            Check(WebpContainer.Parse(File.ReadAllBytes(trail)).Frames.Count == 4, "Parse ignores trailing bytes");
            var t = Load(trail)!;
            Check(t.Frames?.Count == 4 && t.Timeline!.StartTicks.SequenceEqual(plain.Timeline!.StartTicks), "same frames and timeline");
        });

        Add("CorruptWebpFrameNoPartial", $"EDGE-{Fmt}-9 EDGE-{Fmt}-25", () =>
        {
            var bytes = File.ReadAllBytes(Webp4("c.webp"));
            int lastVp8l = LastIndexOf(bytes, "VP8L"u8.ToArray());
            for (int i = lastVp8l + 14; i < Math.Min(bytes.Length, lastVp8l + 40); i++) bytes[i] ^= 0xA5;
            string path = F.PathFor("corrupt.webp");
            File.WriteAllBytes(path, bytes);
            Throws(() => Load(path), "corrupt frame: no partial animation");
        });

        Add("SingleFrameAnimatedIsStill", $"EDGE-{Fmt}-10", () =>
        {
            var one = Load(new WebpWriter(64, 48).Frame(0, 0, F.Solid(64, 48, 200, 0, 0), 100).Save("one.webp"))!;
            Check(one.Frames == null && one.Note == PictureNote.None, "one ANMF is a still");
            var sub = Load(new WebpWriter(64, 48).Frame(4, 4, F.Solid(17, 7, 0, 0, 200), 100, lossless: false).Save("onesub.webp"))!;
            Check(sub.Still.Width == 64 && sub.Still.Height == 48 && Near(At(sub.Still, 10, 7), (0, 0, 200), 12)
                && Alpha(sub.Still, 40, 30) == 0 && Alpha(sub.Still, 0, 0) == 0, "lossy sub-rect: transparent outside");
            string apng = new ApngWriter(8, 8).Frame(F.Solid(8, 8, 1, 2, 3), 1, 10).Save("one.png");
            using (var s = File.OpenRead(apng)) Check(ApngContainer.Read(s) == null, "one fcTL is not animatable");
            var a = Load(apng)!;
            Check(a.Frames == null && a.Note == PictureNote.None, "one-fcTL APNG is a still");
        });

        Add("PlainStillsUnchanged", $"EDGE-{Fmt}-11", () =>
        {
            string png = F.PathFor("plain.png"); F.Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=40x30:d=1", "-frames:v", "1", png);
            string jpg = F.PathFor("plain.jpg"); F.Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=40x30:d=1", "-frames:v", "1", jpg);
            using (var s = File.OpenRead(png)) Check(AnimatedFormat.Sniff(s) == PictureKind.Png, "PNG sniffs Png");
            using (var s = File.OpenRead(png)) Check(ApngContainer.Read(s) == null, "plain PNG is not an APNG");
            using (var s = File.OpenRead(jpg)) Check(AnimatedFormat.Sniff(s) == PictureKind.Other, "JPEG sniffs Other");
            foreach (var path in new[] { png, jpg })
                Check(Load(path)!.Still.Pixels.AsSpan().SequenceEqual(WicStill(path).Pixels), Path.GetFileName(path) + " same as WIC");
        });

        Add("StillWebpScaledKeepsAlpha", $"EDGE-{Fmt}-13", () =>
        {
            string wide = F.PathFor("wide.webp"); File.WriteAllBytes(wide, F.StillWebp(F.Solid(3000, 20, 10, 200, 10), true, "wide"));
            Check(Load(wide)!.Still.Width == 2048, "scaled to 2048");
            string holes = F.PathFor("holes.webp"); File.WriteAllBytes(holes, F.StillWebp(F.Solid(20, 20, 10, 200, 10, 0), true, "holes"));
            Check(Alpha(Load(holes)!.Still, 5, 5) == 0, "alpha 0 kept");
        });

        Add("ApngDisposeBlendOps", $"EDGE-{Fmt}-14", () =>
        {
            var a = new ApngWriter(16, 16);
            a.Frame(F.Solid(16, 16, 200, 0, 0), 1, 10)
             .Frame(F.Solid(4, 4, 0, 0, 200), 1, 10, x: 2, y: 2, dispose: 1)
             .Frame(F.Solid(4, 4, 0, 200, 0), 1, 10, x: 8, y: 8, dispose: 2)
             .Frame(F.Solid(2, 2, 255, 255, 255, 128), 1, 10, x: 0, y: 0, blend: 1);
            var f = Load(a.Save("ops.png"))!.Frames!;
            Check(Near(At(f[1], 3, 3), (0, 0, 200), 0), "frame 1 drawn");
            Check(Near(At(f[2], 3, 3), (0, 0, 0), 0) && Near(At(f[2], 9, 9), (0, 200, 0), 0), "OP_BACKGROUND cleared");
            Check(Near(At(f[3], 9, 9), (200, 0, 0), 0) && Near(At(f[3], 0, 0), (228, 128, 128), 2), $"OP_PREVIOUS restored, OP_OVER blended ({At(f[3], 0, 0)})");
        });

        Add("VideoFpsCap", $"EDGE-{Fmt}-16", () =>
        {
            Check(Load(Mp4N(60, 60, "v60.mp4"))!.SourceFrameCount == 30, "60 fps capped to 30");
            Check(Load(F.Webm(F.ColourFrames(12), 12, "v12.webm"))!.SourceFrameCount == 12, "12 fps kept");
            string src = Mp4N(30, 10, "vfr-src.mp4");
            foreach (var ext in new[] { "mp4", "webm" })
            {
                string vfr = F.PathFor("vfr." + ext);
                F.Ffmpeg("-i", src, "-vf", "setpts='(N/10+if(eq(mod(N,3),0),0.013,0))/TB'", "-fps_mode", "vfr",
                    "-c:v", ext == "mp4" ? "libx264" : "libvpx-vp9", vfr);
                var pic = Load(vfr)!;
                Check(pic.Frames?.Count >= 2, ext + " VFR loads");
            }
        });

        Add("VideoRotationUpright", $"EDGE-{Fmt}-17", () =>
        {
            string src = Mp4N(5, 10, "rot-src.mp4", 64, 32);
            string rot = F.PathFor("rot.mp4");
            F.Ffmpeg("-display_rotation", "90", "-i", src, "-c", "copy", rot);
            var f = Load(rot)!.Frames!;
            Check(f[0].Width == 32 && f[0].Height == 64, $"upright 32x64 ({f[0].Width}x{f[0].Height})");
        });

        Add("VideoScaledNotRefused", $"EDGE-{Fmt}-18", () =>
        {
            string hd = F.PathFor("hd.mp4");
            F.Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=10:d=1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", hd);
            var pic = Load(hd)!;
            Check(pic.Frames?[0].Width == 1024 && pic.Frames[0].Height == 576 && pic.Note != PictureNote.TooLarge, "1024x576, no too-large note");
        });

        Add("SingleFrameVideoIsStill", $"EDGE-{Fmt}-20", () =>
        {
            var pic = Load(Mp4N(1, 10, "one.mp4"))!;
            Check(pic.Frames == null && pic.Note == PictureNote.None, "one-frame clip is a still");
        });

        Add("CapacityOneIsStillFormats", $"EDGE-{Fmt}-21", () =>
        {
            var w = new WebpWriter(8, 8);
            var a = new ApngWriter(8, 8);
            for (int k = 0; k < 3; k++) { var c = F.Colour(k); w.Frame(0, 0, F.Solid(8, 8, c.R, c.G, c.B), 100, 0x02); a.Frame(F.Solid(8, 8, c.R, c.G, c.B), 1, 10); }
            foreach (var path in new[] { w.Save("cap1.webp"), a.Save("cap1.png"), Mp4N(3, 10, "cap1.mp4") })
            {
                var pic = Load(path, max: 1)!;
                Check(pic.Frames == null && pic.Note == PictureNote.Capped && pic.SourceFrameCount == 3, Path.GetFileName(path) + " capped still");
            }
        });

        Add("FfmpegMissing", $"EDGE-{Fmt}-23", () =>
        {
            string mp4 = Mp4N(5, 10, "miss.mp4"), webp = Webp4("miss.webp");
            WithFfmpeg<object?>(() =>
            {
                Throws(() => Load(mp4), "mp4 without ffmpeg", e => e is InvalidOperationException);
                Throws(() => Load(webp), "webp without ffmpeg", e => e is InvalidOperationException);
                return null;
            }, exe: Path.Combine(F.Dir, "no-such-ffmpeg.exe"));
        });

        Add("FfmpegTimeoutKills", $"EDGE-{Fmt}-24", () =>
        {
            string mp4 = F.PathFor("slow.mp4");
            F.Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=1024x1024:r=30:d=10", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", mp4);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            WithFfmpeg<object?>(() => { Throws(() => Load(mp4), "timeout", e => e is TimeoutException); return null; }, timeout: TimeSpan.FromMilliseconds(1));
            Check(watch.Elapsed < TimeSpan.FromSeconds(5), "returns quickly");
            bool gone;
            try { gone = System.Diagnostics.Process.GetProcessById(FfmpegFrames.LastProcessId!.Value).HasExited; } catch { gone = true; }
            Check(gone, "ffmpeg process gone");
        });

        Add("FrameShortfallThrows", $"EDGE-{Fmt}-25", () =>
        {
            int count = FfmpegFrames.Decode(new[] { "-f", "lavfi", "-i", "color=c=red:s=8x8:r=10:d=0.3" }, "format=bgra", 8, 8, null, (_, _) => true);
            Check(count == 3, $"Decode reports what ffmpeg gave ({count})");
            var shortApng = new ApngWriter(8, 8) { DropLastFdat = true };
            for (int k = 0; k < 4; k++) { var c = F.Colour(k); shortApng.Frame(F.Solid(8, 8, c.R, c.G, c.B), 1, 10); }
            var pic = Load(shortApng.Save("short-fdat.png"))!;
            Check(pic.Frames == null && pic.Note == PictureNote.None && Near(At(pic.Still, 2, 2), C(0), 0), "frame shortfall: WIC still, no partial animation");
        });

        Add("WebpTempCleaned", $"EDGE-{Fmt}-26", () =>
        {
            int before = TempFolders();
            Load(Webp4("tmp.webp"));
            Check(TempFolders() == before, "after success");
            var bytes = File.ReadAllBytes(Webp4("tmp2.webp"));
            int at = LastIndexOf(bytes, "VP8L"u8.ToArray());
            for (int i = at + 14; i < Math.Min(bytes.Length, at + 40); i++) bytes[i] ^= 0xA5;
            string bad = F.PathFor("tmpbad.webp"); File.WriteAllBytes(bad, bytes);
            Throws(() => Load(bad), "corrupt");
            Check(TempFolders() == before, "after failure");
            WithFfmpeg<object?>(() => { Throws(() => Load(Webp4("tmp3.webp")), "timeout", e => e is TimeoutException); return null; }, timeout: TimeSpan.FromMilliseconds(1));
            Check(TempFolders() == before, "after timeout");
        });

        Add("OddPaths", $"EDGE-{Fmt}-27", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "AnimHarness", "it's a ü folder");
            Directory.CreateDirectory(dir);
            string mp4 = Path.Combine(dir, "-clip.mp4"); File.Copy(Mp4N(5, 10, "odd.mp4"), mp4, true);
            string webp = Path.Combine(dir, "-clip.webp"); File.Copy(Webp4("odd.webp"), webp, true);
            Check(Load(mp4)!.Frames?.Count == 5 && Load(webp)!.Frames?.Count == 4, "odd paths load");
        });

        Add("ShowinfoParseInvariant", $"EDGE-{Fmt}-28", () =>
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                comma.NumberFormat.NumberDecimalSeparator = ",";
                CultureInfo.CurrentCulture = comma;
                var a = FfmpegFrames.ParseShowinfoLine("[Parsed_showinfo_1 @ 000001] n:   3 pts:  15000 pts_time:0.06667 duration:   5000 duration_time:0.05    fmt:rgba cl:unspecified sar:0/1 s:64x48 i:P iskey:1");
                Check(a != null && Math.Abs(a.PtsSeconds!.Value - 0.06667) < 1e-9 && a.DurationSeconds == 0.05 && a.Width == 64, "parses with a comma culture");
                var b = FfmpegFrames.ParseShowinfoLine("[Parsed_showinfo_1 @ 000001] n:   0 pts:NOPTS pts_time:NOPTS fmt:rgba s:8x8 i:P");
                Check(b != null && b.PtsSeconds == null && b.DurationSeconds == null, "NOPTS and missing duration");
                Check(FfmpegFrames.ParseShowinfoLine("[Parsed_showinfo_1 @ 000001] color_range:tv") == null, "other showinfo lines ignored");

                FfmpegFrameInfo I(double? pts, double? dur = 0.1) => new(pts, dur, 8, 8);
                var t0 = PictureLoader.VideoTimeline(new[] { I(null), I(0.1), I(0.2) }, 10000);
                Check(t0.Starts.SequenceEqual(new[] { 0.0, 100, 200 }) && Math.Abs(t0.EndMs - 300) < 1e-6, "NOPTS at 0 counts from zero");
                var t1 = PictureLoader.VideoTimeline(new[] { I(0), I(null), I(0.3) }, 10000);
                Check(Math.Abs(t1.Starts[1] - 100) < 1e-6, $"NOPTS at 1 follows a 100 ms default ({t1.Starts[1]})");
                var t2 = PictureLoader.VideoTimeline(new[] { I(0), I(0.1), I(null), I(0.35) }, 10000);
                Check(Math.Abs(t2.Starts[2] - 200) < 1e-6, "NOPTS later repeats the previous delay");
                var t3 = PictureLoader.VideoTimeline(new[] { I(0), I(0.1), I(0.2, null) }, 10000);
                Check(Math.Abs(t3.EndMs - 300) < 1e-6, "missing last duration repeats the previous delay");
                var t4 = PictureLoader.VideoTimeline(new[] { I(0), I(0.1), I(0.2, 0) }, 10000);
                Check(Math.Abs(t4.EndMs - 300) < 1e-6, "zero last duration repeats the previous delay");
                var t5 = PictureLoader.VideoTimeline(new[] { I(0), I(5), I(10), I(10.1) }, 10000);
                Check(t5.Trimmed && t5.Usable == 2 && Math.Abs(t5.EndMs - 10000) < 1e-6, "a frame at exactly 10.000 s is cut");
                var t6 = PictureLoader.VideoTimeline(new[] { I(1.5), I(1.6) }, 10000);
                Check(t6.Starts.SequenceEqual(new[] { 0.0, 100 }) && !t6.Trimmed, "timed from the first frame");
            }
            finally { CultureInfo.CurrentCulture = old; }
        });

        Add("TimingNoDrift", $"EDGE-{Fmt}-29", () =>
        {
            var t = AnimatedFormat.Timing(Enumerable.Range(0, 90).Select(i => i * 1000.0 / 30).ToList(), 3000);
            Check(t.DelaysCs.Sum() == 300 && t.DelaysCs.All(d => d is 3 or 4) && t.DelaysCs.Take(3).SequenceEqual(new[] { 3, 4, 3 }), "30 fps: 3,4,3 summing to 300");
            var s = AnimatedFormat.Timing(new[] { 0.0, 15, 30 }, 45);
            Check(s.SourceIndices.SequenceEqual(new[] { 0, 1 }) && s.DelaysCs.SequenceEqual(new[] { 2, 3 }), "0/15/30 → [2,3]");
        });

        Add("GifRegression", $"EDGE-{Fmt}-30", () =>
        {
            string gif = F.PathFor("reg.gif");
            new GifWriter(8, 8, Palette).Solid(1).Solid(2).Save(gif);
            using (var s = File.OpenRead(gif)) Check(AnimatedFormat.Sniff(s) == PictureKind.Other, "GIF sniffs Other");
            Check(Load(gif)!.Frames?.Count == 2, "GIF still animates");
        });

        Add("WebpMatchesWic", $"EDGE-{Fmt}-31", () =>
        {
            var frames = new List<BgraImage>();
            for (int k = 0; k < 4; k++)
            {
                var img = new BgraImage(32, 24);
                for (int y = 0; y < 24; y++) for (int x = 0; x < 32; x++)
                {
                    int i = (y * 32 + x) * 4;
                    bool on = x >= k * 6 && x < k * 6 + 8 && y > 4 && y < 16;
                    img.Pixels[i] = (byte)(on ? 30 : 0); img.Pixels[i + 1] = (byte)(on ? 160 : 0); img.Pixels[i + 2] = (byte)(on ? 220 : 0); img.Pixels[i + 3] = (byte)(on ? 255 : 0);
                }
                frames.Add(img);
            }
            string raw = F.WriteRaw(frames, "wic.bgra");
            string path = F.PathFor("wic.webp");
            F.Ffmpeg("-f", "rawvideo", "-pix_fmt", "bgra", "-s", "32x24", "-r", "10", "-i", raw, "-c:v", "libwebp_anim", "-lossless", "1", "-loop", "0", path);
            var wic = WicFrames(path);
            if (wic == null) throw new SkipException("no WebP codec in Windows imaging");
            var ours = Load(path)!.Frames!;
            Check(ours.Count == wic.Count && ours.Zip(wic).All(z => z.First.Pixels.AsSpan().SequenceEqual(z.Second.Pixels)), $"matches WIC ({ours.Count} vs {wic.Count})");
        });

        Add("HighFpsWebpKeepsLoop", $"EDGE-{Fmt}-32", () =>
        {
            string raw = F.WriteRaw(F.ColourFrames(12, 16, 16), "60.bgra");
            string path = F.PathFor("a60.webp");
            F.Ffmpeg("-f", "rawvideo", "-pix_fmt", "bgra", "-s", "16x16", "-r", "60", "-i", raw, "-c:v", "libwebp_anim", "-lossless", "1", "-loop", "0", path);
            var file = WebpContainer.Parse(File.ReadAllBytes(path));
            var starts = new List<double>(); double t = 0;
            foreach (var f in file.Frames) { starts.Add(t); t += AnimatedFormat.BrowserDelayMs(f.DurationMs); }
            var timing = AnimatedFormat.Timing(starts, t);
            Check(timing.DelaysCs.All(d => d >= 2) && timing.DelaysCs.Sum() == 20, $"delays ≥ 2 summing to 20 ({string.Join(",", timing.DelaysCs)})");
            var pic = Load(path)!;
            Check(pic.Timeline!.LoopTicks == 6 && pic.Thinned && pic.Frames!.Count >= 2
                && pic.Timeline.StartTicks.SequenceEqual(FramePlan.Plan(timing.DelaysCs, 48).Timeline.StartTicks), "6-tick loop, thinned");
        });

        Add("ApngDefaultNotFrame", $"EDGE-{Fmt}-33", () =>
        {
            var a = new ApngWriter(8, 8) { DefaultIsFrame = false, DefaultImage = F.Solid(8, 8, 9, 99, 199) };
            a.Frame(F.Solid(8, 8, 200, 0, 0), 1, 10).Frame(F.Solid(8, 8, 0, 200, 0), 1, 10);
            string path = a.Save("nodef.png");
            using (var s = File.OpenRead(path)) Check(ApngContainer.Read(s) == null, "not animatable here");
            var pic = WithFfmpeg(() => Load(path)!, exe: Path.Combine(F.Dir, "no-such-ffmpeg.exe"));
            Check(pic.Frames == null && pic.Note == PictureNote.None && Near(At(pic.Still, 2, 2), (9, 99, 199), 0), "WIC still of the default image");
        });

        Add("ApngFailureFallsBack", $"EDGE-{Fmt}-34", () =>
        {
            string path = Apng4("fb.png");
            var missing = WithFfmpeg(() => Load(path)!, exe: Path.Combine(F.Dir, "no-such-ffmpeg.exe"));
            Check(missing.Frames == null && Near(At(missing.Still, 5, 5), C(0), 0), "ffmpeg missing → WIC still");
            var timeout = WithFfmpeg(() => Load(path)!, timeout: TimeSpan.FromMilliseconds(1));
            Check(timeout.Frames == null, "timeout → WIC still");
            var claim = new ApngWriter(8, 8) { DropLastFdat = true };
            for (int k = 0; k < 3; k++) { var c = F.Colour(k); claim.Frame(F.Solid(8, 8, c.R, c.G, c.B), 1, 10); }
            var pic = Load(claim.Save("claim.png"))!;
            Check(pic.Frames == null && pic.Note == PictureNote.None && Near(At(pic.Still, 2, 2), C(0), 0), "fcTL without fdAT → WIC still of the default image");
        });

        Add("VideoStartOffset", $"EDGE-{Fmt}-35", () =>
        {
            foreach (var (src, ext) in new[] { (Mp4N(20, 10, "off-src.mp4"), "mp4"), (F.Webm(F.ColourFrames(20), 10, "off-src.webm"), "webm") })
            {
                string off = F.PathFor("off." + ext);
                F.Ffmpeg("-i", src, "-c", "copy", "-output_ts_offset", "1.5", off);
                var pic = Load(off)!;
                Check(pic.Frames?.Count == 20 && pic.Timeline!.StartTicks[0] == 0 && pic.Timeline.LoopTicks == 60, $"{ext} offset ({pic.Frames?.Count} {pic.Timeline?.LoopTicks})");
            }
        });

        Add("TruncatedStillsAsToday", $"EDGE-{Fmt}-36", () =>
        {
            string png = F.PathFor("tr.png"); F.Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=64x48:d=1", "-frames:v", "1", png);
            string jpg = F.PathFor("tr.jpg"); F.Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=64x48:d=1", "-frames:v", "1", jpg);
            foreach (var full in new[] { png, jpg })
            {
                var bytes = File.ReadAllBytes(full);
                string cut = F.PathFor("cut-" + Path.GetFileName(full));
                File.WriteAllBytes(cut, bytes[..(bytes.Length * 2 / 3)]);
                BgraImage? wic = null;
                try { wic = WicStill(cut); } catch { }
                if (wic == null) Throws(() => Load(cut), Path.GetFileName(cut) + " throws as WIC does");
                else Check(Load(cut) is { Frames: null } pic && pic.Still.Pixels.AsSpan().SequenceEqual(wic.Pixels), Path.GetFileName(cut) + " same still as WIC");
            }
            string garbage = F.PathFor("garbage.png");
            File.WriteAllBytes(garbage, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Enumerable.Range(0, 200).Select(i => (byte)(i * 37 % 256))).ToArray());
            FfmpegFrames.LastProcessId = null;
            Throws(() => Load(garbage), "PNG signature + garbage is unreadable");
            Check(FfmpegFrames.LastProcessId == null, "no ffmpeg run for a PNG");
        });
    }

    private static void ApplyRestoreFormats(Func<TempMod> make, string[] avfx)
    {
        var w = new WebpWriter(64, 48);
        var a = new ApngWriter(64, 48);
        for (int k = 0; k < 4; k++) { var c = F.Colour(k); w.Frame(0, 0, F.Solid(64, 48, c.R, c.G, c.B), 100, 0x02); a.Frame(F.Solid(64, 48, c.R, c.G, c.B), 1, 10); }
        foreach (var path in new[] { w.Save("apply.webp"), a.Save("apply.png"), Mp4N(6, 10, "apply.mp4") })
        {
            using var mod = make();
            var before = AvfxBytes(mod, avfx);
            var groups = mod.Groups();
            var site = mod.Scan();
            var pic = Load(path, max: site.Capacity)!;
            Check(DjTableAnimationCore.Apply(mod.Host, site, pic.Frames!, pic.Timeline!, FitMode.Fill, null, out _, out string e), $"{Path.GetFileName(path)} apply {e}");
            var status = DjTableAnimationCore.Status(mod.Scan(mod.NewHost()));
            Check(status.Kind == AnimationStatusKind.Ours && status.Frames == pic.Frames!.Count, $"{Path.GetFileName(path)} Ours({pic.Frames!.Count})");
            Check(Restore(mod, out _, out e, mod.NewHost()), e);
            CheckPristine(mod, before, groups, Path.GetFileName(path));
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static byte[] FfmpegStdout(params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo(FfmpegFrames.ExePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-hide_banner", "-loglevel", "error" }.Concat(args)) info.ArgumentList.Add(x);
        using var p = System.Diagnostics.Process.Start(info)!;
        var err = p.StandardError.ReadToEndAsync();
        var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(err.Result);
        return ms.ToArray();
    }

    private static BgraImage WicStill(string path) => Task.Run(async () =>
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var d = await BitmapDecoder.CreateAsync(stream);
        var px = await d.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return new BgraImage((int)d.OrientedPixelWidth, (int)d.OrientedPixelHeight, px.DetachPixelData());
    }).GetAwaiter().GetResult();

    /// <summary>Windows' WebP frames flattened over black, or null when there is no WebP codec.</summary>
    private static List<BgraImage>? WicFrames(string path) => Task.Run(async () =>
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var d = await BitmapDecoder.CreateAsync(stream);
            var list = new List<BgraImage>();
            for (uint i = 0; i < d.FrameCount; i++)
            {
                var f = await d.GetFrameAsync(i);
                var px = (await f.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
                list.Add(FrameFlatten.OverBlack(px, (int)f.PixelWidth, (int)f.PixelHeight));
            }
            return list;
        }
        catch { return null; }
    }).GetAwaiter().GetResult();

    private static byte[] RemoveChunk(byte[] file, string tag)
    {
        var o = new MemoryStream();
        o.Write(file, 0, 12);
        int p = 12;
        while (p + 8 <= file.Length)
        {
            int size = BitConverter.ToInt32(file, p + 4);
            int total = 8 + size + (size & 1);
            if (System.Text.Encoding.ASCII.GetString(file, p, 4) != tag) o.Write(file, p, total);
            p += total;
        }
        var bytes = o.ToArray();
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        return bytes;
    }

    /// <summary>Makes the last chunk inside the RIFF claim <paramref name="extra"/> more bytes than the RIFF holds.</summary>
    private static byte[] GrowLastChunk(byte[] file, int extra)
    {
        int riffEnd = 8 + BitConverter.ToInt32(file, 4);
        int p = 12, last = 12;
        while (p + 8 <= riffEnd) { last = p; int size = BitConverter.ToInt32(file, p + 4); p += 8 + size + (size & 1); }
        BitConverter.GetBytes(BitConverter.ToInt32(file, last + 4) + extra).CopyTo(file, last + 4);
        return file;
    }

    private static int LastIndexOf(byte[] hay, byte[] needle)
    {
        for (int i = hay.Length - needle.Length; i >= 0; i--)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
