using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;

namespace AnimHarness;

/// <summary>The laptop screen animates with overlay frames, as a second layer beside the table (#12).</summary>
internal static partial class Program
{
    private const string Lap = "gh12-laptop-overlay";
    private const int LapCropBytes = 699_120;
    private const int LaptopParticle = 3, LaptopModel = 2;
    private static readonly TexRect LapWindow = new(2944, 2560, 512, 1024);
    private static readonly TexRect LapRectInWindow = new(49, 199, 387, 656);
    private static readonly TexRect LidLogo = new(3109, 3660, 196, 196);

    private static string LTag(string ids) =>
        string.Join(" ", ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(id => id[0] == 'A'
            ? $"AC-{Lap}-{id[2..]}" : $"EDGE-{Lap}-{id[2..]}"));

    private static readonly string[] LaptopKeys = { "vfx/atex/eq3.atex" };

    private static TableAvfxInfo LaptopInfo(byte[] avfx) =>
        AvfxTableAnimation.Inspect(avfx, LaptopKeys, _ => false).Info ?? throw new InvalidDataException("no laptop particle");

    private static bool ApplyLaptop(TempMod mod, int n, out string error, TempModHost? host = null, DjTableAnimationSite? site = null)
    {
        host ??= mod.Host;
        site ??= mod.Scan(host);
        return DjTableAnimationCore.Apply(host, site, AnimatedSurface.Laptop, Fixtures.Frames(n), Fixtures.Timeline(n),
            FitMode.Fill, null, out _, out error, out _);
    }

    private static bool RestoreSurface(TempMod mod, AnimatedSurface surface, out string error, TempModHost? host = null)
    {
        host ??= mod.NewHost();
        return DjTableAnimationCore.Restore(host, mod.Scan(host), surface, out _, out error);
    }

    private static string LapFrame(int gen, int k) => $@"ppe-table-animation\t0000001_{gen}_lap{k:D2}.atex";

    private static T WithLaptopPlanner<T>(Func<DjTableAnimationSite, byte[], OverlayRoute?> planner, Func<T> body)
    {
        var old = DjTableAnimationCore.LaptopPlanner;
        DjTableAnimationCore.LaptopPlanner = planner;
        try { return body(); }
        finally { DjTableAnimationCore.LaptopPlanner = old; }
    }

    /// <summary>What each effect should hold: both live layers composed on the shipped copy.</summary>
    private static void CheckComposed(TempMod mod, string label)
    {
        var site = mod.Scan(mod.NewHost());
        var state = site.State!;
        byte[] Atlas(AnimatedSurface s) => mod.Read(s == AnimatedSurface.Table ? mod.Table.Relative : mod.Laptop!.Relative);
        foreach (var avfx in site.Animatable)
        {
            var expect = AnimationLayers.Compose(avfx, site, AnimationLayers.TableOf(state), AnimationLayers.LaptopOf(state), Atlas, out string p);
            Check(expect != null && mod.Read(avfx.Relative).AsSpan().SequenceEqual(expect), $"{label}: {avfx.Relative} = composed layers {p}");
        }
    }

    private static (double X, double Y) MapPx(byte[] v, PanelUvMap map, double size)
    {
        var (u, w) = AvfxModel.Uv(v);
        var (s, t) = map.ToTexture(u, w);
        return (s * size, t * size);
    }

    private static AvfxModel PicklesModel2(byte[] avfx) =>
        AvfxModel.Parse(AvfxChunk.Parse(avfx).All("Modl").ElementAt(LaptopModel).Data!, out _)!;

    private static void PadParticles(TempMod mod, int extra) =>
        foreachEffect(mod, root =>
        {
            int last = root.Children!.FindLastIndex(c => c.Name == "Ptcl");
            var copy = root.All("Ptcl").First();
            for (int i = 0; i < extra; i++) root.Children.Insert(last + 1, copy.Clone());
            root.SetInt("PrCn", root.ReadInt("PrCn") + extra);
        });

    private static void PadTextures(TempMod mod, int extra) =>
        foreachEffect(mod, root =>
        {
            int last = root.Children!.FindLastIndex(c => c.Name == "Tex");
            for (int i = 0; i < extra; i++) root.Children.Insert(last + 1, AvfxChunk.Leaf("Tex", AvfxChunk.TextBytes($"vfx/pad{i}.atex")));
            root.SetInt("TxCn", root.ReadInt("TxCn") + extra);
        });

    private static void PadItems(TempMod mod, int extra) =>
        foreachEffect(mod, root =>
        {
            var last = root.All("Emit").First().All("ItPr").Last();
            var first = new List<AvfxChunk>();
            foreach (var c in last.Children!) { if (c.Name == "bEnb" && first.Count > 0) break; first.Add(c); }
            for (int i = 0; i < extra; i++) last.Children.AddRange(first.Select(c => c.Clone()));
        });

    private static void foreachEffect(TempMod mod, Action<AvfxChunk> edit)
    {
        foreach (var rel in Fixtures.PicklesAvfx) EditAvfx(mod, rel, edit);
    }

    private static void RegisterLaptop()
    {
        Add("LaptopMapMatchesArt", LTag("A-1"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            var model = PicklesModel2(mod.Read(Fixtures.PicklesSmall));
            var map = PanelUvMap.Laptop;
            var screen = new[] { 3942, 3945 }.SelectMany(t => new[] { model.Triangles[t].A, model.Triangles[t].B, model.Triangles[t].C })
                .Select(i => MapPx(model.Vertices[i], map, 4096)).ToList();
            double x0 = screen.Min(p => p.X), x1 = screen.Max(p => p.X), y0 = screen.Min(p => p.Y), y1 = screen.Max(p => p.Y);
            Check(Math.Abs(x0 - 2970.5) <= 1 && Math.Abs(x1 - 3414) <= 1 && Math.Abs(y0 - 2753) <= 1 && Math.Abs(y1 - 3420) <= 1,
                $"screen quad at x {x0:F1}..{x1:F1}, y {y0:F1}..{y1:F1}");
            var r = mod.Laptop!.Surface.Rect;
            Check(x0 <= r.X && x1 >= r.Right && y0 <= r.Y && y1 >= r.Bottom, "screen quad covers the screen rect");

            var eq3 = DecodeLevel(mod.Read(Fixtures.PicklesEq3), 0);
            bool Lit(int x, int y) { var c = Rgb(eq3, x, y); return Math.Max(c.R, Math.Max(c.G, c.B)) > 8; }
            int midY = (r.Y + r.Bottom) / 2, midX = (r.X + r.Right) / 2;
            int right = Enumerable.Range(3380, 80).Where(x => Lit(x, midY)).DefaultIfEmpty(-1).Max() + 1;
            int top = Enumerable.Range(2700, 80).Where(y => Lit(midX, y)).DefaultIfEmpty(9999).Min();
            int bottom = Enumerable.Range(3380, 80).Where(y => Lit(midX, y)).DefaultIfEmpty(-1).Max() + 1;
            Check(Math.Abs(right - x1) <= 1 && Math.Abs(top - y0) <= 1 && Math.Abs(bottom - y1) <= 1,
                $"bezel art edges right {right}, top {top}, bottom {bottom} match the quad");

            var all = model.Vertices.Select(v => map.ToTexture(AvfxModel.Uv(v).U, AvfxModel.Uv(v).V).S).ToList();
            Check(Math.Abs(all.Min() - 0.5) < 1e-3 && Math.Abs(all.Max() - 1.0) < 1e-3, $"U spans the right half exactly ({all.Min():F4}..{all.Max():F4})");

            var corners = new[] { (LidLogo.X + 0.5, LidLogo.Y + 0.5), (LidLogo.Right - 0.5, LidLogo.Y + 0.5), (LidLogo.X + 0.5, LidLogo.Bottom - 0.5), (LidLogo.Right - 0.5, LidLogo.Bottom - 0.5) };
            var tris = model.Triangles.Select(t => new[] { MapPx(model.Vertices[t.A], map, 4096), MapPx(model.Vertices[t.B], map, 4096), MapPx(model.Vertices[t.C], map, 4096) }).ToList();
            Check(corners.All(c => tris.Any(t => AvfxModel.Contains(t, c.Item1, c.Item2))), "the lid back covers the badge");
        });

        Add("MoonPanelUsesLaptopMap", LTag("A-1"), () =>
        {
            using var moon = Fixtures.Moon();
            var root = AvfxChunk.Parse(moon.Read(Fixtures.MoonCat));
            var particles = root.All("Ptcl").ToList();
            var frameParticles = new[] { 7 }.Concat(Enumerable.Range(9, 48)).ToList();
            Check(frameParticles.All(i => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(particles[i].Child("UvSt")!.Data!)) == AvfxTableOverlay.LaptopUvStSha
                && particles[i].ReadInt("RMT") == 8 && particles[i].ReadInt("DsDw") == 1 && particles[i].ReadInt("DsDt") == 1
                && particles[i].Child("Data")!.Child("MdNo")!.Data![0] == 6), "Moon's frame particles: laptop UvSt and draw setup, model 6");

            var model6 = AvfxModel.Parse(root.All("Modl").ElementAt(6).Data!, out _)!;
            var region = new TexRect(132, 260, 504, 504);
            var island = model6.Triangles.Where(t => new[] { t.A, t.B, t.C }.All(i =>
            {
                var p = MapPx(model6.Vertices[i], PanelUvMap.Calibrated, 1024);
                return p.X >= region.X && p.X <= region.Right && p.Y >= region.Y && p.Y <= region.Bottom;
            })).SelectMany(t => new[] { t.A, t.B, t.C }).Select(i => MapPx(model6.Vertices[i], PanelUvMap.Laptop, 1024)).ToList();
            double ix0 = island.Min(p => p.X), ix1 = island.Max(p => p.X), iy0 = island.Min(p => p.Y), iy1 = island.Max(p => p.Y);
            Check(Math.Abs(ix0 - 396) <= 4 && Math.Abs(ix1 - 543) <= 4 && Math.Abs(iy0 - 425) <= 4 && Math.Abs(iy1 - 569) <= 4,
                $"panel island under the laptop map at ({ix0:F0}-{ix1:F0}, {iy0:F0}-{iy1:F0})");

            // Where Moon's own frames differ from each other: inside that island.
            var src = Path.Combine(Path.GetTempPath(), "AnimHarness", "moon-src");
            var texs = root.All("Tex").Select(t => t.Text()).ToList();
            var options = PenumbraOptions.Read(src);
            byte[] FrameFile(int particle)
            {
                string game = texs[AvfxTableAnimation.Tlst(particles[particle])];
                string rel = options.SelectMany(o => o.Files).First(f => string.Equals(f.Key, game, StringComparison.OrdinalIgnoreCase)).Value;
                return File.ReadAllBytes(Path.Combine(src, rel));
            }
            var a = DecodeLevel(FrameFile(9), 0);
            int dx0 = int.MaxValue, dx1 = -1, dy0 = int.MaxValue, dy1 = -1;
            foreach (int p in new[] { 20, 33, 47 })
            {
                var b = DecodeLevel(FrameFile(p), 0);
                for (int y = 0; y < a.Height; y++)
                    for (int x = 0; x < a.Width; x++)
                    {
                        int i = (y * a.Width + x) * 4;
                        if (a.Pixels.AsSpan(i, 3).SequenceEqual(b.Pixels.AsSpan(i, 3))) continue;
                        dx0 = Math.Min(dx0, x); dx1 = Math.Max(dx1, x); dy0 = Math.Min(dy0, y); dy1 = Math.Max(dy1, y);
                    }
            }
            Check(dx1 >= 0 && dx0 >= ix0 - 3 && dx1 <= ix1 + 3 && dy0 >= iy0 - 3 && dy1 <= iy1 + 3,
                $"Moon's frames differ at ({dx0}-{dx1}, {dy0}-{dy1}), inside the island");
        });

