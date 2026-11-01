using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;

namespace AnimHarness;

internal static partial class Program
{
    private static readonly string[] MoonAvfx = { Fixtures.MoonCat, Fixtures.MoonCat2 };

    private static void RegisterApply()
    {
        Add("PicklesApplyEndToEnd", "AC-animated-table-2 EDGE-animated-table-11 EDGE-animated-table-21 " + AppTag("A-1"), () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            Check(ApplyFrames(mod, 12, out string error), "apply: " + error);

            foreach (var rel in Fixtures.PicklesAvfx)
            {
                var bytes = mod.Read(rel);
                var root = AvfxChunk.Parse(bytes);
                Check(root.ReadInt("PrCn") == 21 && root.ReadInt("TxCn") == 39 && root.ReadInt("MdCn") == 10, $"{rel} counts 21/39, MdCn 10");
                var particles = root.All("Ptcl").ToList();
                var texs = root.All("Tex").Select(t => t.Text()).ToList();
                for (int k = 0; k < 12; k++)
                {
                    int tlst = AvfxTableAnimation.Tlst(particles[9 + k]);
                    Check(tlst == 27 + k && texs[tlst] == $"vfx/atex/ppe_t0000001_1_{k:D2}.atex"
                        && particles[9 + k].Child("Data")!.Child("MdNo")!.Data![0] == 8, $"{rel} clone {k}");
                }
                var pristine = Fixtures.Particles(before[rel]);
                var panel = particles[7].Clone();
                Check(panel.Child("Data")!.Child("MdNo")!.Data![0] == 9, $"{rel} panel draws the shell part");
                panel.Child("Data")!.Child("MdNo")!.Data![0] = 6;
                Check(Fixtures.ParticleBytes(panel).AsSpan().SequenceEqual(Fixtures.ParticleBytes(pristine[7])), $"{rel} panel = shipped but MdNo");
                foreach (int i in new[] { 0, 1, 2, 3, 4, 5, 6, 8 })
                    Check(Fixtures.ParticleBytes(particles[i]).AsSpan().SequenceEqual(Fixtures.ParticleBytes(pristine[i])), $"{rel} particle {i} untouched");

                var emit = root.All("Emit").First();
                var itprs = emit.All("ItPr").ToList();
                Check(itprs.Count == 20 && emit.ReadInt("PrCn") == 20, $"{rel} 20 items");
                var targets = Fixtures.Targets(bytes);
                Check(Enumerable.Range(9, 12).All(targets.Contains), $"{rel} targets 9..20");
                bool cumulative = itprs.Select((c, i) => c.Children!.Count == 26 * (i + 1)).All(x => x);
                Check(cumulative, $"{rel} cumulative ItPr");
            }

            var site = mod.Scan();
            foreach (var group in mod.Groups().Where(g => g["Name"]?.ToString() == "DJ Table"))
                foreach (var option in ((JArray)group["Options"]!).Where(o => o["Files"]?["vfx/alinasexy.avfx"] != null))
                    for (int k = 0; k < 12; k++)
                    {
                        string? file = option["Files"]?[$"vfx/atex/ppe_t0000001_1_{k:D2}.atex"]?.ToString();
                        Check(file != null && File.Exists(mod.Full(file)), $"{option["Name"]} maps frame {k}");
                    }

            var atlas = mod.Read(mod.Table.Relative);
            var frames = Fixtures.Frames(12);
            var state = site.State!;
            Check(state.FrameRect == new TexRect(37, 142, 440, 289), $"FrameRect ({state.FrameRect})");
            var window = TexCrop.Window(1024, 1024, mod.Table.Surface.Rect)!.Value;
            for (int k = 0; k < 12; k += 5)
            {
                var frame = mod.Read($@"ppe-table-animation\t0000001_1_{k:D2}.atex");
                Check(frame.Length == 349_600, $"frame {k} is a 512x512 crop ({frame.Length})");
                var rect = TexPaste.ReadRect(frame, state.FrameRect ?? mod.Table.Surface.Rect, out _)!;
                var expect = mod.Table.Surface.ToTextureSpace(frames[k], FitMode.Fill);
                Check(Fixtures.Psnr(rect, expect) > 30, $"frame {k} PSNR {Fixtures.Psnr(rect, expect):F1}");
                var corner = new TexRect(0, 0, 64, 64);
                var atlasCorner = new TexRect(window.X, window.Y, 64, 64);
                Check(Fixtures.Psnr(TexPaste.ReadRect(frame, corner, out _)!, TexPaste.ReadRect(atlas, atlasCorner, out _)!) >= 99, $"frame {k} outside rect");
            }

            var status = DjTableAnimationCore.Status(site);
            Check(status.Kind == AnimationStatusKind.Ours && status.Frames == 12, $"Ours(12) ({status})");
            var view = TableCard.Describe(status, site.Mode, site.HasState, false);
            Check(view.PlaysFrames && view.RestoreEnabled && view.StateKey == TableText.YourAnimation, "card view");
        });

