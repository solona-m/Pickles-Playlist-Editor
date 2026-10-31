using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;

namespace AnimHarness;

internal static partial class Program
{
    private static bool IsAvfxWrite(Call c) =>
        c.Kind == CallKind.Write && c.Path.EndsWith(".avfx") && !c.Path.Contains(@"\pristine\");

    private static bool IsFrameWrite(Call c) => c.Kind == CallKind.Write && c.Path.Contains(DjAnimationScan.FrameFolder);

    private static AnimationState ReadState(TempMod mod) =>
        AnimationState.Parse(File.ReadAllBytes(DjAnimationScan.StatePath(mod.StateFolder)));

    private static AnimationStatusKind StatusOf(TempMod mod) => DjTableAnimationCore.Status(mod.Scan(mod.NewHost())).Kind;

    /// <summary>A plausible mod update: the effect with a harmless byte changed and no frames of ours.</summary>
    private static void UpdateEffect(TempMod mod, string rel, byte[] baseline, bool removePanel = false)
    {
        var root = AvfxChunk.Parse(baseline);
        if (removePanel) root.All("Tex").ElementAt(26).Data = AvfxChunk.TextBytes("vfx/atex/zz55.atex");
        else root.All("Tex").ElementAt(0).Data = AvfxChunk.TextBytes("vfx/ws/updated.atex");
        File.WriteAllBytes(mod.Full(rel), root.ToBytes());
    }

    private static void RegisterState()
    {
        Add("StateKeepsTimeline", "AC-animated-table-2", () =>
        {
            using var mod = Fixtures.Pickles();
            Check(ApplyFrames(mod, 12, out string error), error);
            var st = ReadState(mod);
            var t = Fixtures.Timeline(12);
            Check(st.StartTicks.SequenceEqual(t.StartTicks) && st.LoopTicks == t.LoopTicks, "timeline stored");
            Check(DjTableAnimationCore.LiveTimeline(st)?.LoopTicks == t.LoopTicks, "LiveTimeline");
            var host = mod.NewHost();
            int frames = 0;
            host.KillAfter = c => IsFrameWrite(c) && ++frames == 5;
            ApplyFrames(mod, 5, out _, host, delayCs: 20);
            var killed = ReadState(mod);
            Check(killed.Phase == AnimationState.Applying && killed.StartTicks.SequenceEqual(t.StartTicks), "Applying state keeps gen-1 ticks");
            Check(ApplyFrames(mod, 5, out error, mod.NewHost(), delayCs: 20), error);
            Check(ReadState(mod).StartTicks.SequenceEqual(Fixtures.Timeline(5, 20).StartTicks), "gen-2 ticks after a full apply");
        });

        Add("InterruptedStatus", "AC-animated-table-11", () =>
        {
            using (var mod = Fixtures.Pickles())
            {
                mod.Host.KillAfter = IsAvfxWrite;
                ApplyFrames(mod, 4, out _);
                var site = mod.Scan(mod.NewHost());
                var st = DjTableAnimationCore.Status(site);
                var v = TableCard.Describe(st, site.Mode, site.HasState, false);
                Check(st.Kind == AnimationStatusKind.Interrupted && v.RestoreEnabled && v.StateKey == TableText.Interrupted, $"(a) killed: {st.Kind}");
                Check(Restore(mod, out var w, out string e), "(a) restore " + e);
                Check(w.Count == 0, "(a) no warnings");
                Check(ApplyFrames(mod, 3, out e, mod.NewHost()), "(a) apply after " + e);
            }
            using (var mod = Fixtures.Pickles())
            {
                var lala = mod.Read(Fixtures.PicklesAvfx[0]);
                Check(ApplyFrames(mod, 4, out _), "apply");
                UpdateEffect(mod, Fixtures.PicklesAvfx[0], lala);
                Check(StatusOf(mod) == AnimationStatusKind.Interrupted, "(b) update -> Interrupted");
                Check(Restore(mod, out var w, out string e, mod.NewHost()) && w.Count == 1, "(b) restore warns once " + e);
                Check(ApplyFrames(mod, 3, out e, mod.NewHost()), "(b) apply after " + e);
            }
        });

        Add("ModUpdateAfterApply", "EDGE-animated-table-13", () =>
        {
            using (var mod = Fixtures.Pickles())
            {
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                Check(ApplyFrames(mod, 4, out _), "apply");
                UpdateEffect(mod, Fixtures.PicklesAvfx[0], before[Fixtures.PicklesAvfx[0]]);
                var updated = mod.Read(Fixtures.PicklesAvfx[0]);
                Check(StatusOf(mod) == AnimationStatusKind.Interrupted, "Interrupted");
                Check(Restore(mod, out var w, out string e, mod.NewHost()), e);
                Check(w.Count == 1 && w[0].StartsWith("LeftAlone"), "warns about the updated file");
                Check(mod.Read(Fixtures.PicklesAvfx[0]).AsSpan().SequenceEqual(updated), "updated file left alone");
                Check(Fixtures.PicklesAvfx.Skip(1).All(r => mod.Read(r).AsSpan().SequenceEqual(before[r])), "others restored");
            }
            using (var mod = Fixtures.Pickles())
            {
                var lala = mod.Read(Fixtures.PicklesAvfx[0]);
                Check(ApplyFrames(mod, 4, out _), "apply");
                var oldPristine = Directory.GetFiles(Path.Combine(mod.StateFolder, "pristine")).ToDictionary(f => f, File.ReadAllBytes);
                UpdateEffect(mod, Fixtures.PicklesAvfx[0], lala);
                string newSha = AvfxTableAnimation.Sha(mod.Read(Fixtures.PicklesAvfx[0]));
                Check(ApplyFrames(mod, 3, out string e, mod.NewHost()), "re-baseline apply " + e);
                Check(File.Exists(DjAnimationScan.PristinePath(mod.StateFolder, newSha)), "new pristine copy");
                Check(oldPristine.All(p => File.ReadAllBytes(p.Key).AsSpan().SequenceEqual(p.Value)), "old copies untouched");
            }
        });

        Add("ModUpdateNoPanel", "EDGE-animated-table-13", () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            Check(ApplyFrames(mod, 4, out _), "apply");
            UpdateEffect(mod, Fixtures.PicklesAvfx[0], before[Fixtures.PicklesAvfx[0]], removePanel: true);
            var updated = mod.Read(Fixtures.PicklesAvfx[0]);
            Check(StatusOf(mod) == AnimationStatusKind.Interrupted, "Interrupted");
            var host = mod.NewHost();
            AnimationState? step4 = null;
            host.KillAfter = c => { if (c.Kind == CallKind.Write && c.Path.EndsWith("state.json")) step4 ??= ReadState(mod); return false; };
            Check(ApplyFrames(mod, 3, out string e, host), "apply " + e);
            Check(step4 != null && step4.Find(Fixtures.PicklesAvfx[0]) == null, "step-4 state drops Lala");
            Check(mod.Read(Fixtures.PicklesAvfx[0]).AsSpan().SequenceEqual(updated), "Lala unchanged");
            Check(StatusOf(mod) == AnimationStatusKind.Ours, "Ours after");
            Check(Restore(mod, out var w, out e, mod.NewHost()) && w.Count == 0, "restore without warnings " + e);
            Check(Fixtures.PicklesAvfx.Skip(1).All(r => mod.Read(r).AsSpan().SequenceEqual(before[r])), "others pristine");
            Check(!Directory.Exists(mod.StateFolder) && !Directory.Exists(mod.Full(DjAnimationScan.FrameFolder)), "no state, no frames");
        });

        Add("RollbackOnFailure", "EDGE-animated-table-14", () =>
        {
            foreach (bool live in new[] { false, true })
                foreach (string fault in new[] { "edit", "avfx2" })
                {
                    using var mod = Fixtures.Pickles();
                    if (live) Check(ApplyFrames(mod, 4, out _), "gen 1");
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
                    Check(mod.Groups().Zip(groups).All(z => Newtonsoft.Json.Linq.JToken.DeepEquals(z.First, z.Second)), $"{label}: groups as before");
                    if (!live) Check(!Directory.Exists(mod.StateFolder), $"{label}: no state folder");
                    else Check(StatusOf(mod) == AnimationStatusKind.Ours, $"{label}: gen 1 still ours");
                }
        });

        Add("RestoreAfterCrash", "EDGE-animated-table-15", () =>
        {
            using (var mod = Fixtures.Pickles())
            {
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                mod.Host.KillAfter = IsAvfxWrite;
                ApplyFrames(mod, 4, out _);
                var pristine = Directory.GetFiles(Path.Combine(mod.StateFolder, "pristine")).ToDictionary(f => f, File.ReadAllBytes);
                Check(StatusOf(mod) == AnimationStatusKind.Interrupted, "(a) Interrupted");
                Check(Restore(mod, out _, out string e, mod.NewHost()), e);
                CheckPristine(mod, before, groups, "(a)");
                Check(pristine.Values.All(b => before.Values.Any(o => o.AsSpan().SequenceEqual(b))), "(a) pristine copies were the originals");
            }
            using (var mod = Fixtures.Pickles())
            {
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                mod.Host.KillAfter = IsAvfxWrite;
                ApplyFrames(mod, 4, out _);
                var st = ReadState(mod);
                Check(ApplyFrames(mod, 3, out string e, mod.NewHost()), "(b) apply after crash " + e);
                var st2 = ReadState(mod);
                Check(st.Avfx.All(a => st2.Find(a.Relative)?.OriginalSha == a.OriginalSha), "(b) no re-baseline");
                Check(StatusOf(mod) == AnimationStatusKind.Ours, "(b) Ours");
                Check(Restore(mod, out _, out e, mod.NewHost()), e);
                CheckPristine(mod, before, groups, "(b)");
            }
        });

        Add("RestoreKeepsEditedFrames", "EDGE-animated-table-17", () =>
        {
            using var mod = Fixtures.Pickles();
            Check(ApplyFrames(mod, 4, out _), "apply");
            File.WriteAllBytes(mod.Full(@"ppe-table-animation\t0000001_1_01.atex"), new byte[] { 9 });
            File.Delete(mod.Full(@"ppe-table-animation\t0000001_1_02.atex"));
            Check(Restore(mod, out var w, out string e), e);
            var left = Directory.GetFiles(mod.Full(DjAnimationScan.FrameFolder)).Select(Path.GetFileName).ToList();
            Check(left.SequenceEqual(new[] { "t0000001_1_01.atex" }), "only the edited frame kept");
            Check(w.Count(x => x.StartsWith("KeptEditedFrame")) == 1, $"warning names it ({string.Join(",", w)})");
        });

        Add("StaleGenerationRetried", "EDGE-animated-table-33", () =>
        {
            using var mod = Fixtures.Pickles();
            Check(ApplyFrames(mod, 3, out _), "gen 1");
            var host = mod.NewHost();
            host.ThrowOn = c => c.Kind == CallKind.Delete && c.Path.EndsWith("t0000001_1_01.atex");
            Check(ApplyFrames(mod, 3, out string e, host), "gen 2 applied " + e);
            Check(ReadState(mod).StaleFrames.Select(f => f.Relative).SequenceEqual(new[] { @"ppe-table-animation\t0000001_1_01.atex" }), "leftover listed");
            Check(ApplyFrames(mod, 3, out e, mod.NewHost()), "gen 3 " + e);
            Check(!File.Exists(mod.Full(@"ppe-table-animation\t0000001_1_01.atex")) && ReadState(mod).StaleFrames.Count == 0, "leftover deleted");
        });

        Add("PruneFailsKeepsOldFiles", "EDGE-animated-table-33", () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var groups = mod.Groups();
            Check(ApplyFrames(mod, 3, out _), "gen 1");
            var host = mod.NewHost();
            int edits = 0;
            host.ThrowOn = c => c.Kind == CallKind.EditGroups && ++edits == 3;   // collision read, add, prune
            Check(ApplyFrames(mod, 3, out string e, host), "gen 2 applied " + e);
            var st = ReadState(mod);
            Check(StatusOf(mod) == AnimationStatusKind.Ours && st.Generation == 2, "Ours gen 2");
            Check(st.StaleFrames.Count == 3 && st.StaleFrames.All(f => File.Exists(mod.Full(f.Relative))), "gen-1 files kept and listed");
            Check(mod.Groups().Any(g => g.ToString().Contains("_1_00")), "gen-1 keys kept");
            Check(Restore(mod, out _, out e, mod.NewHost()), e);
            CheckPristine(mod, before, groups, "after restore");
        });

