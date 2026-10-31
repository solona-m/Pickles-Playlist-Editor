using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;

namespace AnimHarness;

internal static partial class Program
{
    private static void Register()
    {
        RegisterFormat();
        RegisterApply();
        RegisterState();
    }

    private static void RegisterFormat()
    {
        Add("ChunkRoundTrip", "AC-animated-table-2", () =>
        {
            using var p = Fixtures.Pickles();
            foreach (var rel in Fixtures.PicklesAvfx)
            {
                var bytes = p.Read(rel);
                Check(AvfxChunk.Parse(bytes).ToBytes().AsSpan().SequenceEqual(bytes), $"round trip {rel}");
            }
            using var m = Fixtures.Moon();
            foreach (var rel in new[] { Fixtures.MoonCat, Fixtures.MoonCat2, Fixtures.MoonPlain })
            {
                var bytes = m.Read(rel);
                Check(AvfxChunk.Parse(bytes).ToBytes().AsSpan().SequenceEqual(bytes), $"round trip {rel}");
            }
        });

        Add("MoonAnchorKeys", "AC-animated-table-2", () =>
        {
            using var m = Fixtures.Moon();
            var particles = Fixtures.Particles(m.Read(Fixtures.MoonCat));
            var timeline = new FrameTimeline(Enumerable.Range(0, 49).Select(f => 3 * f).ToList(), 149);
            for (int f = 1; f <= 48; f++)
            {
                var a = particles[8 + f].Child("Col")!.Child("A")!;
                var keys = AvfxTableAnimation.AlphaKeys(f, timeline);
                Check(keys.AsSpan().SequenceEqual(a.Child("Keys")!.Data), $"frame {f} keys match particle {8 + f}");
                Check(a.ReadInt("KeyC") == 4 && a.ReadInt("BvPr") == 1 && a.ReadInt("BvPo") == 1, $"frame {f} KeyC/Bv");
            }
        });

        Add("FramePlanTiming", "AC-animated-table-6 EDGE-animated-table-4", () =>
        {
            var plan = FramePlan.Plan(Enumerable.Repeat(4, 120).ToList(), 48);
            Check(plan.KeptSourceIndices.Count == 48, "48 kept");
            Check(Math.Abs(plan.Timeline.LoopTicks - 144) <= 1, $"loop 144±1 (got {plan.Timeline.LoopTicks})");
            Check(plan.Timeline.StartTicks.Zip(plan.Timeline.StartTicks.Skip(1)).All(z => z.Second > z.First), "start ticks increase");
            Check(new[] { 0, 1, 5 }.Select(FramePlan.NormaliseDelay).SequenceEqual(new[] { 10, 10, 5 }), "delays 0/1/5 -> 10/10/5");
        });

        Add("CapacityThinning", "EDGE-animated-table-4", () =>
        {
            var plan = FramePlan.Plan(Enumerable.Repeat(10, 60).ToList(), 20);
            Check(plan.KeptSourceIndices.Count == 20 && plan.Thinned, "20 kept, thinned");
            Check(plan.Timeline.LoopTicks == 180, $"loop kept at 180 (got {plan.Timeline.LoopTicks})");
        });

        Add("FastGifMerges", "EDGE-animated-table-5", () =>
        {
            var plan = FramePlan.Plan(Enumerable.Repeat(1, 100).ToList(), 48);
            var s = plan.Timeline.StartTicks;
            Check(s.Zip(s.Skip(1)).All(z => z.Second > z.First) && plan.Timeline.LoopTicks > s[^1], "no zero-length slot");
            var fast = FramePlan.Plan(Enumerable.Repeat(2, 40).ToList(), 48);  // 0.6 ticks each
            var f = fast.Timeline.StartTicks;
            Check(fast.KeptSourceIndices.Count <= 24 && f.Zip(f.Skip(1)).All(z => z.Second > z.First), "2 cs frames merge, no empty slot");
            var merged = FramePlan.Plan(new[] { 2, 2 }.ToList(), 48);  // 0.6 + 0.6 ticks
            Check(merged.KeptSourceIndices.Count == 1, $"2 frames that round to one slot merge to a still ({merged.KeptSourceIndices.Count})");
        });

        Add("LoopClamp", "EDGE-animated-table-23", () =>
        {
            var plan = FramePlan.Plan(Enumerable.Repeat(655, 2000).ToList(), 48);
            Check(plan.Timeline.LoopTicks == 65535, $"loop clamped (got {plan.Timeline.LoopTicks})");
            var s = plan.Timeline.StartTicks;
            Check(s.Zip(s.Skip(1)).All(z => z.Second > z.First), "windows still increase");
        });

        Add("DescribeTable", "AC-animated-table-8 AC-animated-table-10", () =>
        {
            var statuses = new[]
            {
                new AnimationStatus(AnimationStatusKind.None), new AnimationStatus(AnimationStatusKind.Author, 49),
                new AnimationStatus(AnimationStatusKind.Ours, 12), new AnimationStatus(AnimationStatusKind.Ours, 1),
                new AnimationStatus(AnimationStatusKind.Interrupted), new AnimationStatus(AnimationStatusKind.Orphaned),
                new AnimationStatus(AnimationStatusKind.Unsupported, Reason: "x"),
            };
            foreach (var status in statuses)
                foreach (var mode in new AnimationMode?[] { AnimationMode.Append, AnimationMode.InPlace, null })
                    foreach (bool hasState in new[] { false, true })
                        foreach (bool still in new[] { false, true })
                        {
                            var v = TableCard.Describe(status, mode, hasState, still);
                            string at = $"{status.Kind}/{status.Frames} {mode} state={hasState} still={still}";
                            if (status.Kind == AnimationStatusKind.Orphaned)
                            {
                                Check(v.StateKey == TableText.Orphaned && !v.TableChangeAllowed && !v.RestoreEnabled
                                    && !v.AnimationAllowed && v.StillRoute == null, $"orphaned {at}");
                                continue;
                            }
                            Check(v.TableChangeAllowed && v.RestoreEnabled == (still || hasState), $"enable {at}");
                            bool unsupported = status.Kind == AnimationStatusKind.Unsupported;
                            var route = mode == AnimationMode.InPlace && !unsupported ? StillRoute.OneFrameAnimation
                                : hasState ? StillRoute.PasteThenRestore : StillRoute.Existing;
                            Check(v.StillRoute == route, $"route {at}");
                            Check(v.AnimationAllowed == !unsupported, $"animation {at}");
                            var key = status.Kind switch
                            {
                                AnimationStatusKind.Ours => status.Frames > 1 ? TableText.YourAnimation : TableText.YourStill,
                                AnimationStatusKind.Author => TableText.AuthorAnimation,
                                AnimationStatusKind.Interrupted => TableText.Interrupted,
                                AnimationStatusKind.Unsupported => TableText.Unsupported,
                                _ => TableText.Default,
                            };
                            Check(v.StateKey == key && v.PlaysFrames == (status.Kind == AnimationStatusKind.Ours && status.Frames > 1), $"key {at}");
                        }
            var f1 = TableCard.ScanFailed(true);
            Check(f1.StateKey == TableText.ScanFailed && f1.RestoreEnabled && !f1.TableChangeAllowed && !f1.AnimationAllowed
                && !f1.PlaysFrames && f1.StillRoute == null, "ScanFailed(true)");
            Check(!TableCard.ScanFailed(false).RestoreEnabled, "ScanFailed(false)");
        });

        Add("VerifyDetectsLostKeys", "EDGE-animated-table-16", () =>
        {
            var group = JObject.Parse("{\"Name\":\"g\",\"Options\":[{\"Files\":{\"vfx/a.avfx\":\"a\\\\x.avfx\"}}]}");
            var rels = new HashSet<string>(new[] { @"a\x.avfx" });
            var frames = new Dictionary<string, string> { ["vfx/atex/ppe_t_1_00.atex"] = @"ppe-table-animation\t_1_00.atex" };
            Check(DjTableAnimationCore.VerifyManifest(new[] { group }, rels, frames) != null, "missing key warns");
            DjTableAnimationCore.AddFrameKeys(group, rels, frames);
            Check(DjTableAnimationCore.VerifyManifest(new[] { group }, rels, frames) == null, "present key is fine");
        });

        Add("ContainersEdited", "EDGE-animated-table-10", () =>
        {
            var group = JObject.Parse("{\"Name\":\"g\",\"Containers\":[{\"Files\":{\"vfx/a.avfx\":\"a\\\\x.avfx\"}},{\"Files\":{}}]}");
            var rels = new HashSet<string>(new[] { @"a\x.avfx" });
            var frames = new Dictionary<string, string> { ["vfx/atex/ppe_t_1_00.atex"] = @"ppe-table-animation\t_1_00.atex" };
            Check(DjTableAnimationCore.AddFrameKeys(group, rels, frames), "container gains keys");
            Check(group["Containers"]![0]!["Files"]!["vfx/atex/ppe_t_1_00.atex"] != null, "key present");
            Check(!((JObject)group["Containers"]![1]!["Files"]!).HasValues, "unrelated container untouched");
            Check(DjTableAnimationCore.PruneFrameKeys(group, new HashSet<string>()), "prune removes");
            Check(group["Containers"]![0]!["Files"]!["vfx/atex/ppe_t_1_00.atex"] == null, "key gone");
        });
    }
}