        Add("MoonApplyEndToEnd", "AC-animated-table-3 EDGE-animated-table-11 EDGE-animated-table-21 EDGE-animated-table-34", () =>
        {
            using var mod = Fixtures.Moon();
            var before = AvfxBytes(mod, MoonAvfx.Append(Fixtures.MoonPlain));
            var plainGroups = mod.Groups();
            var site = mod.Scan();
            var info = site.Animatable.First().Inspect!.Info!;
            Check(info.PanelParticle == 55 && info.Mode == AnimationMode.InPlace && info.Capacity == 48, $"P=55 InPlace cap 48 ({info.PanelParticle} {info.Mode} {info.Capacity})");
            Check(info.AnimationSet.SequenceEqual(new[] { 7 }.Concat(Enumerable.Range(9, 48))), "set {7, 9..56}");
            Check(info.Slots.Count == 49, $"49 slots ({info.Slots.Count})");
            Check(site.Animatable.Count() == 2, "two cat effects");

            Check(ApplyFrames(mod, 48, out string error), "apply: " + error);
            foreach (var rel in MoonAvfx)
            {
                var bytes = mod.Read(rel);
                var root = AvfxChunk.Parse(bytes);
                var orig = AvfxChunk.Parse(before[rel]);
                Check(root.ReadInt("PrCn") == 57 && root.ReadInt("TxCn") == 74 && root.All("Emit").First().ReadInt("PrCn") == 56, $"{rel} counts");
                var p = root.All("Ptcl").ToList();
                var o = orig.All("Ptcl").ToList();
                Check(p.Zip(o).All(z => AvfxTableAnimation.Tlst(z.First) == AvfxTableAnimation.Tlst(z.Second)), $"{rel} TLst unchanged");
                var texs = root.All("Tex").Select(t => t.Text()).ToList();
                var ours = new[] { 24 }.Concat(Enumerable.Range(26, 47)).ToList();
                Check(ours.All(i => texs[i].StartsWith("vfx/atex/ppe_")), $"{rel} Tex 24, 26..72 ours");
                Check(texs.Distinct().Count(t => t.StartsWith("vfx/atex/ppe_")) == 48, $"{rel} 48 distinct frame paths (37/38 by index)");
                Check(texs[73] == "vfx/atex/eq56.atex", $"{rel} Tex 73 eq56");
                Check(Fixtures.AlphaKeys(p[56]).AsSpan().SequenceEqual(AvfxTableAnimation.HiddenKeys()), $"{rel} particle 56 hidden");
                foreach (int i in new[] { 0, 1, 2, 3, 4, 5, 6, 8 })
                    Check(Fixtures.ParticleBytes(p[i]).AsSpan().SequenceEqual(Fixtures.ParticleBytes(o[i])), $"{rel} particle {i} untouched");
                var e = root.All("Emit").ToList();
                var eo = orig.All("Emit").ToList();
                Check(e.Zip(eo).All(z => z.First.All("ItPr").Zip(z.Second.All("ItPr")).All(w =>
                    Fixtures.ParticleBytes(w.First).AsSpan().SequenceEqual(Fixtures.ParticleBytes(w.Second)))), $"{rel} ItPr identical");
            }
            Check(mod.Read(Fixtures.MoonPlain).AsSpan().SequenceEqual(before[Fixtures.MoonPlain]), "plain effect untouched");
            foreach (var group in mod.Groups())
                foreach (var option in ((JArray?)group["Options"] ?? new JArray()).OfType<JObject>())
                {
                    int keys = ((JObject?)option["Files"])?.Properties().Count(pr => pr.Name.StartsWith("vfx/atex/ppe_")) ?? 0;
                    string name = option["Name"]?.ToString() ?? "";
                    if (name.Contains("Cat Picture")) Check(keys == 48, $"{group["Name"]}/{name} 48 keys");
                    else Check(keys == 0, $"{group["Name"]}/{name} no keys");
                }
            var status = DjTableAnimationCore.Status(mod.Scan());
            Check(status.Kind == AnimationStatusKind.Ours && status.Frames == 48, $"Ours(48) ({status})");
        });

        Add("RestoreIsExact", "AC-animated-table-4 EDGE-animated-table-21" + " " + AppTag("A-7"), () =>
        {
            foreach (var make in new Func<TempMod>[] { () => Fixtures.Pickles(), () => Fixtures.Moon() })
            {
                using var mod = make();
                var rels = mod.Scan().Avfx.Where(a => a.CurrentSha != null).Select(a => a.Relative).ToList();
                var before = AvfxBytes(mod, rels);
                var groups = mod.Groups();
                Check(ApplyFrames(mod, 10, out string error), "apply: " + error);
                Check(Restore(mod, out var warnings, out error), "restore: " + error);
                Check(warnings.Count == 0, "no warnings: " + string.Join(", ", warnings));
                CheckPristine(mod, before, groups, Path.GetFileName(mod.Root));
            }
        });