        Add("CrashBeforeAvfxKeepsOldAnimation", "EDGE-animated-table-38", () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var groups = mod.Groups();
            Check(ApplyFrames(mod, 3, out _), "gen 1");
            var gen1 = ReadState(mod);
            var host = mod.NewHost();
            int edits = 0;
            host.KillAfter = c => c.Kind == CallKind.EditGroups && ++edits == 2;
            ApplyFrames(mod, 3, out _, host);
            Check(gen1.Avfx.All(a => AvfxTableAnimation.Sha(mod.Read(a.Relative)) == a.AppliedSha), "effects still gen 1");
            var rels = new HashSet<string>(gen1.Avfx.Select(a => a.Relative), StringComparer.OrdinalIgnoreCase);
            Check(DjTableAnimationCore.VerifyManifest(mod.Groups(), rels, gen1.Frames.ToDictionary(f => f.GamePath, f => f.Relative)) == null, "gen-1 keys intact");
            Check(gen1.Frames.All(f => File.Exists(mod.Full(f.Relative))), "gen-1 files intact");
            Check(StatusOf(mod) == AnimationStatusKind.Interrupted, "Interrupted");
            Check(Restore(mod, out _, out string e, mod.NewHost()), e);
            CheckPristine(mod, before, groups, "after restore");
        });

        Add("RollbackDeleteFails", "EDGE-animated-table-39", () =>
        {
            using (var mod = Fixtures.Pickles())
            {
                var snap = AvfxBytes(mod, Fixtures.PicklesAvfx);
                Check(ApplyFrames(mod, 3, out _), "gen 1");
                var gen1Avfx = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                var host = mod.NewHost();
                host.ThrowOn = c => (IsAvfxWrite(c) && host.Log.Count(IsAvfxWrite) == 2)
                    || (c.Kind == CallKind.Delete && c.Path.EndsWith("t0000001_2_00.atex"));
                Check(!ApplyFrames(mod, 3, out _, host), "(a) gen 2 fails");
                Check(Fixtures.PicklesAvfx.All(r => mod.Read(r).AsSpan().SequenceEqual(gen1Avfx[r])), "(a) effects as before");
                var st = ReadState(mod);
                Check(st.Frames.All(f => f.Relative.Contains("_1_")) && st.Generation == 2
                    && st.StaleFrames.Any(f => f.Relative.EndsWith("t0000001_2_00.atex")), "(a) state keeps gen 1, gen 2, leftover");
                Check(StatusOf(mod) == AnimationStatusKind.Ours, "(a) Ours");
                Check(ApplyFrames(mod, 3, out string e, mod.NewHost()), "(a) gen 3 " + e);
                Check(ReadState(mod).Generation == 3 && !File.Exists(mod.Full(@"ppe-table-animation\t0000001_2_00.atex")), "(a) leftover deleted");
            }
            using (var mod = Fixtures.Pickles())
            {
                var host = mod.Host;
                host.ThrowOn = c => (IsAvfxWrite(c) && host.Log.Count(IsAvfxWrite) == 2)
                    || (c.Kind == CallKind.Delete && c.Path.EndsWith("t0000001_1_00.atex"));
                Check(!ApplyFrames(mod, 3, out _), "(b) fails");
                var st = ReadState(mod);
                Check(st.IsEmpty && st.StaleFrames.Count == 1, "(b) empty state holds the leftover");
                var site = mod.Scan(mod.NewHost());
                var status = DjTableAnimationCore.Status(site);
                Check(status.Kind == AnimationStatusKind.None && !TableCard.Describe(status, site.Mode, site.HasState, false).RestoreEnabled, "(b) None, no Restore");
                Check(ApplyFrames(mod, 3, out string e, mod.NewHost()), "(b) next apply " + e);
                Check(!File.Exists(mod.Full(@"ppe-table-animation\t0000001_1_00.atex")), "(b) leftover deleted");
                Check(Restore(mod, out _, out e, mod.NewHost()), e);
                Check(!Directory.Exists(mod.StateFolder) && !Directory.Exists(mod.Full(DjAnimationScan.FrameFolder)), "(b) clean");
            }
        });

        Add("ApplyOverApplying", "EDGE-animated-table-41", () =>
        {
            using var mod = Fixtures.Pickles();
            int frames = 0;
            mod.Host.KillAfter = c => IsFrameWrite(c) && ++frames == 3;
            ApplyFrames(mod, 3, out _);
            var host = mod.NewHost();
            AnimationState? step4 = null;
            host.KillAfter = c => { if (c.Kind == CallKind.Write && c.Path.EndsWith("state.json")) step4 ??= ReadState(mod); return false; };
            Check(ApplyFrames(mod, 2, out string e, host), "gen 2 " + e);
            Check(step4 != null && step4.StaleFrames.Count == 3 && step4.StaleFrames.All(f => f.Relative.Contains("_1_")), "gen-1 pending moved to stale");
            var st = ReadState(mod);
            Check(st.Phase == AnimationState.Applied && st.StaleFrames.Count == 0, "Applied, nothing stale");
            Check(!Directory.GetFiles(mod.Full(DjAnimationScan.FrameFolder)).Any(f => f.Contains("_1_")), "gen-1 files gone");
            Check(!mod.Groups().Any(g => g.ToString().Contains("_1_0")), "no gen-1 keys");
        });

        Add("StaleSiteRefused", "AC-animated-table-5 EDGE-animated-table-42", () =>
        {
            using var mod = Fixtures.Pickles();
            var site0 = mod.Scan();
            Check(DjTableAnimationCore.Apply(mod.Host, site0, Fixtures.Frames(3), Fixtures.Timeline(3), FitMode.Fill, null, out _, out string e), "A " + e);
            var snap = mod.Snapshot();
            var host = mod.NewHost();
            Check(!DjTableAnimationCore.Apply(host, site0, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out e)
                && e.StartsWith("ModChanged"), "B on stale site refused " + e);
            Check(!DjTableAnimationCore.Restore(host, site0, out _, out e) && e.StartsWith("ModChanged"), "Restore on stale site refused");
            Check(!host.Log.Any(c => c.Kind != CallKind.Read) && mod.Snapshot().SequenceEqual(snap), "nothing written");
            Check(ApplyFrames(mod, 2, out e, mod.NewHost()), "B on a rescan " + e);

            using var other = Fixtures.Pickles();
            var site = other.Scan();
            UpdateEffect(other, Fixtures.PicklesAvfx[1], other.Read(Fixtures.PicklesAvfx[1]));
            Check(!ApplyFrames(other, 2, out e, site: site) && e.StartsWith("ModChanged"), "outside edit refused");
        });

        Add("DoubleInterruptRestore", "EDGE-animated-table-43", () =>
        {
            foreach (string second in new[] { "frames", "avfx" })
            {
                using var mod = Fixtures.Pickles();
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                mod.Host.KillAfter = IsAvfxWrite;
                ApplyFrames(mod, 3, out _);
                var host = mod.NewHost();
                int f = 0;
                host.KillAfter = second == "frames" ? c => IsFrameWrite(c) && ++f == 3 : IsAvfxWrite;
                ApplyFrames(mod, 3, out _, host);
                var site = mod.Scan(mod.NewHost());
                Check(DjTableAnimationCore.Status(site).Kind == AnimationStatusKind.Interrupted, $"{second}: Interrupted");
                Check(site.Avfx.Where(a => ReadState(mod).Find(a.Relative) != null).All(a => a.Ours), $"{second}: all listed ours");
                Check(ReadState(mod).StaleFrames.Any(s => s.Relative.Contains("_1_")), $"{second}: gen-1 pending is stale");
                Check(Restore(mod, out _, out string e, mod.NewHost()), e);
                CheckPristine(mod, before, groups, second);
            }
        });

        Add("RollbackPutBackFails", "EDGE-animated-table-44 AC-animated-table-11", () =>
        {
            foreach (string fault in new[] { "avfx", "groups" })
            {
                using var mod = Fixtures.Pickles();
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                Check(ApplyFrames(mod, 3, out _), "gen 1");
                var host = mod.NewHost();
                int avfxWrites = 0, edits = 0;
                bool failed = false;
                host.ThrowOn = c =>
                {
                    if (IsAvfxWrite(c) && ++avfxWrites == 2) return failed = true;
                    if (failed && fault == "avfx" && IsAvfxWrite(c)) return true;
                    if (c.Kind == CallKind.EditGroups && ++edits == 3 && fault == "groups") return true;
                    return false;
                };
                Check(!ApplyFrames(mod, 3, out string e, host) && e.StartsWith("RollbackStopped"), $"{fault}: stopped " + e);
                var st = ReadState(mod);
                Check(st.Phase == AnimationState.Applying && st.PendingFrames.All(p => File.Exists(mod.Full(p.Relative))), $"{fault}: Applying, gen-2 files present");
                int failAt = host.Log.FindIndex(c => IsAvfxWrite(c) && c.Index >= 1 && host.Log.Take(host.Log.IndexOf(c)).Count(IsAvfxWrite) == 1);
                Check(!host.Log.Skip(Math.Max(0, failAt)).Any(c => c.Kind == CallKind.Delete), $"{fault}: no delete after the failure");
                var site = mod.Scan(mod.NewHost());
                var status = DjTableAnimationCore.Status(site);
                Check(status.Kind == AnimationStatusKind.Interrupted && TableCard.Describe(status, site.Mode, site.HasState, false).RestoreEnabled, $"{fault}: Interrupted");
                Check(Restore(mod, out _, out e, mod.NewHost()), e);
                CheckPristine(mod, before, groups, fault);
            }
        });

        Add("ProblemWithStateRestores", "EDGE-animated-table-45 AC-animated-table-11", () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            Check(ApplyFrames(mod, 3, out _), "apply");
            EditAvfx(mod, Fixtures.PicklesAvfx[0], root => { });
            var upd = AvfxChunk.Parse(before[Fixtures.PicklesAvfx[0]]);
            Retarget(upd, 0, 0, 7);
            File.WriteAllBytes(mod.Full(Fixtures.PicklesAvfx[0]), upd.ToBytes());
            var site = mod.Scan(mod.NewHost());
            var status = DjTableAnimationCore.Status(site);
            var v = TableCard.Describe(status, site.Mode, site.HasState, false);
            Check(status.Kind == AnimationStatusKind.Unsupported && site.HasState, "Unsupported with state");
            Check(v.RestoreEnabled && !v.AnimationAllowed && v.TableChangeAllowed && v.StillRoute == StillRoute.PasteThenRestore, "card");
            var snap = mod.Snapshot();
            Check(!ApplyFrames(mod, 2, out _, site: site) && mod.Snapshot().SequenceEqual(snap), "apply refused, nothing written");
            Check(Restore(mod, out var w, out string e, mod.NewHost()) && w.Count == 1, "restore warns once " + e);
            Check(Fixtures.PicklesAvfx.Skip(1).All(r => mod.Read(r).AsSpan().SequenceEqual(before[r])), "others pristine");
            Check(!Directory.Exists(mod.StateFolder) && !Directory.Exists(mod.Full(DjAnimationScan.FrameFolder)), "nothing left");
        });

        Add("MissingListedAvfx", "EDGE-animated-table-47 AC-animated-table-11", () =>
        {
            using (var mod = Fixtures.Pickles())
            {
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                Check(ApplyFrames(mod, 3, out _), "apply");
                File.Delete(mod.Full(Fixtures.PicklesAvfx[0]));
                var site = mod.Scan(mod.NewHost());
                var lala = site.Avfx.First(a => a.Relative == Fixtures.PicklesAvfx[0]);
                Check(lala.CurrentSha == null && !lala.Unreadable && site.Warnings.Count == 1, "missing entry and one warning");
                var status = DjTableAnimationCore.Status(site);
                Check(status.Kind == AnimationStatusKind.Interrupted && TableCard.Describe(status, site.Mode, site.HasState, false).RestoreEnabled, "Interrupted");
                Check(DjTableAnimationCore.Restore(mod.Host, site, out var w, out string e) && w.Any(x => x.StartsWith("FileMissing")), "restore names it " + e);
                before.Remove(Fixtures.PicklesAvfx[0]);
                CheckPristine(mod, before, groups, "after restore");
                Check(!File.Exists(mod.Full(Fixtures.PicklesAvfx[0])), "Lala stays absent");
            }
            using (var mod = Fixtures.Pickles())
            {
                File.Delete(mod.Full(Fixtures.PicklesAvfx[0]));
                var site = mod.Scan();
                Check(site.Avfx.First(a => a.Relative == Fixtures.PicklesAvfx[0]).CurrentSha == null && site.Warnings.Count == 0, "no state: no warning");
            }
        });

        Add("UnreadableListedAvfx", "EDGE-animated-table-49 AC-animated-table-11", () =>
        {
            using (var mod = Fixtures.Pickles())
            {
                var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
                var groups = mod.Groups();
                Check(ApplyFrames(mod, 12, out _), "apply");
                var snap = mod.Snapshot();
                var host = mod.NewHost();
                host.ThrowOn = c => c.Kind == CallKind.Read && c.Path.EndsWith(Fixtures.PicklesAvfx[0]);
                var site = mod.Scan(host);
                var lala = site.Avfx.First(a => a.Relative == Fixtures.PicklesAvfx[0]);
                Check(lala.CurrentSha == null && lala.Unreadable && site.Warnings.Count == 0, "unreadable entry, no warning");
                Check(DjTableAnimationCore.Status(site).Kind == AnimationStatusKind.Interrupted, "Interrupted");
                Check(!DjTableAnimationCore.Apply(host, site, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out string e)
                    && e.StartsWith("FileUnreadable") && e.Contains("laladjtable"), "apply refused " + e);
                Check(!DjTableAnimationCore.Restore(host, site, out _, out e) && e.StartsWith("FileUnreadable"), "restore refused " + e);
                Check(!host.Log.Any(c => c.Kind != CallKind.Read) && mod.Snapshot().SequenceEqual(snap), "nothing written");

                var host2 = mod.NewHost();
                host2.ThrowOn = c => c.Kind == CallKind.Read && c.Path.EndsWith(Fixtures.PicklesAvfx[0]) && c.Index == 2;
                var site2 = mod.Scan(host2);
                Check(DjTableAnimationCore.Status(site2).Kind == AnimationStatusKind.Ours, "second-call fault: scan OK");
                Check(!DjTableAnimationCore.Apply(host2, site2, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out e)
                    && e.StartsWith("FileUnreadable"), "snapshot re-read refused " + e);
                Check(!host2.Log.Any(c => c.Kind != CallKind.Read), "nothing written");

                var site3 = mod.Scan(mod.NewHost());
                Check(DjTableAnimationCore.Status(site3).Kind == AnimationStatusKind.Ours, "fault lifted: Ours(12)");
                Check(DjTableAnimationCore.Restore(mod.Host, site3, out _, out e), e);
                CheckPristine(mod, before, groups, "after restore");
            }
            using (var mod = Fixtures.Pickles())
            {
                var host = mod.Host;
                host.ThrowOn = c => c.Kind == CallKind.Read && Fixtures.PicklesAvfx.Any(r => c.Path.EndsWith(r));
                var site = mod.Scan(host);
                Check(site.Avfx.Count(a => a.Unreadable) == 4 && site.Problem?.StartsWith("FileUnreadable") == true, "no state: unreadable reason");
                Check(!DjTableAnimationCore.Apply(host, site, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out string e)
                    && e.StartsWith("FileUnreadable"), "apply names the file");
            }
        });

        Add("ScanFailedView", "EDGE-animated-table-48", () =>
        {
            using (var mod = Fixtures.Pickles())
            {
                Check(ApplyFrames(mod, 3, out _), "apply");
                File.WriteAllText(DjAnimationScan.StatePath(mod.StateFolder), "{");
                bool threw = false;
                try { mod.Scan(); } catch { threw = true; }
                Check(threw && DjAnimationScan.TryReadState(mod.StateFolder) == null && !TableCard.ScanFailed(false).RestoreEnabled, "bad state.json");
            }
            using (var mod = Fixtures.Pickles())
            {
                Check(ApplyFrames(mod, 3, out _), "apply");
                File.WriteAllText(mod.Full("meta.json"), "{");
                bool threw = false;
                try { mod.Scan(); } catch { threw = true; }
                Check(threw && DjAnimationScan.TryReadState(mod.StateFolder) != null, "bad manifest, state readable");
                var v = TableCard.ScanFailed(true);
                Check(v.RestoreEnabled && !v.TableChangeAllowed, "ScanFailed(true)");
            }
        });

        Add("OrphanedDetected", "AC-animated-table-10 EDGE-animated-table-30", () =>
        {
            using var mod = Fixtures.Pickles();
            Check(ApplyFrames(mod, 3, out _), "apply");
            Directory.Delete(mod.StateFolder, true);
            var snap = mod.Snapshot();
            var site = mod.Scan(mod.NewHost());
            var status = DjTableAnimationCore.Status(site);
            var v = TableCard.Describe(status, site.Mode, site.HasState, true);
            Check(status.Kind == AnimationStatusKind.Orphaned && v.StateKey == TableText.Orphaned && !v.TableChangeAllowed
                && !v.RestoreEnabled && !v.AnimationAllowed && v.StillRoute == null, "Orphaned card");
            Check(!DjTableAnimationCore.Apply(mod.Host, site, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out string e) && e.StartsWith("Orphaned"), "apply refused");
            Check(!DjTableAnimationCore.Restore(mod.Host, site, out _, out e) && e.StartsWith("Orphaned"), "restore refused");
            Check(mod.Snapshot().SequenceEqual(snap) && !Directory.Exists(mod.StateFolder), "nothing written");
        });

        Add("PristineMissingOrphaned", "EDGE-animated-table-30", () =>
        {
            using var mod = Fixtures.Pickles();
            Check(ApplyFrames(mod, 3, out _), "apply");
            foreach (var f in Directory.GetFiles(Path.Combine(mod.StateFolder, "pristine"))) File.Delete(f);
            Check(StatusOf(mod) == AnimationStatusKind.Orphaned, "Orphaned");
        });

        Add("ForeignTokenOrphaned", "EDGE-animated-table-30", () =>
        {
            using var mod = Fixtures.Pickles();
            Check(ApplyFrames(mod, 3, out _), "apply");
            var st = ReadState(mod);
            st.ModToken = "deadbeef";
            File.WriteAllBytes(DjAnimationScan.StatePath(mod.StateFolder), st.ToBytes());
            var site = mod.Scan(mod.NewHost());
            Check(DjTableAnimationCore.Status(site).Kind == AnimationStatusKind.Orphaned, "Orphaned");
            Check(!DjTableAnimationCore.Apply(mod.Host, site, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out _)
                && !DjTableAnimationCore.Restore(mod.Host, site, out _, out _), "apply and restore refuse");
        });

        Add("RenamedModFolder", "EDGE-animated-table-36", () =>
        {
            using var mod = Fixtures.Pickles("A");
            Check(ApplyFrames(mod, 3, out _), "apply under A");
            var stateA = mod.Snapshot().Where(kv => kv.Key.StartsWith(mod.StateFolder)).ToList();
            string stateFolderA = mod.StateFolder;
            mod.StateFolder = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(mod.StateFolder))!, "B", "animation");
            var snap = mod.Snapshot();
            var site = mod.Scan(mod.NewHost());
            Check(DjTableAnimationCore.Status(site).Kind == AnimationStatusKind.Orphaned, "Orphaned under B");
            Check(!DjTableAnimationCore.Apply(mod.Host, site, Fixtures.Frames(2), Fixtures.Timeline(2), FitMode.Fill, null, out _, out _)
                && !DjTableAnimationCore.Restore(mod.Host, site, out _, out _), "refused");
            Check(mod.Snapshot().SequenceEqual(snap), "unchanged");
            Check(stateA.All(kv => File.Exists(kv.Key)) && Directory.Exists(stateFolderA), "state A untouched");
        });

        Add("RescanAfterApply", "EDGE-animated-table-37", () =>
        {
            using (var mod = Fixtures.Moon())
            {
                Check(ApplyFrames(mod, 48, out _), "moon apply");
                var site = mod.Scan(mod.NewHost());
                var info = site.Animatable.First().Inspect!.Info!;
                Check(site.Mode == AnimationMode.InPlace && site.Capacity == 48 && info.AnimationSet.SequenceEqual(new[] { 7 }.Concat(Enumerable.Range(9, 48)))
                    && DjTableAnimationCore.Status(site) is { Kind: AnimationStatusKind.Ours, Frames: 48 } && site.Animatable.All(a => a.Ours), "moon rescan");
                Check(DjTableAnimationCore.Apply(mod.Host, site, Fixtures.Frames(1), new FrameTimeline(new[] { 0 }, 1), FitMode.Fill, null, out _, out string e), "Ours(1) " + e);
                Check(ApplyFrames(mod, 48, out e, mod.NewHost()), "48 again " + e);
                var root = AvfxChunk.Parse(mod.Read(Fixtures.MoonCat));
                Check(root.ReadInt("PrCn") == 57 && root.ReadInt("TxCn") == 74 && root.All("Emit").First().ReadInt("PrCn") == 56, "57/74/56");
                var entry = mod.Scan(mod.NewHost()).Animatable.First();
                Check(AvfxTableAnimation.Build(mod.Read(Fixtures.MoonCat), entry.Inspect!.Info!, new[] { "x" }, new FrameTimeline(new[] { 0 }, 1), out _) == null, "Build refuses non-baseline bytes");
            }
            using (var mod = Fixtures.Pickles())
            {
                Check(ApplyFrames(mod, 12, out _), "pickles apply");
                var site = mod.Scan(mod.NewHost());
                Check(site.Mode == AnimationMode.Append && site.Capacity == 48 && site.Animatable.All(a => a.Inspect!.Info!.AnimationSet.SequenceEqual(new[] { 7 }))
                    && DjTableAnimationCore.Status(site) is { Kind: AnimationStatusKind.Ours, Frames: 12 }, "pickles rescan");
            }
        });

        Add("StillOnOursPasteFails", "EDGE-animated-table-35", () =>
        {
            using var mod = Fixtures.Pickles();
            Check(ApplyFrames(mod, 3, out _), "apply");
            var snap = mod.Snapshot();
            Check(!DjTableAnimationCore.PasteThenRestore(mod.Host, mod.Scan(), () => throw new IOException("eq55 locked"), out _, out string e)
                && e.Contains("eq55 locked"), "paste failure reported");
            Check(mod.Snapshot().SequenceEqual(snap) && StatusOf(mod) == AnimationStatusKind.Ours, "animation untouched");
        });

        Add("StillOnOursRestoreFails", "EDGE-animated-table-35", () =>
        {
            using var mod = Fixtures.Pickles();
            var before = AvfxBytes(mod, Fixtures.PicklesAvfx);
            var groups = mod.Groups();
            var eq55 = mod.Read(mod.Table.Relative);
            Check(ApplyFrames(mod, 3, out _), "apply");
            var still = Fixtures.Frames(1)[0];
            var host = mod.NewHost();
            host.ThrowOn = c => c.Kind == CallKind.EditGroups;
            Check(!DjTableAnimationCore.PasteThenRestore(host, mod.Scan(host), () => { PasteStill(mod, still); return null; }, out _, out _),
                "restore fails after its avfx writes");
            var site = mod.Scan(mod.NewHost());
            var status = DjTableAnimationCore.Status(site);
            var v = TableCard.Describe(status, site.Mode, site.HasState, true);
            Check(status.Kind == AnimationStatusKind.Interrupted && v.RestoreEnabled && v.StateKey == TableText.Interrupted, "Interrupted");
            Check(Fixtures.PicklesAvfx.All(r => mod.Read(r).AsSpan().SequenceEqual(before[r])), "effects pristine");
            Check(mod.Groups().Any(g => g.ToString().Contains("ppe_")) && Directory.GetFiles(mod.Full(DjAnimationScan.FrameFolder)).Length == 3, "keys and files remain");
            var rect = TexPaste.ReadRect(mod.Read(mod.Table.Relative), mod.Table.Surface.Rect, out _)!;
            Check(Fixtures.Psnr(rect, mod.Table.Surface.ToTextureSpace(still, FitMode.Fill)) > 30, "eq55 holds the still");
            Check(Restore(mod, out _, out string e, mod.NewHost()), e);
            File.WriteAllBytes(mod.Full(mod.Table.Relative), eq55);
            CheckPristine(mod, before, groups, "after restore");
        });
    }
}
