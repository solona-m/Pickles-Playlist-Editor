using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;

namespace AnimHarness;

/// <summary>The app writes overlay frames (#12 step 2), falling back to full-atlas copies.</summary>
internal static partial class Program
{
    private const string OApp = "gh12-overlay-app";
    private const int CropBytes = 349_600;

    private static string AppTag(string ids) =>
        string.Join(" ", ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(id => id[0] == 'A'
            ? $"AC-{OApp}-{id[2..]}" : $"EDGE-{OApp}-{id[2..]}"));

    /// <summary>Runs <paramref name="body"/> on the full-atlas route, as the previous version wrote.</summary>
    private static T WithoutOverlay<T>(Func<T> body) => WithPlanner((_, _) => null, body);

    private static T WithPlanner<T>(Func<DjTableAnimationSite, byte[], OverlayRoute?> planner, Func<T> body)
    {
        var old = DjTableAnimationCore.OverlayPlanner;
        DjTableAnimationCore.OverlayPlanner = planner;
        try { return body(); }
        finally { DjTableAnimationCore.OverlayPlanner = old; }
    }

    /// <summary>The reasons the core reported while <paramref name="body"/> ran, and its failures.</summary>
    private static (T Result, List<string> Skipped, int Failed) Watching<T>(Func<T> body)
    {
        var skipped = new List<string>();
        int failed = 0;
        var oldSkip = DjTableAnimationCore.OverlaySkipped;
        var oldFail = DjTableAnimationCore.Failed;
        DjTableAnimationCore.OverlaySkipped = skipped.Add;
        DjTableAnimationCore.Failed = _ => failed++;
        try { return (body(), skipped, failed); }
        finally { DjTableAnimationCore.OverlaySkipped = oldSkip; DjTableAnimationCore.Failed = oldFail; }
    }

    private static string StateText(TempMod mod) => File.ReadAllText(DjAnimationScan.StatePath(mod.StateFolder));

    private static List<string> FrameKeys(int gen, int n) =>
        Enumerable.Range(0, n).Select(k => $"vfx/atex/ppe_t0000001_{gen}_{k:D2}.atex").ToList();

    private static byte[] FrameFile(TempMod mod, int gen, int k) => mod.Read($@"ppe-table-animation\t0000001_{gen}_{k:D2}.atex");

    /// <summary>Every output of an apply, by SHA: effects, frames and the state file.</summary>
    private static Dictionary<string, string> Outputs(TempMod mod)
    {
        var result = new Dictionary<string, string>();
        foreach (var rel in Fixtures.PicklesAvfx) result[rel] = AvfxTableAnimation.Sha(mod.Read(rel));
        foreach (var f in Directory.GetFiles(mod.Full(DjAnimationScan.FrameFolder)).OrderBy(x => x))
            result[Path.GetFileName(f)] = AvfxTableAnimation.Sha(File.ReadAllBytes(f));
        result["state"] = AvfxTableAnimation.Sha(File.ReadAllBytes(DjAnimationScan.StatePath(mod.StateFolder)));
        return result;
    }

    private static Dictionary<string, string> FullAtlasOutputs(int n)
    {
        using var mod = Fixtures.Pickles();
        string e = string.Empty;
        Check(WithoutOverlay(() => ApplyFrames(mod, n, out e)), "full-atlas reference apply " + e);
        return Outputs(mod);
    }