        Add("ReapplyReplaces", "AC-animated-table-5 EDGE-animated-table-22 " + AppTag("A-7 E-7"), () =>
        {
            using var mod = Fixtures.Pickles();
            Check(ApplyFrames(mod, 10, out string error), "gen 1: " + error);
            var gen1 = AnimationState.Parse(File.ReadAllBytes(DjAnimationScan.StatePath(mod.StateFolder)));
            var host = mod.NewHost();
            var states = new List<AnimationState>();
            var site = mod.Scan(host);
            // Capture each state write and each manifest edit as it happens.
            int editCalls = 0;
            bool gen1KeysAfterEdit1 = false;
            host.KillAfter = call =>
            {
                if (call.Kind == CallKind.Write && call.Path.EndsWith("state.json"))
                    states.Add(AnimationState.Parse(File.ReadAllBytes(call.Path)));
                if (call.Kind == CallKind.EditGroups && ++editCalls == 2)
                    gen1KeysAfterEdit1 = gen1.Frames.All(f => mod.Groups().Any(g => g.ToString().Contains(f.GamePath)));
                return false;
            };
            Check(DjTableAnimationCore.Apply(host, site, Fixtures.Frames(5), Fixtures.Timeline(5), FitMode.Fill, null, out _, out error), "gen 2: " + error);
            Check(!host.Log.Any(c => c.Kind == CallKind.Write && gen1.Frames.Any(f => c.Path.EndsWith(f.Relative))), "no write to gen-1 files");
            var applying = states[0];
            Check(applying.Phase == AnimationState.Applying && applying.Frames.Select(f => f.Relative).SequenceEqual(gen1.Frames.Select(f => f.Relative))
                && applying.Avfx.All(a => gen1.Find(a.Relative)!.AppliedSha == a.AppliedSha), "step 4 carries gen-1 Frames and AppliedSha");
            Check(gen1KeysAfterEdit1, "gen-1 keys still present after the first edit");
            var applied = states[1];
            Check(applied.Phase == AnimationState.Applied && applied.StaleFrames.Select(f => f.Relative).OrderBy(x => x)
                .SequenceEqual(gen1.Frames.Select(f => f.Relative).OrderBy(x => x)), "step 8 StaleFrames = gen-1 Frames");
            int appliedAt = host.Log.FindIndex(c => c.Kind == CallKind.Write && c.Path.EndsWith("state.json") && host.Log.IndexOf(c) > 0
                && host.Log.Take(host.Log.IndexOf(c)).Count(x => x.Kind == CallKind.Write && x.Path.EndsWith("state.json")) == 1);
            int firstDelete = host.Log.FindIndex(c => c.Kind == CallKind.Delete);
            int lastEdit = host.Log.FindLastIndex(c => c.Kind == CallKind.EditGroups);
            Check(firstDelete > appliedAt && lastEdit > appliedAt, "prune and deletes after Applied");

            var final = mod.Scan();
            var info = final.Animatable.First();
            var oplan = AvfxTableOverlay.Plan(info.Baseline!, info.Inspect!.Info!, mod.Table.Surface.Rect, mod.Read(mod.Table.Relative), PanelUvMap.Calibrated, out _)!;
            var expected = AvfxTableOverlay.Build(info.Baseline!, info.Inspect!.Info!, oplan,
                Enumerable.Range(0, 5).Select(k => $"vfx/atex/ppe_t0000001_2_{k:D2}.atex").ToList(), Fixtures.Timeline(5), out _, out _, FramePlan.MaxFrames);
            Check(expected != null && mod.Read(info.Relative).AsSpan().SequenceEqual(expected), "avfx == overlay Build(pristine, 5)");
            Check(applying.FrameRect == gen1.FrameRect && gen1.FrameRect != null && applied.FrameRect != null, "FrameRect carried at step 4, set at step 8");
            var files = Directory.GetFiles(mod.Full(DjAnimationScan.FrameFolder));
            Check(files.Length == 5 && files.All(f => Path.GetFileName(f).StartsWith("t0000001_2_")), "5 gen-2 files only");
            Check(!mod.Groups().Any(g => g.ToString().Contains("_1_0")), "no gen-1 keys");
        });

        Add("PreviewFramesMatchFit", "AC-animated-table-1 " + AppTag("A-2"), () =>
        {
            using var mod = Fixtures.Pickles();
            var snap = mod.Snapshot();
            var frames = Fixtures.Frames(6);
            var atlas = mod.Read(mod.Table.Relative);
            var preview = DjTableAnimationCore.PreviewFrames(atlas, mod.Table.Surface, frames, FitMode.Fill);
            var s = mod.Table.Surface;
            Check(preview.Count == 6 && preview.All(f => f.Width == s.UprightWidth && f.Height == s.UprightHeight), "6 frames at upright size");
            for (int k = 0; k < 6; k++)
            {
                var fitted = ImageOps.FitTo(frames[k], s.UprightWidth, s.UprightHeight, FitMode.Fill);
                Check(Fixtures.Psnr(preview[k], fitted) > 30, $"frame {k} PSNR");
            }
            var site = mod.Scan();
            var plan = FramePlan.Plan(Enumerable.Repeat(4, 120).ToList(), 48);
            var sum = DjTableAnimationCore.Summarise(site, 48, plan.Timeline, plan.Thinned);
            Check(sum.Frames == 48 && Math.Abs(sum.Seconds - plan.Timeline.LoopTicks / 30.0) < 1e-9
                && sum.AddedBytes == 48 * 349_600 && sum.CapNote, $"summary ({sum.AddedBytes})");
            Check(mod.Snapshot().SequenceEqual(snap), "nothing written");
        });

        Add("RefitChangesFrames", "EDGE-animated-table-19", () =>
        {
            using var mod = Fixtures.Pickles();
            var frames = Fixtures.Frames(2, 300, 60);
            var atlas = mod.Read(mod.Table.Relative);
            var fill = DjTableAnimationCore.PreviewFrames(atlas, mod.Table.Surface, frames, FitMode.Fill);
            var fit = DjTableAnimationCore.PreviewFrames(atlas, mod.Table.Surface, frames, FitMode.Fit);
            Check(fill.Count == 2 && fit.Count == 2 && Fixtures.Psnr(fill[0], fit[0]) < 30, "Fill and Fit differ");
            var s = mod.Table.Surface;
            Check(Fixtures.Psnr(fit[0], ImageOps.FitTo(frames[0], s.UprightWidth, s.UprightHeight, FitMode.Fit)) > 30, "Fit matches its input");
        });

        Add("ParallelEqualsSequential", "EDGE-animated-table-24", () =>
        {
            foreach (var make in new Func<TempMod>[] { () => Fixtures.Pickles(), () => Fixtures.Moon() })
            {
                using var mod = make();
                var atlas = mod.Read(mod.Table.Relative);
                var frames = Fixtures.Frames(4);
                var s = mod.Table.Surface;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var seq = frames.Select(f => TexPaste.PasteRect(atlas, s.Rect, s.ToTextureSpace(f, FitMode.Fill), out _)!).ToList();
                double ms = watch.Elapsed.TotalMilliseconds / 4;
                var par = new byte[4][];
                Parallel.For(0, 4, k => par[k] = TexPaste.PasteRect(atlas, s.Rect, s.ToTextureSpace(frames[k], FitMode.Fill), out _)!);
                Check(seq.Zip(par).All(z => z.First.AsSpan().SequenceEqual(z.Second)), $"{Path.GetFileName(mod.Root)} parallel == sequential");
                Console.WriteLine($"   {Path.GetFileName(mod.Root)}: {ms:F0} ms/frame");
            }
        });

