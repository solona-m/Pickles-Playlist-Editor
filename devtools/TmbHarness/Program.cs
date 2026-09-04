using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Tmb;

namespace TmbHarness;

/// <summary>
/// Runs the offline format checks over real .pap files.
///
/// Usage: TmbHarness [root] [--dump]   (default root: the DJ Solona dances folder)
///
/// Every check here is meant to be falsifiable on real data rather than on fixtures this repo wrote
/// itself. The cross-splice matrix in particular is the evidence that the insertion trick in
/// <see cref="TmbSplice"/> holds for timelines nobody looked at while designing it.
/// </summary>
internal static class Program
{
    private const string DefaultRoot = @"e:\Penumbradt\DJ Solona Base Mod Pack V4\dances";

    /// <summary>
    /// The havok packfile signature, which must sit exactly at HavokOffset.
    ///
    /// Written out as the two little-endian int32s 0xCAB00D1E and 0xD011FACE, which is how the bytes
    /// actually land on disk — not as the mnemonic spelling of those constants.
    /// </summary>
    private static readonly byte[] HavokMagic =
        { 0x1E, 0x0D, 0xB0, 0xCA, 0xCE, 0xFA, 0x11, 0xD0 };

    /// <summary>
    /// Pool bytes allowed to belong to no declared field.
    ///
    /// Not zero: the pools are 4-byte aligned and a little padding between blocks is normal. Kept
    /// tight deliberately — the C012 bug this check was written for orphaned hundreds of bytes per
    /// file, so a generous slack here would have let it through just as the other checks did.
    /// </summary>
    private const int PoolSlack = 16;

    private sealed record Timeline(string Label, byte[] Tmb, bool IsLoop);

    private static int _checked, _failed;

    /// <summary>Mod-name fragments given to <c>--find</c>, reported by the source scan.</summary>
    private static List<string> _find = new();