        Add("LaptopPlanPickles", LTag("A-1 E-12 E-23"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            byte[] eq3 = mod.Read(Fixtures.PicklesEq3);
            var profile = OverlayProfile.Laptop(AnimationLayers.KeepOut(mod.Laptop!.Surface));
            OverlayPlan? first = null;
            foreach (var rel in Fixtures.PicklesAvfx)
            {
                byte[] avfx = mod.Read(rel);
                var plan = AvfxTableOverlay.Plan(avfx, LaptopInfo(avfx), mod.Laptop.Surface.Rect, eq3, profile, out var r);
                Check(plan != null, $"{rel} plans ({r})");
                if (plan == null) continue;
                Check(plan.PanelModel == LaptopModel && plan.Window == LapWindow && plan.ClipRegion == new TexRect(2948, 2564, 504, 1016)
                    && plan.RectInWindow == LapRectInWindow && plan.Coverage == 1.0, $"{rel}: window, region, rect, coverage {plan.Coverage}");
                Check(!(LidLogo.X < plan.Window.Right && plan.Window.X < LidLogo.Right && LidLogo.Y < plan.Window.Bottom && plan.Window.Y < LidLogo.Bottom), "window clear of the badge");
                if (first == null) first = plan;
                else Check(plan.ModlData.AsSpan().SequenceEqual(first.ModlData) && plan.OutsideModlData.AsSpan().SequenceEqual(first.OutsideModlData), $"{rel}: same models");
            }
            var route = AnimationLayers.PlanLaptop(mod.Scan(), eq3, out string refusal);
            Check(route != null && route.Crop.Length == LapCropBytes && route.RectInWindow == LapRectInWindow, $"one shared route, crop {route?.Crop.Length} ({refusal})");
        });

        Add("LaptopBuildShape", LTag("A-1 E-11"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            byte[] pristine = mod.Read(Fixtures.PicklesSmall);
            var info = LaptopInfo(pristine);
            var plan = AvfxTableOverlay.Plan(pristine, info, mod.Laptop!.Surface.Rect, mod.Read(Fixtures.PicklesEq3),
                OverlayProfile.Laptop(AnimationLayers.KeepOut(mod.Laptop.Surface)), out _)!;
            var keys = Enumerable.Range(0, 12).Select(k => $"vfx/atex/ppe_x_1_lap{k:D2}.atex").ToList();
            var built = AvfxTableOverlay.Build(pristine, info, plan, keys, Fixtures.Timeline(12), out var r, out string p, 48, AvfxTableAnimation.LaptopVisible);
            Check(built != null, $"built ({r} {p})");
            if (built == null) return;
            var before = AvfxChunk.Parse(pristine);
            var root = AvfxChunk.Parse(built);
            int models = before.All("Modl").Count();
            Check(root.All("Modl").Count() == models + 2, "two new models");
            var parts = root.All("Ptcl").ToList();
            var old = before.All("Ptcl").ToList();
            var clones = parts.Skip(old.Count).ToList();
            Check(clones.Count == 12 && clones.All(c => c.Child("Data")!.Child("MdNo")!.Data![0] == models), "12 clones draw the inside model");
            for (int k = 0; k < 12; k++)
            {
                var col = clones[k].Child("Col")!;
                Check(col.Children?.Count == 1 && col.Child("A")!.Child("Keys")!.Data!.AsSpan()
                    .SequenceEqual(AvfxTableAnimation.AlphaKeys(k, Fixtures.Timeline(12), AvfxTableAnimation.LaptopVisible)), $"clone {k}: alpha only, visible 1.0");
            }
            var panel = parts[LaptopParticle].Clone();
            Check(panel.Child("Data")!.Child("MdNo")!.Data![0] == models + 1 && panel.Child("Col")!.Children == null, "laptop particle draws the outside, Col still empty");
            panel.Child("Data")!.Child("MdNo")!.Data![0] = LaptopModel;
            Check(Fixtures.ParticleBytes(panel).AsSpan().SequenceEqual(Fixtures.ParticleBytes(old[LaptopParticle])), "laptop particle = shipped but MdNo");
            for (int i = 0; i < old.Count; i++)
                if (i != LaptopParticle) Check(Fixtures.ParticleBytes(parts[i]).AsSpan().SequenceEqual(Fixtures.ParticleBytes(old[i])), $"particle {i} untouched");
            Check(Enumerable.Range(0, models).All(i => root.All("Modl").ElementAt(i).Data!.AsSpan().SequenceEqual(before.All("Modl").ElementAt(i).Data)), "models untouched");
            var inside = AvfxModel.Parse(plan.ModlData, out _)!;
            var src = PicklesModel2(pristine);
            foreach (int t in new[] { 3942, 3945 })
            {
                var tri = src.Triangles[t];
                Check(new[] { tri.A, tri.B, tri.C }.All(i => inside.Vertices.Any(v => v.AsSpan(0, 20).SequenceEqual(src.Vertices[i].AsSpan(0, 20)))), $"screen triangle {t} verbatim");
            }
            var table = AvfxTableAnimation.Build(mod.Read(Fixtures.PicklesSmall), Fixtures.Inspect(pristine), keys, Fixtures.Timeline(12), out _);
            using var plain = Fixtures.Pickles("plain");
            var reference = AvfxTableAnimation.Build(plain.Read(Fixtures.PicklesSmall), Fixtures.Inspect(pristine), keys, Fixtures.Timeline(12), out _);
            Check(table != null && reference != null && table.AsSpan().SequenceEqual(reference), "table panel Col path unchanged");
        });

