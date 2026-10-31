using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;

namespace AnimHarness;

/// <summary>
/// Writes "PPE overlay probe", a Penumbra mod built from the shipped Pickles Small table, for the
/// in-game check that the overlay sits where the full-atlas frames do. The app is not involved.
/// </summary>
internal static class OverlayProbe
{
    public const string Marker = "ppe-probe.txt";
    public const string AvfxGamePath = "vfx/alinasexy.avfx";
    public const string KeyPrefix = "vfx/atex/ppeprobe";

    public static readonly string[] OptionNames =
        { "0 Shipped", "1 Full-atlas calibration", "2 Overlay calibration", "3 Full-atlas grid", "4 Overlay grid" };

    public const string DeleteReminder = "Delete this folder after the in-game check; the app lists the probe as a mod otherwise.";

    public static string FrameKey(int option, int k) => $"{KeyPrefix}{option}_{k:D2}.atex";

    public static string FrameFile(int option, int k) => $@"probe\{option}\{k:D2}.atex";

    public static int Run(string folder, string picklesRoot, string backupsRoot)
    {
        if (!Directory.Exists(picklesRoot)) { Console.WriteLine($"Pickles base not found: {picklesRoot}"); return 1; }
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() && !File.Exists(Path.Combine(folder, Marker)))
        {
            Console.WriteLine($"Refusing: {folder} is not empty and is not a probe folder.");
            return 1;
        }

        string source;
        try { source = Fixtures.PicklesShippedSource(picklesRoot, backupsRoot); }
        catch (InvalidDataException ex) { Console.WriteLine("Refusing: " + ex.Message); return 1; }