        Add("StillOnAuthorAnimation", "AC-animated-table-8", () =>
        {
            using var mod = Fixtures.Moon();
            var site = mod.Scan();
            Check(DjTableAnimationCore.Status(site).Kind == AnimationStatusKind.Author, "Author");
            var view = TableCard.Describe(DjTableAnimationCore.Status(site), site.Mode, site.HasState, false);
            Check(view.StillRoute == StillRoute.OneFrameAnimation, "routed as a one-frame animation");
            Check(DjTableAnimationCore.Apply(mod.Host, site, Fixtures.Frames(1), new FrameTimeline(new[] { 0 }, 1), FitMode.Fill, null, out _, out string error), error);
            var root = AvfxChunk.Parse(mod.Read(Fixtures.MoonCat));
            var p = root.All("Ptcl").ToList();
            var a7 = p[7].Child("Col")!.Child("A")!;
            Check(a7.Child("Keys")!.Data!.Length == 16 && BitConverter.ToUInt16(a7.Child("Keys")!.Data!, 2) == 1
                && a7.ReadInt("BvPr") == 0 && a7.ReadInt("BvPo") == 0, "particle 7 static (0,1,1.0)");
            Check(Enumerable.Range(9, 48).All(i => Fixtures.AlphaKeys(p[i]).AsSpan().SequenceEqual(AvfxTableAnimation.HiddenKeys())), "others hidden");
            Check(root.ReadInt("PrCn") == 57 && root.ReadInt("TxCn") == 74, "counts 57/74");
            var after = mod.Scan();
            var st = DjTableAnimationCore.Status(after);
            var v = TableCard.Describe(st, after.Mode, after.HasState, false);
            Check(st.Kind == AnimationStatusKind.Ours && st.Frames == 1 && !v.PlaysFrames && v.StateKey == TableText.YourStill, "Ours(1) YourStill");
        });

        Add("StillOnOursMoon", "AC-animated-table-8", () =>
        {
            using var mod = Fixtures.Moon();
            var eq55 = mod.Read(mod.Table.Relative);
            Check(ApplyFrames(mod, 48, out string error), error);
            var site = mod.Scan();
            Check(TableCard.Describe(DjTableAnimationCore.Status(site), site.Mode, site.HasState, false).StillRoute == StillRoute.OneFrameAnimation, "route");
            Check(DjTableAnimationCore.Apply(mod.Host, site, Fixtures.Frames(1), new FrameTimeline(new[] { 0 }, 1), FitMode.Fill, null, out _, out error), error);
            var after = mod.Scan();
            var entry = after.Animatable.First(a => a.Relative == Fixtures.MoonCat);
            var expect = AvfxTableAnimation.Build(entry.Baseline!, entry.Inspect!.Info!, new[] { "vfx/atex/ppe_t0000001_2_00.atex" }, new FrameTimeline(new[] { 0 }, 1), out _);
            Check(expect != null && mod.Read(Fixtures.MoonCat).AsSpan().SequenceEqual(expect), "avfx == Build(pristine, 1)");
            var root = AvfxChunk.Parse(mod.Read(Fixtures.MoonCat));
            Check(root.ReadInt("PrCn") == 57 && root.ReadInt("TxCn") == 74, "57/74");
            foreach (var g in mod.Groups())
                foreach (var o in ((JArray?)g["Options"] ?? new JArray()).OfType<JObject>().Where(o => o["Name"]!.ToString().Contains("Cat")))
                    Check(((JObject)o["Files"]!).Properties().Count(pr => pr.Name.StartsWith("vfx/atex/ppe_")) == 1, "one key");
            Check(Directory.GetFiles(mod.Full(DjAnimationScan.FrameFolder)).Length == 1, "no gen-1 file");
            var st = DjTableAnimationCore.Status(after);
            Check(st.Kind == AnimationStatusKind.Ours && st.Frames == 1, "Ours(1)");
            Check(mod.Read(mod.Table.Relative).AsSpan().SequenceEqual(eq55), "eq55 untouched");
        });

