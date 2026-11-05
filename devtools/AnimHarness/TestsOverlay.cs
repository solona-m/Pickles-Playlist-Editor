using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;
using System.Globalization;
using System.Text;

namespace AnimHarness;

internal static partial class Program
{
    private const string Ovl = "gh12-overlay-panels";

    private static string OTag(string ids) =>
        string.Join(" ", ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(id => id[0] == 'A'
            ? $"AC-{Ovl}-{id[2..]}" : $"EDGE-{Ovl}-{id[2..]}"));

    private static readonly DjTextureSurface TableSurface = DjTextureScan.Surfaces.First(s => s.Key == "table");

    private sealed record SmallSetup(TempMod Mod, byte[] Baseline, TableAvfxInfo Info, byte[] Atlas, OverlayPlan Plan) : IDisposable
    {
        public void Dispose() => Mod.Dispose();
    }

    private static SmallSetup Small(string rel = Fixtures.PicklesSmall)
    {
        var mod = Fixtures.Pickles();
        byte[] baseline = mod.Read(rel), atlas = mod.Read(Fixtures.PicklesEq55);
        var info = Fixtures.Inspect(baseline);
        var plan = AvfxTableOverlay.Plan(baseline, info, TableSurface.Rect, atlas, PanelUvMap.Calibrated, out var refusal)
            ?? throw new InvalidOperationException("Pickles overlay refused: " + refusal);
        return new SmallSetup(mod, baseline, info, atlas, plan);
    }

    private static List<string> ProbeKeys(int n, string prefix = "vfx/atex/ppex_") =>
        Enumerable.Range(0, n).Select(k => $"{prefix}{k:D3}.atex").ToList();

    private static OverlayRefusal PlanRefusal(byte[] baseline, byte[] atlas, PanelUvMap? map = null, TexRect? rect = null)
    {
        AvfxTableOverlay.Plan(baseline, Fixtures.Inspect(baseline), rect ?? TableSurface.Rect, atlas, map ?? PanelUvMap.Calibrated, out var r);
        return r;
    }

    private static byte[] EditEffect(byte[] avfx, Action<AvfxChunk> edit)
    {
        var root = AvfxChunk.Parse(avfx);
        edit(root);
        return root.ToBytes();
    }

    private static AvfxChunk PanelOf(AvfxChunk root) => root.All("Ptcl").ElementAt(7);

    private static byte[] ModlPayload(params (string Name, byte[] Data)[] chunks)
    {
        using var ms = new MemoryStream();
        foreach (var (name, data) in chunks)
        {
            ms.Write(Encoding.ASCII.GetBytes(new string(name.Reverse().ToArray())));
            ms.Write(BitConverter.GetBytes(data.Length));
            ms.Write(data);
            ms.Write(new byte[((data.Length + 3) & ~3) - data.Length]);
        }
        return ms.ToArray();
    }

    private static (byte[] VDrw, byte[] VIdx) ModlParts(byte[] modl)
    {
        var m = AvfxModel.Parse(modl, out string e) ?? throw new InvalidDataException(e);
        var data = m.ToData();
        // ToData is VDrw then VIdx for these models.
        int dsize = BitConverter.ToInt32(data, 4);
        int ioff = 8 + ((dsize + 3) & ~3);
        return (data[8..(8 + dsize)], data[(ioff + 8)..(ioff + 8 + BitConverter.ToInt32(data, ioff + 4))]);
    }