        try { return BuildAndWrite(folder, source); }
        finally { try { Directory.Delete(Path.GetDirectoryName(source)!, true); } catch { } }
    }

    private static int BuildAndWrite(string folder, string source)
    {
        var option = Fixtures.SmallOption(source);
        string avfxRel = option.Files[AvfxGamePath];
        byte[] baseline = File.ReadAllBytes(Path.Combine(source, avfxRel));
        byte[] eq55 = File.ReadAllBytes(Path.Combine(source, Fixtures.PicklesEq55));
        var surface = DjTextureScan.Surfaces.First(s => s.Key == "table");
        var info = Fixtures.Inspect(baseline);

        var plan = AvfxTableOverlay.Plan(baseline, info, surface.Rect, eq55, PanelUvMap.Calibrated, out var refusal);
        if (plan == null) { Console.WriteLine($"Overlay refused: {refusal}"); return 1; }

        var frames = Fixtures.PanelCalibrationFrames().Select(f => surface.ToTextureSpace(f, FitMode.Fill)).ToList();
        var timeline = FramePlan.Plan(Enumerable.Repeat(100, frames.Count).ToList(), frames.Count).Timeline;
        byte[] crop = TexCrop.Crop(eq55, plan.Window, out string error) ?? throw new InvalidDataException(error);
        byte[] grid = Fixtures.GridAtlas(eq55, surface);
        byte[] gridCrop = TexCrop.Crop(grid, plan.Window, out error) ?? throw new InvalidDataException(error);

        var avfx = new byte[5][];
        var textures = new List<byte[]>[5];
        avfx[0] = baseline;
        textures[0] = new();
        textures[1] = frames.Select(f => TexPaste.PasteRect(eq55, surface.Rect, f, out error) ?? throw new InvalidDataException(error)).ToList();
        textures[2] = frames.Select(f => TexPaste.PasteRect(crop, plan.RectInWindow, f, out error) ?? throw new InvalidDataException(error)).ToList();
        textures[3] = new() { grid };
        textures[4] = new() { gridCrop };

        for (int n = 1; n <= 4; n++)
        {
            var keys = Enumerable.Range(0, textures[n].Count).Select(k => FrameKey(n, k)).ToList();
            var t = keys.Count == 1 ? Fixtures.Timeline(1) : timeline;
            string problem;
            byte[]? built;
            if (n % 2 == 1)
            {
                built = AvfxTableAnimation.Build(baseline, info, keys, t, out problem);
            }
            else
            {
                built = AvfxTableOverlay.Build(baseline, info, plan, keys, t, out refusal, out problem);
                if (built == null) { Console.WriteLine($"Overlay refused: {refusal} {problem}"); return 1; }
            }
            avfx[n] = built ?? throw new InvalidDataException(problem);
        }

        string staging = folder.TrimEnd('\\', '/') + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staging);
        try
        {
            WriteMod(staging, source, option, avfx, textures);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            string? parent = Path.GetDirectoryName(Path.GetFullPath(folder));
            if (parent != null) Directory.CreateDirectory(parent);
            Directory.Move(staging, folder);
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch { }
            throw;
        }
        Console.WriteLine($"Wrote {folder}");
        Console.WriteLine("Options: " + string.Join(" | ", OptionNames));
        Console.WriteLine($"Window {plan.Window.X},{plan.Window.Y} {plan.Window.Width}x{plan.Window.Height}; clip region " +
            $"{plan.ClipRegion.X},{plan.ClipRegion.Y} {plan.ClipRegion.Width}x{plan.ClipRegion.Height}");
        var m = PanelUvMap.Calibrated;
        Console.WriteLine($"Map ({m.ScaleU}u+{m.OffsetU}, {m.ScaleV}v+{m.OffsetV}); coverage of the panel {plan.Coverage.ToString("P1", System.Globalization.CultureInfo.InvariantCulture)}");
        Console.WriteLine($"eq55 SHA-256 {AvfxTableAnimation.Sha(eq55)}");
        Console.WriteLine($"Per-frame bytes: full atlas {textures[1][0].Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}, overlay {textures[2][0].Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}");
        Console.WriteLine(DeleteReminder);
        return 0;
    }

    private static void WriteMod(string staging, string source, ModOption option, byte[][] avfx, List<byte[]>[] textures)
    {
        var shared = option.Files.Where(f => f.Key != AvfxGamePath).ToList();
        foreach (var (_, rel) in shared) Copy(Path.Combine(source, rel), Path.Combine(staging, rel));

        var options = new JArray();
        for (int n = 0; n < 5; n++)
        {
            var files = new JObject();
            foreach (var (game, rel) in shared) files[game] = rel;
            files[AvfxGamePath] = $@"probe\{n}\alinasexy.avfx";
            Write(Path.Combine(staging, "probe", n.ToString(), "alinasexy.avfx"), avfx[n]);
            for (int k = 0; k < textures[n].Count; k++)
            {
                files[FrameKey(n, k)] = FrameFile(n, k);
                Write(Path.Combine(staging, FrameFile(n, k)), textures[n][k]);
            }
            options.Add(new JObject
            {
                ["Name"] = OptionNames[n], ["Description"] = "", ["Image"] = "",
                ["Files"] = files, ["FileSwaps"] = new JObject(), ["Manipulations"] = new JArray(),
            });
        }

        File.WriteAllText(Path.Combine(staging, "meta.json"), new JObject
        {
            ["FileVersion"] = 3, ["Name"] = "PPE overlay probe", ["Author"] = "Pickles Playlist Editor",
            ["Description"] = "In-game check for the overlay panel (GitHub #12).", ["Version"] = "1",
            ["Website"] = "", ["Image"] = "", ["ModTags"] = new JArray(),
        }.ToString());
        File.WriteAllText(Path.Combine(staging, "default_mod.json"), new JObject
        {
            ["Version"] = 0, ["Files"] = new JObject(), ["FileSwaps"] = new JObject(), ["Manipulations"] = new JArray(),
        }.ToString());
        File.WriteAllText(Path.Combine(staging, "group_001_overlay probe.json"), new JObject
        {
            ["Version"] = 0, ["Name"] = "Overlay probe", ["Description"] = "", ["Image"] = "", ["Page"] = 0,
            ["Priority"] = 0, ["Type"] = "Single", ["DefaultSettings"] = 0, ["Options"] = options,
        }.ToString());
        File.WriteAllText(Path.Combine(staging, Marker), "Written by AnimHarness --overlay-probe.\n");

    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, true);
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