        Add("StillOnOurs", "AC-animated-table-8" + " " + AppTag("A-9"), () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var groups = mod.Groups();
            var eq55 = mod.Read(mod.Table.Relative);
            Check(ApplyFrames(mod, 6, out string error), error);
            var site = mod.Scan();
            Check(TableCard.Describe(DjTableAnimationCore.Status(site), site.Mode, site.HasState, false).StillRoute == StillRoute.PasteThenRestore, "route");
            var still = Fixtures.Frames(1)[0];
            Check(DjTableAnimationCore.PasteThenRestore(mod.Host, site, () => { PasteStill(mod, still); return null; }, out _, out error), error);
            CheckPristine(mod, before, groups, "after still");
            var rect = TexPaste.ReadRect(mod.Read(mod.Table.Relative), mod.Table.Surface.Rect, out _)!;
            Check(Fixtures.Psnr(rect, mod.Table.Surface.ToTextureSpace(still, FitMode.Fill)) > 30, "eq55 holds the still");

        });

        Add("UnanimatableRefused", "AC-animated-table-10 EDGE-animated-table-9", () =>
        {
            void Expect(TempMod mod, string label)
            {
                var snap = mod.Snapshot();
                var site = mod.Scan();
                Check(site.Problem != null, $"{label}: problem ({site.Problem})");
                Check(!ApplyFrames(mod, 4, out _, site: site), $"{label}: apply refused");
                Check(mod.Snapshot().SequenceEqual(snap), $"{label}: unchanged");
                var v = TableCard.Describe(DjTableAnimationCore.Status(site), site.Mode, site.HasState, false);
                Check(!v.AnimationAllowed && v.TableChangeAllowed && !v.RestoreEnabled && v.StillRoute == StillRoute.Existing, $"{label}: card");
            }

            using (var mod = Fixtures.Pickles())
            {
                foreach (var rel in Fixtures.PicklesAvfx)
                    EditAvfx(mod, rel, root => root.All("Ptcl").ElementAt(7).Child("TC1")!.Child("TLst")!.Data = new byte[] { 0xFF });
                Expect(mod, "no TLst");
            }
            using (var mod = Fixtures.Pickles())
            {
                foreach (var rel in Fixtures.PicklesAvfx) EditAvfx(mod, rel, root => Retarget(root, 0, 0, 7));
                Expect(mod, "two items");
            }
            using (var mod = Fixtures.Pickles())
            {
                MoveToDefaultData(mod);
                Expect(mod, "default data");
            }
            using (var mod = Fixtures.Pickles())
            {
                MapLalaToOtherAtlas(mod);
                Expect(mod, "mismatched atlas");
            }
            using (var mod = Fixtures.Moon())
            {
                foreach (var rel in MoonAvfx)
                    EditAvfx(mod, rel, root =>
                    {
                        // No item spawns any set member, so none is a usable slot.
                        var items = new[] { 7 }.Concat(Enumerable.Range(9, 48)).Select(i => ItemOf(root, i)).ToList();
                        foreach (int item in items) Retarget(root, 0, item, 0);
                    });
                Expect(mod, "zero slots");
                Check(mod.Scan().Problem?.StartsWith("NoUsableSlot") == true, "zero slots: reason");
            }
        });

        Add("DefaultDataRefused", "EDGE-animated-table-10", () =>
        {
            using var mod = Fixtures.Pickles();
            MoveToDefaultData(mod);
            Check(mod.Scan().Problem?.StartsWith("DefaultData") == true, "default data problem");
        });

        Add("SharedAvfxOnce", "EDGE-animated-table-11" + " " + AppTag("E-15"), () =>
        {
            using var mod = Fixtures.Pickles();
            var meta = JObject.Parse(File.ReadAllText(mod.Full("meta.json")));
            var table = meta["Groups"]!.First(g => g["Name"]?.ToString() == "DJ Table");
            var opts = table["Options"]!.Where(o => o["Files"]?["vfx/alinasexy.avfx"] != null).ToList();
            opts[0]["Files"]!["vfx/alinasexy.avfx"] = @"common\59\alinasexy.avfx";
            File.WriteAllText(mod.Full("meta.json"), meta.ToString());
            Check(ApplyFrames(mod, 3, out string error), error);
            int writes = mod.Host.Log.Count(c => c.Kind == CallKind.Write && c.Path.EndsWith(@"common\59\alinasexy.avfx"));
            Check(writes == 1, $"shared effect written once ({writes})");
            var options = mod.Groups().First(g => g["Name"]?.ToString() == "DJ Table")["Options"]!.Where(o => o["Files"]?["vfx/alinasexy.avfx"] != null).ToList();
            Check(options.All(o => o["Files"]!["vfx/atex/ppe_t0000001_1_00.atex"] != null), "every option keyed");
        });

        Add("CollisionRefused", "EDGE-animated-table-12", () =>
        {
            using (var mod = Fixtures.Pickles())
            {
                Directory.CreateDirectory(mod.Full(DjAnimationScan.FrameFolder));
                File.WriteAllBytes(mod.Full(@"ppe-table-animation\t0000001_1_00.atex"), new byte[] { 1 });
                var snap = mod.Snapshot();
                Check(!ApplyFrames(mod, 3, out string error) && error.StartsWith("Collision"), "planted file refused: " + error);
                Check(mod.Snapshot().SequenceEqual(snap), "unchanged");
            }
            using (var mod = Fixtures.Pickles())
            {
                var meta = JObject.Parse(File.ReadAllText(mod.Full("meta.json")));
                meta["Groups"]![0]!["Options"]![0]!["Files"]!["vfx/atex/ppe_t0000001_1_00.atex"] = "elsewhere.atex";
                File.WriteAllText(mod.Full("meta.json"), meta.ToString());
                Check(!ApplyFrames(mod, 3, out string error) && error.StartsWith("Collision"), "planted key refused: " + error);
            }
        });

        Add("SolonaLayout", "EDGE-animated-table-25", () =>
        {
            using var mod = Fixtures.Pickles();
            var meta = File.ReadAllText(mod.Full("meta.json")).Replace("\"vfx/atex/eq55.atex\"", "\"vfx/atex/eq56.atex\"");
            File.WriteAllText(mod.Full("meta.json"), meta);
            foreach (var rel in Fixtures.PicklesAvfx)
                EditAvfx(mod, rel, root => root.All("Tex").ElementAt(26).Data = AvfxChunk.TextBytes("vfx/atex/eq56.atex"));
            var site = mod.Scan();
            Check(site.Problem == null && site.Animatable.Count() == 4 && site.Animatable.All(a => a.Inspect!.Info!.PanelParticle == 7), $"P found via eq56 ({site.Problem})");
        });

        Add("TokensDiffer", "EDGE-animated-table-26", () =>
        {
            using var a = Fixtures.Pickles("a");
            using var b = Fixtures.Pickles("b");
            a.NewHost(DjTableAnimationCore.RandomToken);
            b.NewHost(DjTableAnimationCore.RandomToken);
            Check(ApplyFrames(a, 3, out string e1) && ApplyFrames(b, 3, out string e2), "both applied");
            var sa = AnimationState.Parse(File.ReadAllBytes(DjAnimationScan.StatePath(a.StateFolder)));
            var sb = AnimationState.Parse(File.ReadAllBytes(DjAnimationScan.StatePath(b.StateFolder)));
            Check(sa.Frames.Select(f => f.GamePath).Intersect(sb.Frames.Select(f => f.GamePath)).Count() == 0, "disjoint paths");
            Check(System.Text.RegularExpressions.Regex.IsMatch(sa.ModToken, "^[0-9a-f]{8}$"), "token format");
            a.NewHost(DjTableAnimationCore.RandomToken);
            Check(ApplyFrames(a, 2, out _), "re-apply");
            var sa2 = AnimationState.Parse(File.ReadAllBytes(DjAnimationScan.StatePath(a.StateFolder)));
            Check(sa2.ModToken == sa.ModToken, "token kept");
        });

        Add("AuthorCapacityCap", "EDGE-animated-table-27", () =>
        {
            using var mod = Fixtures.Moon();
            foreach (var rel in MoonAvfx)
                EditAvfx(mod, rel, root =>
                {
                    var parts = root.All("Ptcl").ToList();
                    foreach (int i in Enumerable.Range(30, 25).Append(56)) MakeStatic(parts[i]);
                });
            var site = mod.Scan();
            var info = site.Animatable.First().Inspect!.Info!;
            Check(info.AnimationSet.SequenceEqual(new[] { 7 }.Concat(Enumerable.Range(9, 21)).Append(55)) && site.Capacity == 23, $"set and capacity 23 ({site.Capacity})");
            var plan = FramePlan.Plan(Enumerable.Repeat(10, 48).ToList(), site.Capacity);
            Check(plan.KeptSourceIndices.Count == 23 && plan.Thinned && plan.Timeline.LoopTicks == 144, "planned to 23, loop kept");
        });

        Add("LoopingNonPanelUntouched", "EDGE-animated-table-28", () =>
        {
            using var mod = Fixtures.Moon();
            foreach (var rel in MoonAvfx)
                EditAvfx(mod, rel, root =>
                {
                    var parts = root.All("Ptcl").ToList();
                    var a = parts[10].Child("Col")!.Child("A")!.Clone();
                    var col8 = parts[8].Child("Col")!;
                    int idx = col8.Children!.FindIndex(c => c.Name == "A");
                    if (idx >= 0) col8.Children[idx] = a; else col8.Children.Add(a);
                });
            var p8 = Fixtures.ParticleBytes(Fixtures.Particles(mod.Read(Fixtures.MoonCat))[8]);
            var site = mod.Scan();
            Check(!site.Animatable.First().Inspect!.Info!.AnimationSet.Contains(8), "particle 8 not in set");
            Check(ApplyFrames(mod, 10, out string error, site: site), error);
            Check(Fixtures.ParticleBytes(Fixtures.Particles(mod.Read(Fixtures.MoonCat))[8]).AsSpan().SequenceEqual(p8), "particle 8 byte-identical");
        });

        Add("PartialRefusal", "EDGE-animated-table-29", () =>
        {
            using var mod = Fixtures.Pickles();
            EditAvfx(mod, Fixtures.PicklesAvfx[0], root => Retarget(root, 0, 0, 7));
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var site = mod.Scan();
            Check(site.Problem != null, "whole-table problem");
            Check(!ApplyFrames(mod, 3, out _, site: site), "refused");
            Check(Fixtures.PicklesAvfx.All(r => mod.Read(r).AsSpan().SequenceEqual(before[r])), "no effect changed");
        });

        Add("PlainSkipped", "EDGE-animated-table-29", () =>
        {
            using var mod = Fixtures.Moon();
            var site = mod.Scan();
            Check(site.ParsedCount == 2, $"only the two cat effects parsed ({site.ParsedCount})");
            Check(ApplyFrames(mod, 4, out string error), error);
            Check(mod.Scan().ParsedCount == 2, "still two after apply");
        });

        Add("MismatchedAtlasRefused", "EDGE-animated-table-31", () =>
        {
            using var mod = Fixtures.Pickles();
            MapLalaToOtherAtlas(mod);
            Check(mod.Scan().Problem?.StartsWith("MismatchedAtlas") == true, "mismatched atlas problem");
        });

        Add("SharedTexNotExclusive", "EDGE-animated-table-32", () =>
        {
            using var mod = Fixtures.Moon();
            foreach (var rel in MoonAvfx)
                EditAvfx(mod, rel, root => root.All("Ptcl").ElementAt(8).Child("TP")!.Child("TxNo")!.Data = BitConverter.GetBytes(30));
            var site = mod.Scan();
            var info = site.Animatable.First().Inspect!.Info!;
            Check(!info.Slots.Contains(13) && info.Capacity == 48, $"13 not a slot, capacity 48 ({info.Capacity})");
            Check(ApplyFrames(mod, 48, out string error, site: site), error);
            var root2 = AvfxChunk.Parse(mod.Read(Fixtures.MoonCat));
            var texs = root2.All("Tex").Select(t => t.Text()).ToList();
            var parts = root2.All("Ptcl").ToList();
            Check(texs[30] == Fixtures.Textures(File.ReadAllBytes(Path.Combine(Path.GetTempPath(), "AnimHarness", "moon-src", Fixtures.MoonCat)))[30], "Tex 30 keeps its path");
            Check(Fixtures.AlphaKeys(parts[13]).AsSpan().SequenceEqual(AvfxTableAnimation.HiddenKeys()), "13 hidden");
            Check(texs[AvfxTableAnimation.Tlst(parts[56])].EndsWith("_47.atex"), "56 carries frame 47");
        });

        Add("SetParticleItemRule", "EDGE-animated-table-9", () =>
        {
            using var mod = Fixtures.Moon();
            foreach (var rel in MoonAvfx) EditAvfx(mod, rel, root => Retarget(root, 0, ItemOf(root, 20), 21));
            var site = mod.Scan();
            var info = site.Animatable.First().Inspect!.Info!;
            Check(!info.Slots.Contains(20) && !info.Slots.Contains(21) && info.Capacity == 47, $"20/21 not slots, capacity 47 ({info.Capacity})");
            var tex20 = Fixtures.Textures(mod.Read(Fixtures.MoonCat))[AvfxTableAnimation.Tlst(Fixtures.Particles(mod.Read(Fixtures.MoonCat))[20])];
            Check(ApplyFrames(mod, 47, out string error, site: site), error);
            var parts = Fixtures.Particles(mod.Read(Fixtures.MoonCat));
            var texs = Fixtures.Textures(mod.Read(Fixtures.MoonCat));
            Check(Fixtures.AlphaKeys(parts[20]).AsSpan().SequenceEqual(AvfxTableAnimation.HiddenKeys())
                && Fixtures.AlphaKeys(parts[21]).AsSpan().SequenceEqual(AvfxTableAnimation.HiddenKeys()), "20/21 hidden");
            Check(texs[AvfxTableAnimation.Tlst(parts[20])] == tex20, "20's texture unchanged");
        });

        Add("TextureIndexCap", "EDGE-animated-table-8" + " " + AppTag("A-8"), () =>
        {
            using var mod = Fixtures.Pickles();
            foreach (var rel in Fixtures.PicklesAvfx)
                EditAvfx(mod, rel, root =>
                {
                    int last = root.Children!.FindLastIndex(c => c.Name == "Tex");
                    for (int i = 0; i < 213; i++) root.Children.Insert(last + 1, AvfxChunk.Leaf("Tex", AvfxChunk.TextBytes($"vfx/pad{i}.atex")));
                    root.SetInt("TxCn", root.ReadInt("TxCn") + 213);
                });
            var site = mod.Scan();
            var entry = site.Animatable.First();
            Check(entry.Inspect!.Info!.Capacity == 15, $"index ceiling 15 ({entry.Inspect.Info.Capacity})");
            Check(site.Capacity == 0 && DjTableAnimationCore.Status(site).Kind == AnimationStatusKind.Unsupported,
                $"the tested size binds first: capacity 0, not offered (#16) ({site.Capacity})");
            var built = AvfxTableAnimation.Build(entry.Baseline!, entry.Inspect!.Info!,
                Enumerable.Range(0, 48).Select(k => $"vfx/atex/ppe_x_1_{k:D2}.atex").ToList(), Fixtures.Timeline(48), out _);
            Check(built == null, "48 frames refused");
        });

        Add("RestoreUndoesBoth", "EDGE-animated-table-18" + " " + AppTag("E-6"), () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var groups = mod.Groups();
            var still = Fixtures.Frames(1)[0];
            PasteStill(mod, still);                               // a still in eq55 first
            Check(ApplyFrames(mod, 4, out string error), error);  // then animated
            // The dialog's single Restore runs the animation Restore, then DjTextures.Restore (app-only; user check U8).
            Check(Restore(mod, out _, out error, mod.NewHost()), error);
            CheckPristine(mod, before, groups, "after the animation restore");
            var rect = TexPaste.ReadRect(mod.Read(mod.Table.Relative), mod.Table.Surface.Rect, out _)!;
            Check(Fixtures.Psnr(rect, mod.Table.Surface.ToTextureSpace(still, FitMode.Fill)) > 30,
                "eq55 still holds the still, left for DjTextures.Restore");
        });

        Add("OutsideModSkipped", "EDGE-animated-table-29", () =>
        {
            using var mod = Fixtures.Pickles();
            string outside = Path.Combine(Path.GetDirectoryName(mod.Root)!, "outside.avfx");
            File.Copy(mod.Full(Fixtures.PicklesAvfx[0]), outside);
            var meta = JObject.Parse(File.ReadAllText(mod.Full("meta.json")));
            var lala = meta["Groups"]!.First(g => g["Name"]?.ToString() == "DJ Table")["Options"]!
                .First(o => o["Files"]?["vfx/alinasexy.avfx"]?.ToString().Contains("laladjtable") == true);
            lala["Files"]!["vfx/alinasexy.avfx"] = @"..\outside.avfx";
            File.WriteAllText(mod.Full("meta.json"), meta.ToString());
            var before = File.ReadAllBytes(outside);
            var site = mod.Scan();
            Check(!site.Avfx.Any(a => a.Relative.Contains("outside")), "no entry for the outside file");
            var applyHost = mod.Host;
            Check(ApplyFrames(mod, 2, out string e1, applyHost, site), "apply " + e1);
            var restoreHost = mod.NewHost();
            Check(Restore(mod, out _, out string e2, restoreHost), "restore " + e2);
            Check(!applyHost.Log.Concat(restoreHost.Log).Any(c => c.Path.Contains("outside")), "never read or written");
            Check(File.ReadAllBytes(outside).AsSpan().SequenceEqual(before), "outside file unchanged");
        });

        Add("ApplyOverCapacity", "EDGE-animated-table-8", () =>
        {
            using var mod = Fixtures.Moon();
            foreach (var rel in MoonAvfx)
                EditAvfx(mod, rel, root => { foreach (int i in Enumerable.Range(30, 25).Append(56)) MakeStatic(root.All("Ptcl").ElementAt(i)); });
            var snap = mod.Snapshot();
            Check(!ApplyFrames(mod, 24, out string e) && e.StartsWith("TooManyTextures"), "capacity + 1 refused " + e);
            Check(mod.Snapshot().SequenceEqual(snap), "nothing written");
        });

        Add("ApplyNeverThrows", "EDGE-animated-table-14" + " " + AppTag("A-10"), () =>
        {
            using var mod = Fixtures.Pickles();
            var snap = mod.Snapshot();
            bool ok = DjTableAnimationCore.Apply(mod.Host, mod.Scan(), Fixtures.Frames(3), Fixtures.Timeline(2), FitMode.Fill, null, out _, out string e);
            Check(!ok && e.StartsWith("WriteFailed"), "frames/timeline mismatch " + e);
            foreach (string where in new[] { "pristine", "state.json" })
            {
                var host = mod.NewHost();
                host.ThrowOn = c => c.Kind == CallKind.Write && c.Path.Contains(where);
                ok = DjTableAnimationCore.Apply(host, mod.Scan(host), Fixtures.Frames(3), Fixtures.Timeline(3), FitMode.Fill, null, out _, out e);
                Check(!ok && e.StartsWith("WriteFailed"), $"{where} write fault " + e);
                var now = mod.Snapshot();
                Check(snap.Where(kv => !kv.Key.Contains(@"\state\")).All(kv => now.GetValueOrDefault(kv.Key) == kv.Value), $"{where}: mod unchanged");
            }
            using var m2 = Fixtures.Pickles();
            Check(ApplyFrames(m2, 2, out _), "apply");
            var h2 = m2.NewHost();
            h2.ThrowOn = c => c.Kind == CallKind.Read && c.Path.EndsWith("state.json");
            var site = m2.Scan(h2);
            var before = m2.Snapshot();
            Check(!DjTableAnimationCore.Restore(h2, site, out _, out e) && e.StartsWith("WriteFailed"), "restore state read fault " + e);
            Check(!h2.Log.Any(c => c.Kind != CallKind.Read) && m2.Snapshot().SequenceEqual(before), "restore wrote nothing");
        });

        Add("DuplicatePanelRefused", "EDGE-animated-table-9", () =>
        {
            using var mod = Fixtures.Pickles();
            foreach (var rel in Fixtures.PicklesAvfx)
                EditAvfx(mod, rel, root => root.All("Ptcl").ElementAt(8).Child("TC1")!.Child("TLst")!.Data = new byte[] { 26 });
            Check(mod.Scan().Problem?.StartsWith("DuplicatePanel") == true, "two panel particles refused");
        });

        Add("UnparseablePanel", "EDGE-animated-table-9", () =>
        {
            using var mod = Fixtures.Pickles();
            foreach (var rel in Fixtures.PicklesAvfx)
                EditAvfx(mod, rel, root => root.All("Ptcl").ElementAt(7).Children!.RemoveAll(c => c.Name == "Col"));
            var site = mod.Scan();
            Check(site.Problem?.StartsWith("Unparseable") == true, $"panel without colour curve refused ({site.Problem})");
        });

        Add("FormattingKept", "EDGE-animated-table-21", () =>
        {
            using var mod = Fixtures.Moon();
            var files = Directory.GetFiles(mod.Root, "group_*.json").ToDictionary(f => f, File.ReadAllBytes);
            Check(ApplyFrames(mod, 3, out string error), error);
            var untouched = files.Where(f => !File.ReadAllText(f.Key).Contains("ppe_")).ToList();
            Check(untouched.Count > 0 && untouched.All(f => File.ReadAllBytes(f.Key).AsSpan().SequenceEqual(f.Value)), "untouched group files byte-identical");
        });
    }

    // ---- synthetic edits --------------------------------------------------------------------------

    private static void EditAvfx(TempMod mod, string rel, Action<AvfxChunk> edit)
    {
        var root = AvfxChunk.Parse(mod.Read(rel));
        edit(root);
        File.WriteAllBytes(mod.Full(rel), root.ToBytes());
    }

    /// <summary>Points item <paramref name="item"/> of the emitter's last ItPr at another particle.</summary>
    private static void Retarget(AvfxChunk root, int emitter, int item, int target)
    {
        var last = root.All("Emit").ElementAt(emitter).All("ItPr").Last();
        last.Children!.Where(c => c.Name == "TgtB").ElementAt(item).Data = BitConverter.GetBytes(target);
    }

    private static int ItemOf(AvfxChunk root, int particle)
    {
        var targets = root.All("Emit").First().All("ItPr").Last().Children!.Where(c => c.Name == "TgtB")
            .Select(c => BitConverter.ToInt32(c.Data!, 0)).ToList();
        return targets.IndexOf(particle);
    }

    private static void MakeStatic(AvfxChunk particle)
    {
        var a = particle.Child("Col")!.Child("A")!;
        a.SetInt("KeyC", 1);
        a.SetInt("BvPr", 0);
        a.SetInt("BvPo", 0);
        a.Child("Keys")!.Data = Convert.FromHexString("000001000000803f0000803f0000803f");
    }

    private static void MoveToDefaultData(TempMod mod)
    {
        var meta = JObject.Parse(File.ReadAllText(mod.Full("meta.json")));
        var table = meta["Groups"]!.First(g => g["Name"]?.ToString() == "DJ Table");
        meta["DefaultData"] = new JObject { ["Files"] = table["Options"]!.First(o => o["Files"]?["vfx/alinasexy.avfx"] != null)["Files"]!.DeepClone() };
        File.WriteAllText(mod.Full("meta.json"), meta.ToString());
    }

    private static void MapLalaToOtherAtlas(TempMod mod)
    {
        File.Copy(mod.Full(mod.Table.Relative), mod.Full(@"common\4\other.atex"));
        var meta = JObject.Parse(File.ReadAllText(mod.Full("meta.json")));
        var table = meta["Groups"]!.First(g => g["Name"]?.ToString() == "DJ Table");
        table["Options"]!.First(o => o["Files"]?["vfx/alinasexy.avfx"]?.ToString().Contains("laladjtable") == true)!["Files"]!["vfx/atex/eq55.atex"] = @"common\4\other.atex";
        File.WriteAllText(mod.Full("meta.json"), meta.ToString());
    }

    private static void PasteStill(TempMod mod, BgraImage still)
    {
        var atlas = mod.Read(mod.Table.Relative);
        var pasted = TexPaste.PasteRect(atlas, mod.Table.Surface.Rect, mod.Table.Surface.ToTextureSpace(still, FitMode.Fill), out _)!;
        File.WriteAllBytes(mod.Full(mod.Table.Relative), pasted);
    }
}
