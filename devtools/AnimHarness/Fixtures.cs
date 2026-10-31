using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;
using System.IO.Compression;
using System.Security.Cryptography;

namespace AnimHarness;

internal sealed class SkipException(string why) : Exception(why);

/// <summary>A throwaway copy of a DJ pack, holding only what the table animation reads.</summary>
internal sealed class TempMod : IDisposable
{
    public required string Root { get; init; }
    public required string StateFolder { get; set; }
    public required DjTextureTarget Table { get; init; }
    public required string Base { get; init; }
    public TempModHost Host { get; set; } = null!;

    public TempModHost NewHost(Func<string>? tokens = null) => Host = new TempModHost(Root, StateFolder, tokens);

    public string Full(string relative) => Path.Combine(Root, relative);

    public DjTableAnimationSite Scan(TempModHost? host = null) =>
        DjAnimationScan.Inspect(Root, Table, StateFolder, (host ?? Host).ReadFile);

    public byte[] Read(string relative) => File.ReadAllBytes(Full(relative));

    /// <summary>Hashes of every file under the mod and the state folder.</summary>
    public Dictionary<string, string> Snapshot()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in new[] { Root, StateFolder })
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    map[f] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        return map;
    }

    /// <summary>Every option group, for DeepEquals.</summary>
    public List<JObject> Groups()
    {
        string meta = Path.Combine(Root, "meta.json");
        var root = JObject.Parse(File.ReadAllText(meta));
        if (root["Groups"] is JArray inline) return inline.OfType<JObject>().ToList();
        return Directory.GetFiles(Root, "group_*.json").OrderBy(f => f).Select(f => JObject.Parse(File.ReadAllText(f))).ToList();
    }

    public void Dispose()
    {
        try { Directory.Delete(Base, true); } catch { }
    }
}