    /// <summary>A model from (x, y, z, u, v) vertices and triangles; every UV pair equal.</summary>
    private static AvfxModel SyntheticModel(IList<(double X, double Y, double Z, double U, double V)> verts, IList<(int, int, int)> tris)
    {
        var vdrw = new List<byte>();
        foreach (var v in verts)
        {
            var b = new byte[AvfxModel.VertexBytes];
            BitConverter.GetBytes((Half)v.X).CopyTo(b, 0); BitConverter.GetBytes((Half)v.Y).CopyTo(b, 2);
            BitConverter.GetBytes((Half)v.Z).CopyTo(b, 4); BitConverter.GetBytes((Half)1).CopyTo(b, 6);
            for (int i = 8; i < 20; i++) b[i] = (byte)(i * 11);
            for (int p = 0; p < 4; p++) { BitConverter.GetBytes((Half)v.U).CopyTo(b, 20 + p * 4); BitConverter.GetBytes((Half)v.V).CopyTo(b, 22 + p * 4); }
            vdrw.AddRange(b);
        }
        var vidx = tris.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).SelectMany(i => BitConverter.GetBytes((ushort)i)).ToArray();
        return AvfxModel.Parse(ModlPayload(("VDrw", vdrw.ToArray()), ("VIdx", vidx)), out string e) ?? throw new InvalidDataException(e);
    }

    private static (double X, double Y) Px(byte[] vertex, PanelUvMap map, double w, double h)
    {
        var (u, v) = AvfxModel.Uv(vertex);
        var (s, t) = map.ToTexture(u, v);
        return (s * w, t * h);
    }

    private static double SignedArea((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        ((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2;

    /// <summary>One whole mip level, decoded.</summary>
    private static BgraImage DecodeLevel(byte[] tex, int level)
    {
        var h = TexHeader.TryRead(tex, out string e) ?? throw new InvalidDataException(e);
        var codec = BlockCodec.For(h.Format, out e) ?? throw new InvalidDataException(e);
        int w = h.MipWidth(level), ht = h.MipHeight(level);
        var img = new BgraImage(w, ht);
        Span<byte> tile = stackalloc byte[64];
        for (int by = 0; by < h.BlocksDown(level); by++)
            for (int bx = 0; bx < h.BlocksAcross(level); bx++)
            {
                codec.DecodeBlock(tex.AsSpan(h.MipOffsets[level] + (by * h.BlocksAcross(level) + bx) * codec.BlockBytes, codec.BlockBytes), tile);
                for (int ty = 0; ty < 4; ty++)
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int x = bx * 4 + tx, y = by * 4 + ty;
                        if (x < w && y < ht) tile.Slice((ty * 4 + tx) * 4, 4).CopyTo(img.Pixels.AsSpan((y * w + x) * 4, 4));
                    }
            }
        return img;
    }

    private static bool Same(BgraImage a, BgraImage b) =>
        a.Width == b.Width && a.Height == b.Height && a.Pixels.AsSpan().SequenceEqual(b.Pixels);

    private static bool White(BgraImage img, int x, int y) { var c = Rgb(img, x, y); return c.R > 200 && c.G > 200 && c.B > 200; }

    private static (int R, int G, int B) Rgb(BgraImage img, int x, int y)
    {
        int i = (y * img.Width + x) * 4;
        return (img.Pixels[i + 2], img.Pixels[i + 1], img.Pixels[i]);
    }

    // ---- the probe, run once ----------------------------------------------------------------------

    private sealed record ProbeRun(string Folder, int Exit, string Output);

    private static ProbeRun? _probe;

    private static ProbeRun RunProbe(string folder, string? backups = null)
    {
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        int exit;
        try { exit = OverlayProbe.Run(folder, Fixtures.PicklesRoot, backups ?? Fixtures.AppBackups); }
        finally { Console.SetOut(old); }
        return new ProbeRun(folder, exit, sw.ToString());
    }

    private static ProbeRun SharedProbe()
    {
        if (!Directory.Exists(Fixtures.PicklesRoot)) throw new SkipException("Pickles base pack not found");
        if (_probe != null) return _probe;
        string folder = Path.Combine(Path.GetTempPath(), "AnimHarness", "runs", "probe-" + Guid.NewGuid().ToString("N")[..8], "PPE overlay probe");
        _probe = RunProbe(folder);
        string probeBase = Path.GetDirectoryName(folder)!;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(probeBase, true); } catch { } };
        if (_probe.Exit != 0) throw new InvalidOperationException("probe failed: " + _probe.Output);
        return _probe;
    }

    private static string ShippedEq55Sha()
    {
        using var mod = Fixtures.Pickles();
        return AvfxTableAnimation.Sha(mod.Read(Fixtures.PicklesEq55));
    }

    private static void RegisterOverlay()
    {
        Add("ProbeModLayout", OTag("A-1 E-1 E-20") + " " + Shell("A-8"), () =>
        {
            var probe = SharedProbe();
            var options = PenumbraOptions.Read(probe.Folder);
            Check(options.Select(o => o.Name).SequenceEqual(OverlayProbe.OptionNames) && options.All(o => o.GroupName == "Overlay probe"),
                "five options in order: " + string.Join(", ", options.Select(o => o.Name)));
            Check(options.All(o => o.Files.All(f => File.Exists(Path.Combine(probe.Folder, f.Value)))), "every mapped file exists");

            byte[] pristine;
            using (var mod = Fixtures.Pickles()) pristine = mod.Read(Fixtures.PicklesSmall);
            Check(File.ReadAllBytes(Path.Combine(probe.Folder, options[0].Files[OverlayProbe.AvfxGamePath])).AsSpan().SequenceEqual(pristine),
                "option 0 is the pristine Small effect");
            string shipped = ShippedEq55Sha();
            Check(options.All(o => AvfxTableAnimation.Sha(File.ReadAllBytes(Path.Combine(probe.Folder, o.Files[Fixtures.TableGamePath]))) == shipped),
                "every option maps the shipped eq55");

            var keys = options.SelectMany(o => o.Files.Keys).ToList();
            Check(!keys.Any(k => k.StartsWith(AvfxTableAnimation.FramePrefix, StringComparison.OrdinalIgnoreCase)), "no ppe_ keys (live tokens pruned)");
            var frameKeys = keys.Where(k => k.StartsWith("vfx/atex/ppe", StringComparison.OrdinalIgnoreCase)).ToList();
            Check(frameKeys.Count == 22 && frameKeys.All(k => k.StartsWith(OverlayProbe.KeyPrefix)), $"22 probe frame keys ({frameKeys.Count})");
            Check(frameKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == frameKeys.Count, "frame keys unique");
            var prefix = Encoding.ASCII.GetBytes(AvfxTableAnimation.FramePrefix);
            Check(options.All(o => File.ReadAllBytes(Path.Combine(probe.Folder, o.Files[OverlayProbe.AvfxGamePath])).AsSpan().IndexOf(prefix) < 0),
                "no probe effect holds the FramePrefix bytes");
            Check(File.Exists(Path.Combine(probe.Folder, OverlayProbe.Marker)), "marker written");
            var meta = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(probe.Folder, "meta.json")));
            Check((string?)meta["Version"] == "2", $"meta Version 2 ({meta["Version"]})");
        });

        Add("ProbeFolderGuard", OTag("E-2") + " " + Shell("E-13"), () =>
        {
            if (!Directory.Exists(Fixtures.PicklesRoot)) throw new SkipException("Pickles base pack not found");
            string baseDir = Path.Combine(Path.GetTempPath(), "AnimHarness", "runs", "guard-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                string foreign = Path.Combine(baseDir, "foreign");
                Directory.CreateDirectory(foreign);
                File.WriteAllText(Path.Combine(foreign, "mine.txt"), "keep");
                var run = RunProbe(foreign);
                Check(run.Exit == 1 && Directory.GetFileSystemEntries(foreign).Length == 1
                    && File.ReadAllText(Path.Combine(foreign, "mine.txt")) == "keep", "unmarked folder refused and unchanged");
                Check(!Directory.GetDirectories(baseDir).Any(d => d.Contains(".tmp-")), "no staging left behind");

                string nested = Path.Combine(baseDir, "a", "b", "probe");
                Check(RunProbe(nested).Exit == 0 && File.Exists(Path.Combine(nested, OverlayProbe.Marker)), "missing parent created");
                File.WriteAllText(Path.Combine(nested, "stale.txt"), "old");
                Check(RunProbe(nested).Exit == 0 && !File.Exists(Path.Combine(nested, "stale.txt"))
                    && File.Exists(Path.Combine(nested, "meta.json")), "marked folder rewritten");
            }
            finally { try { Directory.Delete(baseDir, true); } catch { } }
        });

        Add("ProbeWithoutPickles", OTag("E-3"), () =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "AnimHarness", "runs", "nopk-" + Guid.NewGuid().ToString("N")[..8]);
            var old = Console.Out;
            Console.SetOut(new StringWriter());
            int exit;
            try { exit = OverlayProbe.Run(folder, @"Z:\no such pickles", Fixtures.AppBackups); }
            finally { Console.SetOut(old); }
            Check(exit == 1 && !Directory.Exists(folder), "exit 1, nothing written");
        });

        Add("ShippedEq55Restored", OTag("E-23"), () =>
        {
            if (!Directory.Exists(Fixtures.PicklesRoot)) throw new SkipException("Pickles base pack not found");
            string appBackup = Path.Combine(Fixtures.AppBackups, "textures", Path.GetFileName(Fixtures.PicklesRoot));
            string stateFile = Path.Combine(appBackup, "common-4-eq55.atex", "state.json");
            string sourceSha = AvfxTableAnimation.Sha(File.ReadAllBytes(Path.Combine(Fixtures.PicklesRoot, Fixtures.PicklesEq55)));
            string expect = File.Exists(stateFile)
                ? (string)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(stateFile))["OriginalSha"]!
                : sourceSha;
            Check(string.Equals(ShippedEq55Sha(), expect, StringComparison.OrdinalIgnoreCase), "fixture eq55 is the shipped one");

            string temp = Path.Combine(Path.GetTempPath(), "AnimHarness", "runs", "eq55-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                byte[] shipped = File.ReadAllBytes(Path.Combine(Fixtures.PicklesRoot, Fixtures.PicklesEq55));
                byte[] changed = (byte[])shipped.Clone();
                changed[^1] ^= 0xFF;
                string copy = Path.Combine(temp, "copy");
                void Seed() { Directory.CreateDirectory(Path.Combine(copy, @"common\4")); File.WriteAllBytes(Path.Combine(copy, Fixtures.PicklesEq55), changed); }
                void Backup(string mod, string sha)
                {
                    string dir = Path.Combine(mod, "common-4-eq55.atex");
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, "original.atex"), shipped);
                    File.WriteAllText(Path.Combine(dir, "state.json"), new Newtonsoft.Json.Linq.JObject { ["OriginalSha"] = sha }.ToString());
                }

                Seed();
                string good = Path.Combine(temp, "good");
                Backup(good, AvfxTableAnimation.Sha(shipped));
                Fixtures.RestoreShippedTexture(good, copy, Fixtures.PicklesEq55);
                Check(File.ReadAllBytes(Path.Combine(copy, Fixtures.PicklesEq55)).AsSpan().SequenceEqual(shipped), "matching backup restores");

                Seed();
                string bad = Path.Combine(temp, "bad");
                Backup(bad, new string('0', 64));
                Throws(() => { Fixtures.RestoreShippedTexture(bad, copy, Fixtures.PicklesEq55); return null; }, "mismatched backup throws", e => e is InvalidDataException);
                Check(File.ReadAllBytes(Path.Combine(copy, Fixtures.PicklesEq55)).AsSpan().SequenceEqual(changed), "mismatch leaves the copy");

                Fixtures.RestoreShippedTexture(Path.Combine(temp, "none"), copy, Fixtures.PicklesEq55);
                Check(File.ReadAllBytes(Path.Combine(copy, Fixtures.PicklesEq55)).AsSpan().SequenceEqual(changed), "no state.json leaves the copy");

                string root = Path.Combine(temp, "backups");
                string mod = Path.Combine(root, "textures", Path.GetFileName(Fixtures.PicklesRoot));
                string anim = Path.Combine(appBackup, "animation");
                if (Directory.Exists(anim))
                    foreach (string f in Directory.GetFiles(anim, "*", SearchOption.AllDirectories))
                    {
                        string t = Path.Combine(mod, "animation", Path.GetRelativePath(anim, f));
                        Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                        File.Copy(f, t);
                    }
                string noBackupProbe = Path.Combine(temp, "probe-plain");
                var plain = RunProbe(noBackupProbe, root);
                Check(plain.Exit == 0, "no texture backup: probe runs " + plain.Output);
                if (plain.Exit == 0)
                    Check(PenumbraOptions.Read(noBackupProbe).All(o => AvfxTableAnimation.Sha(File.ReadAllBytes(Path.Combine(noBackupProbe, o.Files[Fixtures.TableGamePath]))) == sourceSha),
                        "no texture backup: source eq55 used");

                Backup(mod, new string('0', 64));
                string badProbe = Path.Combine(temp, "probe-bad");
                Check(RunProbe(badProbe, root).Exit == 1 && !Directory.Exists(badProbe), "mismatched backup: probe exits 1, nothing written");
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        });

        Add("OverlayStructure", OTag("A-2") + " " + Shell("A-3"), () =>
        {
            using var s = Small();
            var keys = ProbeKeys(10);
            var built = AvfxTableOverlay.Build(s.Baseline, s.Info, s.Plan, keys, Fixtures.Timeline(10), out var refusal, out string problem);
            Check(built != null, $"built ({refusal} {problem})");
            if (built == null) return;
            var append = AvfxChunk.Parse(AvfxTableAnimation.Build(s.Baseline, s.Info with { Capacity = 128 }, keys, Fixtures.Timeline(10), out _)!);
            var root = AvfxChunk.Parse(built);
            var orig = AvfxChunk.Parse(s.Baseline);
            Check(root.ReadInt("PrCn") == 19 && root.ReadInt("TxCn") == 37 && root.ReadInt("MdCn") == 10,
                $"counts 19/37/10 ({root.ReadInt("PrCn")}/{root.ReadInt("TxCn")}/{root.ReadInt("MdCn")})");
            var emit = root.All("Emit").ElementAt(s.Info.Emitter);
            Check(emit.ReadInt("PrCn") == orig.All("Emit").ElementAt(s.Info.Emitter).ReadInt("PrCn") + 10, "emitter PrCn +10");
            var targets = Fixtures.Targets(built, s.Info.Emitter);
            Check(Enumerable.Range(9, 10).All(targets.Contains), "items target 9..18");

            var particles = root.All("Ptcl").ToList();
            var before = orig.All("Ptcl").ToList();
            foreach (int i in new[] { 0, 1, 2, 3, 4, 5, 6, 8 })
                Check(Fixtures.ParticleBytes(particles[i]).AsSpan().SequenceEqual(Fixtures.ParticleBytes(before[i])), $"particle {i} untouched");
            var panel = particles[7].Clone();
            Check(panel.Child("Data")!.Child("MdNo")!.Data![0] == 9, "panel draws model 9, the shell part");
            panel.Child("Data")!.Child("MdNo")!.Data![0] = 6;
            Check(Fixtures.ParticleBytes(panel).AsSpan().SequenceEqual(Fixtures.ParticleBytes(before[7])), "panel = shipped but MdNo");
            var models = root.All("Modl").ToList();
            var oldModels = orig.All("Modl").ToList();
            Check(models.Count == 10 && Enumerable.Range(0, 8).All(i => models[i].Data!.AsSpan().SequenceEqual(oldModels[i].Data)), "models 0–7 untouched");
            Check(models[8].Data!.AsSpan().SequenceEqual(s.Plan.ModlData), "model 8 is the overlay");
            Check(models[9].Data!.AsSpan().SequenceEqual(s.Plan.OutsideModlData), "model 9 is the shell part");
            var tex = root.All("Tex").Select(t => t.Text()).ToList();
            var oldTex = orig.All("Tex").Select(t => t.Text()).ToList();
            Check(tex.Take(27).SequenceEqual(oldTex), "Tex 0–26 untouched");

            var appended = append.All("Ptcl").ToList();
            for (int k = 0; k < 10; k++)
            {
                var clone = particles[9 + k];
                Check(AvfxTableAnimation.Tlst(clone) == 27 + k && clone.Child("Data")!.Child("MdNo")!.Data![0] == 8 && tex[27 + k] == keys[k],
                    $"clone {k}: TLst {27 + k}, MdNo 8");
                var copy = clone.Clone();
                copy.Child("Data")!.Child("MdNo")!.Data = new byte[] { 6 };
                Check(Fixtures.ParticleBytes(copy).AsSpan().SequenceEqual(Fixtures.ParticleBytes(appended[9 + k])), $"clone {k} = Append clone but MdNo");
            }
            Check(AvfxChunk.Parse(built).ToBytes().AsSpan().SequenceEqual(built), "built effect round-trips");
        });

        Add("OverlayModelGeometry", OTag("A-2 E-9 E-19"), () =>
        {
            using var s = Small();
            var root = AvfxChunk.Parse(s.Baseline);
            var source = AvfxModel.Parse(root.All("Modl").ElementAt(6).Data!, out _)!;
            var overlay = AvfxModel.Parse(s.Plan.ModlData, out string e);
            Check(overlay != null, "overlay model parses " + e);
            if (overlay == null) return;
            var map = PanelUvMap.Calibrated;
            var w = s.Plan.Window;
            var region = s.Plan.ClipRegion;

            int last = -1;
            foreach (int t in new[] { 31, 47, 48 })
            {
                var (a, b, c) = source.Triangles[t];
                int found = overlay.Triangles.FindIndex(o =>
                    overlay.Vertices[o.A].AsSpan(0, 20).SequenceEqual(source.Vertices[a].AsSpan(0, 20))
                    && overlay.Vertices[o.B].AsSpan(0, 20).SequenceEqual(source.Vertices[b].AsSpan(0, 20))
                    && overlay.Vertices[o.C].AsSpan(0, 20).SequenceEqual(source.Vertices[c].AsSpan(0, 20)));
                Check(found > last, $"triangle {t} verbatim, in source order (at {found})");
                last = found;
                foreach (int v in new[] { a, b, c })
                {
                    var src = Px(source.Vertices[v], map, 1024, 1024);
                    var o = overlay.Vertices[overlay.Triangles[found].A];
                    var match = new[] { overlay.Triangles[found].A, overlay.Triangles[found].B, overlay.Triangles[found].C }
                        .Select(i => overlay.Vertices[i]).First(x => x.AsSpan(0, 20).SequenceEqual(source.Vertices[v].AsSpan(0, 20)));
                    var (cs, ct) = Px(match, map, 1, 1);
                    Check(Math.Abs(w.X + cs * w.Width - src.X) <= 1 && Math.Abs(w.Y + ct * w.Height - src.Y) <= 1, $"tri {t} vertex {v} UV within 1/512");
                }
            }

            bool inside = overlay.Vertices.All(v =>
            {
                var (cs, ct) = Px(v, map, 1, 1);
                double x = w.X + cs * w.Width, y = w.Y + ct * w.Height;
                return cs >= -0.002 && cs <= 1.002 && ct >= -0.002 && ct <= 1.002
                    && x >= region.X - 1 && x <= region.Right + 1 && y >= region.Y - 1 && y <= region.Bottom + 1;
            });
            Check(inside, "every vertex inside the clip region, UVs inside the crop");

            // Triangle by triangle: winding and axis-aligned planes survive the clip.
            for (int t = 0; t < source.Triangles.Count; t++)
            {
                var one = AvfxModel.Parse(root.All("Modl").ElementAt(6).Data!, out _)!;
                one.Triangles.Clear();
                one.Triangles.Add(source.Triangles[t]);
                var clipped = AvfxModel.ClipToWindow(one, map, w, AvfxTableOverlay.FilterMargin, TableSurface.Rect, 1024, 1024, out _, out _);
                if (clipped == null) continue;
                var (a, b, c) = source.Triangles[t];
                double sign = Math.Sign(SignedArea(Px(source.Vertices[a], map, 1024, 1024), Px(source.Vertices[b], map, 1024, 1024), Px(source.Vertices[c], map, 1024, 1024)));
                foreach (var o in clipped.Triangles)
                {
                    double area = SignedArea(Px(clipped.Vertices[o.A], map, 512, 512), Px(clipped.Vertices[o.B], map, 512, 512), Px(clipped.Vertices[o.C], map, 512, 512));
                    Check(Math.Sign(area) == sign || Math.Abs(area) < 0.05, $"tri {t} keeps its winding");
                }
                for (int axis = 0; axis < 3; axis++)
                {
                    var bytes = source.Vertices[a].AsSpan(axis * 2, 2).ToArray();
                    if (!source.Vertices[b].AsSpan(axis * 2, 2).SequenceEqual(bytes) || !source.Vertices[c].AsSpan(axis * 2, 2).SequenceEqual(bytes)) continue;
                    Check(clipped.Vertices.All(v => v.AsSpan(axis * 2, 2).SequenceEqual(bytes)), $"tri {t} stays on its axis-{axis} plane");
                }
            }
        });

        Add("ClipSynthetic", OTag("E-9"), () =>
        {
            const double S = 1024;
            var map = new PanelUvMap(1, 0, 1, 0);
            var window = new TexRect(256, 256, 512, 512);
            var verts = new List<(double, double, double, double, double)>();
            var tris = new List<(int, int, int)>();
            void Tri((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
            {
                int i = verts.Count;
                foreach (var p in new[] { a, b, c }) verts.Add((p.X / 1000, p.Y / 1000, 0.5, p.X / S, p.Y / S));
                tris.Add((i, i + 1, i + 2));
            }
            Tri((300, 300), (400, 300), (300, 400));       // inside
            Tri((500, 500), (500.25, 500), (500, 500.25)); // tiny, inside
            Tri((700, 300), (800, 300), (700, 400));       // crosses x = 764
            Tri((763.5, 400), (900, 400), (763.5, 400.25)); // clipped part is a sliver
            Tri((900, 900), (950, 900), (900, 950));       // outside
            var model = SyntheticModel(verts, tris);
            var clipped = AvfxModel.ClipToWindow(model, map, window, 4, new TexRect(300, 300, 50, 50), 1024, 1024, out double coverage, out var refusal);
            Check(clipped != null, "clipped " + refusal);
            if (clipped == null) return;
            Check(clipped.Triangles.Count == 4, $"inside + tiny + two from the crossing one ({clipped.Triangles.Count})");
            for (int t = 0; t < 2; t++)
            {
                var o = clipped.Triangles[t];
                var src = tris[t];
                Check(clipped.Vertices[o.A].AsSpan(0, 20).SequenceEqual(model.Vertices[src.Item1].AsSpan(0, 20))
                    && clipped.Vertices[o.B].AsSpan(0, 20).SequenceEqual(model.Vertices[src.Item2].AsSpan(0, 20))
                    && clipped.Vertices[o.C].AsSpan(0, 20).SequenceEqual(model.Vertices[src.Item3].AsSpan(0, 20)), $"triangle {t} verbatim but UVs");
            }
            var crossing = clipped.Triangles.Skip(2).SelectMany(o => new[] { o.A, o.B, o.C }).Select(i => Px(clipped.Vertices[i], map, 512, 512)).ToList();
            Check(crossing.All(p => p.X + 256 <= 764.5) && crossing.Any(p => Math.Abs(p.X + 256 - 764) < 0.5), "crossing triangle cut at x = 764");
            foreach (var o in clipped.Triangles)
            {
                double area = SignedArea(Px(clipped.Vertices[o.A], map, 512, 512), Px(clipped.Vertices[o.B], map, 512, 512), Px(clipped.Vertices[o.C], map, 512, 512));
                Check(area >= 0, "winding kept");
            }
            Check(coverage > 0.99, $"coverage of the inside rect {coverage:F3}");
        });

        Add("BothSidesKept", OTag("E-10"), () =>
        {
            var verts = new List<(double, double, double, double, double)>
            {
                (-1, -1, 1, 0.25, 0.25), (1, -1, 1, 0.75, 0.25), (-1, 1, 1, 0.25, 0.75),
                (-1, -1, -1, 0.25, 0.25), (-1, 1, -1, 0.25, 0.75), (1, -1, -1, 0.75, 0.25),
            };
            var model = SyntheticModel(verts, new List<(int, int, int)> { (0, 1, 2), (3, 4, 5) });
            var clipped = AvfxModel.ClipToWindow(model, new PanelUvMap(1, 0, 1, 0), new TexRect(0, 0, 1024, 1024), 4,
                new TexRect(300, 300, 100, 100), 1024, 1024, out _, out _);
            Check(clipped != null && clipped.Triangles.Count == 2, "both faces kept");
            Check(clipped != null && clipped.Vertices.Select(v => BitConverter.ToHalf(v, 4)).Distinct().Count() == 2, "one on each side");
        });

        Add("OverlayFrameTexture", OTag("A-3 E-14"), () =>
        {
            using var s = Small();
            var crop = TexCrop.Crop(s.Atlas, s.Plan.Window, out string e);
            Check(crop != null, "cropped " + e);
            if (crop == null) return;
            var h = TexHeader.TryRead(crop, out e)!;
            var ah = TexHeader.TryRead(s.Atlas, out _)!;
            Check(h.Width == 512 && h.Height == 512 && h.Format == ah.Format && h.MipCount == 8 && crop.Length == 349_600,
                $"512x512 {h.Format} x{h.MipCount}, {crop.Length} bytes");
            var w = s.Plan.Window;
            for (int l = 0; l < 6; l++)
            {
                bool same = true;
                int across = (512 >> l) / 4;
                for (int by = 0; by < across; by++)
                {
                    int src = ah.MipOffsets[l] + (((w.Y >> l) / 4 + by) * ah.BlocksAcross(l) + (w.X >> l) / 4) * 16;
                    same &= crop.AsSpan(h.MipOffsets[l] + by * across * 16, across * 16).SequenceEqual(s.Atlas.AsSpan(src, across * 16));
                }
                Check(same, $"level {l} byte-copied");
            }
            var r = s.Plan.RectInWindow;
            for (int l = 6; l < 8; l++)
            {
                var img = DecodeLevel(crop, l);
                int x0 = r.X >> l, y0 = r.Y >> l, x1 = (r.Right + (1 << l) - 1) >> l, y1 = (r.Bottom + (1 << l) - 1) >> l;
                bool black = true;
                for (int y = 0; y < img.Height; y++)
                    for (int x = 0; x < img.Width; x++)
                        if (x < x0 || x >= x1 || y < y0 || y >= y1) { var c = Rgb(img, x, y); black &= Math.Max(c.R, Math.Max(c.G, c.B)) <= 8; }
                Check(img.Width == 512 >> l && black, $"level {l} re-encoded: valid, black outside the panel");
            }

            var picture = TableSurface.ToTextureSpace(Fixtures.Frames(1, 300, 200)[0], FitMode.Fill);
            var full = TexPaste.PasteRect(s.Atlas, TableSurface.Rect, picture, out _)!;
            var over = TexPaste.PasteRect(crop, r, picture, out _)!;
            Check(full.Length == 1_398_176, $"full-atlas frame {full.Length} bytes");
            Check(Same(TexPaste.ReadRect(full, w, out _)!, TexPaste.ReadRect(over, new TexRect(0, 0, 512, 512), out _)!),
                "overlay frame = full-atlas frame over the window");
        });

        Add("CropBc7OneMip", OTag("E-13"), () =>
        {
            var file = new byte[TexHeader.Size + 1024 * 1024];
            BitConverter.GetBytes(0x00800000u).CopyTo(file, 0);
            BitConverter.GetBytes((uint)TexFormat.Bc7).CopyTo(file, 4);
            BitConverter.GetBytes((ushort)1024).CopyTo(file, 8);
            BitConverter.GetBytes((ushort)1024).CopyTo(file, 10);
            BitConverter.GetBytes((ushort)1).CopyTo(file, 12);
            file[14] = 1;
            BitConverter.GetBytes(1u).CopyTo(file, 0x14);
            BitConverter.GetBytes(2u).CopyTo(file, 0x18);
            BitConverter.GetBytes(80).CopyTo(file, 0x1C);
            var tile = new byte[64];
            for (int i = 0; i < 64; i += 4) { tile[i] = 90; tile[i + 1] = 120; tile[i + 2] = 150; tile[i + 3] = 255; }
            var block = new byte[16];
            Bc7.EncodeBlock(tile, block);
            for (int o = TexHeader.Size; o < file.Length; o += 16) block.CopyTo(file, o);
            var crop = TexCrop.Crop(file, new TexRect(128, 256, 512, 512), out string e);
            var h = crop == null ? null : TexHeader.TryRead(crop, out e);
            Check(h != null && h.Format == TexFormat.Bc7 && h.MipCount == 1 && crop!.Length == 80 + 512 * 512, $"BC7 one-mip crop ({e})");
            Check(crop != null && BitConverter.ToUInt32(crop, 0x14) == 0 && BitConverter.ToUInt32(crop, 0x18) == 0, "LoD levels clamped to 0");
        });

        Add("OverlayKeepsAlpha", OTag("E-18"), () =>
        {
            using var s = Small();
            var crop = TexCrop.Crop(s.Atlas, s.Plan.Window, out _)!;
            var pic = Fixtures.Frames(1, 440, 289)[0];
            for (int i = 3; i < pic.Pixels.Length; i += 8) pic.Pixels[i] = 0;
            var tex = TableSurface.ToTextureSpace(pic, FitMode.Fill);
            var over = TexPaste.PasteRect(crop, s.Plan.RectInWindow, tex, out _)!;
            var full = TexPaste.PasteRect(s.Atlas, TableSurface.Rect, tex, out _)!;
            var got = TexPaste.ReadRect(over, s.Plan.RectInWindow, out _)!;
            var before = TexPaste.ReadRect(crop, s.Plan.RectInWindow, out _)!;
            var fullRect = TexPaste.ReadRect(full, TableSurface.Rect, out _)!;
            Check(Fixtures.Psnr(got, tex) > 30, $"colour replaced (PSNR {Fixtures.Psnr(got, tex):F1})");
            bool alpha = true, same = true;
            for (int i = 3; i < got.Pixels.Length; i += 4) { alpha &= got.Pixels[i] == before.Pixels[i]; same &= got.Pixels[i] == fullRect.Pixels[i]; }
            Check(alpha && same, "crop alpha kept, as on the full-atlas frame");
        });

        Add("OverlaySamplesMatchFullAtlas", OTag("A-4 E-21"), () =>
        {
            using var mod = Fixtures.Pickles();
            byte[] atlas = mod.Read(Fixtures.PicklesEq55);
            var map = PanelUvMap.Calibrated;
            OverlayPlan? first = null;
            foreach (var rel in Fixtures.PicklesAvfx)
            {
                byte[] baseline = mod.Read(rel);
                var info = Fixtures.Inspect(baseline);
                var plan = AvfxTableOverlay.Plan(baseline, info, TableSurface.Rect, atlas, map, out var refusal);
                Check(plan != null, $"{rel} plans ({refusal})");
                if (plan == null) continue;
                if (first == null) first = plan;
                else Check(plan.ModlData.AsSpan().SequenceEqual(first.ModlData) && plan.Window == first.Window && plan.ClipRegion == first.ClipRegion,
                    $"{rel} plans the same model, window and region");

                var source = AvfxModel.Parse(AvfxChunk.Parse(baseline).All("Modl").ElementAt(plan.PanelModel).Data!, out _)!;
                var overlay = AvfxModel.Parse(plan.ModlData, out _)!;
                int samples = 0, bad = 0;
                var r = plan.ClipRegion;
                foreach (var o in overlay.Triangles)
                {
                    var vs = new[] { overlay.Vertices[o.A], overlay.Vertices[o.B], overlay.Vertices[o.C] };
                    for (int i = 1; i < 6; i++)
                        for (int j = 1; i + j < 6; j++)
                        {
                            double a = i / 6.0, b = j / 6.0, c = 1 - a - b;
                            var p = Pos(vs, a, b, c);
                            var (cs, ct) = Uvs(vs, a, b, c, map);
                            double nx = plan.Window.X + cs * plan.Window.Width, ny = plan.Window.Y + ct * plan.Window.Height;
                            var src = SourcePx(source, p, map);
                            if (src == null) { bad++; continue; }
                            samples++;
                            bool edge = nx - r.X < 1 || r.Right - nx < 1 || ny - r.Y < 1 || r.Bottom - ny < 1;
                            int dx = Math.Abs((int)Math.Floor(nx) - (int)Math.Floor(src.Value.X)), dy = Math.Abs((int)Math.Floor(ny) - (int)Math.Floor(src.Value.Y));
                            if (!(dx == 0 && dy == 0) && !(edge && dx <= 1 && dy <= 1) && !(Math.Abs(nx - src.Value.X) < 0.05 && Math.Abs(ny - src.Value.Y) < 0.05)) bad++;
                        }
                }
                Check(samples > 0 && bad == 0, $"{rel}: {samples} samples, {bad} off by a texel or unmatched");
            }
        });

        Add("OverlayFrameBudget", OTag("A-5 E-17") + " " + Shell("A-3 A-6"), () =>
        {
            using var s = Small();
            Check(AvfxTableOverlay.Capacity(s.Info) == 128, $"capacity 128 ({AvfxTableOverlay.Capacity(s.Info)})");
            var built = AvfxTableOverlay.Build(s.Baseline, s.Info, s.Plan, ProbeKeys(128), Fixtures.Timeline(128, maxFrames: 128), out var r, out string p);
            Check(built != null, $"128 frames build ({r} {p})");
            if (built != null)
            {
                var root = AvfxChunk.Parse(built);
                Check(root.All("Ptcl").Max(AvfxTableAnimation.Tlst) == 154 && root.ReadInt("MdCn") == 10, "max TLst 154, MdCn 10");
            }
            built = AvfxTableOverlay.Build(s.Baseline, s.Info, s.Plan, ProbeKeys(129), Fixtures.Timeline(129, maxFrames: 129), out r, out _);
            Check(built == null && r == OverlayRefusal.TooManyFrames, $"129 → TooManyFrames ({r})");
            Check(AvfxTableOverlay.Build(s.Baseline, s.Info, s.Plan, ProbeKeys(10), Fixtures.Timeline(10), out _, out _, maxFrames: 10) != null, "cap 10: 10 build");
            built = AvfxTableOverlay.Build(s.Baseline, s.Info, s.Plan, ProbeKeys(11), Fixtures.Timeline(11), out r, out _, maxFrames: 10);
            Check(built == null && r == OverlayRefusal.TooManyFrames, $"cap 10: 11 → TooManyFrames ({r})");
            Check(FramePlan.MaxFrames == 48, "live cap stays 48");
            var site = s.Mod.Scan();
            Check(site.Capacity == 48 && site.Mode == AnimationMode.Append, $"app still Append, capacity 48 ({site.Mode} {site.Capacity})");
        });

        Add("OverlayFrameBudgetCrowded", OTag("A-5 E-25"), () =>
        {
            using var s = Small();
            var crowded = Fixtures.WithExtraTextures(s.Baseline, 200);
            var info = Fixtures.Inspect(crowded);
            var plan = AvfxTableOverlay.Plan(crowded, info, TableSurface.Rect, s.Atlas, PanelUvMap.Calibrated, out var r);
            Check(plan != null && AvfxTableOverlay.Capacity(info) == 55, $"200 textures: plans, capacity 55 ({r} {AvfxTableOverlay.Capacity(info)})");
            if (plan == null) return;
            var built = AvfxTableOverlay.Build(crowded, info, plan, ProbeKeys(55), Fixtures.Timeline(55, maxFrames: 55), out r, out string p);
            Check(built != null && AvfxChunk.Parse(built).All("Ptcl").Max(AvfxTableAnimation.Tlst) == 254, $"55 build, max TLst 254 ({r} {p})");
            built = AvfxTableOverlay.Build(crowded, info, plan, ProbeKeys(56), Fixtures.Timeline(56, maxFrames: 56), out r, out _);
            Check(built == null && r == OverlayRefusal.TooManyTextures, $"56 → TooManyTextures ({r})");
            var full = Fixtures.WithExtraTextures(s.Baseline, 255);
            Check(PlanRefusal(full, s.Atlas) == OverlayRefusal.TooManyTextures, $"255 textures → TooManyTextures ({PlanRefusal(full, s.Atlas)})");
        });

        Add("OverlayAppendFailure", OTag("E-26"), () =>
        {
            using var s = Small();
            var keys = ProbeKeys(60);
            var t = Fixtures.Timeline(60);
            var built = AvfxTableOverlay.Build(s.Baseline, s.Info, s.Plan, keys, t, out var r, out string problem);
            AvfxTableAnimation.Build(s.Baseline, s.Info with { Capacity = 128 }, keys, t, out string inner);
            Check(built == null && r == OverlayRefusal.AppendFailed && problem.Length > 0 && problem == inner, $"timeline mismatch → AppendFailed ({r} '{problem}')");
            var changed = (byte[])s.Baseline.Clone();
            changed[^1] ^= 1;
            built = AvfxTableOverlay.Build(changed, s.Info, s.Plan, ProbeKeys(5), Fixtures.Timeline(5), out r, out problem);
            Check(built == null && r == OverlayRefusal.AppendFailed && problem == AnimationText.Format(AnimationMessage.ModChanged, string.Empty),
                $"changed baseline → AppendFailed ModChanged ({r} '{problem}')");
        });

        Add("OverlayRefusesInPlace", OTag("A-6 E-4"), () =>
        {
            using var mod = Fixtures.Moon();
            var site = mod.Scan();
            var avfx = site.Animatable.First();
            AvfxTableOverlay.Plan(avfx.Baseline!, avfx.Inspect!.Info!, TableSurface.Rect, mod.Read(mod.Table.Relative), PanelUvMap.Calibrated, out var r);
            Check(r == OverlayRefusal.InPlace, $"Moon → InPlace ({r})");
            Check(ApplyFrames(mod, 48, out string error), "Moon apply " + error);
            Check(AvfxTableAnimation.Sha(mod.Read(Fixtures.MoonCat)) == Goldens["moon " + Fixtures.MoonCat], "Moon InPlace build = golden");
        });

        Add("OverlayRefusesUncalibratedUv", OTag("A-6 E-5"), () =>
        {
            using var s = Small();
            var scl = EditEffect(s.Baseline, root => { var d = PanelOf(root).Child("UvSt")!.Data!; BitConverter.GetBytes(1f).CopyTo(d, d.Length - 4); });
            Check(PlanRefusal(scl, s.Atlas) == OverlayRefusal.UvSetNotCalibrated, "Scl key 1.0 → UvSetNotCalibrated");
            var uvsn = EditEffect(s.Baseline, root => PanelOf(root).SetInt("UvSN", 0));
            Check(PlanRefusal(uvsn, s.Atlas) == OverlayRefusal.UvSetNotCalibrated, "UvSN 0 → UvSetNotCalibrated");
            var tc1 = EditEffect(s.Baseline, root => PanelOf(root).Child("TC1")!.SetInt("UvSN", 1));
            Check(PlanRefusal(tc1, s.Atlas) == OverlayRefusal.UvSetNotCalibrated, "TC1 UvSN 1 → UvSetNotCalibrated");
        });

        Add("OverlayRefusesDrawSetup", OTag("A-6 E-24"), () =>
        {
            using var s = Small();
            Check(PlanRefusal(s.Baseline, s.Atlas) == OverlayRefusal.None, "unedited Pickles plans");
            foreach (var field in new[] { "RMT", "DsDw", "DsDt" })
            {
                var edited = EditEffect(s.Baseline, root => PanelOf(root).SetInt(field, 0));
                Check(PlanRefusal(edited, s.Atlas) == OverlayRefusal.DrawSetupNotCalibrated, $"{field} 0 → DrawSetupNotCalibrated");
            }
        });

        Add("OverlayRefusesBadModel", OTag("A-6 E-6"), () =>
        {
            using var s = Small();
            var (vdrw, vidx) = ModlParts(AvfxChunk.Parse(s.Baseline).All("Modl").ElementAt(6).Data!);
            byte[] Edit(byte[] modl) => EditEffect(s.Baseline, root => root.All("Modl").ElementAt(6).Data = modl);
            Check(PlanRefusal(Edit(ModlPayload(("VDrw", vdrw))), s.Atlas) == OverlayRefusal.ModelUnreadable, "no VIdx → ModelUnreadable");
            Check(PlanRefusal(Edit(ModlPayload(("VDrw", vdrw[..^4]), ("VIdx", vidx))), s.Atlas) == OverlayRefusal.ModelUnreadable, "VDrw not 36-aligned → ModelUnreadable");
            var badIdx = (byte[])vidx.Clone();
            BitConverter.GetBytes((ushort)9999).CopyTo(badIdx, 0);
            Check(PlanRefusal(Edit(ModlPayload(("VDrw", vdrw), ("VIdx", badIdx))), s.Atlas) == OverlayRefusal.ModelUnreadable, "index past the vertices → ModelUnreadable");
        });

        Add("OverlayRefusesUvPairs", OTag("A-6 E-7"), () =>
        {
            using var s = Small();
            var (vdrw, vidx) = ModlParts(AvfxChunk.Parse(s.Baseline).All("Modl").ElementAt(6).Data!);
            var edited = (byte[])vdrw.Clone();
            BitConverter.GetBytes((Half)0.123).CopyTo(edited, 24);
            var effect = EditEffect(s.Baseline, root => root.All("Modl").ElementAt(6).Data = ModlPayload(("VDrw", edited), ("VIdx", vidx)));
            Check(PlanRefusal(effect, s.Atlas) == OverlayRefusal.UvPairsDiffer, $"UV2 edited → UvPairsDiffer ({PlanRefusal(effect, s.Atlas)})");
        });

        Add("OverlayRefusesNoGeometry", OTag("A-6 E-8"), () =>
        {
            using var s = Small();
            var map = PanelUvMap.Calibrated with { OffsetU = 5 };
            Check(PlanRefusal(s.Baseline, s.Atlas, map) == OverlayRefusal.NoPanelGeometry, "map OffsetU 5 → NoPanelGeometry");
            var model = SyntheticModel(new List<(double, double, double, double, double)> { (0, 0, 0, 0.3, 0.3), (1, 0, 0, 0.35, 0.3), (0, 1, 0, 0.3, 0.35) },
                new List<(int, int, int)> { (0, 1, 2) });
            var clipped = AvfxModel.ClipToWindow(model, new PanelUvMap(1, 0, 1, 0), new TexRect(0, 0, 1024, 1024), 4,
                new TexRect(700, 700, 50, 50), 1024, 1024, out double coverage, out var r);
            Check(clipped == null && coverage == 0 && r == OverlayRefusal.NoPanelGeometry, $"triangles kept but none on the panel → NoPanelGeometry ({r})");
        });

        Add("OverlayRefusesUnreadableAtlas", OTag("A-6"), () =>
        {
            using var s = Small();
            Check(PlanRefusal(s.Baseline, s.Atlas[..40]) == OverlayRefusal.AtlasUnreadable, "truncated header → AtlasUnreadable");
            var bgra = (byte[])s.Atlas.Clone();
            BitConverter.GetBytes((uint)TexFormat.Bgra8).CopyTo(bgra, 4);
            Check(PlanRefusal(s.Baseline, bgra) == OverlayRefusal.AtlasUnreadable, "uncompressed atlas → AtlasUnreadable");
            Check(PlanRefusal(s.Baseline, s.Atlas, rect: new TexRect(900, 900, 440, 289)) == OverlayRefusal.AtlasUnreadable, "rect off the atlas → AtlasUnreadable");
            var md = EditEffect(s.Baseline, root => PanelOf(root).Child("Data")!.Child("MdNo")!.Data![0] = 200);
            Check(PlanRefusal(md, s.Atlas) == OverlayRefusal.ModelUnreadable, "MdNo past the models → ModelUnreadable");
        });

        Add("CropRefusals", OTag("A-3"), () =>
        {
            using var s = Small();
            var w = new TexRect(128, 256, 512, 512);
            var array = (byte[])s.Atlas.Clone();
            array[15] = 2;
            Check(TexCrop.Crop(array, w, out _) == null, "array texture refused");
            Check(TexCrop.Crop(s.Atlas, new TexRect(130, 256, 512, 512), out _) == null, "window off the block grid refused");
            Check(TexCrop.Crop(s.Atlas, new TexRect(600, 600, 512, 512), out _) == null, "window off the atlas refused");
            // Eight mips, the last cut short: every offset still lies inside the file.
            var shortMip = (byte[])s.Atlas.Clone();
            shortMip[14] = 8;
            int mip7 = BitConverter.ToInt32(shortMip, 0x1C + 7 * 4);
            Check(TexCrop.Crop(shortMip[..(mip7 + 4)], w, out string e) == null && e.Contains("truncated"), "truncated mip refused: " + e);
            Check(!TexCrop.BlackOutside(s.Atlas[..1000], TableSurface.Rect), "truncated atlas is not black outside");
            foreach (var r in new[] { new TexRect(101, 100, 500, 100), new TexRect(8, 8, 440, 289), new TexRect(377, 211, 301, 333) })
                if (TexCrop.Window(1024, 1024, r) is { } v)
                    Check(v.X % 4 == 0 && v.Y % 4 == 0 && TexCrop.Crop(s.Atlas, v, out _) != null, $"{r}: window {v} crops");
        });

        Add("ProbeRefusalWritesNothing", OTag("A-6"), () =>
        {
            if (!Directory.Exists(Fixtures.PicklesRoot)) throw new SkipException("Pickles base pack not found");
            string temp = Path.Combine(Path.GetTempPath(), "AnimHarness", "runs", "refuse-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                string pack = Path.Combine(temp, "glow pack");
                string shipped = Fixtures.PicklesShippedSource(Fixtures.PicklesRoot, Fixtures.AppBackups);
                foreach (string f in Directory.GetFiles(shipped, "*", SearchOption.AllDirectories))
                {
                    string t = Path.Combine(pack, Path.GetRelativePath(shipped, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                    File.Copy(f, t);
                }
                Directory.Delete(Path.GetDirectoryName(shipped)!, true);
                var patch = new BgraImage(8, 8);
                Array.Fill(patch.Pixels, (byte)255);
                string eq55 = Path.Combine(pack, Fixtures.PicklesEq55);
                File.WriteAllBytes(eq55, TexPaste.PasteRect(File.ReadAllBytes(eq55), new TexRect(20, 20, 8, 8), patch, out _)!);
                string folder = Path.Combine(temp, "probe");
                var old = Console.Out;
                var sw = new StringWriter();
                Console.SetOut(sw);
                int exit;
                try { exit = OverlayProbe.Run(folder, pack, Path.Combine(temp, "no backups")); }
                finally { Console.SetOut(old); }
                Check(exit == 1 && sw.ToString().Contains("Overlay refused: GlowOutsidePanel") && !Directory.Exists(folder)
                    && !Directory.GetDirectories(temp).Any(d => d.Contains(".tmp-")), "refused by name, nothing written: " + sw.ToString().Trim());
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        });

        Add("OverlayRefusesGlow", OTag("A-6 E-11"), () =>
        {
            using var s = Small();
            var patch = new BgraImage(8, 8);
            Array.Fill(patch.Pixels, (byte)255);
            var glow = TexPaste.PasteRect(s.Atlas, new TexRect(20, 20, 8, 8), patch, out _)!;
            Check(PlanRefusal(s.Baseline, glow) == OverlayRefusal.GlowOutsidePanel, "white patch → GlowOutsidePanel");
            var still = TexPaste.PasteRect(s.Atlas, TableSurface.Rect, TableSurface.ToTextureSpace(Fixtures.Frames(1)[0], FitMode.Fill), out _)!;
            Check(PlanRefusal(s.Baseline, still) == OverlayRefusal.None, "a still on the panel still plans");
        });

        Add("WindowPlacement", OTag("A-6 E-12"), () =>
        {
            Check(TexCrop.Window(1024, 1024, TableSurface.Rect) == new TexRect(128, 256, 512, 512), "Pickles window 128,256 512x512");
            using (var s = Small()) Check(s.Plan.ClipRegion == new TexRect(132, 260, 504, 504), $"clip region ({s.Plan.ClipRegion})");
            var near = new TexRect(8, 8, 440, 289);
            var w = TexCrop.Window(1024, 1024, near);
            Check(w is { } v && v.X >= 0 && v.Y >= 0 && v.X <= near.X - 4 && v.Y <= near.Y - 4 && v.Right >= near.Right + 4 && v.Bottom >= near.Bottom + 4,
                $"8 px from the edge → clamped window ({w})");
            Check(TexCrop.Window(1024, 1024, new TexRect(0, 398, 440, 289)) == null, "touching the edge → NoWindow");
            Check(TexCrop.Window(1024, 1024, new TexRect(100, 100, 505, 100)) == null, "505 wide → NoWindow");
            Check(TexCrop.Window(1024, 1024, new TexRect(100, 100, 100, 505)) == null, "505 tall → NoWindow");
            Check(TexCrop.Window(1024, 1024, new TexRect(100, 100, 504, 100)) is { Width: 512 } at && at.X % 4 == 0, "504 wide still fits");
            var odd = TexCrop.Window(1024, 1024, new TexRect(101, 100, 500, 100));
            Check(odd is { X: 96 }, $"clamped origin stays on the block grid ({odd})");
        });

        Add("OverlayRefusesModelCount", OTag("A-6 E-16") + " " + Shell("A-7 E-10"), () =>
        {
            using var s = Small();
            var crowded = EditEffect(s.Baseline, root =>
            {
                int last = root.Children!.FindLastIndex(c => c.Name == "Modl");
                var copy = root.Children[last];
                for (int i = root.All("Modl").Count(); i < 255; i++) root.Children.Insert(last + 1, copy.Clone());
                root.SetInt("MdCn", 255);
            });
            Check(PlanRefusal(crowded, s.Atlas) == OverlayRefusal.TooManyModels, $"255 models → TooManyModels ({PlanRefusal(crowded, s.Atlas)})");

            byte[] WithModels(int count) => EditEffect(s.Baseline, root =>
            {
                int last = root.Children!.FindLastIndex(c => c.Name == "Modl");
                var copy = root.Children[last];
                for (int i = root.All("Modl").Count(); i < count; i++) root.Children.Insert(last + 1, copy.Clone());
                root.SetInt("MdCn", count);
            });
            Check(PlanRefusal(WithModels(254), s.Atlas) == OverlayRefusal.TooManyModels, "254 models, two needed → TooManyModels");
            byte[] at253 = WithModels(253);
            var info = Fixtures.Inspect(at253);
            var plan = AvfxTableOverlay.Plan(at253, info, TableSurface.Rect, s.Atlas, PanelUvMap.Calibrated, out var refusal);
            Check(plan != null && plan.OutsideModel == 254, $"253 models plan ({refusal})");
            var built = plan == null ? null : AvfxTableOverlay.Build(at253, info, plan, ProbeKeys(4), Fixtures.Timeline(4), out _, out _);
            Check(built != null && AvfxChunk.Parse(built).ReadInt("MdCn") == 255 && AvfxChunk.Parse(built).All("Modl").Count() == 255, "253 models build to 255");
        });

        Add("ModlRoundTrip", OTag("E-15"), () =>
        {
            using (var mod = Fixtures.Pickles())
                foreach (var rel in Fixtures.PicklesAvfx)
                    foreach (var (m, i) in AvfxChunk.Parse(mod.Read(rel)).All("Modl").Select((m, i) => (m, i)))
                    {
                        var parsed = AvfxModel.Parse(m.Data!, out string e);
                        Check(parsed != null && parsed.ToData().AsSpan().SequenceEqual(m.Data), $"{rel} Modl {i} round-trips {e}");
                    }
            using (var mod = Fixtures.Moon())
                foreach (var rel in new[] { Fixtures.MoonCat, Fixtures.MoonCat2 })
                    foreach (var (m, i) in AvfxChunk.Parse(mod.Read(rel)).All("Modl").Select((m, i) => (m, i)))
                    {
                        var parsed = AvfxModel.Parse(m.Data!, out string e);
                        Check(parsed != null && parsed.ToData().AsSpan().SequenceEqual(m.Data), $"Moon {rel} Modl {i} round-trips {e}");
                    }
            var odd = SyntheticModel(new List<(double, double, double, double, double)> { (0, 0, 0, 0, 0), (1, 0, 0, 1, 0), (0, 1, 0, 0, 1) },
                new List<(int, int, int)> { (0, 1, 2) });
            var data = odd.ToData();
            int vdrw = BitConverter.ToInt32(data, 4);
            int idxAt = 8 + vdrw;
            Check(BitConverter.ToInt32(data, idxAt + 4) == 6 && data.Length == idxAt + 8 + 8, "VIdx of one triangle padded to 8");
            Check(AvfxModel.Parse(data, out _)?.Triangles.Count == 1, "padded model re-parses");
        });

        Add("FullAtlasUnchanged", OTag("A-7") + " " + Shell("A-6") + " " + AppTag("A-5 A-6"), () =>
        {
            var now = CurrentGoldens();
            foreach (var (key, sha) in Goldens)
                Check(now.TryGetValue(key, out var got) && got == sha, $"golden {key}");
        });

        Add("PreviousVersionAnimationRestores", OTag("A-7 E-22") + " " + AppTag("A-7 E-8"), () =>
        {
            // The synthetic previous-version animation always runs; the live backup too when there is one.
            using var live = Fixtures.LivePicklesAnimation();
            using var synthetic = Fixtures.Pickles("prev");
            string error = string.Empty;
            Check(WithoutOverlay(() => ApplyFrames(synthetic, 12, out error)), "previous-version apply " + error);
            foreach (var mod in new[] { synthetic, live }.Where(m => m != null).Select(m => m!))
            {
                var site = mod.Scan();
                var status = DjTableAnimationCore.Status(site);
                string label = mod == live ? "live gen " + site.State?.Generation : "previous-version apply";
                Check(status.Kind == AnimationStatusKind.Ours, $"{label} scans as Ours ({status.Kind})");
                var entries = site.State!.Avfx.ToList();
                Check(RestoreToShipped(mod, out string restoreError), $"{label}: restore " + restoreError);
                foreach (var entry in entries)
                    Check(AvfxTableAnimation.Sha(mod.Read(entry.Relative)) == entry.OriginalSha, $"{label}: {entry.Relative} restored byte-exact");
            }
        });

        Add("ProbeDiagnostics", OTag("A-1"), () =>
        {
            var probe = SharedProbe();
            var lines = probe.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            Check(lines.Any(l => l.StartsWith("Window 128,256 512x512; clip region 132,260 504x504")), "window and clip region");
            var cov = lines.FirstOrDefault(l => l.Contains("coverage of the panel"));
            double coverage = cov == null ? -1 : double.Parse(cov[(cov.IndexOf("panel ") + 6)..].Replace("%", "").Trim(), CultureInfo.InvariantCulture) / 100;
            Check(coverage > 0 && coverage <= 1, $"coverage in (0,1] ({coverage})");
            Check(lines.Any(l => l == "eq55 SHA-256 " + ShippedEq55Sha()), "eq55 SHA printed");
            Check(lines.Any(l => l.Contains("full atlas 1,398,176, overlay 349,600")), "per-frame sizes");
            Check(lines.LastOrDefault() == OverlayProbe.DeleteReminder, "delete reminder last");
        });

        Add("ProbeCalibrationContent", OTag("A-9"), () =>
        {
            var probe = SharedProbe();
            var rect = TableSurface.Rect;
            byte[] OptionFile(int n, int k) => File.ReadAllBytes(Path.Combine(probe.Folder, OverlayProbe.FrameFile(n, k)));
            byte[] window = File.Exists(Path.Combine(probe.Folder, OverlayProbe.FrameFile(2, 0))) ? OptionFile(2, 0) : Array.Empty<byte>();
            var win = TexCrop.Window(1024, 1024, rect)!.Value;
            var inWindow = new TexRect(rect.X - win.X, rect.Y - win.Y, rect.Width, rect.Height);
            for (int k = 0; k < 10; k++)
            {
                var r1 = TexPaste.ReadRect(OptionFile(1, k), rect, out _)!;
                var up = ImageOps.Rotate(r1, TableSurface.QuarterTurns);
                int W = up.Width, H = up.Height;
                for (int band = 0; band < 3; band++)
                {
                    var c = new[] { Fixtures.Bands[0][band], Fixtures.Bands[1][band], Fixtures.Bands[2][band], Fixtures.Bands[3][band] };
                    double[] psnr = new double[4];
                    for (int edge = 0; edge < 4; edge++)
                    {
                        double sum = 0; int count = 0;
                        for (int along = -20; along < 20; along++)
                            for (int d = band * 4 + 1; d < band * 4 + 3; d++)
                            {
                                var (x, y) = edge switch { 0 => (W / 2 + along, d), 1 => (W / 2 + along, H - 1 - d), 2 => (d, H / 2 + along), _ => (W - 1 - d, H / 2 + along) };
                                var px = Rgb(up, x, y);
                                sum += Math.Pow(px.R - c[edge].R, 2) + Math.Pow(px.G - c[edge].G, 2) + Math.Pow(px.B - c[edge].B, 2);
                                count += 3;
                            }
                        psnr[edge] = sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / (sum / count));
                    }
                    Check(psnr.All(p => p > 30), $"frame {k} band {band}: PSNR {string.Join("/", psnr.Select(p => p.ToString("F1")))}");
                }
                var rows = GifWriter.Digits[k].Split(',');
                int ox = (W - 160) / 2, oy = (H - 224) / 2;
                bool digit = true;
                for (int cy = 0; cy < 7; cy++)
                    for (int cx = 0; cx < 5; cx++)
                    {
                        var px = Rgb(up, ox + cx * 32 + 16, oy + cy * 32 + 16);
                        digit &= rows[cy][cx] == '1' ? px.R > 200 && px.G > 200 && px.B > 200 : px.R < 60 && px.G < 60 && px.B < 60;
                    }
                Check(digit, $"frame {k}: digit {k} upright");
                int lit = Enumerable.Range(12, W - 24).Count(x => { var p = Rgb(up, x, H - 12 - 6); return p.R > 180 && p.G < 140 && p.B < 100; });
                int expect = (W - 24) * (k + 1) / 10;
                Check(Math.Abs(lit - expect) <= 6, $"frame {k}: bar {lit} px (expect {expect})");

                var r2 = TexPaste.ReadRect(OptionFile(2, k), inWindow, out _)!;
                Check(Same(r1, r2), $"frame {k}: overlay = full atlas on the panel");
            }

            var grid = OptionFile(3, 0);
            var full = DecodeLevel(grid, 0);
            bool outline = Enumerable.Range(rect.X, rect.Width).All(x => White(full, x, rect.Y) && White(full, x, rect.Bottom - 1))
                && Enumerable.Range(rect.Y, rect.Height).All(y => White(full, rect.X, y) && White(full, rect.Right - 1, y));
            Check(outline, "grid: R outlined in white on all four edges");
            // Every texel of the 16 px band inside R's edges is white exactly where the grid draws the
            // outline, a tick or a label: ticks every 4 px, 6 px long and 12 px every 16 px.
            var expectGrid = Fixtures.GridImage(TableSurface);
            int bandTexels = 0, wrong = 0;
            for (int y = rect.Y; y < rect.Bottom; y++)
                for (int x = rect.X; x < rect.Right; x++)
                {
                    if (x >= rect.X + 16 && x < rect.Right - 16 && y >= rect.Y + 16 && y < rect.Bottom - 16) continue;
                    bandTexels++;
                    if (White(full, x, y) != White(expectGrid, x, y)) wrong++;
                }
            bool tickShape = Enumerable.Range(0, 12).All(t => White(expectGrid, rect.X + 16, rect.Y + t)) && !White(expectGrid, rect.X + 20, rect.Y + 8)
                && White(expectGrid, rect.X + 20, rect.Y + 5);
            Check(tickShape && wrong <= bandTexels / 50, $"grid: ticks every 4 px, 6 or 12 px long, on all four edges ({wrong}/{bandTexels} texels differ)");
            var label = Fixtures.CellLabel(5, 7);
            var labelTex = TexPaste.ReadRect(grid, new TexRect(5 * 64 + (64 - label.Width) / 2, 7 * 64 + (64 - label.Height) / 2, label.Width, label.Height), out _)!;
            var upright = ImageOps.Rotate(labelTex, TableSurface.QuarterTurns);
            int agree = 0;
            for (int i = 0; i < label.Pixels.Length; i += 4)
                if ((label.Pixels[i + 3] != 0) == (upright.Pixels[i] > 200 && upright.Pixels[i + 1] > 200 && upright.Pixels[i + 2] > 200)) agree++;
            Check(agree >= label.Pixels.Length / 4 * 0.9, $"grid: labels read upright ({agree}/{label.Pixels.Length / 4})");
            var crop4 = OptionFile(4, 0);
            Check(Same(TexPaste.ReadRect(grid, win, out _)!, TexPaste.ReadRect(crop4, new TexRect(0, 0, 512, 512), out _)!),
                "option 4's crop = option 3's atlas window");
        });

        RegisterOverlayShell();
        RegisterOverlayApp();
        RegisterLaptop();
    }

    // ---- sampling ---------------------------------------------------------------------------------

    private static (double X, double Y, double Z) Pos(byte[][] vs, double a, double b, double c)
    {
        double P(int v, int axis) => (double)BitConverter.ToHalf(vs[v], axis * 2);
        return (a * P(0, 0) + b * P(1, 0) + c * P(2, 0), a * P(0, 1) + b * P(1, 1) + c * P(2, 1), a * P(0, 2) + b * P(1, 2) + c * P(2, 2));
    }

    private static (double S, double T) Uvs(byte[][] vs, double a, double b, double c, PanelUvMap map)
    {
        var uv = vs.Select(v => AvfxModel.Uv(v)).ToArray();
        return map.ToTexture(a * uv[0].U + b * uv[1].U + c * uv[2].U, a * uv[0].V + b * uv[1].V + c * uv[2].V);
    }

    /// <summary>Where the full atlas samples this 3D point: the source triangle holding it, mapped through T.</summary>
    private static (double X, double Y)? SourcePx(AvfxModel source, (double X, double Y, double Z) p, PanelUvMap map)
    {
        foreach (var (ia, ib, ic) in source.Triangles)
        {
            var vs = new[] { source.Vertices[ia], source.Vertices[ib], source.Vertices[ic] };
            var A = Pos(vs, 1, 0, 0); var B = Pos(vs, 0, 1, 0); var C = Pos(vs, 0, 0, 1);
            var ab = Sub(B, A); var ac = Sub(C, A); var ap = Sub(p, A);
            var n = CrossV(ab, ac);
            double nn = Dot(n, n);
            if (nn < 1e-12) continue;
            if (Math.Abs(Dot(ap, n)) / Math.Sqrt(nn) > 2e-3) continue;
            double v = Dot(CrossV(ap, ac), n) / nn, w = Dot(CrossV(ab, ap), n) / nn, u = 1 - v - w;
            if (u < -1e-3 || v < -1e-3 || w < -1e-3) continue;
            var (s, t) = Uvs(vs, u, v, w, map);
            return (s * 1024, t * 1024);
        }
        return null;
    }

    private static (double, double, double) Sub((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static (double, double, double) CrossV((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
