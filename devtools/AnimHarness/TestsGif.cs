using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Tex;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AnimHarness;

internal static partial class Program
{
    private static readonly (byte, byte, byte)[] Palette =
        { (0, 0, 0), (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255) };

    private static string GifPath(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AnimHarness", "gifs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static LoadedPicture? Load(string path, bool animate = true, int max = 48, IProgress<string>? progress = null) =>
        PictureLoader.LoadAsync(path, animate, max, progress).GetAwaiter().GetResult();

    /// <summary>The pixel at (x, y) as (r, g, b).</summary>
    private static (int R, int G, int B) At(BgraImage img, int x, int y)
    {
        int i = (y * img.Width + x) * 4;
        return (img.Pixels[i + 2], img.Pixels[i + 1], img.Pixels[i]);
    }

    private static void RegisterGif()
    {
        Add("GifComposeDisposal", "AC-animated-table-7", () =>
        {
            string path = GifPath("disposal.gif");
            new GifWriter(8, 8, Palette)
                .Frame(0, 0, 8, 8, (_, _) => 1, disposal: 1)
                .Frame(2, 2, 4, 4, (x, y) => (byte)((x + y) % 2 == 0 ? 2 : 9), disposal: 2, transparent: 9)
                .Frame(0, 0, 2, 2, (_, _) => 3, disposal: 3)
                .Frame(7, 7, 1, 1, (_, _) => 4)
                .Save(path);
            var pic = Load(path)!;
            Check(pic.Frames?.Count == 4, $"4 frames ({pic.Frames?.Count})");
            var f = pic.Frames!;
            Check(At(f[0], 3, 3) == (255, 0, 0), "f0 red");
            Check(At(f[1], 2, 2) == (0, 255, 0) && At(f[1], 3, 2) == (255, 0, 0), "f1 green over red, transparent shows red");
            Check(At(f[2], 0, 0) == (0, 0, 255) && At(f[2], 3, 3) == (0, 0, 0), "f2 blue, disposal 2 cleared to black");
            Check(At(f[3], 0, 0) == (255, 0, 0) && At(f[3], 3, 3) == (0, 0, 0) && At(f[3], 7, 7) == (255, 255, 255), "f3 restore-previous");
        });

        Add("CanvasFromContainer", "AC-animated-table-7 EDGE-animated-table-46", () =>
        {
            string path = GifPath("canvas.gif");
            new GifWriter(64, 48, Palette)
                .Frame(8, 8, 16, 16, (_, _) => 1)
                .Frame(0, 0, 64, 48, (_, _) => 2)
                .Save(path);
            var pic = Load(path)!;
            var f0 = pic.Frames![0];
            Check(f0.Width == 64 && f0.Height == 48, $"canvas 64x48 ({f0.Width}x{f0.Height})");
            Check(At(f0, 0, 0) == (0, 0, 0) && At(f0, 10, 10) == (255, 0, 0), "frame 0 placed on black");
        });

        Add("FirstFrameForStillCards", "AC-animated-table-9", () =>
        {
            string path = GifPath("still.gif");
            new GifWriter(8, 8, Palette).Solid(1).Solid(2).Save(path);
            var pic = Load(path, animate: false)!;
            Check(pic.Frames == null && pic.Note == PictureNote.TableOnly && At(pic.Still, 4, 4) == (255, 0, 0), "first frame, table-only note");
        });

        Add("SingleFrameGifIsStill", "EDGE-animated-table-1", () =>
        {
            string path = GifPath("one.gif");
            new GifWriter(8, 8, Palette).Solid(3).Save(path);
            var pic = Load(path)!;
            Check(pic.Frames == null && At(pic.Still, 1, 1) == (0, 0, 255), "still");
        });

        Add("TiffIsStill", "EDGE-animated-table-2", () =>
        {
            string path = GifPath("pages.tif");
            WriteTiff(path).GetAwaiter().GetResult();
            var pic = Load(path)!;
            Check(pic.Frames == null && pic.Still.Width == 6 && At(pic.Still, 1, 1).R == 255, "first page as a still");
        });

        Add("DelayDefaults", "EDGE-animated-table-3", () =>
        {
            string path = GifPath("delays.gif");
            new GifWriter(8, 8, Palette)
                .Frame(0, 0, 8, 8, (_, _) => 1, gce: false)
                .Frame(0, 0, 8, 8, (_, _) => 2, delayCs: 0)
                .Frame(0, 0, 8, 8, (_, _) => 3, delayCs: 1)
                .Frame(0, 0, 8, 8, (_, _) => 4, delayCs: 5)
                .Save(path);
            var pic = Load(path)!;
            Check(pic.Frames?.Count == 4, "decodes without a GCE");
            Check(pic.Timeline!.StartTicks.SequenceEqual(new[] { 0, 3, 6, 9 }), $"10/10/10/5 cs ({string.Join(",", pic.Timeline.StartTicks)})");
        });

        Add("HugeCanvasStill", "EDGE-animated-table-6", () =>
        {
            string path = GifPath("huge.gif");
            new GifWriter(2050, 10, Palette).Solid(1).Solid(2).Save(path);
            var pic = Load(path)!;
            Check(pic.Frames == null && pic.Note == PictureNote.TooLarge && pic.Still.Width == 2048, $"still, too-large note ({pic.Still.Width})");
        });

        Add("FrameClippedToCanvas", "EDGE-animated-table-7", () =>
        {
            string path = GifPath("clip.gif");
            new GifWriter(8, 8, Palette)
                .Solid(1)
                .Frame(4, 4, 6, 6, (_, _) => 3)
                .Save(path);
            var pic = Load(path)!;
            var f1 = pic.Frames![1];
            Check(At(f1, 5, 5) == (0, 0, 255) && At(f1, 0, 0) == (255, 0, 0), "clipped frame composited");
        });

        Add("ManyFramesBoundedMemory", "EDGE-animated-table-40", () =>
        {
            string path = GifPath("many.gif");
            var gif = new GifWriter(512, 512, Palette);
            for (int i = 0; i < 600; i++) gif.Frame(i % 500, 0, 4, 4, (_, _) => (byte)(1 + i % 3), delayCs: 4);
            gif.Save(path);
            GC.Collect();
            long baseline = GC.GetTotalMemory(true), peak = 0;
            var progress = new SyncProgress(_ => peak = Math.Max(peak, GC.GetTotalMemory(true) - baseline));
            var pic = Load(path, progress: progress)!;
            long frame = 512 * 512 * 4;
            long bound = 48 * frame + 4 * frame + 64L * 1024 * 1024;
            Check(pic.Frames?.Count == 48, $"48 frames ({pic.Frames?.Count})");
            Check(peak <= bound, $"peak {peak / 1048576} MB <= {bound / 1048576} MB");
        });

        Add("Calibration", "U2", () =>
        {
            string path = GifPath("calibration.gif");
            File.WriteAllBytes(path, GifWriter.Calibration());
            var pic = Load(path)!;
            Check(pic.Frames?.Count == 10 && pic.Timeline!.LoopTicks == 300 && pic.Timeline.StartTicks[1] == 30, "10 digits, 1 s each");
        });
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static async Task WriteTiff(string path)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, stream);
        var red = Enumerable.Range(0, 6 * 4).SelectMany(_ => new byte[] { 0, 0, 255, 255 }).ToArray();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 6, 4, 96, 96, red);
        await encoder.GoToNextFrameAsync();
        var blue = Enumerable.Range(0, 3 * 3).SelectMany(_ => new byte[] { 255, 0, 0, 255 }).ToArray();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 3, 3, 96, 96, blue);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        File.WriteAllBytes(path, bytes);
    }
}
