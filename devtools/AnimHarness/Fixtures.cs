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
        foreach (var rel in PicklesAvfx.Append(@"common\4\eq55.atex")) CopyFile(PicklesRoot, root, rel);
        return Make(root, baseDir, folderName);
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

    public static FrameTimeline Timeline(int n, int delayCs = 10) =>
        FramePlan.Plan(Enumerable.Repeat(delayCs, n).ToList(), 48).Timeline;

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