    private static bool SameOutputs(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static void PatchGlow(TempMod mod)
    {
        var white = new BgraImage(8, 8);
        Array.Fill(white.Pixels, (byte)255);
        File.WriteAllBytes(mod.Full(mod.Table.Relative), TexPaste.PasteRect(mod.Read(mod.Table.Relative), new TexRect(20, 20, 8, 8), white, out _)!);
    }

    private static void BreakUvSt(TempMod mod, string rel) =>
        EditAvfx(mod, rel, root => { var d = PanelOf(root).Child("UvSt")!.Data!; d[^1] ^= 1; });

    private static void PadModels(TempMod mod, string rel, int count) =>
        EditAvfx(mod, rel, root =>
        {
            int last = root.Children!.FindLastIndex(c => c.Name == "Modl");
            var copy = root.Children[last];
            for (int i = root.All("Modl").Count(); i < count; i++) root.Children.Insert(last + 1, copy.Clone());
            root.SetInt("MdCn", count);
        });

    /// <summary>Checks the apply wrote today's full-atlas frames and effects for these baselines.</summary>
    private static void CheckFullAtlas(TempMod mod, Dictionary<string, byte[]> baselines, byte[] atlas, int n, string label)
    {
        var keys = FrameKeys(1, n);
        foreach (var (rel, baseline) in baselines)
        {
            var built = AvfxTableAnimation.Build(baseline, Fixtures.Inspect(baseline), keys, Fixtures.Timeline(n), out _);
            Check(built != null && mod.Read(rel).AsSpan().SequenceEqual(built), $"{label}: {rel} is the full-atlas build");
        }
        var frames = Fixtures.Frames(n);
        for (int k = 0; k < n; k++)
        {
            var expect = TexPaste.PasteRect(atlas, mod.Table.Surface.Rect, mod.Table.Surface.ToTextureSpace(frames[k], FitMode.Fill), out _)!;
            Check(FrameFile(mod, 1, k).AsSpan().SequenceEqual(expect), $"{label}: frame {k} is the full-atlas copy");
        }
        Check(!StateText(mod).Contains("FrameRect"), $"{label}: no FrameRect");
    }

    private static void CheckFullAtlasGen2(TempMod mod, Dictionary<string, byte[]> baselines, int n)
    {
        var keys = FrameKeys(2, n);
        foreach (var (rel, baseline) in baselines)
        {
            var built = AvfxTableAnimation.Build(baseline, Fixtures.Inspect(baseline), keys, Fixtures.Timeline(n), out _);
            Check(built != null && mod.Read(rel).AsSpan().SequenceEqual(built), $"gen 2: {rel} is the full-atlas build");
        }
        var files = Directory.GetFiles(mod.Full(DjAnimationScan.FrameFolder));
        long atlas = mod.Read(mod.Table.Relative).Length;
        Check(files.Length == n && files.All(f => Path.GetFileName(f).StartsWith("t0000001_2_") && new FileInfo(f).Length == atlas), "gen-2 full-atlas files only");
        Check(!StateText(mod).Contains("FrameRect"), "no FrameRect");
    }

    private static void RegisterOverlayApp()
    {
        Add("AppOverlayApplyPickles", AppTag("A-1 E-15"), () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var atlas = mod.Read(mod.Table.Relative);
            Check(ApplyFrames(mod, 12, out string error), "apply " + error);
            var keys = FrameKeys(1, 12);
            var plans = new List<OverlayPlan>();
            foreach (var (rel, baseline) in before)
            {
                var info = Fixtures.Inspect(baseline);
                var plan = AvfxTableOverlay.Plan(baseline, info, mod.Table.Surface.Rect, atlas, PanelUvMap.Calibrated, out _)!;
                plans.Add(plan);
                var built = AvfxTableOverlay.Build(baseline, info, plan, keys, Fixtures.Timeline(12), out _, out _, FramePlan.MaxFrames);
                Check(built != null && mod.Read(rel).AsSpan().SequenceEqual(built), $"{rel} is the overlay build");
            }
            Check(plans.All(p => p.Window == new TexRect(128, 256, 512, 512) && p.RectInWindow == new TexRect(37, 142, 440, 289)), "plans share the window");
            for (int k = 0; k < 12; k++)
            {
                var f = FrameFile(mod, 1, k);
                var h = TexHeader.TryRead(f, out _);
                Check(f.Length == CropBytes && h is { Width: 512, Height: 512 } && h.Format == TexHeader.TryRead(atlas, out _)!.Format, $"frame {k} is a 512x512 crop");
            }
            var site = mod.Scan();
            Check(site.State!.FrameRect == new TexRect(37, 142, 440, 289), $"FrameRect ({site.State.FrameRect})");
            foreach (var group in mod.Groups().Where(g => g["Name"]?.ToString() == "DJ Table"))
                foreach (var option in ((JArray)group["Options"]!).Where(o => o["Files"]?["vfx/alinasexy.avfx"] != null))
                    Check(keys.All(k => option["Files"]?[k]?.ToString() is { } file && File.Exists(mod.Full(file))), $"{option["Name"]} maps every frame");
            var status = DjTableAnimationCore.Status(site);
            Check(status.Kind == AnimationStatusKind.Ours && status.Frames == 12, $"Ours(12) ({status})");
            Check(TableCard.Describe(status, site.Mode, site.HasState, false).StateKey == TableText.YourAnimation, "card view");
        });

        Add("AppSummaryOverlaySize", AppTag("A-2 E-16"), () =>
        {
            var plan = FramePlan.Plan(Enumerable.Repeat(4, 120).ToList(), 48);
            using (var mod = Fixtures.Pickles())
            {
                long atlas = mod.Read(mod.Table.Relative).Length;
                var sum = DjTableAnimationCore.Summarise(mod.Scan(), 48, plan.Timeline, plan.Thinned);
                Check(sum.AddedBytes == 48L * CropBytes && (sum.AddedBytes + 524288) / 1048576 == 16, $"Pickles 48 frames: 16 MB ({sum.AddedBytes})");
                var off = WithoutOverlay(() => DjTableAnimationCore.Summarise(mod.Scan(), 48, plan.Timeline, plan.Thinned));
                Check(off.AddedBytes == 48 * atlas && (off.AddedBytes + 524288) / 1048576 == 64, $"full-atlas: 64 MB ({off.AddedBytes})");
                var (thrown, skipped, _) = Watching(() => WithPlanner((_, _) => throw new InvalidOperationException("boom"),
                    () => DjTableAnimationCore.Summarise(mod.Scan(), 1, Fixtures.Timeline(1), false)));
                Check(thrown.AddedBytes == atlas && skipped.Count == 0, "planner throws: atlas size, nothing logged");
                PatchGlow(mod);
                var glow = DjTableAnimationCore.Summarise(mod.Scan(), 48, plan.Timeline, plan.Thinned);
                Check(glow.AddedBytes == 48 * mod.Read(mod.Table.Relative).Length, "glow outside the panel: atlas size");
            }
            using (var moon = Fixtures.Moon())
            {
                var sum = DjTableAnimationCore.Summarise(moon.Scan(), 48, plan.Timeline, plan.Thinned);
                Check(sum.AddedBytes == 48 * moon.Read(moon.Table.Relative).Length, "Moon: atlas size");
            }
        });

        Add("AppPreviewMatchesWritten", AppTag("A-3"), () =>
        {
            foreach (var fit in new[] { FitMode.Fill, FitMode.Fit })
            {
                using var mod = Fixtures.Pickles();
                var frames = Fixtures.Frames(6);
                var previews = DjTableAnimationCore.PreviewFrames(mod.Read(mod.Table.Relative), mod.Table.Surface, frames, fit);
                var site = mod.Scan();
                Check(DjTableAnimationCore.Apply(mod.Host, site, frames, Fixtures.Timeline(6), fit, null, out _, out string e), "apply " + e);
                var state = ReadState(mod);
                for (int k = 0; k < 6; k++)
                {
                    var read = DjTableAnimationCore.ReadFrame(FrameFile(mod, 1, k), state, mod.Table.Surface);
                    Check(read != null && Same(read, previews[k]), $"{fit} frame {k}: preview = written");
                }
            }
        });

        Add("AppCardReadsBack", AppTag("A-4 E-10 E-11"), () =>
        {
            using (var mod = Fixtures.Pickles())
            {
                Check(ApplyFrames(mod, 3, out string e), e);
                var state = ReadState(mod);
                Check(state.FrameRect == new TexRect(37, 142, 440, 289), $"overlay state ({state.FrameRect})");
                var s = mod.Table.Surface;
                var img = DjTableAnimationCore.ReadFrame(FrameFile(mod, 1, 0), state, s);
                Check(img != null && img.Width == s.UprightWidth && img.Height == s.UprightHeight, "overlay frame reads back upright");
                Check(state.Copy().FrameRect == state.FrameRect, "Copy keeps FrameRect");
                var json = JObject.Parse(StateText(mod));
                json.Remove("FrameRect");
                Check(AnimationState.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToString())).FrameRect == null, "state without FrameRect parses as null");
                Check(DjTableAnimationCore.ReadFrame(FrameFile(mod, 1, 0)[..200], state, s) == null, "truncated frame: null");
            }
            using (var mod = Fixtures.Pickles())
            {
                string e = string.Empty;
                Check(WithoutOverlay(() => ApplyFrames(mod, 3, out e)), e);
                Check(!StateText(mod).Contains("FrameRect"), "full-atlas state has no FrameRect");
                var state = ReadState(mod);
                var s = mod.Table.Surface;
                var frame = FrameFile(mod, 1, 1);
                var expect = ImageOps.Rotate(TexPaste.ReadRect(frame, s.Rect, out _)!, s.QuarterTurns);
                var got = DjTableAnimationCore.ReadFrame(frame, state, s);
                Check(got != null && Same(got, expect), "full-atlas frame reads back as before");
            }
        });

        Add("AppFallbackRefusals", AppTag("A-5 E-2"), () =>
        {
            foreach (var (variant, expect) in new[] { ("uvst", "UvSetNotCalibrated"), ("glow", "GlowOutsidePanel"), ("models", "TooManyModels") })
            {
                using var mod = Fixtures.Pickles();
                foreach (var rel in Fixtures.PicklesAvfx)
                {
                    if (variant == "uvst") BreakUvSt(mod, rel);
                    if (variant == "models") PadModels(mod, rel, 254);
                }
                if (variant == "glow") PatchGlow(mod);
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                var atlas = mod.Read(mod.Table.Relative);
                string e = string.Empty;
                var (ok, skipped, failed) = Watching(() => ApplyFrames(mod, 4, out e));
                Check(ok, $"{variant}: applies " + e);
                Check(failed == 0 && skipped.Count == 1 && skipped[0].StartsWith(expect), $"{variant}: logged {expect} ({string.Join("; ", skipped)})");
                CheckFullAtlas(mod, before, atlas, 4, variant);
                using (var twin = Fixtures.Pickles("twin"))
                {
                    foreach (var (rel, bytes) in before) File.WriteAllBytes(twin.Full(rel), bytes);
                    File.WriteAllBytes(twin.Full(twin.Table.Relative), atlas);
                    Check(WithoutOverlay(() => ApplyFrames(twin, 4, out e)) && SameOutputs(Outputs(mod), Outputs(twin)),
                        $"{variant}: output = the full-atlas run, state included");
                }
                Check(Restore(mod, out _, out e), e);
                CheckPristine(mod, before, groups, variant);
            }
        });

        Add("AppFallbackOneEffect", AppTag("A-5 E-3"), () =>
        {
            using var mod = Fixtures.Pickles();
            BreakUvSt(mod, @"common\59\medium.avfx");
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var atlas = mod.Read(mod.Table.Relative);
            string e = string.Empty;
            var (ok, skipped, failed) = Watching(() => ApplyFrames(mod, 4, out e));
            Check(ok, e);
            Check(failed == 0 && skipped.Count == 1 && skipped[0].StartsWith("UvSetNotCalibrated") && skipped[0].Contains("medium.avfx"),
                $"logged the one refusal ({string.Join("; ", skipped)})");
            CheckFullAtlas(mod, before, atlas, 4, "one refuses");
        });

        Add("AppFallbackOnException", AppTag("A-5 E-4 E-5"), () =>
        {
            var reference = FullAtlasOutputs(4);
            var cases = new (string Name, Func<DjTableAnimationSite, byte[], OverlayRoute?> Planner)[]
            {
                ("planner throws", (_, _) => throw new InvalidOperationException("boom")),
                ("no plans", (site, atlas) => DjTableAnimationCore.PlanOverlay(site, atlas, out _)! with { Plans = new Dictionary<string, OverlayPlan>() }),
                ("encode throws", (site, atlas) => DjTableAnimationCore.PlanOverlay(site, atlas, out _)! with { Crop = null! }),
            };
            foreach (var (name, planner) in cases)
            {
                using var mod = Fixtures.Pickles();
                string e = string.Empty;
                var (ok, skipped, failed) = Watching(() => WithPlanner(planner, () => ApplyFrames(mod, 4, out e)));
                Check(ok, $"{name}: applies " + e);
                Check(failed == 0 && skipped.Count == 1 && skipped[0].StartsWith("exception:"), $"{name}: logged as a skip, not a failure");
                Check(SameOutputs(Outputs(mod), reference), $"{name}: output = the full-atlas run, state included");
                Check(!StateText(mod).Contains("FrameRect"), $"{name}: no FrameRect");
            }
        });

        Add("AppFallbackOnEncodeFailure", AppTag("A-5 E-5"), () =>
        {
            var reference = FullAtlasOutputs(4);
            using var mod = Fixtures.Pickles();
            string e = string.Empty;
            var (ok, skipped, failed) = Watching(() => WithPlanner(
                (site, atlas) => DjTableAnimationCore.PlanOverlay(site, atlas, out _)! with { Crop = atlas[..80] },
                () => ApplyFrames(mod, 4, out e)));
            Check(ok, "applies " + e);
            Check(failed == 0 && skipped.Count == 1 && skipped[0].StartsWith("encode:"), $"logged encode, not a failure ({string.Join("; ", skipped)})");
            Check(SameOutputs(Outputs(mod), reference), "output = the full-atlas run, state included");
            Check(!StateText(mod).Contains("FrameRect"), "no FrameRect");
        });

        Add("AppBuildGuardUnreachable", AppTag("E-18"), () =>
        {
            foreach (bool crowded in new[] { false, true })
            {
                using var mod = Fixtures.Pickles();
                if (crowded)
                    foreach (var rel in Fixtures.PicklesAvfx)
                        EditAvfx(mod, rel, root =>
                        {
                            int last = root.Children!.FindLastIndex(c => c.Name == "Tex");
                            for (int i = 0; i < 33; i++) root.Children.Insert(last + 1, AvfxChunk.Leaf("Tex", AvfxChunk.TextBytes($"vfx/pad{i}.atex")));
                            root.SetInt("TxCn", root.ReadInt("TxCn") + 33);
                        });
                var site = mod.Scan();
                var atlas = mod.Read(mod.Table.Relative);
                var route = DjTableAnimationCore.PlanOverlay(site, atlas, out string refusal);
                Check(route != null, $"crowded={crowded}: plans ({refusal})");
                if (route == null) continue;
                var keys = Enumerable.Range(0, site.Capacity).Select(k => $"vfx/atex/ppe_x_1_{k:D2}.atex").ToList();
                foreach (var avfx in site.Animatable)
                {
                    var info = avfx.Inspect!.Info!;
                    Check(AvfxTableOverlay.Capacity(info, FramePlan.MaxFrames) >= site.Capacity, $"crowded={crowded}: {avfx.Relative} overlay capacity covers the site");
                    var built = AvfxTableOverlay.Build(avfx.Baseline!, info, route.Plans[DjAnimationScan.Normalise(avfx.Relative)], keys,
                        Fixtures.Timeline(site.Capacity), out var r, out string p, FramePlan.MaxFrames);
                    Check(built != null, $"crowded={crowded}: {avfx.Relative} builds at capacity ({r} {p})");
                }
            }
        });

        Add("AppFullAtlasReapplyAndRollback", AppTag("A-5 A-7 A-10"), () =>
        {
            // The full-atlas route on an Append table still re-applies and rolls back cleanly.
            using (var mod = Fixtures.Pickles())
            {
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                string e = string.Empty;
                Check(WithoutOverlay(() => ApplyFrames(mod, 10, out e)), "gen 1 " + e);
                Check(WithoutOverlay(() => ApplyFrames(mod, 5, out e, mod.NewHost())), "gen 2 " + e);
                CheckFullAtlasGen2(mod, before, 5);
                Check(Restore(mod, out _, out e, mod.NewHost()), e);
                CheckPristine(mod, before, groups, "full-atlas restore");
            }
            foreach (bool live in new[] { false, true })
            {
                using var mod = Fixtures.Pickles();
                string e = string.Empty;
                if (live) Check(WithoutOverlay(() => ApplyFrames(mod, 4, out e)), "gen 1 " + e);
                var snap = mod.Snapshot();
                var groups = mod.Groups();
                var host = mod.NewHost();
                host.ThrowOn = c => IsAvfxWrite(c) && host.Log.Count(IsAvfxWrite) == 2;
                Check(!WithoutOverlay(() => ApplyFrames(mod, 3, out e, host)), $"live={live}: fails");
                var now = mod.Snapshot();
                bool Mod(string f) => !f.StartsWith(mod.StateFolder) && !f.EndsWith(".json");
                Check(snap.Where(kv => Mod(kv.Key)).All(kv => now.GetValueOrDefault(kv.Key) == kv.Value)
                    && now.Keys.Where(Mod).All(snap.ContainsKey), $"live={live}: mod files as before");
                Check(mod.Groups().Zip(groups).All(z => JToken.DeepEquals(z.First, z.Second)), $"live={live}: groups as before");
                if (live) Check(StatusOf(mod) == AnimationStatusKind.Ours && ReadState(mod).FrameRect == null, "gen 1 still ours, full-atlas");
                else Check(!Directory.Exists(mod.StateFolder), "no state folder");
            }
        });

        Add("AppInPlaceUnchanged", AppTag("A-6 E-1"), () =>
        {
            using var mod = Fixtures.Moon();
            int calls = 0;
            string e = string.Empty;
            bool ok = WithPlanner((site, atlas) => { calls++; return DjTableAnimationCore.PlanOverlay(site, atlas, out _); },
                () => ApplyFrames(mod, 48, out e));
            Check(ok, "Moon applies " + e);
            Check(calls == 0, $"planner never called ({calls})");
            Check(!StateText(mod).Contains("FrameRect"), "no FrameRect");
        });

        Add("AppRestoreOverlay", AppTag("A-7"), () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var groups = mod.Groups();
            Check(ApplyFrames(mod, 10, out string e), e);
            Check(ReadState(mod).FrameRect != null, "overlay route");
            Check(Restore(mod, out var warnings, out e) && warnings.Count == 0, "restore, no warnings " + e);
            CheckPristine(mod, before, groups, "restore");
        });

        Add("AppReapplyOverOverlay", AppTag("A-7 E-7"), () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var atlas = mod.Read(mod.Table.Relative);
            Check(ApplyFrames(mod, 10, out string e), "gen 1 " + e);
            Check(ApplyFrames(mod, 5, out e, mod.NewHost()), "gen 2 " + e);
            var keys = FrameKeys(2, 5);
            foreach (var (rel, baseline) in before)
            {
                var info = Fixtures.Inspect(baseline);
                var plan = AvfxTableOverlay.Plan(baseline, info, mod.Table.Surface.Rect, atlas, PanelUvMap.Calibrated, out _)!;
                var built = AvfxTableOverlay.Build(baseline, info, plan, keys, Fixtures.Timeline(5), out _, out _, FramePlan.MaxFrames);
                Check(built != null && mod.Read(rel).AsSpan().SequenceEqual(built), $"{rel} = overlay build with gen-2 keys");
            }
            var files = Directory.GetFiles(mod.Full(DjAnimationScan.FrameFolder));
            Check(files.Length == 5 && files.All(f => Path.GetFileName(f).StartsWith("t0000001_2_") && new FileInfo(f).Length == CropBytes), "5 gen-2 crops only");
            Check(!mod.Groups().Any(g => g.ToString().Contains("_1_0")), "no gen-1 keys");
        });

        Add("AppOverlayOverPreviousVersion", AppTag("A-7 E-8"), () =>
        {
            using var live = Fixtures.LivePicklesAnimation();
            var sources = new List<(string Name, Func<TempMod?> Make)>
            {
                ("synthetic full-atlas", () =>
                {
                    var m = Fixtures.Pickles("prev");
                    string e = string.Empty;
                    Check(WithoutOverlay(() => ApplyFrames(m, 12, out e)), "previous-version apply " + e);
                    return m;
                }),
            };
            if (live != null) sources.Add(("live", () => null));
            foreach (var (name, make) in sources)
            {
                using var made = make();
                var mod = made ?? live!;
                var old = ReadState(mod);
                var host = mod.NewHost(() => "t0000009");
                Check(ApplyFrames(mod, 6, out string e, host), $"{name}: overlay apply " + e);
                var now = ReadState(mod);
                Check(now.FrameRect != null, $"{name}: overlay route");
                Check(old.Frames.All(f => !File.Exists(mod.Full(f.Relative))), $"{name}: old frames gone");
                Check(old.Avfx.All(a => now.Find(a.Relative)?.OriginalSha == a.OriginalSha), $"{name}: no re-baseline");
                Check(now.Frames.All(f => new FileInfo(mod.Full(f.Relative)).Length == CropBytes), $"{name}: crops");
                Check(RestoreToShipped(mod, out e, mod.NewHost()), e);
                foreach (var a in old.Avfx) Check(AvfxTableAnimation.Sha(mod.Read(a.Relative)) == a.OriginalSha, $"{name}: {a.Relative} restored byte-exact");
            }
        });

        Add("AppFallbackOverOverlay", AppTag("A-7 E-9"), () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var groups = mod.Groups();
            Check(ApplyFrames(mod, 6, out string e), "gen 1 " + e);
            var gen1 = ReadState(mod);
            PatchGlow(mod);
            long atlas = mod.Read(mod.Table.Relative).Length;
            var (ok, skipped, failed) = Watching(() => ApplyFrames(mod, 4, out e, mod.NewHost()));
            Check(ok, "gen 2 " + e);
            Check(failed == 0 && skipped.Count == 1 && skipped[0].StartsWith("GlowOutsidePanel"), $"logged GlowOutsidePanel ({string.Join("; ", skipped)})");
            var gen2 = ReadState(mod);
            Check(gen1.FrameRect != null && gen2.FrameRect == null && !StateText(mod).Contains("FrameRect"), "FrameRect gone");
            Check(gen2.Frames.All(f => new FileInfo(mod.Full(f.Relative)).Length == atlas), "full-atlas frames");
            Check(gen1.Frames.All(f => !File.Exists(mod.Full(f.Relative))), "gen-1 files gone");
            Check(Restore(mod, out _, out e, mod.NewHost()), e);
            CheckPristine(mod, before, groups, "restore");
        });

        Add("AppRestoreIgnoresFrameRect", AppTag("A-7 E-10"), () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var groups = mod.Groups();
            Check(ApplyFrames(mod, 4, out string e), e);
            var json = JObject.Parse(StateText(mod));
            Check(json.Remove("FrameRect"), "FrameRect removed");
            File.WriteAllText(DjAnimationScan.StatePath(mod.StateFolder), json.ToString());
            Check(Restore(mod, out _, out e, mod.NewHost()), e);
            CheckPristine(mod, before, groups, "restore");
        });

        Add("AppCapStays48", AppTag("A-8 E-14"), () =>
        {
            Check(FramePlan.MaxFrames == 48, "live cap 48");
            Check(FramePlan.Plan(Enumerable.Repeat(4, 120).ToList(), 48).Timeline.StartTicks.Count == 48, "120 frames thin to 48");
            using (var mod = Fixtures.Pickles())
            {
                var site = mod.Scan();
                Check(site.Capacity == 48, $"capacity 48 ({site.Capacity})");
                var snap = mod.Snapshot();
                bool ok = DjTableAnimationCore.Apply(mod.Host, site, Fixtures.Frames(49), Fixtures.Timeline(49, maxFrames: 49), FitMode.Fill, null, out _, out string e);
                Check(!ok && e.StartsWith(nameof(AnimationMessage.TooManyTextures)) && mod.Snapshot().SequenceEqual(snap), $"49 refused, nothing written ({e})");
                Check(ApplyFrames(mod, 48, out e, mod.NewHost()), e);
                var files = Directory.GetFiles(mod.Full(DjAnimationScan.FrameFolder));
                Check(files.Length == 48 && files.All(f => new FileInfo(f).Length == CropBytes), "48 crops");
            }
            using (var mod = Fixtures.Pickles())
            {
                foreach (var rel in Fixtures.PicklesAvfx)
                    EditAvfx(mod, rel, root =>
                    {
                        int last = root.Children!.FindLastIndex(c => c.Name == "Tex");
                        for (int i = 0; i < 33; i++) root.Children.Insert(last + 1, AvfxChunk.Leaf("Tex", AvfxChunk.TextBytes($"vfx/pad{i}.atex")));
                        root.SetInt("TxCn", root.ReadInt("TxCn") + 33);
                    });
                Check(mod.Scan().Capacity == 15, "crowded capacity 15 (60 textures, tested size 75)");
                Check(!ApplyFrames(mod, 16, out string e), "16 refused");
                Check(ApplyFrames(mod, 15, out e, mod.NewHost()) && ReadState(mod).FrameRect != null, "15 through the overlay " + e);
            }
        });

        Add("AppStillOnOverlay", AppTag("A-9 E-17"), () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var groups = mod.Groups();
            var atlas = mod.Read(mod.Table.Relative);
            Check(ApplyFrames(mod, 6, out string e), e);
            foreach (var (rel, baseline) in before)
            {
                var plan = AvfxTableOverlay.Plan(baseline, Fixtures.Inspect(baseline), mod.Table.Surface.Rect, atlas, PanelUvMap.Calibrated, out _)!;
                var root = AvfxChunk.Parse(mod.Read(rel));
                Check(PanelOf(root).Child("Data")!.Child("MdNo")!.Data![0] == 9 && root.All("Modl").ElementAt(9).Data!.AsSpan().SequenceEqual(plan.OutsideModlData),
                    $"{rel}: panel draws the shell part");
            }
            var site = mod.Scan();
            var still = Fixtures.Frames(1)[0];
            Check(DjTableAnimationCore.PasteThenRestore(mod.Host, site, () => { PasteStill(mod, still); return null; }, out _, out e), e);
            CheckPristine(mod, before, groups, "after still");
            var rect = TexPaste.ReadRect(mod.Read(mod.Table.Relative), mod.Table.Surface.Rect, out _)!;
            Check(Fixtures.Psnr(rect, mod.Table.Surface.ToTextureSpace(still, FitMode.Fill)) > 30, "eq55 holds the still");
        });

        Add("AppOverlayKeepsPastedStill", AppTag("E-6"), () =>
        {
            using var mod = Fixtures.Pickles();
            var still = Fixtures.Frames(1, 200, 90)[0];
            PasteStill(mod, still);
            Check(ApplyFrames(mod, 3, out string e), e);
            var state = ReadState(mod);
            Check(state.FrameRect != null, "overlay route over a pasted still");
            var frame0 = TexPaste.ReadRect(FrameFile(mod, 1, 0), state.FrameRect!.Value, out _)!;
            Check(Fixtures.Psnr(frame0, mod.Table.Surface.ToTextureSpace(Fixtures.Frames(3)[0], FitMode.Fill)) > 30, "frame 0 shows the frame, not the still");
            Check(Restore(mod, out _, out e, mod.NewHost()), e);
            var rect = TexPaste.ReadRect(mod.Read(mod.Table.Relative), mod.Table.Surface.Rect, out _)!;
            Check(Fixtures.Psnr(rect, mod.Table.Surface.ToTextureSpace(still, FitMode.Fill)) > 30, "eq55 still holds the still");
        });

        Add("AppOverlayCrash", AppTag("A-10 E-12"), () =>
        {
            using (var mod = Fixtures.Pickles())
            {
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                mod.Host.KillAfter = IsAvfxWrite;
                ApplyFrames(mod, 4, out _);
                var st = ReadState(mod);
                Check(st.PendingFrames.All(f => new FileInfo(mod.Full(f.Relative)).Length == CropBytes), "crops written before the crash");
                Check(StatusOf(mod) == AnimationStatusKind.Interrupted, "Interrupted");
                Check(Restore(mod, out _, out string e, mod.NewHost()), e);
                CheckPristine(mod, before, groups, "after crash");
            }
            using (var mod = Fixtures.Pickles())
            {
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                mod.Host.KillAfter = IsAvfxWrite;
                ApplyFrames(mod, 4, out _);
                var st = ReadState(mod);
                Check(ApplyFrames(mod, 3, out string e, mod.NewHost()), "apply after crash " + e);
                var st2 = ReadState(mod);
                Check(st.Avfx.All(a => st2.Find(a.Relative)?.OriginalSha == a.OriginalSha) && st2.FrameRect != null, "no re-baseline, overlay");
                Check(Restore(mod, out _, out e, mod.NewHost()), e);
                CheckPristine(mod, before, groups, "second run");
            }
        });

        Add("AppOverlayRollback", AppTag("A-10 E-13"), () =>
        {
            foreach (bool live in new[] { false, true })
                foreach (string fault in new[] { "edit", "avfx2" })
                {
                    using var mod = Fixtures.Pickles();
                    if (live) Check(ApplyFrames(mod, 4, out _) && ReadState(mod).FrameRect != null, "gen 1 overlay");
                    var snap = mod.Snapshot();
                    var groups = mod.Groups();
                    var host = mod.NewHost();
                    host.ThrowOn = fault == "edit"
                        ? c => c.Kind == CallKind.EditGroups && c.Index == 2
                        : c => IsAvfxWrite(c) && host.Log.Count(IsAvfxWrite) == 2;
                    string label = $"{(live ? "live" : "clean")}/{fault}";
                    Check(!ApplyFrames(mod, 3, out _, host), $"{label}: fails");
                    var now = mod.Snapshot();
                    bool Mod(string f) => !f.StartsWith(mod.StateFolder) && !f.EndsWith(".json");
                    Check(snap.Where(kv => Mod(kv.Key)).All(kv => now.GetValueOrDefault(kv.Key) == kv.Value)
                        && now.Keys.Where(Mod).All(snap.ContainsKey), $"{label}: mod files as before");
                    Check(mod.Groups().Zip(groups).All(z => JToken.DeepEquals(z.First, z.Second)), $"{label}: groups as before");
                    if (!live) Check(!Directory.Exists(mod.StateFolder), $"{label}: no state folder");
                    else Check(StatusOf(mod) == AnimationStatusKind.Ours && ReadState(mod).FrameRect != null, $"{label}: gen 1 still ours, with its FrameRect");
                }
        });
    }
}
