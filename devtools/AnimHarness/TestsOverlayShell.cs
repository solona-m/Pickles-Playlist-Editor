using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;

namespace AnimHarness;

/// <summary>The overlay keeps the table shell outside the window (#12 step 1 follow-up).</summary>
internal static partial class Program
{
    private const string ShellSlug = "gh12-overlay-shell";

    private static string Shell(string ids) =>
        string.Join(" ", ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(id => id[0] == 'A'
            ? $"AC-{ShellSlug}-{id[2..]}" : $"EDGE-{ShellSlug}-{id[2..]}"));

    // Synthetic cases: identity map, window (256,256,512,512), margin 4, so the region is 260..764.
    // Every vertex and clip crossing is a whole pixel, exact in halves.
    private static readonly TexRect SynthWindow = new(256, 256, 512, 512);
    private static readonly TexRect SynthRect = new(300, 300, 50, 50);
    private static readonly PanelUvMap Identity = new(1, 0, 1, 0);
    private static readonly (double X, double Y)[] SynthAnchor = { (300, 300), (400, 300), (300, 400) };

    private static readonly Dictionary<string, (double X, double Y)[]> SynthCases = new()
    {
        ["outside"] = new[] { (900.0, 900.0), (950.0, 900.0), (900.0, 950.0) },
        ["inside"] = new[] { (400.0, 400.0), (500.0, 400.0), (400.0, 500.0) },
        ["crossX1"] = new[] { (700.0, 300.0), (800.0, 300.0), (700.0, 400.0) },
        ["enclose"] = new[] { (100.0, 100.0), (1500.0, 100.0), (100.0, 1500.0) },
        ["sliverCentre"] = new[] { (763.5, 400.0), (900.0, 400.0), (763.5, 400.25) },
        ["cornerVertex"] = new[] { (260.0, 260.0), (300.0, 200.0), (200.0, 300.0) },
        ["onX0"] = new[] { (260.0, 400.0), (200.0, 500.0), (400.0, 500.0) },
        ["cornerCover"] = new[] { (200.0, 200.0), (400.0, 200.0), (200.0, 400.0) },
    };

    /// <summary>The case's triangle, then the anchor, which keeps the split from refusing.</summary>
    private static AvfxModel SynthModel((double X, double Y)[] tri)
    {
        var verts = tri.Concat(SynthAnchor).Select(p => (p.X / 1024, p.Y / 1024, 0.5, p.X / 1024, p.Y / 1024)).ToList();
        return SyntheticModel(verts, new List<(int, int, int)> { (0, 1, 2), (3, 4, 5) });
    }

    private static (AvfxModel Inside, AvfxModel Outside)? SynthSplit(AvfxModel m) =>
        AvfxModel.SplitAtWindow(m, Identity, SynthWindow, 4, SynthRect, 1024, 1024, out _, out _);

    // Pickles: model 6 one triangle at a time, plus an anchor in the clip region, far from the table in 3D.
    private static readonly TexRect PicklesAnchorRect = new(300, 300, 50, 50);

    private static AvfxModel PicklesSingle(byte[] modl6, int t)
    {
        var m = AvfxModel.Parse(modl6, out _)!;
        var tri = m.Triangles[t];
        m.Triangles.Clear();
        m.Triangles.Add(tri);
        int first = m.Vertices.Count;
        foreach (var (p, k) in SynthAnchor.Select((p, k) => (p, k)))
        {
            var v = (byte[])m.Vertices[0].Clone();
            BitConverter.GetBytes((Half)(10 + (k == 1 ? 1 : 0))).CopyTo(v, 0);
            BitConverter.GetBytes((Half)(10 + (k == 2 ? 1 : 0))).CopyTo(v, 2);
            BitConverter.GetBytes((Half)10).CopyTo(v, 4);
            var (u, w) = PanelUvMap.Calibrated.ToModel(p.X / 1024, p.Y / 1024);
            for (int pair = 0; pair < 4; pair++)
            {
                BitConverter.GetBytes((Half)u).CopyTo(v, 20 + pair * 4);
                BitConverter.GetBytes((Half)w).CopyTo(v, 22 + pair * 4);
            }
            m.Vertices.Add(v);
        }
        m.Triangles.Add(((ushort)first, (ushort)(first + 1), (ushort)(first + 2)));
        return m;
    }