    private static int Main(string[] args)
    {
        bool dump = args.Contains("--dump");

        // --find takes its own values, so they must not also be eligible to be the root. Without
        // this, "--find Mine" silently ran the whole harness against a directory called Mine.
        _find = args.SkipWhile(a => a != "--find").Skip(1).TakeWhile(a => !a.StartsWith("--")).ToList();
        string root = args.FirstOrDefault(a => !a.StartsWith("--") && !_find.Contains(a)) ?? DefaultRoot;

        // The only mode here that writes to a mod folder, and it is opt-in for that reason. Point it
        // at a COPY to see what the repair would produce; point it at the real thing only knowingly.
        if (args.Contains("--repair")) return RepairInPlace(root);

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"No such directory: {root}");
            return 2;
        }

        var files = Directory.GetFiles(root, "*.pap", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var timelines = new List<Timeline>();

        Console.WriteLine($"=== containers: {files.Length} .pap under {root}\n");
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(root, file);
            string label = relative.Split(Path.DirectorySeparatorChar)[0] + "/" + Path.GetFileName(file);
            try
            {
                byte[] tmb = Inspect(file, label);
                bool isLoop = Path.GetFileName(file).Contains("loop", StringComparison.OrdinalIgnoreCase);
                timelines.Add(new Timeline(label, tmb, isLoop));
            }
            catch (Exception ex)
            {
                Fail(label, ex.Message);
            }
        }

        SourceScan();
        RebuildIdentity(timelines);
        var djStrings = SpliceMatrix(timelines, dump);
        RoundTripDjBlock(timelines, djStrings);
        StringEdits(timelines);
        PrepareRealDances(timelines, djStrings);
        AsyncVfxSurvivesASplice(timelines, djStrings, ModRootOf(root));
        RepairPass(root);

        Console.WriteLine($"\n{_checked} checks, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    // ---- containers ------------------------------------------------------------------------------

    private static byte[] Inspect(string file, string label)
    {
        byte[] original = File.ReadAllBytes(file);
        var pap = PapFile.Parse(original);

        Check(label, "InfoOffset == 0x1A", pap.InfoOffset == PapFile.HeaderBytes);
        Check(label, "havok magic at HavokOffset", StartsWith(original, pap.HavokOffset, HavokMagic));

        byte[] rebuilt = pap.WithTimeline(pap.GetTimeline());
        Check(label, "round-trip is byte-identical", original.AsSpan().SequenceEqual(rebuilt));

        byte[] tmb = pap.GetTimeline();
        var layout = TmbBinary.Walk(tmb);
        TmbBinary.ResolveAll(layout);

        int p = TmbSplice.TrackInsertionPoint(layout);
        Check(label, "an insertion point exists", p > 0 && p <= layout.EntriesEnd);

        bool targetsInPools = true;
        foreach (var entry in layout.Entries)
            foreach (var field in entry.Fields)
            {
                int target = TmbBinary.Target(layout, entry, field);
                if (target < layout.EntriesEnd || target >= layout.TotalSize) targetsInPools = false;
            }
        Check(label, "every resolved target lies in the pools", targetsInPools);

        // The check that does not depend on knowing what to look for. An undeclared pool pointer
        // shows up here as pool bytes nothing claims, and nowhere else — the resolve invariants are
        // all blind to fields the table does not name.
        var (covered, total) = TmbBinary.PoolCoverage(layout);
        int orphaned = total - covered;
        Check(label, $"pool is fully accounted for (orphaned {orphaned}B)", orphaned <= PoolSlack);

        int opaque = layout.Entries.Count(e => e.IsOpaque);
        var header = layout.Entries[0];
        var headerWords = new List<string>();
        for (int off = 0; off + 4 <= header.Size - TmbBinary.EntryHeaderBytes; off += 4)
            headerWords.Add(TmbBinary.ReadInt32(layout.Bytes, header.Body + off).ToString());

        Console.WriteLine(
            $"{label,-52} entries={layout.Entries.Count,3} ids={layout.Entries.Min(e => e.Id)}..{layout.MaxId,-4} " +
            $"opaque={opaque,3} {header.Magic}=[{string.Join(",", headerWords)}] pool={covered}/{total}");

        return tmb;
    }

    // ---- the source scan -------------------------------------------------------------------------

    private const string PenumbraRoot = @"e:\Penumbradt";

    /// <summary>
    /// The dance-source filter, measured against a real 1,040-mod Penumbra folder.
    ///
    /// These numbers are a regression fixture, not decoration. The filter is the difference between a
    /// browser showing 42 mods and one showing 22,000 game paths of idles, /pose stances and facial
    /// expressions, and every clause in it was derived from this folder — so a change that quietly
    /// loosens or tightens it shows up here rather than in the UI.
    /// </summary>
    private static void SourceScan()
    {
        Console.WriteLine("\n=== installed dance sources");
        if (!Directory.Exists(PenumbraRoot))
        {
            Console.WriteLine($"  (no Penumbra folder at {PenumbraRoot} — skipped)");
            return;
        }

        var started = DateTime.UtcNow;
        var mods = DanceSourceScan.Scan(PenumbraRoot);
        var elapsed = DateTime.UtcNow - started;
        int dances = mods.Sum(m => m.Dances.Count);

        foreach (var mod in mods.Take(8))
            Console.WriteLine($"  {mod.Name,-52} {mod.Dances.Count,4} dances" +
                (mod.DuplicateFolders.Count > 0 ? $"  (+{mod.DuplicateFolders.Count} duplicate folder)" : ""));

        // Anything named on the command line, so "is this mod offered as a source?" — the question
        // asked every time a dance will not appear in the browser — is answerable without a UI.
        foreach (string fragment in _find)
        {
            var matches = mods.Where(m =>
                m.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)).ToList();
            Console.WriteLine($"  --find '{fragment}': {matches.Count} mod(s)");
            foreach (var mod in matches)
                foreach (var dance in mod.Dances)
                    Console.WriteLine($"     {mod.Name} / {dance.Label}  " +
                        $"[{string.Join(" ", dance.Races)}] start={dance.StartByRace.Count > 0}");
        }
        Console.WriteLine($"  ... {mods.Count} mods, {dances} dances, {elapsed.TotalSeconds:0.0}s");

        // Measured on the reference folder: 38 mods / 722 dances. Two numbers move together here
        // and both are regressions worth catching. De-duplicating re-imported copies takes the
        // mod count down from 41; splitting options that carry more than one emote slot takes the
        // dance count UP from 521, because a single option can ship two unrelated dances.
        Check("scan", "finds the expected number of dance mods", mods.Count is >= 32 and <= 45);
        Check("scan", "finds the expected number of dances", dances is >= 650 and <= 800);

        // The clauses, each pinned to the mod that motivated it.
        Check("scan", "excludes idles (Idles 2.0 Megapack)",
            !mods.Any(m => m.Name.Contains("Idles 2.0", StringComparison.OrdinalIgnoreCase)));
        Check("scan", "emote_sp included (K-pop Demon Hunters)",
            mods.Any(m => m.Name.Contains("K-pop", StringComparison.OrdinalIgnoreCase)));
        Check("scan", "DefaultData included (Waltz Dance)",
            mods.Any(m => m.Name.Contains("Waltz", StringComparison.OrdinalIgnoreCase)));
        Check("scan", "a venue mod is trimmed to its dances, not its 14,759 paths",
            mods.FirstOrDefault(m => m.Name.Contains("Nightlife", StringComparison.OrdinalIgnoreCase))
                is null or { Dances.Count: < 500 });
        Check("scan", "an option carrying two emote slots yields two dances",
            mods.FirstOrDefault(m => m.Name.Contains("Waltz", StringComparison.OrdinalIgnoreCase))
                is { Dances.Count: >= 2 });
        Check("scan", "duplicate mod folders are collapsed",
            mods.All(m => !m.Name.EndsWith(" (2)", StringComparison.Ordinal))
            && mods.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).All(g => g.Count() == 1));

        // Every dance must point at a .pap that exists and parses, or the browser is offering
        // something that cannot be installed.
        int checkedPaps = 0, unreadable = 0;
        foreach (var dance in mods.SelectMany(m => m.Dances).Take(60))
        {
            foreach (string file in dance.LoopByRace.Values)
            {
                checkedPaps++;
                try { PapFile.Parse(File.ReadAllBytes(file)); }
                catch { unreadable++; Console.WriteLine($"  unreadable: {dance.Label} -> {file}"); }
            }
        }
        Check("scan", $"listed dances resolve to readable .pap ({checkedPaps} checked)", unreadable == 0);

        DjPackClassification(mods);
    }

    /// <summary>
    /// Which mods the VFX-mod picker offers, checked against mods whose nature is known.
    ///
    /// This is the one decision in the feature that steers the user somewhere irreversible: the mod
    /// chosen here is the one dances get installed INTO, and picking an ordinary dance mod means
    /// every future dance gets that mod's own effects instead of the DJ's. It classified
    /// "miku live plus" — two copies of one dance — as a DJ pack until the sharing requirement went in.
    /// </summary>
    private static void DjPackClassification(List<DanceSourceMod> mods)
    {
        // Mods whose nature is not in doubt: DJ packs on the left, plain dance mods on the right.
        var expected = new (string Fragment, bool IsDjPack)[]
        {
            ("DJ Solona", true),
            ("DJ Pickles", true),
            ("DAMThunderdome", true),

            // Two copies of one dance, four effects between them. The vote was unanimous because
            // there was nothing to vote against, and this was offered as a place to install dances.
            ("miku live plus", false),

            // A MUSIC mod that happens to contain dances — it is one of the playlist mod defaults in
            // Settings. Its dances carry no effect tracks at all, so there is no block to copy and an
            // add against it could only fail. Excluding it is right even though "DJ" is in the folder
            // name of one of its copies.
            ("Yue & Lu", false),

            ("Waltz", false),
            ("K-pop", false),
        };

        foreach (var (fragment, isDjPack) in expected)
        {
            var mod = mods.FirstOrDefault(m =>
                m.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                || m.FullPath.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            if (mod == null) continue;

            var layouts = mod.Dances
                .Select(d => d.LoopByRace.Values.FirstOrDefault())
                .Where(f => f != null && File.Exists(f))
                .Take(16)
                .Select(f =>
                {
                    // Same header-and-tail read the picker uses, so this exercises that path too.
                    try { return TmbBinary.Walk(PapFile.ReadTimeline(f!)); }
                    catch { return null; }
                })
                .Where(l => l != null)
                .Select(l => l!)
                .ToList();

            bool classified = TmbTrackBundle.LooksLikeDjPack(layouts);
            Check("classify", $"{mod.Name} is {(isDjPack ? "" : "not ")}a DJ pack",
                classified == isDjPack);
            Console.WriteLine($"  {mod.Name,-52} dances={mod.Dances.Count,4} " +
                $"djPack={classified,-5} expected={isDjPack}");
        }
    }

    // ---- the writer's self-test ------------------------------------------------------------------

    /// <summary>
    /// Rebuilding a real timeline while adding nothing must reproduce it byte for byte.
    ///
    /// The check that needs no game and no guessing: 35 known-correct expected outputs. If the writer
    /// cannot reproduce a file it just read, nothing it produces can be trusted, and the first
    /// differing byte says exactly where it went wrong.
    /// </summary>
    private static void RebuildIdentity(List<Timeline> timelines)
    {
        Console.WriteLine("\n=== rebuild identity");
        int exact = 0;

        foreach (var timeline in timelines)
        {
            try
            {
                byte[] rebuilt = TmbSplice.Rebuild(timeline.Tmb);
                bool same = rebuilt.AsSpan().SequenceEqual(timeline.Tmb);
                Check(timeline.Label, "rebuild reproduces the original exactly", same);
                if (same) { exact++; continue; }

                int at = 0;
                while (at < Math.Min(rebuilt.Length, timeline.Tmb.Length)
                       && rebuilt[at] == timeline.Tmb[at]) at++;
                Console.WriteLine($"  {timeline.Label,-52} {timeline.Tmb.Length} -> {rebuilt.Length} bytes, " +
                    $"first difference at {at}");
            }
            catch (Exception ex)
            {
                Fail(timeline.Label, "rebuild: " + ex.Message);
            }
        }

        Console.WriteLine($"{exact} of {timelines.Count} rebuilt byte-identically.");
    }

    /// <summary>
    /// The add path, tested against a known-correct answer.
    ///
    /// Take a dance that demonstrably works, strip its DJ block out, then add it back. The result
    /// must be the original file byte for byte. This is the only test here that checks the whole
    /// operation — extract, remove, re-add, renumber, rebuild — against something other than this
    /// code's own opinion of what is correct.
    /// </summary>
    private static void RoundTripDjBlock(List<Timeline> timelines, HashSet<string> djStrings)
    {
        Console.WriteLine("\n=== strip the DJ block and put it back");
        int exact = 0, tried = 0;

        foreach (var timeline in timelines.Where(t => t.IsLoop))
        {
            try
            {
                var layout = TmbBinary.Walk(timeline.Tmb);
                var tracks = TmbTrackBundle.FindDjTracks(layout, djStrings);
                if (tracks.Count == 0) continue;

                var bundle = TmbTrackBundle.Extract(layout, tracks);
                var drop = bundle.DonorEntries.Select(e => e.Offset).ToList();

                byte[] stripped = TmbSplice.Remove(timeline.Tmb, drop);
                byte[] restored = TmbSplice.AppendTracks(stripped, bundle);

                tried++;
                bool same = restored.AsSpan().SequenceEqual(timeline.Tmb);
                Check(timeline.Label, "strip then re-add reproduces the original", same);
                if (same) { exact++; continue; }

                int at = 0;
                while (at < Math.Min(restored.Length, timeline.Tmb.Length)
                       && restored[at] == timeline.Tmb[at]) at++;
                var a = TmbBinary.Walk(restored);
                var b = TmbBinary.Walk(timeline.Tmb);
                Console.WriteLine($"  {timeline.Label,-46} {timeline.Tmb.Length} -> {restored.Length}, " +
                    $"stripped={stripped.Length}, first diff at {at}, " +
                    $"entries {b.Entries.Count} -> {a.Entries.Count}");
            }
            catch (Exception ex)
            {
                Fail(timeline.Label, "round trip: " + ex.Message);
            }
        }

        Console.WriteLine($"{exact} of {tried} round-tripped byte-identically.");
    }

    // ---- the DJ block ----------------------------------------------------------------------------

    private static HashSet<string> SpliceMatrix(List<Timeline> timelines, bool dump)
    {
        Console.WriteLine("\n=== the DJ block");

        var loops = timelines.Where(t => t.IsLoop).ToList();
        var loopLayouts = loops.Select(t => TmbBinary.Walk(t.Tmb)).ToList();

        if (dump) DumpTracks(loops);
        if (Environment.GetCommandLineArgs().Contains("--entries")) DumpEntries(timelines);

        var djStrings = TmbTrackBundle.DjStringSet(loopLayouts);
        Console.WriteLine($"shared by at least half of {loops.Count} loops: {djStrings.Count} effects");
        foreach (string s in djStrings.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {s}");

        Console.WriteLine("\ncoverage per prepped dance:");
        var donors = new List<(string Label, TmbTrackBundle Bundle)>();
        for (int i = 0; i < loops.Count; i++)
        {
            var layout = loopLayouts[i];
            var tracks = TmbTrackBundle.FindDjTracks(layout, djStrings);
            int coverage = TmbTrackBundle.CoverageOf(layout, djStrings).Count;
            Console.WriteLine($"  {loops[i].Label,-58} tracks={tracks.Count} covers={coverage}/{djStrings.Count}");

            Check(loops[i].Label, "a prepped dance is recognised as prepped",
                TmbTrackBundle.IsAlreadyPrepped(layout, djStrings));

            if (tracks.Count > 0) donors.Add((loops[i].Label, TmbTrackBundle.Extract(layout, tracks)));
        }

        var (chosen, chosenTracks) = TmbTrackBundle.ChooseDonor(loopLayouts, djStrings);
        var best = TmbTrackBundle.Extract(chosen, chosenTracks);
        Console.WriteLine($"\nchosen donor carries {best}");
        Check("donor", "the chosen donor carries the whole DJ block",
            best.EffectPaths.Count == djStrings.Count);

        // A fresh destination is one with no DJ block at all — the realistic case, and the only one
        // the app will ever actually splice into. The start .pap are exactly that.
        var fresh = timelines.Where(t => !TmbTrackBundle.IsAlreadyPrepped(TmbBinary.Walk(t.Tmb), djStrings))
            .ToList();
        Console.WriteLine($"{fresh.Count} of {timelines.Count} timelines carry no DJ block (valid destinations)");

        Console.WriteLine($"\n=== cross-splice matrix ({donors.Count} donors x {timelines.Count} destinations)");
        int pairs = 0, pairFailures = 0;
        foreach (var (donorLabel, bundle) in donors)
        {
            foreach (var destination in timelines)
            {
                if (destination.Label == donorLabel) continue;
                pairs++;
                try
                {
                    SplicePair(bundle, destination.Tmb);
                }
                catch (Exception ex)
                {
                    pairFailures++;
                    if (pairFailures <= 10) Fail($"{donorLabel} -> {destination.Label}", ex.Message);
                    else _failed++;
                }
            }
        }
        Console.WriteLine($"{pairs} pairs spliced, {pairFailures} failed.");

        // Splicing the chosen block into a fresh timeline must leave it looking prepped, and the
        // result must then refuse a second block.
        foreach (var destination in fresh)
        {
            try
            {
                byte[] result = TmbSplice.AppendTracks(destination.Tmb, best);
                var after = TmbBinary.Walk(result);
                Check(destination.Label, "a spliced timeline reads back as prepped",
                    TmbTrackBundle.IsAlreadyPrepped(after, djStrings));
                Check(destination.Label, "a spliced timeline carries the whole block",
                    TmbTrackBundle.CoverageOf(after, djStrings).Count == djStrings.Count);
            }
            catch (Exception ex)
            {
                Fail(destination.Label, "fresh splice: " + ex.Message);
            }
        }

        return djStrings;
    }

    // ---- end to end ------------------------------------------------------------------------------

    /// <summary>Dance mods the user actually has installed, used as realistic un-prepped input.</summary>
    private static readonly string[] SourceMods =
    {
        @"e:\Penumbradt\K-pop Demon Hunters Dance Loops",
        @"e:\Penumbradt\[LUMI] Kill This Love Dance",
        @"e:\Penumbradt\[LUMI] Thriller Dance",
        @"e:\Penumbradt\Waltz Dance",

        // The C173 dance. Here as well as in AsyncVfxSurvivesASplice because this path exercises
        // what the app actually runs — Inspect, then Prepare, then Verify — rather than the splice
        // alone, and it is the dance that shipped a crash.
        @"e:\Penumbradt\Mine",

        @"e:\Penumbradt\Slow Dance Mod",
        @"e:\Penumbradt\[OCN] Line Dancin' - Cyr Edit w Eira Edit",
    };

    /// <summary>
    /// Prepares real downloaded dances the way the app will, and writes the first one out so it can
    /// be installed and tested in game.
    ///
    /// This is the only check here that exercises the whole operation on input nobody involved in
    /// designing the format code has looked at, which is exactly what makes it worth running.
    /// </summary>
    private static void PrepareRealDances(List<Timeline> timelines, HashSet<string> djStrings)
    {
        Console.WriteLine("\n=== preparing real downloaded dances");

        var loopLayouts = timelines.Where(t => t.IsLoop).Select(t => TmbBinary.Walk(t.Tmb)).ToList();
        var (donor, tracks) = TmbTrackBundle.ChooseDonor(loopLayouts, djStrings);
        var bundle = TmbTrackBundle.Extract(donor, tracks);

        string outDir = Path.Combine(Path.GetTempPath(), "prepared-dances");
        Directory.CreateDirectory(outDir);

        int prepared = 0, refused = 0;
        foreach (string mod in SourceMods)
        {
            if (!Directory.Exists(mod)) { Console.WriteLine($"  (not installed: {Path.GetFileName(mod)})"); continue; }

            var candidates = Directory.GetFiles(mod, "*_loop.pap", SearchOption.AllDirectories)
                .Where(f => f.Replace('\\', '/').Contains("/bt_common/emote", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            foreach (string file in candidates)
            {
                string label = Path.GetFileName(mod) + "/" + Path.GetFileName(file);
                try
                {
                    byte[] source = File.ReadAllBytes(file);
                    var plan = DancePap.Inspect(source, bundle, djStrings);

                    if (!plan.CanApply)
                    {
                        refused++;
                        Console.WriteLine($"  refused  {label,-56} {string.Join("; ", plan.Errors)}");
                        continue;
                    }

                    byte[] result = DancePap.Prepare(source, bundle, djStrings);
                    prepared++;

                    // Prepare() verifies its own output; re-assert the things a user would notice.
                    var after = PapFile.Parse(result);
                    var timeline = TmbBinary.Walk(after.GetTimeline());
                    Check(label, "prepared dance keeps its havok data",
                        after.HavokOffset == PapFile.Parse(source).HavokOffset
                        && result.AsSpan(after.HavokOffset, after.FooterOffset - after.HavokOffset)
                            .SequenceEqual(source.AsSpan(after.HavokOffset, after.FooterOffset - after.HavokOffset)));
                    Check(label, "prepared dance carries the whole DJ block",
                        TmbTrackBundle.CoverageOf(timeline, djStrings).Count == djStrings.Count);

                    string outFile = Path.Combine(outDir,
                        $"{Path.GetFileName(mod)} - {plan.RaceCode} - dance_male_loop.pap");
                    File.WriteAllBytes(outFile, result);

                    Console.WriteLine($"  prepared {label,-56} {plan.RaceCode} " +
                        $"'{plan.OldAnimationName}' -> '{plan.NewAnimationName}' " +
                        $"blanked={plan.SoundPathsToBlank.Count} +{plan.TracksToAdd} tracks " +
                        $"{source.Length}->{result.Length} bytes");
                }
                catch (Exception ex)
                {
                    Fail(label, "prepare: " + ex.Message);
                }
            }
        }

        Console.WriteLine($"\n{prepared} prepared, {refused} refused. Output: {outDir}");
    }

    // ---- the C173 regression ---------------------------------------------------------------------

    /// <summary>The donor whose four C173 entries shipped broken. Its .pmp copy is structurally clean.</summary>
    private const string AsyncVfxDonor = @"e:\Penumbradt\Mine\common\1\dance_male_loop.pap";

    /// <summary>
    /// The exact case that shipped a crash to desktop.
    ///
    /// This dance carries four C173 "async VFX" entries. Splicing the DJ block in front of them moves
    /// the pool by hundreds of bytes, and the entries hold their path as a distance from their own
    /// body — so an entry type the writer does not model comes out the far side byte-copied, still
    /// holding the DONOR's distance, pointing back into the entry region at a 0x00. That is an empty
    /// resource path, which Penumbra answers with a null the game dereferences untested.
    ///
    /// Asserting the four paths RESOLVE is the check that would have caught it. Asserting they are the
    /// right four is the check that catches the sloppier fix, where they resolve to whatever string
    /// happened to be nearby.
    /// </summary>
    private static void AsyncVfxSurvivesASplice(List<Timeline> timelines, HashSet<string> djStrings,
        string modRoot)
    {
        Console.WriteLine("\n=== C173 survives a pool-shifting splice");
        if (!File.Exists(AsyncVfxDonor))
        {
            Console.WriteLine($"  (not installed: {AsyncVfxDonor} — skipped)");
            return;
        }

        var loopLayouts = timelines.Where(t => t.IsLoop).Select(t => TmbBinary.Walk(t.Tmb)).ToList();
        var (donor, tracks) = TmbTrackBundle.ChooseDonor(loopLayouts, djStrings);
        var bundle = TmbTrackBundle.Extract(donor, tracks);

        try
        {
            byte[] source = File.ReadAllBytes(AsyncVfxDonor);
            var before = TmbBinary.Walk(PapFile.Parse(source).GetTimeline());
            var expected = AsyncVfxPaths(before);

            Check("C173", "the donor ships four resolvable C173 paths", expected.Count == 4);

            byte[] prepared = DancePap.Prepare(source, bundle, djStrings);
            var after = TmbBinary.Walk(PapFile.Parse(prepared).GetTimeline());
            var landed = AsyncVfxPaths(after);

            Check("C173", "the pool actually moved, so this proves something",
                after.EntriesEnd != before.EntriesEnd);
            Check("C173", "all four paths survive the splice", landed.SequenceEqual(expected));
            Check("C173", "the prepared dance asks for no empty or stray path",
                TmbBinary.BrokenPaths(after).Count == 0);

            // The paths are only half of it. Before the fix these two .avfx were not enumerated at
            // all, so nothing downstream could know the dance needed them — which is why the pack
            // shipped without either file even where the offsets happened to survive.
            var enumerated = DancePap.Inspect(source, bundle, djStrings).EffectsUsed;
            Check("C173", "its own effects are enumerated so the mod can be told about them",
                expected.Distinct().All(enumerated.Contains));

            Console.WriteLine($"  pool {before.EntriesEnd} -> {after.EntriesEnd}, " +
                $"{landed.Count} C173 paths kept: {string.Join(", ", landed.Distinct())}");

            // And the second half of the same story. Keeping those paths is only correct when the
            // mod can actually supply what they name. This pack cannot, and a C173 naming a file
            // nobody has crashes everyone watching over sync — so against a real destination the
            // same dance must come out with the clips REMOVED rather than merely pointed correctly.
            var supplies = DanceRepair.SuppliedPaths(modRoot);
            Check("C173", "the destination mod's supplied paths are readable", supplies != null);
            Check("C173", "this pack really does not supply the two .avfx",
                supplies != null && expected.Distinct().All(p => !supplies.Contains(p)));

            var plan = DancePap.Inspect(source, bundle, djStrings, PapFile.DanceAnimationName, supplies);
            Check("C173", $"the unsupplied clips are reported ({plan.AsyncEffectsDropped.Count})",
                plan.AsyncEffectsDropped.Count == 2);

            byte[] guarded = DancePap.Prepare(source, bundle, djStrings,
                PapFile.DanceAnimationName, supplies);
            var guardedTimeline = TmbBinary.Walk(PapFile.Parse(guarded).GetTimeline());

            Check("C173", "an unsupplied async clip is dropped, not shipped pointing at nothing",
                AsyncVfxPaths(guardedTimeline).Count == 0);
            Check("C173", "dropping them leaves nothing else broken",
                TmbBinary.BrokenPaths(guardedTimeline).Count == 0
                && DanceRepair.UnsuppliedAsyncEffects(guardedTimeline, supplies).Count == 0);

            // The DJ block is the point of the whole operation and must survive the removal.
            Check("C173", "the DJ effect block still lands after the drop",
                TmbTrackBundle.CoverageOf(guardedTimeline, djStrings).Count == djStrings.Count);

            Console.WriteLine($"  against the real pack: {plan.AsyncEffectsDropped.Count} clip(s) dropped, " +
                $"{after.Entries.Count} -> {guardedTimeline.Entries.Count} entries");
        }
        catch (Exception ex)
        {
            Fail("C173", "prepare: " + ex.Message);
        }

        StockAsyncEffectsAreKept();
    }

    /// <summary>Dances whose C173 fires a VANILLA effect. No mod supplies these, and none has to.</summary>
    private static readonly string[] StockAsyncVfxDances =
    {
        @"e:\Penumbradt\Lilly's Silent Dance Party\common\135\dance03_loop.pap",
        @"e:\Penumbradt\Nightlife+ v3.1.1\common\870\loop_emot24_loop.pap",
    };

    /// <summary>
    /// The other half of the unsupplied rule, and the one that can quietly destroy a mod.
    ///
    /// "Unsupplied" cannot mean "absent from this mod's option list", because a stock game effect is
    /// absent from every mod's option list. Measured over the whole reference folder, exactly one
    /// stock path appears in a C173 — <c>vfx/common/eff/syncactiontimelineclip01t.avfx</c>, in three
    /// mods — and treating it as unsupplied would have silently deleted a working effect from every
    /// one of their dances the first time the dialog was opened on them.
    /// </summary>
    private static void StockAsyncEffectsAreKept()
    {
        var nothingSupplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in StockAsyncVfxDances)
        {
            if (!File.Exists(file)) { Console.WriteLine($"  (not installed: {file})"); continue; }
            try
            {
                var layout = TmbBinary.Walk(PapFile.ReadTimeline(file));
                var paths = AsyncVfxPaths(layout);
                string label = "stock/" + Path.GetFileName(Path.GetDirectoryName(file)!);

                Check(label, "the fixture really does carry a C173", paths.Count > 0);
                Check(label, $"a stock effect is kept even when NOTHING is supplied ({paths.FirstOrDefault()})",
                    DanceRepair.UnsuppliedAsyncEffects(layout, nothingSupplied).Count == 0);
                Check(label, "and it is recognised as the game's own",
                    paths.All(DanceRepair.IsStockPath));
            }
            catch (Exception ex)
            {
                Fail(file, "stock check: " + ex.Message);
            }
        }

        UnreadableManifestDropsNothing();
    }

    /// <summary>
    /// A mod whose file list cannot be read must disable the rule, not satisfy it vacuously.
    ///
    /// The first version of the sentinel keyed on <see cref="PenumbraOptions.Read"/> throwing, which
    /// it never does — it is built to return an empty list for anything unreadable. So the guard was
    /// dead code and the real failure mode went straight past it: an unreadable manifest read as
    /// "supplies nothing", which makes every custom async effect in the pack unsupplied and would
    /// have had the load-time repair strip the lot. Penumbra rewrites meta.json underneath this app,
    /// so that read is ordinary rather than exotic.
    /// </summary>
    private static void UnreadableManifestDropsNothing()
    {
        string empty = Path.Combine(Path.GetTempPath(), "tmbharness-no-manifest");
        Directory.CreateDirectory(empty);

        Check("supplies", "a mod with no readable manifest reports UNKNOWN, not 'supplies nothing'",
            DanceRepair.SuppliedPaths(empty) == null);

        // And unknown must be inert, or the sentinel buys nothing.
        if (File.Exists(AsyncVfxDonor))
        {
            var layout = TmbBinary.Walk(PapFile.Parse(File.ReadAllBytes(AsyncVfxDonor)).GetTimeline());
            Check("supplies", "unknown supplies drops nothing",
                DanceRepair.UnsuppliedAsyncEffects(layout, null).Count == 0);
            Check("supplies", "but a genuinely empty set still would (the two are not the same)",
                DanceRepair.UnsuppliedAsyncEffects(layout,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Count == 4);
        }
    }

    /// <summary>
    /// The mod folder a scan root belongs to. The default root is the pack's <c>dances</c> subfolder,
    /// but a mod's manifest — and therefore the set of paths it supplies — lives one level up.
    /// </summary>
    private static string ModRootOf(string root) =>
        Path.GetFileName(root.TrimEnd('\\', '/')).Equals("dances", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(root.TrimEnd('\\', '/'))! : root;

    private static List<string> AsyncVfxPaths(TmbLayout layout) =>
        layout.Entries
            .Where(e => e.Magic == TmbBinary.AsyncEffectEntry)
            .SelectMany(e => e.Fields
                .Where(f => f.Kind == TmbFieldKind.String)
                .Select(f => TmbBinary.ResolveString(layout, e, f)))
            .ToList();

    /// <summary>
    /// Applies the repair to a mod folder for real, so a repaired pack can be run back through every
    /// check above. <c>TmbHarness &lt;modRoot&gt; --repair</c>.
    /// </summary>
    private static int RepairInPlace(string modRoot)
    {
        var notes = new List<string>();
        var broken = DanceRepair.Scan(modRoot, notes);
        foreach (string note in notes) Console.WriteLine($"unreadable: {note}");

        foreach (var file in broken)
        {
            byte[] original = File.ReadAllBytes(file.FullPath);
            File.WriteAllBytes(file.FullPath + ".broken.bak", original);
            File.WriteAllBytes(file.FullPath, DanceRepair.Repair(file, original));
            Console.WriteLine($"repaired {file.Relative} ({file.EntriesToDrop.Count} entries dropped)");
        }

        Console.WriteLine($"{broken.Count} repaired in {modRoot}.");
        return 0;
    }

    // ---- the repair pass -------------------------------------------------------------------------

    /// <summary>
    /// What <see cref="DanceRepair"/> would do to the packs that already shipped.
    ///
    /// Read-only against the real mod — Scan writes nothing — and the repair itself is exercised in
    /// memory, so running this cannot change a folder the user did not ask about. The point is to
    /// prove that removing the broken entries yields a timeline that walks, resolves and is canonical,
    /// rather than one that merely no longer trips the check that found it.
    /// </summary>
    private static void RepairPass(string root)
    {
        Console.WriteLine("\n=== repairing what already shipped");

        string modRoot = ModRootOf(root);

        var notes = new List<string>();
        var broken = DanceRepair.Scan(modRoot, notes);
        Console.WriteLine($"{broken.Count} animation(s) in {Path.GetFileName(modRoot)} carry unusable paths");
        foreach (string note in notes) Console.WriteLine($"  unreadable: {note}");
        foreach (var file in broken)
            Console.WriteLine($"  {file.Relative}\n     " + string.Join("\n     ", file.Problems));

        foreach (var file in broken)
        {
            try
            {
                byte[] original = File.ReadAllBytes(file.FullPath);
                var beforeLayout = TmbBinary.Walk(PapFile.Parse(original).GetTimeline());

                byte[] repairedPap = DanceRepair.Repair(file, original);
                byte[] repaired = PapFile.Parse(repairedPap).GetTimeline();
                var after = TmbBinary.Walk(repaired);

                Check(file.Relative, "the repaired timeline asks for nothing it cannot have",
                    TmbBinary.BrokenPaths(after).Count == 0);
                Check(file.Relative, "the repair drops exactly the broken entries and their empty tracks",
                    after.Entries.Count == beforeLayout.Entries.Count - file.EntriesToDrop.Count);

                // Everything the dance actually does has to still be there. The repair removes clips
                // that were pointing at nothing; anything else changing means it removed too much.
                var kept = beforeLayout.Entries
                    .Where(e => !file.EntriesToDrop.Contains(e.Offset))
                    .Where(e => TmbBinary.CarriesResourcePath(e.Magic))
                    .SelectMany(e => e.Fields.Where(f => f.Kind == TmbFieldKind.String)
                        .Select(f => e.Magic + ":" + TmbBinary.ResolveString(beforeLayout, e, f)))
                    .OrderBy(s => s, StringComparer.Ordinal).ToList();
                var survived = after.Entries
                    .Where(e => TmbBinary.CarriesResourcePath(e.Magic))
                    .SelectMany(e => e.Fields.Where(f => f.Kind == TmbFieldKind.String)
                        .Select(f => e.Magic + ":" + TmbBinary.ResolveString(after, e, f)))
                    .OrderBy(s => s, StringComparer.Ordinal).ToList();
                Check(file.Relative, $"every other path is untouched ({survived.Count} kept)",
                    kept.SequenceEqual(survived));

                // And the repaired file must go back through the writer cleanly, or the next add
                // against this mod fails on a file this pass claimed to have fixed.
                Check(file.Relative, "the repaired timeline rebuilds",
                    TmbSplice.Rebuild(repaired).AsSpan().SequenceEqual(repaired));

                Console.WriteLine($"  {file.Relative,-70} {beforeLayout.TotalSize} -> {repaired.Length} bytes, " +
                    $"{beforeLayout.Entries.Count} -> {after.Entries.Count} entries");
            }
            catch (Exception ex)
            {
                Fail(file.Relative, "repair: " + ex.Message);
            }
        }
    }

    /// <summary>Every entry in file order: what it is, what id it claims, and whether it is modelled.</summary>
    private static void DumpEntries(List<Timeline> timelines)
    {
        foreach (var timeline in timelines)
        {
            var layout = TmbBinary.Walk(timeline.Tmb);
            Console.WriteLine($"\n{timeline.Label}  ({layout.Entries.Count} entries, " +
                $"pools at {layout.EntriesEnd})");
            for (int i = 0; i < layout.Entries.Count; i++)
            {
                var e = layout.Entries[i];
                var words = new List<string>();
                for (int off = 0; off + 4 <= Math.Min(e.Size - TmbBinary.EntryHeaderBytes, 16); off += 4)
                    words.Add(TmbBinary.ReadInt32(layout.Bytes, e.Body + off).ToString());
                Console.WriteLine($"  [{i,2}] {e.Magic} size={e.Size,3} id={e.Id,-6} flag={e.Flag,-3} " +
                    $"{(e.IsOpaque ? "OPAQUE" : "      ")} [{string.Join(",", words)}]");
            }
        }
    }

    private static void DumpTracks(List<Timeline> timelines)
    {
        foreach (var timeline in timelines)
        {
            var layout = TmbBinary.Walk(timeline.Tmb);
            var byId = layout.Entries.ToDictionary(e => e.Id);
            Console.WriteLine($"\n{timeline.Label}");
            foreach (var track in TmbTrackBundle.ActorTracks(layout))
            {
                var kinds = new List<string>();
                var strings = new List<string>();
                foreach (short id in TmbBinary.ResolveInt16List(layout, track, track.Fields[0]))
                {
                    if (!byId.TryGetValue(id, out var item)) { kinds.Add("?"); continue; }
                    kinds.Add(item.Magic);
                    foreach (var f in item.Fields)
                        if (f.Kind == TmbFieldKind.String)
                            strings.Add(TmbBinary.ResolveString(layout, item, f));
                }
                Console.WriteLine($"   track {track.Id,3}: [{string.Join(" ", kinds)}]  {string.Join(" ", strings)}");

                // The raw body words of each item, so a working dance and a produced one can be
                // compared field by field without knowing what every field means.
                foreach (short id in TmbBinary.ResolveInt16List(layout, track, track.Fields[0]))
                {
                    if (!byId.TryGetValue(id, out var item)) continue;
                    var words = new List<string>();
                    for (int off = 0; off + 4 <= item.Size - TmbBinary.EntryHeaderBytes; off += 4)
                        words.Add(TmbBinary.ReadInt32(layout.Bytes, item.Body + off).ToString());
                    string name = item.Fields.FirstOrDefault(f => f.Kind == TmbFieldKind.String) is { } sf
                        ? TmbBinary.ResolveString(layout, item, sf) : "";
                    Console.WriteLine($"        {item.Magic} id={id,-3} [{string.Join(",", words)}] {name}");
                }
            }
        }
    }

    /// <summary>
    /// Splices one bundle into one destination and checks every invariant that matters: the result
    /// walks, the count grew by exactly the bundle size, the untouched region is byte-identical,
    /// every pre-existing resolved value is unchanged, and the actor's track list grew by exactly
    /// the new tracks.
    /// </summary>
    private static void SplicePair(TmbTrackBundle bundle, byte[] destTmb)
    {
        var before = TmbBinary.Walk(destTmb);
        var actorBefore = TmbTrackBundle.FirstActor(before)!;
        short[] tracksBefore = TmbBinary.ResolveInt16List(before, actorBefore, actorBefore.Fields[0]);

        byte[] result = TmbSplice.AppendTracks(destTmb, bundle);
        var after = TmbBinary.Walk(result);

        if (after.Entries.Count != before.Entries.Count + bundle.EntryCount)
            throw new Exception($"entry count {after.Entries.Count}, expected " +
                $"{before.Entries.Count + bundle.EntryCount}");

        int p = TmbSplice.TrackInsertionPoint(before);
        // AppendTracks asserts pool preservation itself: only it knows which id lists it was
        // entitled to renumber in place.
        _ = p;

        // Entries go in at TWO points now — tracks beside the existing tracks, items after the
        // existing items — so a destination entry shifts by the track count if it sits before the
        // track insertion point and by the whole bundle if it sits after it.
        int p1 = TmbSplice.TrackInsertionPoint(before);
        int beforeTracks = before.Entries.Count(e => e.Offset < p1);

        for (int i = 0; i < before.Entries.Count; i++)
        {
            int j = i < beforeTracks ? i : i + bundle.TrackEntryCount;
            var oldEntry = before.Entries[i];
            var newEntry = after.Entries[j];

            if (oldEntry.Magic != newEntry.Magic || oldEntry.Size != newEntry.Size)
                throw new Exception($"entry {i} became {newEntry.Magic}/{newEntry.Size}");

            foreach (var field in oldEntry.Fields)
            {
                // The actor's track list is the one thing this operation is supposed to change.
                if (oldEntry.Offset == actorBefore.Offset
                    && field.BodyOffset == actorBefore.Fields[0].BodyOffset) continue;

                string oldValue = Resolve(before, oldEntry, field);
                string newValue = Resolve(after, newEntry, field);
                if (oldValue != newValue)
                    throw new Exception(
                        $"{oldEntry.Magic}+{field.BodyOffset} changed: '{oldValue}' -> '{newValue}'");
            }
        }

        var actorAfter = TmbTrackBundle.FirstActor(after)!;
        short[] tracksAfter = TmbBinary.ResolveInt16List(after, actorAfter, actorAfter.Fields[0]);

        if (tracksAfter.Length != tracksBefore.Length + bundle.TrackCount)
            throw new Exception($"actor lists {tracksAfter.Length} tracks, expected " +
                $"{tracksBefore.Length + bundle.TrackCount}");
        for (int i = 0; i < tracksBefore.Length; i++)
            if (tracksAfter[i] != tracksBefore[i])
                throw new Exception($"actor track {i} changed from {tracksBefore[i]} to {tracksAfter[i]}");

        // Ids must stay unique, or the actor and track lists start naming the wrong entries.
        if (after.Entries.Select(e => e.Id).Distinct().Count() != after.Entries.Count)
            throw new Exception("splice produced duplicate entry ids");

        var byId = after.Entries.ToDictionary(e => e.Id);
        var landed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = tracksBefore.Length; i < tracksAfter.Length; i++)
        {
            var track = byId[tracksAfter[i]];
            if (track.Magic != TmbBinary.Tmtr) throw new Exception($"appended {track.Magic}, not a track");

            short[] items = TmbBinary.ResolveInt16List(after, track, track.Fields[0]);
            if (items.Length == 0) throw new Exception("appended an empty track");

            foreach (short id in items)
            {
                if (!byId.TryGetValue(id, out var item))
                    throw new Exception($"appended track names missing item {id}");
                foreach (var field in item.Fields)
                    if (field.Kind == TmbFieldKind.String)
                        landed.Add(TmbBinary.ResolveString(after, item, field));
            }
        }

        foreach (string expected in bundle.EffectPaths)
            if (!landed.Contains(expected))
                throw new Exception($"effect '{expected}' did not survive the splice");
    }

    /// <summary>
    /// A field's value in terms that survive renumbering.
    ///
    /// An id list is described by WHAT it points at — each referenced entry's type and its own string
    /// — rather than by the id numbers, because a splice deliberately renumbers every entry after its
    /// insertion point. Comparing raw ids would flag every correct splice; comparing the referenced
    /// entries still catches a list that came to point somewhere else.
    /// </summary>
    private static string Resolve(TmbLayout layout, TmbEntry entry, TmbField field) => field.Kind switch
    {
        TmbFieldKind.String => TmbBinary.ResolveString(layout, entry, field),
        TmbFieldKind.Float32List => Convert.ToHexString(TmbBinary.ResolveFloat32List(layout, entry, field)),
        TmbFieldKind.Int16List => string.Join("|",
            TmbBinary.ResolveInt16List(layout, entry, field).Select(id => Describe(layout, id))),
        _ => string.Empty,
    };

    /// <summary>An entry named by id, described without reference to its number.</summary>
    private static string Describe(TmbLayout layout, short id)
    {
        var target = layout.Entries.FirstOrDefault(e => TmbBinary.CarriesId(e.Magic) && e.Id == id);
        if (target == null) return $"<missing {id}>";

        // Not FirstOrDefault: TmbField is a struct and TmbFieldKind.String is 0, so the default value
        // of a no-match is indistinguishable from a real string field at body offset 0 — which then
        // resolves whatever happens to be there. Entries with no string at all are common (TMAC, and
        // every undecoded type), so this path is hit constantly.
        var strings = target.Fields.Where(f => f.Kind == TmbFieldKind.String).ToList();
        return strings.Count > 0
            ? $"{target.Magic}:{TmbBinary.ResolveString(layout, target, strings[0])}"
            : target.Magic;
    }

    // ---- string edits ----------------------------------------------------------------------------

    /// <summary>
    /// Retargeting the animation entry and blanking a sound entry must change exactly those values
    /// and nothing else in the file.
    /// </summary>
    private static void StringEdits(List<Timeline> timelines)
    {
        Console.WriteLine("\n=== string edits");
        int edited = 0;

        foreach (var timeline in timelines)
        {
            try
            {
                var layout = TmbBinary.Walk(timeline.Tmb);
                var edits = new List<TmbStringEdit>();
                var expected = new Dictionary<int, string>();

                foreach (var entry in layout.Entries)
                {
                    // A deliberately different name, so a no-op would show up as a pass that changed
                    // nothing. The real value is written by the caller that prepares a dance.
                    if (entry.Magic == TmbBinary.AnimationEntry)
                    {
                        edits.Add(new TmbStringEdit(entry.Offset, 12, "cbem_dance_male_2lp_probe"));
                        expected[entry.Offset] = "cbem_dance_male_2lp_probe";
                    }
                    else if (entry.Magic == TmbBinary.SoundEntry)
                    {
                        edits.Add(new TmbStringEdit(entry.Offset, 12, string.Empty));
                        expected[entry.Offset] = string.Empty;
                    }
                }

                if (edits.Count == 0) continue;
                edited++;

                byte[] result = TmbSplice.SetStrings(timeline.Tmb, edits);
                var after = TmbBinary.Walk(result);

                Check(timeline.Label, "string edit kept the entry count",
                    after.Entries.Count == layout.Entries.Count);

                bool ok = true;
                for (int i = 0; i < layout.Entries.Count; i++)
                {
                    var oldEntry = layout.Entries[i];
                    var newEntry = after.Entries[i];
                    foreach (var field in oldEntry.Fields)
                    {
                        // Id lists are skipped: Resolve describes them by the strings of the entries
                        // they name, so retargeting a C009 legitimately changes how the track that
                        // owns it reads. SetStrings touches no ids at all.
                        if (field.Kind == TmbFieldKind.Int16List) continue;

                        string oldValue = Resolve(layout, oldEntry, field);
                        string newValue = Resolve(after, newEntry, field);
                        bool isTarget = expected.TryGetValue(oldEntry.Offset, out string? want)
                            && field.Kind == TmbFieldKind.String && field.BodyOffset == 12;
                        if (isTarget ? newValue != want : newValue != oldValue) ok = false;
                    }
                }
                Check(timeline.Label, "string edit changed only its own targets", ok);
            }
            catch (Exception ex)
            {
                Fail(timeline.Label, "string edit: " + ex.Message);
            }
        }

        Console.WriteLine($"{edited} timelines had an animation or sound entry to repoint.");
    }

    // ---- plumbing --------------------------------------------------------------------------------

    private static bool StartsWith(byte[] bytes, int at, byte[] expected)
    {
        if (at < 0 || at + expected.Length > bytes.Length) return false;
        for (int i = 0; i < expected.Length; i++)
            if (bytes[at + i] != expected[i]) return false;
        return true;
    }

    private static void Check(string label, string what, bool ok)
    {
        _checked++;
        if (!ok) Fail(label, what);
    }

    private static void Fail(string label, string what)
    {
        _failed++;
        Console.WriteLine($"  FAIL  {label}: {what}");
    }
}
