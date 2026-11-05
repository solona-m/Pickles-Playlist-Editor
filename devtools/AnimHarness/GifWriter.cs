namespace AnimHarness;

/// <summary>
/// Writes GIF89a fixtures: a global palette, per-frame control extensions and sub-rect frames.
/// LZW uses fixed 9-bit literal codes with a clear code often enough that the code size never grows.
/// </summary>
internal sealed class GifWriter
{
    private readonly int _width, _height;
    private readonly (byte R, byte G, byte B)[] _palette;
    private readonly List<byte[]> _frames = new();

    public GifWriter(int width, int height, params (byte R, byte G, byte B)[] palette)
    {
        _width = width;
        _height = height;
        _palette = new (byte, byte, byte)[256];
        for (int i = 0; i < palette.Length && i < 256; i++) _palette[i] = palette[i];
    }

    /// <param name="gce">False writes the frame with no control extension at all.</param>
    public GifWriter Frame(int left, int top, int w, int h, Func<int, int, byte> index,
        int delayCs = 10, int disposal = 0, int? transparent = null, bool gce = true)
    {
        var ms = new MemoryStream();
        if (gce)
        {
            ms.Write(new byte[] { 0x21, 0xF9, 4, (byte)((disposal << 2) | (transparent.HasValue ? 1 : 0)),
                (byte)delayCs, (byte)(delayCs >> 8), (byte)(transparent ?? 0), 0 });
        }

        ms.WriteByte(0x2C);
        WriteShort(ms, left); WriteShort(ms, top); WriteShort(ms, w); WriteShort(ms, h);
        ms.WriteByte(0);

        var indices = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) indices[y * w + x] = index(x, y);
        WriteLzw(ms, indices);
        _frames.Add(ms.ToArray());
        return this;
    }

    public GifWriter Solid(byte colour, int delayCs = 10, int disposal = 0) =>
        Frame(0, 0, _width, _height, (_, _) => colour, delayCs, disposal);

    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        WriteShort(ms, _width); WriteShort(ms, _height);
        ms.WriteByte(0xF7); ms.WriteByte(0); ms.WriteByte(0);
        foreach (var (r, g, b) in _palette) { ms.WriteByte(r); ms.WriteByte(g); ms.WriteByte(b); }

        // NETSCAPE2.0 loop forever
        ms.Write(new byte[] { 0x21, 0xFF, 11 });
        ms.Write("NETSCAPE2.0"u8);
        ms.Write(new byte[] { 3, 1, 0, 0, 0 });

        foreach (var frame in _frames) ms.Write(frame);
        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());

    private static void WriteShort(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

    private static void WriteLzw(Stream s, byte[] indices)
    {
        const int Clear = 256, Eoi = 257;
        s.WriteByte(8);
        var bits = new BitPacker();
        bits.Add(Clear);
        int sinceClear = 0;
        foreach (byte b in indices)
        {
            if (sinceClear == 250) { bits.Add(Clear); sinceClear = 0; }
            bits.Add(b);
            sinceClear++;
        }
        bits.Add(Eoi);
        var data = bits.ToArray();
        for (int i = 0; i < data.Length; i += 255)
        {
            int n = Math.Min(255, data.Length - i);
            s.WriteByte((byte)n);
            s.Write(data, i, n);
        }
        s.WriteByte(0);
    }

    private sealed class BitPacker
    {
        private readonly List<byte> _bytes = new();
        private int _acc, _count;

        public void Add(int code)
        {
            _acc |= code << _count;
            _count += 9;
            while (_count >= 8) { _bytes.Add((byte)_acc); _acc >>= 8; _count -= 8; }
        }

        public byte[] ToArray()
        {
            if (_count > 0) _bytes.Add((byte)_acc);
            return _bytes.ToArray();
        }
    }

    // ---- calibration ------------------------------------------------------------------------------

    internal static readonly string[] Digits =
    {
        "01110,10001,10011,10101,11001,10001,01110", "00100,01100,00100,00100,00100,00100,01110",
        "01110,10001,00001,00010,00100,01000,11111", "11111,00010,00100,00010,00001,10001,01110",
        "00010,00110,01010,10010,11111,00010,00010", "11111,10000,11110,00001,00001,10001,01110",
        "00110,01000,10000,11110,10001,10001,01110", "11111,00001,00010,00100,01000,01000,01000",
        "01110,10001,10001,01110,10001,10001,01110", "01110,10001,10001,01111,00001,00010,01100",
    };

    /// <summary>A big digit 0–9, one per second, on a 10 s loop: the in-game speed check.</summary>
    public static byte[] Calibration()
    {
        const int W = 440, H = 290, Cell = 32;
        var gif = new GifWriter(W, H, (0, 0, 0), (255, 255, 255), (255, 80, 40));
        for (int d = 0; d < 10; d++)
        {
            var rows = Digits[d].Split(',');
            int ox = (W - 5 * Cell) / 2, oy = (H - 7 * Cell) / 2;
            gif.Frame(0, 0, W, H, (x, y) =>
            {
                int cx = (x - ox) / Cell, cy = (y - oy) / Cell;
                bool on = x >= ox && y >= oy && cx < 5 && cy < 7 && rows[cy][cx] == '1';
                if (on) return 1;
                return (byte)(y > H - 12 && x < W * (d + 1) / 10 ? 2 : 0);
            }, delayCs: 100);
        }
        return gif.ToBytes();
    }
}