        Add("LaptopSamplesSameTexel", LTag("A-1"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            byte[] eq3b = mod.Read(Fixtures.PicklesEq3);
            var route = AnimationLayers.PlanLaptop(mod.Scan(), eq3b, out _)!;
            var plan = route.Plans.Values.First();
            var eq3 = DecodeLevel(eq3b, 0);
            var crop = DecodeLevel(route.Crop, 0);
            var inside = AvfxModel.Parse(plan.ModlData, out _)!;
            var src = PicklesModel2(mod.Read(Fixtures.PicklesSmall));
            var map = PanelUvMap.Laptop;
            // Triangles kept whole carry their source vertices, so old and new UVs pair up by vertex.
            // (Model 2 has triangles too small to locate in 3D after half rounding.)
            // Vertices share position bytes across UV seams, so pair whole triangles, in order.
            string Key(byte[][] vs3) => string.Concat(vs3.Select(v => Convert.ToHexString(v, 0, 20)));
            var bySource = src.Triangles.Select(o => Corners(src, o)).GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
            int samples = 0, bad = 0, whole = 0;
            foreach (var t in inside.Triangles)
            {
                var vs = new[] { inside.Vertices[t.A], inside.Vertices[t.B], inside.Vertices[t.C] };
                var olds = bySource.GetValueOrDefault(Key(vs));
                foreach (var v in vs)
                {
                    var (fs, ft) = Px(v, map, 1, 1);
                    if (fs < -0.002 || fs > 1.002 || ft < -0.002 || ft > 1.002) bad++;
                }
                if (olds == null) continue;
                whole++;
                foreach (var (a, b) in new[] { (1 / 3.0, 1 / 3.0), (0.6, 0.2), (0.2, 0.6), (0.2, 0.2) })
                {
                    double c = 1 - a - b;
                    var (cs, ct) = Uvs(vs, a, b, c, map);
                    double nx = LapWindow.X + cs * LapWindow.Width, ny = LapWindow.Y + ct * LapWindow.Height;
                    samples++;
                    // The shipped UVs are halves: near u = 1 one step is a whole texel of the 4096 atlas.
                    if (!olds.Any(o => { var (os, ot) = Uvs(o, a, b, c, map); return Math.Abs(nx - os * 4096) <= 1.3 && Math.Abs(ny - ot * 4096) <= 1.3; })) { bad++; continue; }
                    int cx = (int)Math.Floor(cs * crop.Width), cy = (int)Math.Floor(ct * crop.Height);
                    if (cx < 0 || cy < 0 || cx >= crop.Width || cy >= crop.Height || !Rgb(crop, cx, cy).Equals(Rgb(eq3, LapWindow.X + cx, LapWindow.Y + cy))) bad++;
                }
            }
            Check(whole > 800, $"{whole} whole triangles checked");
            Check(samples > 0 && bad == 0, $"{samples} samples, {bad} off or outside the crop");
        });

        Add("LaptopApplyPickles", LTag("A-1 A-9 E-21 E-24"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            var eq3 = mod.Read(Fixtures.PicklesEq3);
            var eq55 = mod.Read(Fixtures.PicklesEq55);
            Check(ApplyLaptop(mod, 12, out string e), "apply " + e);
            for (int k = 0; k < 12; k++)
            {
                var f = mod.Read(LapFrame(1, k));
                var h = TexHeader.TryRead(f, out _);
                Check(f.Length == LapCropBytes && h is { Width: 512, Height: 1024, MipCount: 8 } && h.Format == TexHeader.TryRead(eq3, out _)!.Format, $"frame {k} 512x1024");
            }
            CheckComposed(mod, "laptop 12");
            Check(mod.Read(Fixtures.PicklesEq3).AsSpan().SequenceEqual(eq3) && mod.Read(Fixtures.PicklesEq55).AsSpan().SequenceEqual(eq55), "eq3 and eq55 untouched");
            var state = ReadState(mod);
            Check(state.Laptop?.FrameRect == LapRectInWindow && state.Frames.Count == 0 && state.FrameRect == null, "state: laptop section only");
            foreach (var group in mod.Groups().Where(g => g["Name"]?.ToString() == "DJ Table"))
                foreach (var option in ((JArray)group["Options"]!).Where(o => o["Files"]?["vfx/alinasexy.avfx"] != null))
                    Check(Enumerable.Range(0, 12).All(k => option["Files"]?[$"vfx/atex/ppe_t0000001_1_lap{k:D2}.atex"] != null), $"{option["Name"]} maps the laptop frames");
            var site = mod.Scan();
            Check(site.Animatable.All(a => AvfxTableAnimation.FrameTokens(mod.Read(a.Relative)).SetEquals(new[] { "t0000001" })), "one token");
            Check(DjTableAnimationCore.Status(site, AnimatedSurface.Laptop) is { Kind: AnimationStatusKind.Ours, Frames: 12 }
                && DjTableAnimationCore.Status(site, AnimatedSurface.Table).Kind == AnimationStatusKind.None, "laptop Ours(12), table None");

            using var full = Fixtures.Pickles("full", withLaptop: true);
            var plan = FramePlan.Plan(Enumerable.Repeat(4, 120).ToList(), 48);
            Check(DjTableAnimationCore.Apply(full.Host, full.Scan(), AnimatedSurface.Laptop, Fixtures.Frames(plan.Timeline.StartTicks.Count),
                plan.Timeline, FitMode.Fill, null, out _, out e, out _), "120 frames thinned to 48 apply " + e);
        });

        Add("LaptopPreviewAndSummary", LTag("A-2 E-14"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            var frames = Fixtures.Frames(4);
            var previews = DjTableAnimationCore.PreviewFrames(mod.Read(Fixtures.PicklesEq3), mod.Laptop!.Surface, frames, FitMode.Fill);
            var site = mod.Scan();
            var sum = DjTableAnimationCore.Summarise(site, 12, Fixtures.Timeline(12), false, AnimatedSurface.Laptop);
            Check(sum.AddedBytes == 12L * LapCropBytes, $"12 frames add {sum.AddedBytes}");
            Check(WithLaptopPlanner((_, _) => null, () => DjTableAnimationCore.Summarise(site, 12, Fixtures.Timeline(12), false, AnimatedSurface.Laptop)).AddedBytes == 0, "refusal: 0");
            Check(WithLaptopPlanner((_, _) => throw new InvalidOperationException("boom"), () => DjTableAnimationCore.Summarise(site, 12, Fixtures.Timeline(12), false, AnimatedSurface.Laptop)).AddedBytes == 0, "throw: 0, no throw");
            Check(DjTableAnimationCore.Apply(mod.Host, site, AnimatedSurface.Laptop, frames, Fixtures.Timeline(4), FitMode.Fill, null, out _, out string err, out _), err);
            var state = ReadState(mod);
            for (int k = 0; k < 4; k++)
            {
                var read = DjTableAnimationCore.ReadFrame(mod.Read(LapFrame(1, k)), state.Laptop!.FrameRect, mod.Laptop.Surface);
                Check(read != null && Same(read, previews[k]), $"frame {k}: preview = written");
            }
        });

        Add("LaptopCardReadsBack", LTag("A-2 E-18"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            Check(ApplyLaptop(mod, 3, out string e), e);
            var state = ReadState(mod);
            var img = DjTableAnimationCore.ReadFrame(mod.Read(LapFrame(1, 0)), state.Laptop!.FrameRect, mod.Laptop!.Surface);
            Check(img != null && img.Width == mod.Laptop.Surface.UprightWidth && img.Height == mod.Laptop.Surface.UprightHeight, "upright laptop frame");
            Check(DjTableAnimationCore.ReadFrame(mod.Read(LapFrame(1, 0))[..300], state.Laptop.FrameRect, mod.Laptop.Surface) == null, "truncated: null");
            var path = mod.Full(LapFrame(1, 1));
            var bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            Check(DjTableAnimationCore.Restore(mod.NewHost(), mod.Scan(), AnimatedSurface.Laptop, out var warnings, out e), e);
            Check(warnings.Any(w => w.StartsWith(nameof(AnimationMessage.KeptEditedFrame))) && File.Exists(path), "edited frame kept with a warning");
        });