internal static class Fixtures
{
    public static string PicklesRoot = @"E:\Penumbradt\DJ Pickles Base Mod Pack For All V5.0.7 (2)";
    public static string MoonPack = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "DJ Moon Base Mod Pack.pmp");

    private static readonly string Temp = Path.Combine(Path.GetTempPath(), "AnimHarness");

    public static readonly string[] PicklesAvfx =
        { @"common\59\laladjtable.avfx", @"common\59\alinasexy.avfx", @"common\59\medium.avfx", @"common\59\tall.avfx" };

    public const string MoonCat = @"common\59\alinasexy.avfx";
    public const string MoonCat2 = @"common\67\alinasexy2.avfx";
    public const string MoonPlain = @"vfx 02\dj table - plain\vfx\alinasexy.avfx";

    public static TempMod Pickles(string folderName = "pickles")
    {
        if (!Directory.Exists(PicklesRoot)) throw new SkipException("Pickles base pack not found");
        var (root, baseDir) = NewRoot(folderName);
        File.Copy(Path.Combine(PicklesRoot, "meta.json"), Path.Combine(root, "meta.json"));
        foreach (var rel in PicklesAvfx.Append(PicklesEq55)) CopyFile(PicklesRoot, root, rel);
        UndoAppAnimation(PicklesRoot, root, AppBackups);
        RestoreShippedTexture(Path.Combine(AppBackups, "textures", Path.GetFileName(PicklesRoot)), root, PicklesEq55);
        return Make(root, baseDir, folderName);
    }

    public const string PicklesEq55 = @"common\4\eq55.atex";

    /// <summary>The app's own backups folder; only ever read here.</summary>
    public static readonly string AppBackups = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicklesPlaylistEditor", "backups");

    /// <summary>
    /// Puts the shipped texture back when the app has pasted a still into the source pack. The backup
    /// must hash to its recorded OriginalSha, or the copy is left alone and this throws.
    /// </summary>
    internal static void RestoreShippedTexture(string backupModFolder, string copyRoot, string relative)
    {
        string folder = Path.Combine(backupModFolder, relative.Replace('\\', '-').Replace('/', '-'));
        string statePath = Path.Combine(folder, "state.json");
        if (!File.Exists(statePath)) return;
        string expected = (string?)JObject.Parse(File.ReadAllText(statePath))["OriginalSha"] ?? "";
        byte[] original = File.ReadAllBytes(Path.Combine(folder, "original.atex"));
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(original)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{folder}: original.atex does not match OriginalSha.");
        string target = Path.Combine(copyRoot, relative);
        if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(original)) File.WriteAllBytes(target, original);
    }

    /// <summary>
    /// The source pack may have been animated with the app itself. Its pristine backups (read only)
    /// give the shipped effects back, and the copied manifest loses the frame keys.
    /// </summary>
    internal static void UndoAppAnimation(string source, string copy, string backupsRoot)
    {
        string folder = Path.Combine(backupsRoot, "textures", Path.GetFileName(source), "animation");
        var state = DjAnimationScan.TryReadState(folder);
        if (state == null) return;
        foreach (var entry in state.Avfx)
        {
            string pristine = DjAnimationScan.PristinePath(folder, entry.OriginalSha);
            if (File.Exists(pristine) && File.Exists(Path.Combine(copy, entry.Relative)))
                File.Copy(pristine, Path.Combine(copy, entry.Relative), overwrite: true);
        }
        string meta = Path.Combine(copy, "meta.json");
        var root = JObject.Parse(File.ReadAllText(meta));
        foreach (var group in (root["Groups"] as JArray ?? new JArray()).OfType<JObject>())
            DjTableAnimationCore.PruneFrameKeys(group, new HashSet<string>());
        File.WriteAllText(meta, root.ToString());
    }

    public static TempMod Moon(string folderName = "moon")
    {
        string source = MoonSource();
        var (root, baseDir) = NewRoot(folderName);
        foreach (string json in Directory.GetFiles(source, "*.json")) File.Copy(json, Path.Combine(root, Path.GetFileName(json)));
        foreach (var rel in new[] { MoonCat, MoonCat2, @"common\68\alinasexy2.avfx", MoonPlain, @"common\56\eq55.atex" })
            CopyFile(source, root, rel);

        // Other textures only need a header and a length; the scan never reads their pixels.
        foreach (string atex in Directory.GetFiles(Path.Combine(source, "common"), "*.atex", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, atex);
            string target = Path.Combine(root, rel);
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var head = new byte[80];
            long length;
            using (var src = File.OpenRead(atex)) { src.ReadExactly(head); length = src.Length; }
            using var dst = File.Create(target);
            dst.Write(head);
            dst.SetLength(length);
        }
        return Make(root, baseDir, folderName);
    }

    private static string MoonSource()
    {
        string folder = Path.Combine(Temp, "moon-src");
        if (File.Exists(Path.Combine(folder, "meta.json"))) return folder;
        if (!File.Exists(MoonPack)) throw new SkipException("Moon Base .pmp not found");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        ZipFile.ExtractToDirectory(MoonPack, folder);
        return folder;
    }

    private static (string Root, string Base) NewRoot(string folderName)
    {
        string baseDir = Path.Combine(Temp, "runs", Guid.NewGuid().ToString("N")[..8]);
        string root = Path.Combine(baseDir, folderName);
        Directory.CreateDirectory(root);
        return (root, baseDir);
    }

    private static void CopyFile(string from, string to, string rel)
    {
        string target = Path.Combine(to, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(from, rel), target);
    }

    private static TempMod Make(string root, string baseDir, string folderName)
    {
        var table = DjTextureScan.Locate(root, folderName, out _).FirstOrDefault(t => t.Surface.Key == "table")
            ?? throw new InvalidOperationException("No table surface in the temp mod.");
        var mod = new TempMod
        {
            Root = root,
            Base = baseDir,
            StateFolder = Path.Combine(baseDir, "state", folderName, "animation"),
            Table = table,
        };
        mod.NewHost();
        return mod;
    }

    // ---- pictures ---------------------------------------------------------------------------------

    /// <summary>Frame k is a distinct colour with a moving bar, so frames can be told apart.</summary>
    public static List<BgraImage> Frames(int n, int w = 120, int h = 80)
    {
        var frames = new List<BgraImage>();
        for (int k = 0; k < n; k++)
        {
            var img = new BgraImage(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    bool bar = Math.Abs(x - (k * 7 % w)) < 6;
                    img.Pixels[i] = (byte)(bar ? 255 : (k * 37) % 200);
                    img.Pixels[i + 1] = (byte)(bar ? 255 : (k * 71 + y) % 220);
                    img.Pixels[i + 2] = (byte)(bar ? 255 : (k * 13 + x) % 240);
                    img.Pixels[i + 3] = 255;
                }
            frames.Add(img);
        }
        return frames;
    }

    // ---- overlay probe ------------------------------------------------------------------------------

    public const string TableGamePath = "vfx/atex/eq55.atex";

    /// <summary>Upright band colours per edge, outer to inner: top, bottom, left, right.</summary>
    public static readonly (byte R, byte G, byte B)[][] Bands =
    {
        new (byte, byte, byte)[] { (255, 0, 0), (255, 255, 255), (255, 128, 0) },
        new (byte, byte, byte)[] { (0, 255, 0), (255, 255, 255), (0, 255, 255) },
        new (byte, byte, byte)[] { (0, 0, 255), (255, 255, 255), (255, 0, 255) },
        new (byte, byte, byte)[] { (255, 255, 0), (255, 255, 255), (128, 0, 255) },
    };

    public const int BandWidth = 4;

    /// <summary>
    /// Ten upright 440x289 frames: three 4 px bands per edge, a big digit 0–9 and a progress bar,
    /// so each edge's cut-off and the timing can be read in game.
    /// </summary>
    public static List<BgraImage> PanelCalibrationFrames(int w = 440, int h = 289)
    {
        const int Cell = 32, Edge = 3 * BandWidth;
        var frames = new List<BgraImage>();
        for (int d = 0; d < 10; d++)
        {
            var img = new BgraImage(w, h);
            var rows = GifWriter.Digits[d].Split(',');
            int ox = (w - 5 * Cell) / 2, oy = (h - 7 * Cell) / 2;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    (byte R, byte G, byte B) c = (0, 0, 0);
                    if (y < Edge) c = Bands[0][y / BandWidth];
                    else if (y >= h - Edge) c = Bands[1][(h - 1 - y) / BandWidth];
                    else if (x < Edge) c = Bands[2][x / BandWidth];
                    else if (x >= w - Edge) c = Bands[3][(w - 1 - x) / BandWidth];
                    else
                    {
                        int cx = (x - ox) / Cell, cy = (y - oy) / Cell;
                        if (x >= ox && y >= oy && cx < 5 && cy < 7 && rows[cy][cx] == '1') c = (255, 255, 255);
                        else if (y >= h - Edge - 12 && x < Edge + (w - 2 * Edge) * (d + 1) / 10) c = (255, 80, 40);
                    }
                    int i = (y * w + x) * 4;
                    img.Pixels[i] = c.B; img.Pixels[i + 1] = c.G; img.Pixels[i + 2] = c.R; img.Pixels[i + 3] = 255;
                }
            frames.Add(img);
        }
        return frames;
    }

    /// <summary>"cc rr" in the 5x7 digit font at 2x, white on transparent black.</summary>
    public static BgraImage CellLabel(int column, int row)
    {
        string text = $"{column:D2} {row:D2}";
        var img = new BgraImage(text.Length * 12, 14);
        for (int ch = 0; ch < text.Length; ch++)
        {
            if (text[ch] == ' ') continue;
            var rows = GifWriter.Digits[text[ch] - '0'].Split(',');
            for (int y = 0; y < 14; y++)
                for (int x = 0; x < 10; x++)
                    if (rows[y / 2][x / 2] == '1')
                    {
                        int i = (y * img.Width + ch * 12 + x) * 4;
                        img.Pixels[i] = img.Pixels[i + 1] = img.Pixels[i + 2] = img.Pixels[i + 3] = 255;
                    }
        }
        return img;
    }

    public static (byte R, byte G, byte B) CellColour(int column, int row) =>
        ((byte)(40 + column * 13), (byte)(40 + row * 13), (byte)(((column + row) % 2) * 120 + 60));

    /// <summary>
    /// The whole atlas as a 16x16 grid of 64 px cells, each labelled upright, with the panel rect
    /// outlined in white and ticked inward every 4 px (longer every 16 px).
    /// </summary>
    public static BgraImage GridImage(DjTextureSurface surface)
    {
        int w = surface.TextureWidth, h = surface.TextureHeight;
        var img = new BgraImage(w, h);
        void Put(int x, int y, (byte R, byte G, byte B) c)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int i = (y * w + x) * 4;
            img.Pixels[i] = c.B; img.Pixels[i + 1] = c.G; img.Pixels[i + 2] = c.R; img.Pixels[i + 3] = 255;
        }
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) Put(x, y, CellColour(x / 64, y / 64));

        for (int row = 0; row < h / 64; row++)
            for (int column = 0; column < w / 64; column++)
            {
                var label = ImageOps.Rotate(CellLabel(column, row), -surface.QuarterTurns);
                int ox = column * 64 + (64 - label.Width) / 2, oy = row * 64 + (64 - label.Height) / 2;
                for (int y = 0; y < label.Height; y++)
                    for (int x = 0; x < label.Width; x++)
                        if (label.Pixels[(y * label.Width + x) * 4 + 3] != 0) Put(ox + x, oy + y, (255, 255, 255));
            }

        var r = surface.Rect;
        var white = ((byte)255, (byte)255, (byte)255);
        for (int x = r.X; x < r.Right; x++) { Put(x, r.Y, white); Put(x, r.Bottom - 1, white); }
        for (int y = r.Y; y < r.Bottom; y++) { Put(r.X, y, white); Put(r.Right - 1, y, white); }
        for (int x = r.X; x < r.Right; x += 4)
        {
            int len = (x - r.X) % 16 == 0 ? 12 : 6;
            for (int t = 0; t < len; t++) { Put(x, r.Y + t, white); Put(x, r.Bottom - 1 - t, white); }
        }
        for (int y = r.Y; y < r.Bottom; y += 4)
        {
            int len = (y - r.Y) % 16 == 0 ? 12 : 6;
            for (int t = 0; t < len; t++) { Put(r.X + t, y, white); Put(r.Right - 1 - t, y, white); }
        }
        return img;
    }

    /// <summary>The atlas with its colour replaced by <see cref="GridImage"/>; alpha kept.</summary>
    public static byte[] GridAtlas(byte[] atlas, DjTextureSurface surface) =>
        TexPaste.PasteRect(atlas, new TexRect(0, 0, surface.TextureWidth, surface.TextureHeight), GridImage(surface), out string e)
            ?? throw new InvalidDataException(e);

    /// <summary>
    /// A temp copy of the Pickles "DJ Table / Small" option as shipped: pristine effect from the
    /// animation backup, eq55 from the texture backup, no frame keys. Returns the copy's root.
    /// </summary>
    public static string PicklesShippedSource(string picklesRoot, string backupsRoot)
    {
        if (!Directory.Exists(picklesRoot)) throw new SkipException("Pickles base pack not found");
        string root = Path.Combine(Temp, "runs", Guid.NewGuid().ToString("N")[..8], Path.GetFileName(picklesRoot));
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(picklesRoot, "meta.json"), Path.Combine(root, "meta.json"));
        foreach (var (game, rel) in SmallOption(picklesRoot).Files)
            if (!game.StartsWith(AvfxTableAnimation.FramePrefix, StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(root, rel)))
                CopyFile(picklesRoot, root, rel);
        UndoAppAnimation(picklesRoot, root, backupsRoot);
        RestoreShippedTexture(Path.Combine(backupsRoot, "textures", Path.GetFileName(picklesRoot)), root, PicklesEq55);
        return root;
    }

    public static ModOption SmallOption(string modRoot) =>
        PenumbraOptions.Read(modRoot).First(o => o.GroupName == "DJ Table" && o.Name == "Small");

    /// <summary>The effect's Append inspection, with eq55 as the table texture.</summary>
    public static TableAvfxInfo Inspect(byte[] avfx) =>
        AvfxTableAnimation.Inspect(avfx, new[] { TableGamePath }, _ => false).Info
            ?? throw new InvalidDataException("The effect does not inspect as animatable.");

    /// <summary>The effect padded with dummy Tex entries up to <paramref name="total"/> textures.</summary>
    public static byte[] WithExtraTextures(byte[] avfx, int total)
    {
        var root = AvfxChunk.Parse(avfx);
        int have = root.All("Tex").Count();
        int last = root.Children!.FindLastIndex(c => c.Name == "Tex");
        for (int i = have; i < total; i++) root.Children.Insert(++last, AvfxChunk.Leaf("Tex", AvfxChunk.TextBytes($"vfx/pad{i}.atex")));
        root.SetInt("TxCn", total);
        return root.ToBytes();
    }

    /// <summary>
    /// A copy of the user's real Pickles animation as the previous version left it (live effects,
    /// frames and manifest, its state and pristine copies), or null when there is none.
    /// </summary>
    public static TempMod? LivePicklesAnimation()
    {
        if (!Directory.Exists(PicklesRoot)) throw new SkipException("Pickles base pack not found");
        string backup = Path.Combine(AppBackups, "textures", Path.GetFileName(PicklesRoot), "animation");
        var state = DjAnimationScan.TryReadState(backup);
        if (state == null || state.Frames.Count == 0) return null;

        var (root, baseDir) = NewRoot("live");
        File.Copy(Path.Combine(PicklesRoot, "meta.json"), Path.Combine(root, "meta.json"));
        foreach (var rel in PicklesAvfx.Append(PicklesEq55).Concat(state.Frames.Select(f => f.Relative)))
            if (File.Exists(Path.Combine(PicklesRoot, rel))) CopyFile(PicklesRoot, root, rel);
        var mod = Make(root, baseDir, "live");
        foreach (string f in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(mod.StateFolder, Path.GetRelativePath(backup, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
        return mod;
    }

    public static FrameTimeline Timeline(int n, int delayCs = 10, int maxFrames = FramePlan.MaxFrames) =>
        FramePlan.Plan(Enumerable.Repeat(delayCs, n).ToList(), maxFrames).Timeline;

    public static double Psnr(BgraImage a, BgraImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 0;
        double sum = 0;
        int count = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4)
            for (int c = 0; c < 3; c++) { double d = a.Pixels[i + c] - b.Pixels[i + c]; sum += d * d; count++; }
        double mse = sum / count;
        return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    // ---- avfx inspection --------------------------------------------------------------------------

    public static List<AvfxChunk> Particles(byte[] avfx) => AvfxChunk.Parse(avfx).All("Ptcl").ToList();

    public static List<string> Textures(byte[] avfx) => AvfxChunk.Parse(avfx).All("Tex").Select(t => t.Text()).ToList();

    public static byte[] ParticleBytes(AvfxChunk p) => AvfxChunk.Container("Ptcl", p.Children!).ToBytesForCompare();

    public static byte[] AlphaKeys(AvfxChunk particle) => particle.Child("Col")?.Child("A")?.Child("Keys")?.Data ?? Array.Empty<byte>();

    public static List<int> Targets(byte[] avfx, int emitter = 0)
    {
        var e = AvfxChunk.Parse(avfx).All("Emit").ElementAt(emitter);
        return e.All("ItPr").Last().Children!.Where(c => c.Name == "TgtB").Select(c => BitConverter.ToInt32(c.Data!, 0)).ToList();
    }

    private static byte[] ToBytesForCompare(this AvfxChunk chunk)
    {
        var wrapper = AvfxChunk.Container("AVFX", new[] { chunk });
        return Serialize(wrapper);
    }

    private static byte[] Serialize(AvfxChunk c)
    {
        using var ms = new MemoryStream();
        void Write(AvfxChunk x)
        {
            ms.Write(x.Tag);
            if (x.Children == null) { ms.Write(BitConverter.GetBytes(x.Data!.Length)); ms.Write(x.Data); }
            else { ms.Write(BitConverter.GetBytes(x.Children.Count)); foreach (var k in x.Children) Write(k); }
        }
        Write(c);
        return ms.ToArray();
    }
}