    private static (AvfxModel Inside, AvfxModel Outside)? PicklesSplit(AvfxModel m, TexRect window) =>
        AvfxModel.SplitAtWindow(m, PanelUvMap.Calibrated, window, AvfxTableOverlay.FilterMargin, PicklesAnchorRect, 1024, 1024, out _, out _);

    private static byte[] Modl6(byte[] baseline) => AvfxChunk.Parse(baseline).All("Modl").ElementAt(6).Data!;

    private static bool IsAnchor(byte[] v) => (double)BitConverter.ToHalf(v, 4) == 10;

    /// <summary>The window part and every output that passed in game, as SHA-256 (pinned at 0bb7b97).</summary>
    private static Dictionary<string, string> ShellPins()
    {
        var result = new Dictionary<string, string>();
        string Sha(byte[]? b) => b == null ? "null" : AvfxTableAnimation.Sha(b);
        using (var s = Small())
        {
            result["small plan model"] = Sha(s.Plan.ModlData);
            byte[] modl6 = Modl6(s.Baseline);
            int count = AvfxModel.Parse(modl6, out _)!.Triangles.Count;
            using var all = new MemoryStream();
            for (int t = 0; t < count; t++)
            {
                var clipped = AvfxModel.ClipToWindow(PicklesSingle(modl6, t), PanelUvMap.Calibrated, s.Plan.Window,
                    AvfxTableOverlay.FilterMargin, PicklesAnchorRect, 1024, 1024, out _, out _);
                all.Write(System.Text.Encoding.ASCII.GetBytes(Sha(clipped?.ToData())));
            }
            result["pickles single-triangle inside models"] = Sha(all.ToArray());
        }
        foreach (var (name, tri) in SynthCases)
            result["synthetic " + name] = Sha(AvfxModel.ClipToWindow(SynthModel(tri), Identity, SynthWindow, 4, SynthRect, 1024, 1024, out _, out _)?.ToData());

        var probe = SharedProbe();
        var options = PenumbraOptions.Read(probe.Folder);
        foreach (int n in new[] { 0, 1, 3 })
            result[$"probe {n} avfx"] = Sha(File.ReadAllBytes(Path.Combine(probe.Folder, options[n].Files[OverlayProbe.AvfxGamePath])));
        for (int n = 1; n <= 4; n++)
            foreach (var (key, rel) in options[n].Files.Where(f => f.Key.StartsWith(OverlayProbe.KeyPrefix)).OrderBy(f => f.Key))
                result[$"probe frame {key}"] = Sha(File.ReadAllBytes(Path.Combine(probe.Folder, rel)));
        return result;
    }

    private static bool OnTriangle(byte[][] vs, (double X, double Y, double Z) p)
    {
        var A = Pos(vs, 1, 0, 0); var B = Pos(vs, 0, 1, 0); var C = Pos(vs, 0, 0, 1);
        var ab = Sub(B, A); var ac = Sub(C, A); var ap = Sub(p, A);
        var n = CrossV(ab, ac);
        double nn = Dot(n, n);
        if (nn < 1e-12) return false;
        if (Math.Abs(Dot(ap, n)) / Math.Sqrt(nn) > 2e-3) return false;
        double v = Dot(CrossV(ap, ac), n) / nn, w = Dot(CrossV(ab, ap), n) / nn, u = 1 - v - w;
        return u >= -1e-3 && v >= -1e-3 && w >= -1e-3;
    }

    private static byte[][] Corners(AvfxModel m, (ushort A, ushort B, ushort C) t) =>
        new[] { m.Vertices[t.A], m.Vertices[t.B], m.Vertices[t.C] };

    private static double Area3(byte[][] vs)
    {
        var n = CrossV(Sub(Pos(vs, 0, 1, 0), Pos(vs, 1, 0, 0)), Sub(Pos(vs, 0, 0, 1), Pos(vs, 1, 0, 0)));
        return Math.Sqrt(Dot(n, n)) / 2;
    }