        Add("LaptopCardViews", LTag("A-2 E-7 E-10 E-16"), () =>
        {
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                var none = LaptopCard.Describe(mod.Scan(), false);
                Check(none.AnimationAllowed && none.StillRoute == StillRoute.Existing && !none.RestoreEnabled, "none: animation allowed, still existing");
                Check(LaptopCard.Describe(mod.Scan(), true).RestoreEnabled, "still customised: restore enabled");
                Check(ApplyLaptop(mod, 12, out string e), e);
                var v = LaptopCard.Describe(mod.Scan(), false);
                Check(v.StateKey == TableText.YourAnimation && v.StillRoute == StillRoute.PasteThenRestore && v.RestoreEnabled && v.PlaysFrames, "laptop Ours");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                Check(ApplyFrames(mod, 6, out string e), e);
                var site = mod.Scan();
                Check(DjTableAnimationCore.Status(site, AnimatedSurface.Laptop).Kind == AnimationStatusKind.None
                    && DjTableAnimationCore.Status(site, AnimatedSurface.Table).Kind == AnimationStatusKind.Ours, "table only: laptop None");
                mod.Host.KillAfter = IsAvfxWrite;
                ApplyLaptop(mod, 3, out _);
                var crashed = mod.Scan(mod.NewHost());
                Check(!LaptopCard.Describe(crashed, false).AnimationAllowed, "interrupted: laptop not offered");
                var table = TableCard.Describe(DjTableAnimationCore.Status(crashed), crashed.Mode, DjTableAnimationCore.HasLayer(crashed, AnimatedSurface.Table), false);
                Check(table.StateKey == TableText.Interrupted && table.RestoreEnabled, "interrupted on the table card");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                Check(ApplyLaptop(mod, 47, out string e), e);
                var site = mod.Scan();
                var status = DjTableAnimationCore.Status(site, AnimatedSurface.Table);
                Check(status.Kind == AnimationStatusKind.Unsupported && status.Reason!.StartsWith(nameof(AnimationMessage.TooManyTextures)), $"laptop 47: table not offered ({status})");
                Check(LaptopCard.Describe(site, false).StateKey == TableText.YourAnimation, "laptop card unaffected");
            }
            using (var mod = Fixtures.Pickles(withLaptop: false))
                Check(!LaptopCard.Describe(mod.Scan(), false).AnimationAllowed, "no laptop target: not offered");
        });

        Add("TableThenLaptop", LTag("A-3 E-2 E-15"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            Check(ApplyFrames(mod, 12, out string e), "table " + e);
            var tableState = ReadState(mod);
            var tableFrames = tableState.Frames.ToDictionary(f => f.Relative, f => AvfxTableAnimation.Sha(mod.Read(f.Relative)));
            var stale = mod.Scan();
            Check(ApplyLaptop(mod, 8, out e, mod.NewHost()), "laptop " + e);
            CheckComposed(mod, "table + laptop");
            var site = mod.Scan();
            var state = site.State!;
            Check(tableFrames.All(kv => AvfxTableAnimation.Sha(mod.Read(kv.Key)) == kv.Value), "table frames untouched");
            Check(state.FrameRect == tableState.FrameRect && state.Frames.Select(f => f.Sha).SequenceEqual(tableState.Frames.Select(f => f.Sha)), "table part of the state kept");
            Check(DjTableAnimationCore.Status(site, AnimatedSurface.Table) is { Kind: AnimationStatusKind.Ours, Frames: 12 }
                && DjTableAnimationCore.Status(site, AnimatedSurface.Laptop) is { Kind: AnimationStatusKind.Ours, Frames: 8 }, "both Ours");
            var snap = mod.Snapshot();
            bool ok = DjTableAnimationCore.Apply(mod.NewHost(), stale, AnimatedSurface.Laptop, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out e, out _);
            Check(!ok && e.StartsWith(nameof(AnimationMessage.ModChanged)) && mod.Snapshot().SequenceEqual(snap), "stale site: ModChanged, nothing written");
        });

        Add("LaptopThenTableReapply", LTag("A-3 E-4"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            Check(ApplyLaptop(mod, 8, out string e), "laptop " + e);
            var lap = ReadState(mod).Laptop!;
            var lapFiles = lap.Frames.ToDictionary(f => f.Relative, f => AvfxTableAnimation.Sha(mod.Read(f.Relative)));
            void LaptopKept(string label)
            {
                var now = ReadState(mod).Laptop;
                Check(now != null && now.Frames.Select(f => f.Relative).SequenceEqual(lap.Frames.Select(f => f.Relative)) && now.FrameRect == lap.FrameRect, $"{label}: laptop state kept");
                Check(lapFiles.All(kv => File.Exists(mod.Full(kv.Key)) && AvfxTableAnimation.Sha(mod.Read(kv.Key)) == kv.Value), $"{label}: laptop frames kept");
                Check(lap.Frames.All(f => mod.Groups().Any(g => g.ToString().Contains(f.GamePath))), $"{label}: laptop keys kept");
                CheckComposed(mod, label);
            }
            Check(ApplyFrames(mod, 12, out e, mod.NewHost()), "table 12 " + e);
            LaptopKept("table 12");
            var gen2 = ReadState(mod).Frames.Select(f => f.Relative).ToList();
            Check(ApplyFrames(mod, 6, out e, mod.NewHost()), "table 6 " + e);
            LaptopKept("table 6");
            Check(gen2.All(f => !File.Exists(mod.Full(f))), "old table frames deleted");
            var site = mod.Scan(mod.NewHost());
            Check(DjTableAnimationCore.PasteThenRestore(mod.Host, site, AnimatedSurface.Table, () => null, out _, out e), "table still " + e);
            LaptopKept("table still");
            Check(ReadState(mod).Frames.Count == 0, "table layer gone");
            Check(WithoutOverlay(() => ApplyFrames(mod, 4, out e, mod.NewHost())), "full-atlas table " + e);
            LaptopKept("full-atlas table");
            Check(ReadState(mod).FrameRect == null, "table layer full-atlas");
        });

        Add("LaptopOverFullAtlasTable", LTag("A-3 E-3"), () =>
        {
            using var live = Fixtures.LivePicklesAnimation(withLaptop: true);
            using var synthetic = Fixtures.Pickles("prev", withLaptop: true);
            string e = string.Empty;
            Check(WithoutOverlay(() => ApplyFrames(synthetic, 12, out e)), "previous-version table " + e);
            foreach (var mod in new[] { synthetic, live }.Where(m => m != null && ReadState(m).FrameRect == null && ReadState(m).Laptop == null).Select(m => m!))
            {
                string label = mod == live ? "live" : "synthetic";
                int n = Math.Min(8, mod.Scan().LaptopCapacity);
                if (n < 2) { Console.WriteLine($"   info: {label} table leaves no room for the laptop"); continue; }
                var effects = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var stateBytes = File.ReadAllBytes(DjAnimationScan.StatePath(mod.StateFolder));
                Check(ApplyLaptop(mod, n, out e, mod.NewHost()), $"{label}: laptop " + e);
                CheckComposed(mod, label);
                Check(RestoreSurface(mod, AnimatedSurface.Laptop, out e), $"{label}: laptop restore " + e);
                Check(effects.All(kv => mod.Read(kv.Key).AsSpan().SequenceEqual(kv.Value)), $"{label}: effects back to the table-only bytes");
                var st = ReadState(mod);
                var before = AnimationState.Parse(stateBytes);
                Check(st.Laptop == null && st.Frames.Select(f => f.Sha).SequenceEqual(before.Frames.Select(f => f.Sha))
                    && st.Avfx.All(a => before.Find(a.Relative)?.AppliedSha == a.AppliedSha), $"{label}: table-only state again");
            }
            foreach (bool overlay in new[] { true, false })
            {
                using var mod = Fixtures.Pickles(overlay ? "ov" : "fa", withLaptop: true);
                Check(overlay ? ApplyFrames(mod, 5, out e) : WithoutOverlay(() => ApplyFrames(mod, 5, out e)), e);
                CheckComposed(mod, overlay ? "overlay table alone" : "full-atlas table alone");
            }
        });

        Add("CarriedLayerNotKept", LTag("A-4 E-5 E-13"), () =>
        {
            TempMod Both()
            {
                var mod = Fixtures.Pickles("both", withLaptop: true);
                Check(ApplyFrames(mod, 6, out string e) && ApplyLaptop(mod, 4, out e, mod.NewHost()), "table + laptop " + e);
                return mod;
            }
            void Refuses(TempMod mod, string label, Func<TempModHost, DjTableAnimationSite, (bool Ok, string Error)> act)
            {
                var snap = mod.Snapshot();
                var host = mod.NewHost();
                var (ok, error) = act(host, mod.Scan(host));
                Check(!ok && error.StartsWith(nameof(AnimationMessage.LayerNotKept)) && mod.Snapshot().SequenceEqual(snap), $"{label}: LayerNotKept, nothing written ({error})");
            }
            using (var mod = Both())
            {
                PatchGlow(mod);
                Refuses(mod, "glow, laptop re-apply", (h, s) => (DjTableAnimationCore.Apply(h, s, AnimatedSurface.Laptop, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out var e, out _), e));
                Refuses(mod, "glow, table restore", (h, s) => (DjTableAnimationCore.Restore(h, s, AnimatedSurface.Table, out _, out var e), e));
                Refuses(mod, "glow, laptop restore", (h, s) => (DjTableAnimationCore.Restore(h, s, AnimatedSurface.Laptop, out _, out var e), e));
                bool pasted = false;
                Refuses(mod, "glow, laptop still", (h, s) => (DjTableAnimationCore.PasteThenRestore(h, s, AnimatedSurface.Laptop, () => { pasted = true; return null; }, out _, out var e), e));
                Check(!pasted, "the still was never pasted");
            }
            using (var mod = Both())
            {
                var json = JObject.Parse(StateText(mod));
                json["FrameRect"] = JObject.FromObject(new { X = 0, Y = 0, Width = 440, Height = 289 });
                File.WriteAllText(DjAnimationScan.StatePath(mod.StateFolder), json.ToString());
                Refuses(mod, "FrameRect edited, laptop re-apply", (h, s) => (DjTableAnimationCore.Apply(h, s, AnimatedSurface.Laptop, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out var e, out _), e));
            }
            using (var mod = Both())
            {
                var json = JObject.Parse(StateText(mod));
                json["Laptop"]!["StartTicks"]![1] = 7;
                File.WriteAllText(DjAnimationScan.StatePath(mod.StateFolder), json.ToString());
                Refuses(mod, "drift, table re-apply", (h, s) => (DjTableAnimationCore.Apply(h, s, AnimatedSurface.Table, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out var e, out _), e));
                Refuses(mod, "drift, table restore", (h, s) => (DjTableAnimationCore.Restore(h, s, AnimatedSurface.Table, out _, out var e), e));
            }
            using (var mod = Both())
            {
                Refuses(mod, "laptop UvSt changed, table re-apply", (h, s) =>
                {
                    foreach (var a in s.Animatable) { var d = AvfxChunk.Parse(a.Baseline!); d.All("Ptcl").ElementAt(LaptopParticle).Child("UvSt")!.Data![^1] ^= 1; d.ToBytes().CopyTo(a.Baseline!, 0); }
                    return (DjTableAnimationCore.Apply(h, s, AnimatedSurface.Table, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out var e, out _), e);
                });
            }
        });

        Add("BothReplansFailExit", LTag("A-4 E-27"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var groups = mod.Groups();
            Check(ApplyFrames(mod, 6, out string e) && ApplyLaptop(mod, 4, out e, mod.NewHost()), "table + laptop " + e);
            PatchGlow(mod);
            var host = mod.NewHost();
            var site = mod.Scan(host);
            foreach (var a in site.Animatable) { var d = AvfxChunk.Parse(a.Baseline!); d.All("Ptcl").ElementAt(LaptopParticle).Child("UvSt")!.Data![^1] ^= 1; d.ToBytes().CopyTo(a.Baseline!, 0); }
            var snap = mod.Snapshot();
            Check(!DjTableAnimationCore.Restore(host, site, AnimatedSurface.Table, out _, out e) && e.StartsWith(nameof(AnimationMessage.LayerNotKept)), "table restore: LayerNotKept " + e);
            Check(!DjTableAnimationCore.Restore(host, site, AnimatedSurface.Laptop, out _, out e) && e.StartsWith(nameof(AnimationMessage.LayerNotKept)), "laptop restore: LayerNotKept " + e);
            Check(mod.Snapshot().SequenceEqual(snap), "nothing written");
            host = mod.NewHost();
            Check(DjTableAnimationCore.RestoreAll(host, mod.Scan(host), out _, out e), "RestoreAll " + e);
            CheckPristine(mod, before, groups, "exit");
        });

        Add("LaptopRestoreOrders", LTag("A-4 E-6 E-28"), () =>
        {
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                Check(ApplyLaptop(mod, 5, out string e), e);
                Check(RestoreSurface(mod, AnimatedSurface.Laptop, out e), e);
                CheckPristine(mod, before, groups, "laptop only");
                var snap = mod.Snapshot();
                Check(RestoreSurface(mod, AnimatedSurface.Laptop, out e) && mod.Snapshot().SequenceEqual(snap), "no laptop layer: nothing written");
            }
            foreach (bool tableFirst in new[] { true, false })
            {
                using var mod = Fixtures.Pickles(withLaptop: true);
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                Check(ApplyFrames(mod, 6, out string e) && ApplyLaptop(mod, 4, out e, mod.NewHost()), e);
                var first = tableFirst ? AnimatedSurface.Table : AnimatedSurface.Laptop;
                Check(RestoreSurface(mod, first, out e), $"{first} restore " + e);
                CheckComposed(mod, $"after {first} restore");
                var st = ReadState(mod);
                Check(tableFirst ? st.Frames.Count == 0 && st.Laptop != null : st.Laptop == null && st.Frames.Count == 6, $"{first} layer gone, other kept");
                var other = tableFirst ? AnimatedSurface.Laptop : AnimatedSurface.Table;
                Check(RestoreSurface(mod, other, out e), $"{other} restore " + e);
                CheckPristine(mod, before, groups, $"{first} then {other}");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                Check(ApplyLaptop(mod, 4, out string e), e);
                var snap = mod.Snapshot();
                Check(RestoreSurface(mod, AnimatedSurface.Table, out e) && mod.Snapshot().SequenceEqual(snap), "laptop live, table no layer: table restore writes nothing");
                var site = mod.Scan();
                var view = TableCard.Describe(DjTableAnimationCore.Status(site), site.Mode, DjTableAnimationCore.HasLayer(site, AnimatedSurface.Table), true);
                Check(view.StillRoute == StillRoute.Existing && view.RestoreEnabled, "table card: still route, restore from the still alone");
            }
        });

        Add("LaptopStillOnAnimated", LTag("A-5 E-13"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            Check(ApplyFrames(mod, 6, out string e) && ApplyLaptop(mod, 4, out e, mod.NewHost()), e);
            var tableFrames = ReadState(mod).Frames;
            var still = Fixtures.Frames(1)[0];
            var snap = mod.Snapshot();
            var host = mod.NewHost();
            Check(!DjTableAnimationCore.PasteThenRestore(host, mod.Scan(host), AnimatedSurface.Laptop, () => "paste failed", out _, out e)
                && mod.Snapshot().SequenceEqual(snap), "failed paste: nothing changes");
            host = mod.NewHost();
            Check(DjTableAnimationCore.PasteThenRestore(host, mod.Scan(host), AnimatedSurface.Laptop, () =>
            {
                File.WriteAllBytes(mod.Full(Fixtures.PicklesEq3), TexPaste.PasteRect(mod.Read(Fixtures.PicklesEq3), mod.Laptop!.Surface.Rect, mod.Laptop.Surface.ToTextureSpace(still, FitMode.Fill), out _)!);
                return null;
            }, out _, out e), "still " + e);
            var st = ReadState(mod);
            Check(st.Laptop == null && st.Frames.Select(f => f.Relative).SequenceEqual(tableFrames.Select(f => f.Relative)), "laptop layer gone, table kept");
            CheckComposed(mod, "after laptop still");
            var rect = TexPaste.ReadRect(mod.Read(Fixtures.PicklesEq3), mod.Laptop!.Surface.Rect, out _)!;
            Check(Fixtures.Psnr(rect, mod.Laptop.Surface.ToTextureSpace(still, FitMode.Fill)) > 30, "eq3 holds the still");
            Check(LaptopCard.Describe(mod.Scan(), false).StillRoute == StillRoute.Existing, "still route back to Existing");
        });

        Add("LaptopFallbacks", LTag("A-6 E-9"), () =>
        {
            void Refused(string label, string expect, Action<TempMod>? edit = null, Func<DjTableAnimationSite, byte[], OverlayRoute?>? planner = null)
            {
                using var mod = Fixtures.Pickles(withLaptop: true);
                edit?.Invoke(mod);
                var snap = mod.Snapshot();
                bool refused = false;
                string e = string.Empty;
                var (ok, skipped, failed) = Watching(() =>
                {
                    bool Run() => DjTableAnimationCore.Apply(mod.Host, mod.Scan(), AnimatedSurface.Laptop, Fixtures.Frames(3), Fixtures.Timeline(3), FitMode.Fill, null, out _, out e, out refused);
                    return planner == null ? Run() : WithLaptopPlanner(planner, Run);
                });
                Check(!ok && refused && failed == 0 && skipped.Count == 1 && skipped[0].StartsWith(expect) && mod.Snapshot().SequenceEqual(snap),
                    $"{label}: refused as {expect}, nothing written ({string.Join("; ", skipped)})");
            }
            Refused("UvSt", "UvSetNotCalibrated", m => foreachEffect(m, r => r.All("Ptcl").ElementAt(LaptopParticle).Child("UvSt")!.Data![^1] ^= 1));
            Refused("RMT", "DrawSetupNotCalibrated", m => foreachEffect(m, r => r.All("Ptcl").ElementAt(LaptopParticle).SetInt("RMT", 9)));
            Refused("VIdx", "ModelUnreadable", m => foreachEffect(m, r =>
            {
                var modl = r.All("Modl").ElementAt(LaptopModel);
                var d = modl.Data!;
                int vdrw = BitConverter.ToInt32(d, 4), at = 8 + ((vdrw + 3) & ~3);
                BitConverter.GetBytes(BitConverter.ToInt32(d, at + 4) - 2).CopyTo(d, at + 4);
            }));
            Refused("UV pair", "UvPairsDiffer", m => foreachEffect(m, r => r.All("Modl").ElementAt(LaptopModel).Data![8 + 24] ^= 1));
            Refused("models", "TooManyModels", m => { foreach (var rel in Fixtures.PicklesAvfx) PadModels(m, rel, 254); });
            Refused("throw", "exception:", planner: (_, _) => throw new InvalidOperationException("boom"));
            Refused("crop", "encode:", planner: (s, a) => AnimationLayers.PlanLaptop(s, a, out _)! with { Crop = a[..80] });

            using var mod = Fixtures.Pickles(withLaptop: true);
            byte[] avfx = mod.Read(Fixtures.PicklesSmall), eq3 = mod.Read(Fixtures.PicklesEq3);
            var profile = OverlayProfile.Laptop(new[] { new TexRect(3000, 3000, 50, 50) });
            AvfxTableOverlay.Plan(avfx, LaptopInfo(avfx), mod.Laptop!.Surface.Rect, eq3, profile, out var r1);
            Check(r1 == OverlayRefusal.WindowCoversSurface, $"keep-out inside the window: {r1}");
            AvfxTableOverlay.Plan(avfx, LaptopInfo(avfx), new TexRect(2993, 2700, 387, 1020), eq3, OverlayProfile.Laptop(Array.Empty<TexRect>()), out var r2);
            Check(r2 == OverlayRefusal.NoWindow, $"1020 tall: {r2}");
            Check(ApplyFrames(mod, 3, out string e), "the table still animates " + e);
        });

        Add("LaptopNotOffered", LTag("A-6 E-10"), () =>
        {
            void NotOffered(string label, Func<TempMod> make, Action<TempMod>? edit = null)
            {
                using var mod = make();
                edit?.Invoke(mod);
                var site = mod.Scan();
                Check(site.LaptopProblem != null && !LaptopCard.Describe(site, false).AnimationAllowed, $"{label}: not offered ({site.LaptopProblem})");
            }
            NotOffered("other eq3 file", () => Fixtures.Pickles(withLaptop: true), m =>
            {
                File.Copy(m.Full(Fixtures.PicklesEq3), m.Full(@"common\7\eq3b.atex"));
                string meta = m.Full("meta.json");
                var root = JObject.Parse(File.ReadAllText(meta));
                var option = root["Groups"]!.SelectMany(g => g["Options"] as JArray ?? new JArray()).OfType<JObject>()
                    .First(o => o["Files"]?["vfx/alinasexy.avfx"] != null && o["Files"]?["vfx/atex/eq3.atex"] != null);
                option["Files"]!["vfx/atex/eq3.atex"] = @"common\7\eq3b.atex";
                File.WriteAllText(meta, root.ToString());
            });
            NotOffered("two eq3 particles", () => Fixtures.Pickles(withLaptop: true), m => foreachEffect(m, r =>
            {
                var tex = r.All("Tex").Select(t => t.Text()).ToList().FindIndex(t => t == "vfx/atex/eq3.atex");
                r.All("Ptcl").ElementAt(0).Child("TC1")!.Child("TLst")!.Data = new[] { (byte)tex };
            }));
            NotOffered("laptop item twice", () => Fixtures.Pickles(withLaptop: true), m => foreachEffect(m, r =>
            {
                var last = r.All("Emit").First().All("ItPr").Last();
                var items = new List<List<AvfxChunk>>();
                foreach (var c in last.Children!) { if (c.Name == "bEnb" || items.Count == 0) items.Add(new()); items[^1].Add(c); }
                var lap = items.First(i => BitConverter.ToInt32(i.First(c => c.Name == "TgtB").Data!) == LaptopParticle);
                last.Children.AddRange(lap.Select(c => c.Clone()));
            }));
            NotOffered("orphaned", () => Fixtures.Pickles(withLaptop: true), m => foreachEffect(m, r =>
            {
                int last = r.Children!.FindLastIndex(c => c.Name == "Tex");
                r.Children.Insert(last + 1, AvfxChunk.Leaf("Tex", AvfxChunk.TextBytes("vfx/atex/ppe_zzzz_1_00.atex")));
                r.SetInt("TxCn", r.ReadInt("TxCn") + 1);
            }));
            NotOffered("site problem", () => Fixtures.Pickles(withLaptop: true), m => foreachEffect(m, r => r.All("Ptcl").ElementAt(7).Children!.RemoveAll(c => c.Name == "Col")));
            NotOffered("no laptop target", () => Fixtures.Pickles());
        });

        Add("LaptopLidBadge", LTag("A-7 E-12 E-19"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            var eq3 = mod.Read(Fixtures.PicklesEq3);
            Check(ApplyLaptop(mod, 4, out string e), e);
            Check(mod.Read(Fixtures.PicklesEq3).AsSpan().SequenceEqual(eq3), "apply never writes eq3");
            var plan = AnimationLayers.PlanLaptop(mod.Scan(), eq3, out _)!.Plans.Values.First();
            var outside = AvfxModel.Parse(plan.OutsideModlData!, out _)!;
            var inside = AvfxModel.Parse(plan.ModlData, out _)!;
            bool InBadge((double X, double Y) p) => p.X > LidLogo.X && p.X < LidLogo.Right && p.Y > LidLogo.Y && p.Y < LidLogo.Bottom;
            Check(outside.Triangles.Any(t => new[] { t.A, t.B, t.C }.Select(i => MapPx(outside.Vertices[i], PanelUvMap.Laptop, 4096)).All(p => p.Y >= 3580)), "badge part in the outside model");
            Check(!inside.Vertices.Any(v => InBadge((LapWindow.X + Px(v, PanelUvMap.Laptop, 1, 1).X * 512, LapWindow.Y + Px(v, PanelUvMap.Laptop, 1, 1).Y * 1024))), "no inside vertex in the badge");
            var effects = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var frames = ReadState(mod).Laptop!.Frames.ToDictionary(f => f.Relative, f => f.Sha);
            var badge = new BgraImage(196, 196);
            Array.Fill(badge.Pixels, (byte)200);
            File.WriteAllBytes(mod.Full(Fixtures.PicklesEq3), TexPaste.PasteRect(mod.Read(Fixtures.PicklesEq3), LidLogo, badge, out _)!);
            Check(effects.All(kv => mod.Read(kv.Key).AsSpan().SequenceEqual(kv.Value)) && frames.All(kv => AvfxTableAnimation.Sha(mod.Read(kv.Key)) == kv.Value), "badge paste leaves frames and effects");
            var after = mod.Read(Fixtures.PicklesEq3);
            Check(RestoreSurface(mod, AnimatedSurface.Laptop, out e) && mod.Read(Fixtures.PicklesEq3).AsSpan().SequenceEqual(after), "restore never writes eq3 " + e);
        });

        Add("TableOnlyUnchanged", LTag("A-8 E-1"), () =>
        {
            using var plain = Fixtures.Pickles("plain");
            using var lap = Fixtures.Pickles("withlap", withLaptop: true);
            Check(ApplyFrames(plain, 6, out string e) && ApplyFrames(lap, 6, out e), e);
            Check(Fixtures.PicklesAvfx.All(r => plain.Read(r).AsSpan().SequenceEqual(lap.Read(r))), "effects equal with and without eq3");
            Check(StateText(plain) == StateText(lap) && !StateText(lap).Contains("\"Laptop\""), "state equal, no Laptop key");
            Check(plain.Host.Log.Select(c => (c.Kind, Path.GetFileName(c.Path))).SequenceEqual(lap.Host.Log.Select(c => (c.Kind, Path.GetFileName(c.Path)))), "same host calls");
            Check(Restore(plain, out _, out e, plain.NewHost()) && Restore(lap, out _, out e, lap.NewHost()), e);
            Check(Fixtures.PicklesAvfx.All(r => plain.Read(r).AsSpan().SequenceEqual(lap.Read(r))), "restored equal");
        });

        Add("CoverageMatchesBruteForce", LTag("A-8"), () =>
        {
            double Brute(List<(double X, double Y)[]> tris, TexRect rect)
            {
                int hit = 0;
                for (int y = rect.Y; y < rect.Bottom; y++)
                    for (int x = rect.X; x < rect.Right; x++)
                        foreach (var t in tris)
                            if (AvfxModel.Contains(t, x + 0.5, y + 0.5)) { hit++; break; }
                return (double)hit / (rect.Width * rect.Height);
            }
            var rng = new Random(12);
            for (int i = 0; i < 200; i++)
            {
                var rect = new TexRect(rng.Next(0, 50), rng.Next(0, 50), rng.Next(1, 40), rng.Next(1, 40));
                var tris = Enumerable.Range(0, rng.Next(1, 6)).Select(_ =>
                {
                    var p = new (double X, double Y)[3];
                    for (int k = 0; k < 3; k++) p[k] = (rng.NextDouble() * 100 - 5, rng.NextDouble() * 100 - 5);
                    if (rng.Next(10) == 0) { p[1] = p[0]; p[2] = p[0]; }
                    return p;
                }).ToList();
                if (Math.Abs(AvfxModel.Coverage(tris, rect) - Brute(tris, rect)) > 1e-12) { Check(false, $"set {i} differs"); return; }
            }
            Check(true, "200 random sets agree");
            using var mod = Fixtures.Pickles(withLaptop: true);
            var site = mod.Scan();
            var plan = AnimationLayers.PlanLaptop(site, mod.Read(Fixtures.PicklesEq3), out _)!.Plans.Values.First();
            Check(plan.Coverage == 1.0, "laptop coverage 1.0");
            Check(Small().Plan.Coverage is > 0.97 and < 0.99, "table coverage unchanged (98.1%)");
        });

        Add("TwoLayerStateShape", LTag("A-8 E-20"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            Check(ApplyFrames(mod, 6, out string e) && ApplyLaptop(mod, 4, out e, mod.NewHost()), e);
            string text = StateText(mod);
            var old = JsonConvert.DeserializeObject<OldState>(text, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Ignore })!;
            var st = AnimationState.Parse(System.Text.Encoding.UTF8.GetBytes(text));
            Check(old.Frames.Count == 6 && old.StartTicks.Count == 6 && old.FrameRect != null, "an older app reads the table part");
            Check(st.Laptop?.Frames.Count == 4 && st.Laptop.FrameRect == LapRectInWindow && st.Copy().Laptop?.Frames.Count == 4, "Laptop round-trips");
            mod.Host.KillAfter = IsAvfxWrite;
            ApplyLaptop(mod, 3, out _);
            var crashed = ReadState(mod);
            Check(RestoreSurface(mod, AnimatedSurface.Table, out e), "full restore " + e);
            Check(crashed.Laptop!.Frames.Concat(crashed.PendingFrames).All(f => !File.Exists(mod.Full(f.Relative))), "full restore deletes laptop frames");
        });

        Add("LaptopCap", LTag("A-9 E-16 E-29"), () =>
        {
            (bool Ok, string Error, bool FellBack, int Pastes) Run(TempMod mod, int n, int? max = null)
            {
                int pastes = 0;
                var host = mod.NewHost();
                bool ok = DjTableAnimationCore.ApplyLaptopOrStill(host, mod.Scan(host), Fixtures.Frames(n), Fixtures.Timeline(n, maxFrames: max ?? n), FitMode.Fill, null,
                    _ => { pastes++; return null; }, out _, out string e, out bool fell);
                return (ok, e, fell, pastes);
            }
            void HardError(TempMod mod, int n, string label)
            {
                var snap = mod.Snapshot();
                var r = Run(mod, n);
                Check(!r.Ok && r.Error.StartsWith(nameof(AnimationMessage.TooManyTextures)) && !r.FellBack && r.Pastes == 0 && mod.Snapshot().SequenceEqual(snap), $"{label}: hard error, no still ({r.Error})");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                HardError(mod, 49, "laptop 49");
                Check(Run(mod, 48).Ok, "laptop 48");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                Check(ApplyFrames(mod, 24, out _), "table 24");
                Check(mod.Scan().LaptopCapacity == 24, $"laptop capacity 24 ({mod.Scan().LaptopCapacity})");
                HardError(mod, 25, "laptop 25 beside table 24");
                Check(Run(mod, 24).Ok, "laptop 24");
                foreach (var rel in Fixtures.PicklesAvfx)
                {
                    var (p, t, i) = AvfxTableAnimation.Counts(mod.Read(rel), 0);
                    Check(p == 57 && t == 75 && i == 56 && AvfxChunk.Parse(mod.Read(rel)).ToBytes().AsSpan().SequenceEqual(mod.Read(rel)), $"{rel}: 57/75/56 ({p}/{t}/{i})");
                }
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                Check(ApplyFrames(mod, 47, out _), "table 47");
                var site = mod.Scan();
                Check(site.LaptopCapacity == 1 && site.LaptopProblem != null && !LaptopCard.Describe(site, false).AnimationAllowed, "1 left: laptop not offered");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                Check(ApplyLaptop(mod, 30, out _), "laptop 30");
                Check(mod.Scan().Capacity == 18, $"table capacity 18 ({mod.Scan().Capacity})");
                var snap = mod.Snapshot();
                Check(!ApplyFrames(mod, 19, out string e, mod.NewHost()) && e.StartsWith(nameof(AnimationMessage.TooManyTextures)) && mod.Snapshot().SequenceEqual(snap), "table 19: refused");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                PadParticles(mod, 11);
                Check(ApplyLaptop(mod, 30, out string e), "laptop 30 on 20 particles " + e);
                var site = mod.Scan();
                Check(site.Capacity == 7, $"table held to the tested size: 7 ({site.Capacity})");
                var snap = mod.Snapshot();
                Check(!ApplyFrames(mod, 8, out e, mod.NewHost()) && mod.Snapshot().SequenceEqual(snap), "table 8 refused");
                var host = mod.NewHost();
                var stale = mod.Scan(host);
                stale.Capacity = 8;
                Check(!DjTableAnimationCore.Apply(host, stale, Fixtures.Frames(8), Fixtures.Timeline(8), FitMode.Fill, null, out _, out e)
                    && e.StartsWith(nameof(AnimationMessage.TooManyTextures)) && mod.Snapshot().SequenceEqual(snap), "stale capacity: the composed re-check refuses " + e);
                Check(ApplyFrames(mod, 7, out e, mod.NewHost()), "table 7 " + e);
                Check(Fixtures.PicklesAvfx.All(r => AvfxTableAnimation.Counts(mod.Read(r), 0).Particles == 57), "57 particles");
                Check(DjTableAnimationCore.Status(mod.Scan(), AnimatedSurface.Laptop).Kind == AnimationStatusKind.Ours, "laptop still Ours");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                PadTextures(mod, 33);
                Check(ApplyFrames(mod, 10, out _), "table 10 on 60 textures");
                Check(mod.Scan().LaptopCapacity == 5, $"textures bind: 5 ({mod.Scan().LaptopCapacity})");
                HardError(mod, 6, "laptop 6");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                PadParticles(mod, 2);
                Check(mod.Scan().LaptopCapacity == 46, $"particles bind: 46 ({mod.Scan().LaptopCapacity})");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                PadItems(mod, 3);
                Check(mod.Scan().LaptopCapacity == 45, $"items bind: 45 ({mod.Scan().LaptopCapacity})");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                Check(ApplyFrames(mod, 20, out _), "table 20");
                var host = mod.NewHost();
                var stale = mod.Scan(host);
                stale.LaptopCapacity = 40;
                var snap = mod.Snapshot();
                bool ok = DjTableAnimationCore.Apply(host, stale, AnimatedSurface.Laptop, Fixtures.Frames(30), Fixtures.Timeline(30), FitMode.Fill, null, out _, out string e, out bool refused);
                Check(!ok && !refused && e.StartsWith(nameof(AnimationMessage.TooManyTextures)) && mod.Snapshot().SequenceEqual(snap), "stale laptop capacity: recount refuses " + e);
            }
            using (var moon = Fixtures.Moon(withLaptop: true))
            {
                var site = moon.Scan();
                Check(site.LaptopCapacity == 0 && site.LaptopProblem?.StartsWith("TestedSize") == true && !LaptopCard.Describe(site, false).AnimationAllowed, $"Moon: TestedSize ({site.LaptopProblem})");
            }
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                PadParticles(mod, 49);
                var site = mod.Scan();
                Check(site.LaptopProblem?.StartsWith("TestedSize") == true, $"58 particles: TestedSize ({site.LaptopProblem})");
            }
        });

        Add("LaptopRollback", LTag("A-10 E-8"), () =>
        {
            foreach (bool overLaptop in new[] { false, true })
                foreach (string fault in new[] { "frame", "edit", "avfx2" })
                {
                    using var mod = Fixtures.Pickles(withLaptop: true);
                    Check(ApplyFrames(mod, 4, out string e), "table " + e);
                    if (overLaptop) Check(ApplyLaptop(mod, 3, out e, mod.NewHost()), "laptop gen 1 " + e);
                    var effects = AvfxBytes(mod, Fixtures.PicklesAvfx);
                    var groups = mod.Groups();
                    string state = StateText(mod);
                    var host = mod.NewHost();
                    host.ThrowOn = fault switch
                    {
                        "frame" => c => IsFrameWrite(c) && c.Path.Contains("_lap"),
                        "edit" => c => c.Kind == CallKind.EditGroups && c.Index == 2,
                        _ => c => IsAvfxWrite(c) && host.Log.Count(IsAvfxWrite) == 2,
                    };
                    string label = $"{(overLaptop ? "over laptop" : "table only")}/{fault}";
                    Check(!ApplyLaptop(mod, 2, out _, host), $"{label}: fails");
                    Check(effects.All(kv => mod.Read(kv.Key).AsSpan().SequenceEqual(kv.Value)), $"{label}: effects as before");
                    Check(mod.Groups().Zip(groups).All(z => JToken.DeepEquals(z.First, z.Second)), $"{label}: groups as before");
                    var st = ReadState(mod);
                    var was = AnimationState.Parse(System.Text.Encoding.UTF8.GetBytes(state));
                    Check(st.Frames.Select(f => f.Relative).SequenceEqual(was.Frames.Select(f => f.Relative))
                        && (st.Laptop?.Frames.Count ?? 0) == (was.Laptop?.Frames.Count ?? 0), $"{label}: state as before");
                    Check(!Directory.GetFiles(mod.Full(DjAnimationScan.FrameFolder)).Any(f => f.Contains($"_{st.Generation}_lap")), $"{label}: new frames gone");
                    var site = mod.Scan(mod.NewHost());
                    Check(DjTableAnimationCore.Status(site, AnimatedSurface.Table).Kind == AnimationStatusKind.Ours
                        && DjTableAnimationCore.Status(site, AnimatedSurface.Laptop).Kind == (overLaptop ? AnimationStatusKind.Ours : AnimationStatusKind.None), $"{label}: still Ours");
                }
        });

        Add("LaptopCrashRecovery", LTag("A-10 A-4 E-7 E-25 E-26"), () =>
        {
            using (var mod = Fixtures.Pickles(withLaptop: true))
            {
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                Check(ApplyFrames(mod, 4, out string e), e);
                mod.Host.KillAfter = IsAvfxWrite;
                ApplyLaptop(mod, 3, out _);
                var site = mod.Scan(mod.NewHost());
                Check(DjTableAnimationCore.Status(site).Kind == AnimationStatusKind.Interrupted && !LaptopCard.Describe(site, true).AnimationAllowed, "Interrupted, laptop not offered");
                var snap = mod.Snapshot();
                Check(RestoreSurface(mod, AnimatedSurface.Laptop, out e) && mod.Snapshot().SequenceEqual(snap), "laptop card restore leaves everything");
                Check(RestoreSurface(mod, AnimatedSurface.Table, out e), "table card restore " + e);
                CheckPristine(mod, before, groups, "after crash");
            }
            foreach (bool earlierLaptop in new[] { false, true })
            {
                using var mod = Fixtures.Pickles(withLaptop: true);
                Check(ApplyFrames(mod, 4, out string e), e);
                if (earlierLaptop) Check(ApplyLaptop(mod, 8, out e, mod.NewHost()), "laptop 8 " + e);
                var oldLaptop = ReadState(mod).Laptop;
                mod.NewHost().KillAfter = IsAvfxWrite;
                ApplyLaptop(mod, 3, out _, mod.Host);
                var pending = ReadState(mod).PendingFrames;
                Check(pending.Count == 3, "laptop frames pending");
                Check(ApplyFrames(mod, 6, out e, mod.NewHost()), "table re-apply after the crash " + e);
                var st = ReadState(mod);
                Check((st.Laptop?.Frames.Count ?? 0) == (oldLaptop?.Frames.Count ?? 0) && pending.All(f => !File.Exists(mod.Full(f.Relative))), $"earlier laptop={earlierLaptop}: old laptop carried, pending deleted");
                CheckComposed(mod, $"recovered ({earlierLaptop})");
            }
        });

        Add("MoonLaptop", LTag("E-17"), () =>
        {
            using var moon = Fixtures.Moon(withLaptop: true);
            var effects = AvfxBytes(moon, new[] { Fixtures.MoonCat, Fixtures.MoonCat2 });
            var site = moon.Scan();
            Check(site.LaptopProblem?.StartsWith("TestedSize") == true && !LaptopCard.Describe(site, false).AnimationAllowed, "not offered");
            int pastes = 0;
            Check(DjTableAnimationCore.ApplyLaptopOrStill(moon.Host, site, Fixtures.Frames(6), Fixtures.Timeline(6), FitMode.Fill, null,
                _ => { pastes++; return null; }, out _, out _, out bool fell) && fell && pastes == 1, "falls back to one still");
            Check(effects.All(kv => moon.Read(kv.Key).AsSpan().SequenceEqual(kv.Value)) && DjTableAnimationCore.Status(moon.Scan()).Kind == AnimationStatusKind.Author, "effects shipped, table Author");

            site = moon.Scan();
            var eq3 = moon.Read(Fixtures.MoonEq3);
            var route = AnimationLayers.PlanLaptop(site, eq3, out string refusal);
            Check(route != null && route.Crop.Length == 524_368, $"Moon crop {route?.Crop.Length} ({refusal})");
            byte[] Atlas(AnimatedSurface s) => s == AnimatedSurface.Table ? moon.Read(moon.Table.Relative) : eq3;
            var lap = new AnimationLayer(Enumerable.Range(0, 6).Select(k => $"vfx/atex/ppe_m_1_lap{k:D2}.atex").ToList(), Fixtures.Timeline(6), route?.RectInWindow);
            var table = new AnimationLayer(Enumerable.Range(0, 6).Select(k => $"vfx/atex/ppe_m_1_{k:D2}.atex").ToList(), Fixtures.Timeline(6), null);
            foreach (var avfx in site.Animatable)
            {
                var a = AnimationLayers.Compose(avfx, site, null, lap, Atlas, out string p1);
                var b = AnimationLayers.Compose(avfx, site, table, lap, Atlas, out string p2);
                Check(a != null && b != null && AvfxChunk.Parse(b).ToBytes().AsSpan().SequenceEqual(b), $"{avfx.Relative}: composes offline ({p1} {p2})");
                var inPlace = AvfxTableAnimation.Build(avfx.Baseline!, avfx.Inspect!.Info!, table.GamePaths, table.Timeline, out _)!;
                var ip = AvfxChunk.Parse(inPlace).All("Ptcl").ToList();
                var both = AvfxChunk.Parse(b!).All("Ptcl").ToList();
                Check(avfx.Inspect.Info.AnimationSet.All(i => i == LaptopParticle || Fixtures.ParticleBytes(both[i]).AsSpan().SequenceEqual(Fixtures.ParticleBytes(ip[i]))), $"{avfx.Relative}: InPlace particles kept");
            }
        });

        Add("LaptopManifestCheck", LTag("E-22"), () =>
        {
            using var mod = Fixtures.Pickles(withLaptop: true);
            Check(ApplyFrames(mod, 3, out string e) && ApplyLaptop(mod, 2, out e, mod.NewHost()), e);
            var state = ReadState(mod);
            var keys = DjTableAnimationCore.LiveFrameKeys(state);
            Check(keys.Count == 5, "both surfaces' keys");
            var site = mod.Scan();
            var relatives = new HashSet<string>(site.Animatable.Select(a => a.Relative), StringComparer.OrdinalIgnoreCase);
            Check(DjTableAnimationCore.VerifyManifest(mod.Groups(), relatives, keys) == null, "all present");
            var groups = mod.Groups();
            var option = groups.SelectMany(g => g["Options"] as JArray ?? new JArray()).OfType<JObject>()
                .First(o => o["Files"]?[state.Laptop!.Frames[0].GamePath] != null);
            ((JObject)option["Files"]!).Remove(state.Laptop!.Frames[0].GamePath);
            Check(DjTableAnimationCore.VerifyManifest(groups, relatives, keys)?.StartsWith(nameof(AnimationMessage.ManifestLost)) == true, "a missing laptop key is ManifestLost");
        });
    }

    /// <summary>state.json as 2.11.7 reads it.</summary>
    private sealed class OldState
    {
        public List<AnimationFrame> Frames { get; set; } = new();
        public List<int> StartTicks { get; set; } = new();
        public TexRect? FrameRect { get; set; }
    }
}
