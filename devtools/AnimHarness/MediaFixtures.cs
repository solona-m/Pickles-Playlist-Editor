using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Tex;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace AnimHarness;

/// <summary>Fixture files for the WebP, APNG and video routes, made with the bundled ffmpeg or by hand.</summary>
internal static class MediaFixtures
{
    public static readonly string Dir = Path.Combine(Path.GetTempPath(), "AnimHarness", "media");

    public static string PathFor(string name)
    {
        Directory.CreateDirectory(Dir);
        return Path.Combine(Dir, name);
    }

    /// <summary>Runs the bundled ffmpeg; throws with its stderr on failure.</summary>
    public static void Ffmpeg(params string[] args)
    {
        var info = new ProcessStartInfo(FfmpegFrames.ExePath)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("fixture ffmpeg failed: " + err.Result);
    }

    public static BgraImage Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var img = new BgraImage(w, h);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = b; img.Pixels[i + 1] = g; img.Pixels[i + 2] = r; img.Pixels[i + 3] = a; }
        return img;
    }

    /// <summary>Distinct, easily told apart colour for frame k.</summary>
    public static (byte R, byte G, byte B) Colour(int k) =>
        ((byte)(k * 53 % 200 + 40), (byte)(k * 97 % 200 + 30), (byte)(255 - k * 31 % 200));

    public static string WriteRaw(IEnumerable<BgraImage> frames, string name)
    {
        string path = PathFor(name);
        using var f = File.Create(path);
        foreach (var frame in frames) f.Write(frame.Pixels);
        return path;
    }

    /// <summary>Encodes BGRA frames as a clip. <paramref name="codecArgs"/> pick the codec.</summary>
    public static string Video(IReadOnlyList<BgraImage> frames, double fps, string name, params string[] codecArgs)
    {
        string raw = WriteRaw(frames, name + ".bgra");
        string path = PathFor(name);
        Ffmpeg(new[] { "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{frames[0].Width}x{frames[0].Height}",
            "-r", fps.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", raw }
            .Concat(codecArgs).Append(path).ToArray());
        return path;
    }

    public static string Mp4(IReadOnlyList<BgraImage> frames, double fps, string name) =>
        Video(frames, fps, name, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast", "-qp", "0");

    public static string Webm(IReadOnlyList<BgraImage> frames, double fps, string name) =>
        Video(frames, fps, name, "-c:v", "libvpx-vp9", "-lossless", "1", "-pix_fmt", "yuv444p");

    public static List<BgraImage> ColourFrames(int n, int w = 64, int h = 48) =>
        Enumerable.Range(0, n).Select(k => { var c = Colour(k); return Solid(w, h, c.R, c.G, c.B); }).ToList();

    /// <summary>A still WebP made by ffmpeg's libwebp. Lossy with alpha comes out as VP8X + ALPH + VP8.</summary>
    public static byte[] StillWebp(BgraImage image, bool lossless, string name)
    {
        string raw = WriteRaw(new[] { image }, name + ".bgra");
        string path = PathFor(name + ".webp");
        var args = new List<string> { "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{image.Width}x{image.Height}", "-i", raw, "-c:v", "libwebp" };
        bool alpha = image.Pixels.Where((_, i) => i % 4 == 3).Any(a => a != 255);
        if (lossless) args.AddRange(new[] { "-lossless", "1", "-pix_fmt", "bgra" });
        else args.AddRange(new[] { "-lossless", "0", "-q:v", "95", "-pix_fmt", alpha ? "yuva420p" : "yuv420p" });
        args.Add(path);
        Ffmpeg(args.ToArray());
        return File.ReadAllBytes(path);
    }

    /// <summary>The image chunks of a still WebP (ALPH + VP8, or VP8L), for re-use inside an ANMF.</summary>
    public static byte[] ImageChunks(byte[] stillWebp)
    {
        var o = new MemoryStream();
        int p = 12;
        while (p + 8 <= stillWebp.Length)
        {
            string tag = Encoding.ASCII.GetString(stillWebp, p, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(stillWebp.AsSpan(p + 4));
            int total = 8 + size + (size & 1);
            if (tag is "ALPH" or "VP8 " or "VP8L") o.Write(stillWebp, p, Math.Min(total, stillWebp.Length - p));
            p += total;
        }
        return o.ToArray();
    }
}

/// <summary>Hand-assembled animated WebP: VP8X + ANIM + ANMF with explicit rects, timing and flags.</summary>
internal sealed class WebpWriter(int width, int height)
{
    private readonly List<byte[]> _frames = new();
    public uint Background { get; set; }

    /// <param name="flags">bit 0 = dispose to background, bit 1 = do not blend.</param>
    public WebpWriter Frame(int x, int y, BgraImage image, int durationMs, byte flags = 0, bool lossless = true, byte[]? rawChunks = null)
    {
        byte[] chunks = rawChunks ?? MediaFixtures.ImageChunks(MediaFixtures.StillWebp(image, lossless, $"anmf{Guid.NewGuid():N}"));
        var data = new byte[16 + chunks.Length];
        U24(data, 0, x / 2); U24(data, 3, y / 2); U24(data, 6, image.Width - 1); U24(data, 9, image.Height - 1);
        U24(data, 12, durationMs);
        data[15] = flags;
        chunks.CopyTo(data, 16);
        _frames.Add(Chunk("ANMF", data));
        return this;
    }

    public byte[] ToBytes(byte[]? trailing = null)
    {
        var body = new MemoryStream();
        var vp8x = new byte[10];
        vp8x[0] = 0x02 | 0x10;   // animation | alpha
        U24(vp8x, 4, width - 1); U24(vp8x, 7, height - 1);
        body.Write(Chunk("VP8X", vp8x));
        var anim = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(anim, Background);
        body.Write(Chunk("ANIM", anim));
        foreach (var f in _frames) body.Write(f);
        var file = new MemoryStream();
        file.Write("RIFF"u8);
        file.Write(BitConverter.GetBytes((uint)(4 + body.Length)));
        file.Write("WEBP"u8);
        file.Write(body.ToArray());
        if (trailing != null) file.Write(trailing);
        return file.ToArray();
    }

    public string Save(string name, byte[]? trailing = null)
    {
        string path = MediaFixtures.PathFor(name);
        File.WriteAllBytes(path, ToBytes(trailing));
        return path;
    }

    public static byte[] Chunk(string tag, byte[] data)
    {
        var c = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.ASCII.GetBytes(tag).CopyTo(c, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(4), (uint)data.Length);
        data.CopyTo(c, 8);
        return c;
    }

    private static void U24(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); }
}

/// <summary>Hand-assembled APNG: acTL, fcTL with explicit delays/rects/ops, IDAT and fdAT.</summary>
internal sealed class ApngWriter(int width, int height)
{
    private sealed record F(int X, int Y, BgraImage Image, ushort Num, ushort Den, byte Dispose, byte Blend);
    private readonly List<F> _frames = new();
    public bool DefaultIsFrame { get; set; } = true;
    public BgraImage? DefaultImage { get; set; }
    public int ClaimExtraFrames { get; set; }

    public ApngWriter Frame(BgraImage image, ushort num, ushort den, int x = 0, int y = 0, byte dispose = 0, byte blend = 0)
    {
        _frames.Add(new F(x, y, image, num, den, dispose, blend));
        return this;
    }

    public byte[] ToBytes(bool corruptFdat = false)
    {
        var o = new MemoryStream();
        o.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6;
        Write(o, "IHDR", ihdr);
        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)(_frames.Count + ClaimExtraFrames));
        Write(o, "acTL", actl);

        uint seq = 0;
        if (!DefaultIsFrame) Write(o, "IDAT", Compress(DefaultImage ?? MediaFixtures.Solid(width, height, 9, 9, 9)));
        for (int i = 0; i < _frames.Count; i++)
        {
            var f = _frames[i];
            var fctl = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(fctl, seq++);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(4), (uint)f.Image.Width);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(8), (uint)f.Image.Height);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(12), (uint)f.X);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(16), (uint)f.Y);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), f.Num);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), f.Den);
            fctl[24] = f.Dispose; fctl[25] = f.Blend;
            Write(o, "fcTL", fctl);
            var z = Compress(f.Image);
            if (i == 0 && DefaultIsFrame) { Write(o, "IDAT", z); continue; }
            if (corruptFdat && i == _frames.Count - 1) for (int k = 2; k < z.Length; k++) z[k] ^= 0x5A;
            var fdat = new byte[4 + z.Length];
            BinaryPrimitives.WriteUInt32BigEndian(fdat, seq++);
            z.CopyTo(fdat, 4);
            Write(o, "fdAT", fdat);
        }
        Write(o, "IEND", Array.Empty<byte>());
        return o.ToArray();
    }

    public string Save(string name, bool corruptFdat = false)
    {
        string path = MediaFixtures.PathFor(name);
        File.WriteAllBytes(path, ToBytes(corruptFdat));
        return path;
    }

    private static byte[] Compress(BgraImage img)
    {
        var raw = new byte[img.Height * (1 + img.Width * 4)];
        int p = 0;
        for (int y = 0; y < img.Height; y++)
        {
            raw[p++] = 0;
            for (int x = 0; x < img.Width; x++)
            {
                int i = (y * img.Width + x) * 4;
                raw[p++] = img.Pixels[i + 2]; raw[p++] = img.Pixels[i + 1]; raw[p++] = img.Pixels[i]; raw[p++] = img.Pixels[i + 3];
            }
        }
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    private static void Write(Stream o, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        o.Write(len);
        var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        o.Write(td);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(td));
        o.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }
}