    /// <summary>Atlas px of a vertex: inside vertices sample the crop, outside ones the atlas.</summary>
    private static (double X, double Y) InsidePx(byte[] v, PanelUvMap map, TexRect window)
    {
        var (s, t) = Px(v, map, 1, 1);
        return (window.X + s * window.Width, window.Y + t * window.Height);
    }

    private static double UvArea(byte[][] vs, Func<byte[], (double X, double Y)> px) => SignedArea(px(vs[0]), px(vs[1]), px(vs[2]));

    private static bool PairsMatch(byte[] v) =>
        v.AsSpan(20, 4).SequenceEqual(v.AsSpan(24, 4)) && v.AsSpan(20, 4).SequenceEqual(v.AsSpan(28, 4)) && v.AsSpan(20, 4).SequenceEqual(v.AsSpan(32, 4));

    private static bool Degenerate(AvfxModel m) =>
        m.Triangles.Any(t => t.A == t.B || t.B == t.C || t.A == t.C || Area3(Corners(m, t)) == 0);

    private static void RegisterOverlayShell()
    {
        Add("ShellKeepsCheckedParts", Shell("A-5"), () =>
        {
            var now = ShellPins();
            foreach (var (key, sha) in now)
                if (!ShellGoldens.ContainsKey(key)) Console.WriteLine($"   pin [\"{key}\"] = \"{sha}\",");
            Check(ShellGoldens.Count == now.Count, $"{ShellGoldens.Count} pins for {now.Count} outputs");
            foreach (var (key, sha) in ShellGoldens)
                Check(now.TryGetValue(key, out var got) && got == sha, $"pin {key} (now {got})");
        });

        Add("ShellPartition", Shell("A-1 E-5 E-6"), () =>
        {
            using var mod = Fixtures.Pickles();
            byte[] atlas = mod.Read(Fixtures.PicklesEq55);
            var map = PanelUvMap.Calibrated;
            foreach (var rel in Fixtures.PicklesAvfx)
            {
                byte[] baseline = mod.Read(rel);
                var plan = AvfxTableOverlay.Plan(baseline, Fixtures.Inspect(baseline), TableSurface.Rect, atlas, map, out var refusal);
                Check(plan?.OutsideModlData != null, $"{rel} plans an outside part ({refusal})");
                if (plan?.OutsideModlData == null) continue;
                var source = AvfxModel.Parse(Modl6(baseline), out _)!;
                var parts = new[] { AvfxModel.Parse(plan.ModlData, out _)!, AvfxModel.Parse(plan.OutsideModlData, out _)! }
                    .SelectMany(m => m.Triangles.Select(t => Corners(m, t))).ToList();
                var r = plan.ClipRegion;
                int points = 0, gaps = 0, overlaps = 0;
                foreach (var t in source.Triangles)
                {
                    var vs = Corners(source, t);
                    for (int i = 1; i < 6; i++)
                        for (int j = 1; i + j < 6; j++)
                        {
                            double a = i / 6.0, b = j / 6.0, c = 1 - a - b;
                            var p = Pos(vs, a, b, c);
                            var (s, tt) = Uvs(vs, a, b, c, map);
                            double x = s * 1024, y = tt * 1024;
                            bool near = Math.Abs(x - r.X) < 1 || Math.Abs(x - r.Right) < 1 || Math.Abs(y - r.Y) < 1 || Math.Abs(y - r.Bottom) < 1;
                            int src = source.Triangles.Count(o => OnTriangle(Corners(source, o), p));
                            int got = parts.Count(o => OnTriangle(o, p));
                            points++;
                            if (got == 0) gaps++;
                            else if (got > src && !(near && got == src + 1)) overlaps++;
                        }
                }
                Check(points > 0 && gaps == 0 && overlaps == 0, $"{rel}: {points} points, {gaps} undrawn, {overlaps} drawn twice");
            }
        });

        Add("ProbeShellDrawn", Shell("A-1 A-3 E-9"), () =>
        {
            var probe = SharedProbe();
            var options = PenumbraOptions.Read(probe.Folder);
            var pristine = AvfxChunk.Parse(File.ReadAllBytes(Path.Combine(probe.Folder, options[0].Files[OverlayProbe.AvfxGamePath])));
            using var s = Small();
            foreach (int n in new[] { 2, 4 })
            {
                var root = AvfxChunk.Parse(File.ReadAllBytes(Path.Combine(probe.Folder, options[n].Files[OverlayProbe.AvfxGamePath])));
                Check(root.ReadInt("MdCn") == 10, $"option {n}: MdCn 10 ({root.ReadInt("MdCn")})");
                Check(root.All("Modl").ElementAt(9).Data!.AsSpan().SequenceEqual(s.Plan.OutsideModlData), $"option {n}: model 9 is the shell part");
                var particles = root.All("Ptcl").ToList();
                var panel = particles[7].Clone();
                Check(panel.Child("Data")!.Child("MdNo")!.Data![0] == 9, $"option {n}: panel draws model 9");
                panel.Child("Data")!.Child("MdNo")!.Data![0] = 6;
                var shipped = pristine.All("Ptcl").ElementAt(7);
                Check(Fixtures.ParticleBytes(panel).AsSpan().SequenceEqual(Fixtures.ParticleBytes(shipped)), $"option {n}: panel = shipped but MdNo");
                Check(particles[7].Child("Col")!.ToBytes().AsSpan().SequenceEqual(shipped.Child("Col")!.ToBytes()), $"option {n}: panel Col is the shipped one");
                Check(!Fixtures.AlphaKeys(particles[7]).AsSpan().SequenceEqual(AvfxTableAnimation.HiddenKeys()), $"option {n}: panel not hidden");
                Check(particles.Skip(9).All(p => p.Child("Data")!.Child("MdNo")!.Data![0] == 8) && particles.Count > 9, $"option {n}: clones draw model 8");
            }
        });

        Add("ShellSamplesShipped", Shell("A-2 E-11 E-12"), () =>
        {
            using var mod = Fixtures.Pickles();
            byte[] atlas = mod.Read(Fixtures.PicklesEq55);
            var img = DecodeLevel(atlas, 0);
            var map = PanelUvMap.Calibrated;
            var R = TableSurface.Rect;
            byte[]? first = null;
            foreach (var rel in Fixtures.PicklesAvfx)
            {
                byte[] baseline = mod.Read(rel);
                var plan = AvfxTableOverlay.Plan(baseline, Fixtures.Inspect(baseline), R, atlas, map, out _);
                if (plan?.OutsideModlData == null) { Check(false, $"{rel} plans an outside part"); continue; }
                if (first == null) first = plan.OutsideModlData;
                else Check(plan.OutsideModlData.AsSpan().SequenceEqual(first), $"{rel} plans the same outside part");

                var source = AvfxModel.Parse(Modl6(baseline), out _)!;
                var outside = AvfxModel.Parse(plan.OutsideModlData, out _)!;
                Check(outside.Vertices.All(PairsMatch), $"{rel}: four UV pairs equal");
                var r = plan.ClipRegion;
                int samples = 0, bad = 0, inR = 0, notBlack = 0;
                foreach (var t in outside.Triangles)
                {
                    var vs = Corners(outside, t);
                    for (int i = 1; i < 6; i++)
                        for (int j = 1; i + j < 6; j++)
                        {
                            double a = i / 6.0, b = j / 6.0, c = 1 - a - b;
                            var (s, tt) = Uvs(vs, a, b, c, map);
                            double x = s * 1024, y = tt * 1024;
                            var src = SourcePx(source, Pos(vs, a, b, c), map);
                            if (src == null) { bad++; continue; }
                            samples++;
                            bool edge = Math.Abs(x - r.X) < 1 || Math.Abs(x - r.Right) < 1 || Math.Abs(y - r.Y) < 1 || Math.Abs(y - r.Bottom) < 1;
                            int dx = Math.Abs((int)Math.Floor(x) - (int)Math.Floor(src.Value.X)), dy = Math.Abs((int)Math.Floor(y) - (int)Math.Floor(src.Value.Y));
                            if (!(dx == 0 && dy == 0) && !(edge && dx <= 1 && dy <= 1) && !(Math.Abs(x - src.Value.X) < 0.05 && Math.Abs(y - src.Value.Y) < 0.05)) bad++;
                            if (x > R.X && x < R.Right && y > R.Y && y < R.Bottom) inR++;
                            int px = Math.Clamp((int)Math.Floor(x), 0, img.Width - 1), py = Math.Clamp((int)Math.Floor(y), 0, img.Height - 1);
                            var col = Rgb(img, px, py);
                            if (Math.Max(col.R, Math.Max(col.G, col.B)) > 8 || img.Pixels[(py * img.Width + px) * 4 + 3] < 224) notBlack++;
                        }
                }
                Check(samples > 0 && bad == 0, $"{rel}: {samples} samples, {bad} off by a texel or unmatched");
                Check(inR == 0, $"{rel}: no shell sample inside the picture rect ({inR})");
                Check(notBlack == 0, $"{rel}: shell texels opaque black, RGB <= 8 and alpha >= 224 ({notBlack} not)");
            }

            var frame = TableSurface.ToTextureSpace(Fixtures.Frames(1, 440, 289)[0], FitMode.Fill);
            var full = DecodeLevel(TexPaste.PasteRect(atlas, R, frame, out _)!, 0);
            int hx0 = R.X / 4 * 4, hy0 = R.Y / 4 * 4, hx1 = (R.Right + 3) / 4 * 4, hy1 = (R.Bottom + 3) / 4 * 4;
            bool same = true;
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                    if (x < hx0 || x >= hx1 || y < hy0 || y >= hy1)
                    {
                        int i = (y * img.Width + x) * 4;
                        same &= full.Pixels.AsSpan(i, 4).SequenceEqual(img.Pixels.AsSpan(i, 4));
                    }
            Check(same, "full-atlas frame equals eq55 outside the picture's block hull");
        });

        Add("ShellSeams", Shell("A-4 E-7 E-12 E-14"), () =>
        {
            using var s = Small();
            var map = PanelUvMap.Calibrated;
            var w = s.Plan.Window;
            var r = s.Plan.ClipRegion;
            byte[] modl6 = Modl6(s.Baseline);
            var source = AvfxModel.Parse(modl6, out _)!;
            int straddlers = 0;
            for (int t = 0; t < source.Triangles.Count; t++)
            {
                var split = PicklesSplit(PicklesSingle(modl6, t), w);
                Check(split != null, $"tri {t}: split (anchor keeps it from refusing)");
                if (split == null) continue;
                var (inside, outside) = split.Value;
                var srcV = Corners(source, source.Triangles[t]);
                var inV = inside.Vertices.Where(v => !IsAnchor(v)).ToList();
                if (inV.Count == 0 || outside.Triangles.Count == 0) continue;
                straddlers++;

                bool IsSource(byte[] v) => srcV.Any(x => x.AsSpan(0, 20).SequenceEqual(v.AsSpan(0, 20)));
                foreach (var v in inV.Where(v => !IsSource(v)))
                    Check(outside.Vertices.Any(o => o.AsSpan(0, 20).SequenceEqual(v.AsSpan(0, 20))), $"tri {t}: inside seam vertex has an outside twin");
                foreach (var v in outside.Vertices.Where(v => !IsSource(v)))
                {
                    var (x, y) = Px(v, map, 1024, 1024);
                    if (x < r.X - 0.5 || x > r.Right + 0.5 || y < r.Y - 0.5 || y > r.Bottom + 0.5) continue;
                    Check(inV.Any(i => i.AsSpan(0, 20).SequenceEqual(v.AsSpan(0, 20))), $"tri {t}: outside seam vertex ({x:F1},{y:F1}) has an inside twin");
                }
                Check(!Degenerate(outside), $"tri {t}: no outside triangle with a repeated index or zero area");

                double sign = Math.Sign(UvArea(srcV, v => Px(v, map, 1024, 1024)));
                foreach (var o in outside.Triangles)
                {
                    double area = UvArea(Corners(outside, o), v => Px(v, map, 1024, 1024));
                    Check(Math.Sign(area) == sign || Math.Abs(area) < 0.05, $"tri {t}: outside keeps its winding");
                }
                for (int axis = 0; axis < 3; axis++)
                {
                    var bytes = srcV[0].AsSpan(axis * 2, 2).ToArray();
                    if (!srcV[1].AsSpan(axis * 2, 2).SequenceEqual(bytes) || !srcV[2].AsSpan(axis * 2, 2).SequenceEqual(bytes)) continue;
                    Check(outside.Vertices.All(v => v.AsSpan(axis * 2, 2).SequenceEqual(bytes)), $"tri {t}: outside stays on its axis-{axis} plane");
                }
            }
            Check(straddlers > 0, $"{straddlers} straddling triangles checked");

            var corner = SynthSplit(SynthModel(SynthCases["cornerCover"]));
            Check(corner != null, "corner case splits");
            if (corner == null) return;
            var (cin, cout) = corner.Value;
            var at = cout.Vertices.Where(v => { var (x, y) = Px(v, Identity, 1024, 1024); return Math.Abs(x - 260) < 0.01 && Math.Abs(y - 260) < 0.01; }).ToList();
            Check(at.Count == 1, $"corner vertex once in the outside ({at.Count})");
            Check(at.Count == 1 && cin.Vertices.Any(v => v.AsSpan(0, 20).SequenceEqual(at[0].AsSpan(0, 20))), "corner vertex identical in the centre");
            int cornerUses = at.Count == 1 ? cout.Triangles.Count(o => Corners(cout, o).Contains(at[0])) : 0;
            Check(cornerUses >= 3, $"corner vertex shared by the three neighbouring cells ({cornerUses} triangles)");
        });

        Add("ShellCornerCuts", Shell("E-15"), () =>
        {
            using var s = Small();
            var map = PanelUvMap.Calibrated;
            var r = s.Plan.ClipRegion;
            byte[] modl6 = Modl6(s.Baseline);
            var source = AvfxModel.Parse(modl6, out _)!;
            int cuts = 0, axisCuts = 0;
            for (int t = 0; t < source.Triangles.Count; t++)
            {
                var vs = Corners(source, source.Triangles[t]);
                var split = PicklesSplit(PicklesSingle(modl6, t), s.Plan.Window);
                // Only straddlers are cut; a triangle with no inside part stays whole in the outside.
                if (split == null || split.Value.Inside.Vertices.All(IsAnchor)) continue;
                var outside = split.Value.Outside;
                for (int e = 0; e < 3; e++)
                {
                    byte[] va = vs[e], vb = vs[(e + 1) % 3];
                    // A neighbour face on this 3D edge whose UVs differ there.
                    bool Shares((ushort A, ushort B, ushort C) o) =>
                        Corners(source, o).Any(x => x.AsSpan(0, 6).SequenceEqual(va.AsSpan(0, 6)) && !x.AsSpan(20, 4).SequenceEqual(va.AsSpan(20, 4)))
                        && Corners(source, o).Any(x => x.AsSpan(0, 6).SequenceEqual(vb.AsSpan(0, 6)));
                    if (!source.Triangles.Any(Shares)) continue;
                    var pa = Px(va, map, 1024, 1024); var pb = Px(vb, map, 1024, 1024);
                    foreach (var (line, vertical) in new[] { (r.X, true), (r.Right, true), (r.Y, false), (r.Bottom, false) })
                    {
                        double da = (vertical ? pa.X : pa.Y) - line, db = (vertical ? pb.X : pb.Y) - line;
                        if (da * db >= 0) continue;
                        double k = da / (da - db);
                        if (outside.Triangles.Count == 0) continue;
                        cuts++;
                        var fixedAxes = Enumerable.Range(0, 3).Where(ax => va.AsSpan(ax * 2, 2).SequenceEqual(vb.AsSpan(ax * 2, 2))).ToList();
                        if (fixedAxes.Count < 2) continue;
                        axisCuts++;
                        var want = Pos(new[] { va, vb, vb }, 1 - k, k, 0);
                        double Dist(byte[] v) { var p = Pos(new[] { v, v, v }, 1, 0, 0); return Dot(Sub(p, want), Sub(p, want)); }
                        var hit = outside.Vertices.OrderBy(Dist).First();
                        Check(Dist(hit) < 1e-4 && fixedAxes.All(ax => hit.AsSpan(ax * 2, 2).SequenceEqual(va.AsSpan(ax * 2, 2))),
                            $"tri {t} edge {e}: cut at {(vertical ? "x" : "y")}={line} keeps its fixed coordinates exactly");
                    }
                }
            }
            Console.WriteLine($"   info: {cuts} clip cuts on shared corner edges, {axisCuts} on axis-aligned edges");
        });

        Add("ShellSynthetic", Shell("E-1 E-2 E-3 E-4 E-5 E-6 E-14"), () =>
        {
            double Px1024(byte[] v, bool inside, int axis)
            {
                var (s, t) = Px(v, Identity, 1, 1);
                double c = axis == 0 ? s : t;
                return inside ? 256 + c * 512 : c * 1024;
            }
            double AreaOf(AvfxModel m, bool inside, bool skipAnchor) => m.Triangles
                .Where(t => !skipAnchor || !Corners(m, t).All(v => SynthAnchor.Any(a => Math.Abs(Px1024(v, inside, 0) - a.X) < 1e-6 && Math.Abs(Px1024(v, inside, 1) - a.Y) < 1e-6)))
                .Sum(t => Math.Abs(UvArea(Corners(m, t), v => (Px1024(v, inside, 0), Px1024(v, inside, 1)))));
            double SourceArea((double X, double Y)[] p) => Math.Abs(SignedArea(p[0], p[1], p[2]));

            foreach (var (name, tri) in SynthCases)
            {
                var model = SynthModel(tri);
                var split = SynthSplit(model);
                Check(split != null, $"{name}: splits");
                if (split == null) continue;
                var (inside, outside) = split.Value;
                Check(!Degenerate(outside), $"{name}: no outside triangle with a repeated index or zero area");
                double inA = AreaOf(inside, true, true), outA = AreaOf(outside, false, false), src = SourceArea(tri);
                switch (name)
                {
                    case "outside":
                    case "sliverCentre":
                        Check(outside.Triangles.Count == 1 && Corners(outside, outside.Triangles[0]).Select((v, i) => v.AsSpan().SequenceEqual(model.Vertices[i])).All(x => x),
                            $"{name}: whole triangle verbatim in the outside");
                        Check(inside.Triangles.Count == 1, $"{name}: inside holds only the anchor ({inside.Triangles.Count})");
                        break;
                    case "inside":
                        Check(outside.Triangles.Count == 0 && inside.Triangles.Count == 2, $"inside: inside only ({inside.Triangles.Count}/{outside.Triangles.Count})");
                        break;
                    case "enclose":
                        Check(Math.Abs(inA - 504.0 * 504) < 1e-3 * 504 * 504, $"enclose: centre is the region ({inA})");
                        Check(Math.Abs(outA - (src - 504.0 * 504)) < 1e-3 * src, $"enclose: ring = source - region ({outA} vs {src - 504.0 * 504})");
                        break;
                    default:
                        Check(Math.Abs(inA + outA - src) < 1e-3 * src, $"{name}: inside + outside = source ({inA} + {outA} vs {src})");
                        break;
                }
            }
        });

        Add("ShellEmptyKeepsPanelHidden", Shell("E-8"), () =>
        {
            var split = SynthSplit(SynthModel(SynthCases["inside"]));
            Check(split != null && split.Value.Outside.Triangles.Count == 0, "all-inside model: empty outside");
            using var s = Small();
            var plan = s.Plan with { OutsideModlData = null, OutsideModel = -1 };
            var built = AvfxTableOverlay.Build(s.Baseline, s.Info, plan, ProbeKeys(4), Fixtures.Timeline(4), out var r, out string p);
            Check(built != null, $"built ({r} {p})");
            if (built == null) return;
            var root = AvfxChunk.Parse(built);
            var particles = root.All("Ptcl").ToList();
            Check(root.ReadInt("MdCn") == 9, $"MdCn 9 ({root.ReadInt("MdCn")})");
            Check(Fixtures.AlphaKeys(particles[7]).AsSpan().SequenceEqual(AvfxTableAnimation.HiddenKeys()), "panel hidden");
            Check(particles[7].Child("Data")!.Child("MdNo")!.Data![0] == 6, "panel MdNo 6");
            Check(particles.Skip(9).All(x => x.Child("Data")!.Child("MdNo")!.Data![0] == 8), "clones MdNo 8");
        });
    }
}
