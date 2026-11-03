using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pickles_Playlist_Editor;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using Xunit;

namespace ScdAudioTests;

public class BackgroundComputeStatsTests
{
    private static readonly OptionStatsNaming.Parts All = new(true, true, true, true);
    private static readonly OptionStatsNaming.Parts None = new(false, false, false, false);

    private static Option Song(string name, string rel)
    {
        var o = new Option { Name = name };
        o.Files["sound/bpmloop.scd"] = rel;
        return o;
    }

    private static string RelOf(Option o) => o.Files.Count == 0 ? "" : o.Files.Values.First();

    // ── Rules ──

    [Fact] // [AC-background-compute-stats-3] [AC-background-compute-stats-5] [EDGE-background-compute-stats-1] [EDGE-background-compute-stats-2] [EDGE-background-compute-stats-17] [EDGE-background-compute-stats-18] [EDGE-background-compute-stats-24] [EDGE-background-compute-stats-42]
    public void AutoStatsRules_ShouldRun_AndFailedAttempts()
    {
        Assert.True(AutoStatsRules.ShouldRun(null, null, "2.11.15", true));
        Assert.True(AutoStatsRules.ShouldRun("", "", "2.11.15", true));
        Assert.False(AutoStatsRules.ShouldRun("2.11.15", null, "2.11.15", true));
        Assert.True(AutoStatsRules.ShouldRun("2.11.14", null, "2.11.15", true));
        Assert.True(AutoStatsRules.ShouldRun("2.11.15", null, "2.11.15-testing.28", true));
        Assert.True(AutoStatsRules.ShouldRun("2.11.16", null, "2.11.15", true)); // a downgrade runs too
        Assert.False(AutoStatsRules.ShouldRun(null, null, "", true));
        Assert.False(AutoStatsRules.ShouldRun(null, null, "2.11.15", false)); // no readable mod yet

        Assert.True(AutoStatsRules.ShouldRun(null, "2.11.15|2", "2.11.15", true));
        Assert.False(AutoStatsRules.ShouldRun(null, "2.11.15|3", "2.11.15", true));
        Assert.True(AutoStatsRules.ShouldRun(null, "2.11.14|3", "2.11.15", true)); // another version's attempts don't count
        Assert.True(AutoStatsRules.ShouldRun(null, "garbage", "2.11.15", true));
        Assert.Equal("2.11.15|1", AutoStatsRules.NextAttempt("2.11.14|3", "2.11.15"));
        Assert.Equal("2.11.15|3", AutoStatsRules.NextAttempt("2.11.15|2", "2.11.15"));

        foreach (var e in Enum.GetValues<StatsPassEnd>()) Assert.False(AutoStatsRules.CountsAsFailedAttempt(false, e, 3));
        Assert.True(AutoStatsRules.CountsAsFailedAttempt(true, StatsPassEnd.Error, 0));
        Assert.True(AutoStatsRules.CountsAsFailedAttempt(true, StatsPassEnd.Finished, 1));
        Assert.False(AutoStatsRules.CountsAsFailedAttempt(true, StatsPassEnd.Finished, 0));
        Assert.False(AutoStatsRules.CountsAsFailedAttempt(true, StatsPassEnd.Cancelled, 5));
    }

    [Fact] // [AC-background-compute-stats-5] [EDGE-background-compute-stats-3] [EDGE-background-compute-stats-5] [EDGE-background-compute-stats-41]
    public void AutoStatsRules_ShouldWriteMarker()
    {
        Assert.True(AutoStatsRules.ShouldWriteMarker(StatsPassEnd.Finished, 0, 10, 2));
        Assert.True(AutoStatsRules.ShouldWriteMarker(StatsPassEnd.Finished, 0, 0, 2)); // a mod with only empty playlists
        Assert.False(AutoStatsRules.ShouldWriteMarker(StatsPassEnd.Finished, 1, 10, 2));
        Assert.False(AutoStatsRules.ShouldWriteMarker(StatsPassEnd.Cancelled, 0, 10, 2));
        Assert.False(AutoStatsRules.ShouldWriteMarker(StatsPassEnd.Error, 0, 10, 2));
        Assert.False(AutoStatsRules.ShouldWriteMarker(StatsPassEnd.Finished, 0, 0, 0)); // loaded nothing: not a real run
    }

    [Fact] // [AC-background-compute-stats-8] [EDGE-background-compute-stats-17] [EDGE-background-compute-stats-19] [EDGE-background-compute-stats-20]
    public void StatsPassGate_OneRunAtATime_CancelAndEnd()
    {
        using var closing = new CancellationTokenSource();
        var gate = new StatsPassGate(closing.Token);
        Assert.True(gate.TryBegin(StatsPassKind.Auto, out var a));
        Assert.False(gate.TryBegin(StatsPassKind.Manual, out var none));
        Assert.Equal(default, none);
        gate.CancelRunning();
        Assert.True(a.Token.IsCancellationRequested);
        Assert.False(gate.End(none));
        Assert.True(gate.IsRunning);
        Assert.True(gate.End(a));
        Assert.False(gate.End(a)); // stale
        Assert.True(gate.TryBegin(StatsPassKind.Manual, out var b));
        closing.Cancel();
        Assert.True(b.Token.IsCancellationRequested);
        Assert.Equal(StatsPassKind.Manual, b.Kind);
    }

    [Fact] // [AC-background-compute-stats-4] [EDGE-background-compute-stats-3] [EDGE-background-compute-stats-11] [EDGE-background-compute-stats-26] [EDGE-background-compute-stats-36]
    public void StatsPlan_AnalysesOnlyMissing_DedupesByExactRel_SkipsOffDefaultAndNoScd()
    {
        var pl = new Playlist { Name = "P" };
        var targets = new List<(Playlist, Option)>
        {
            (pl, Song("A", "p/a.scd")), (pl, Song("A2", "p/a.scd")), (pl, Song("A3", @"P\A.scd")),
            (pl, Song("B", "p/b.scd")), (pl, Song("Off", "p/off.scd")), (pl, Song("Default", "p/d.scd")),
            (pl, new Option { Name = "NoScd" }), (pl, Song(null!, "p/n.scd")),
        };
        var entries = StatsPlan.Snapshot(targets, RelOf, _ => false);
        Assert.Equal(new[] { "A", "A2", "A3", "B" }, entries.Select(e => e.Name));
        var probed = new List<string>();
        var plan = StatsPlan.Build(entries, rel => { probed.Add(rel); return rel == "p/b.scd"; });
        Assert.Equal(new[] { "p/a.scd", @"P\A.scd" }, plan.ToDetect); // exact spellings stay separate
        Assert.Equal(new[] { "p/a.scd", @"P\A.scd", "p/b.scd" }, probed);
        Assert.Empty(StatsPlan.Build(entries, _ => true).ToDetect);
        Assert.Equal(StatsPlan.RelKeyOf("p/a.scd"), StatsPlan.RelKeyOf(@"P\A.scd"));
    }

    // ── Detect loop and gate ──

    [Fact] // [AC-background-compute-stats-6]
    public async Task StatsDetectLoop_OneSongAtATime_UnderGate()
    {
        var gate = new LibraryReadGate();
        int inside = 0, maxInside = 0;
        var progress = new List<int>();
        bool otherBlocked = true;
        var failed = await StatsDetectLoop.RunAsync(["a", "b", "c"], rel =>
        {
            int n = Interlocked.Increment(ref inside);
            maxInside = Math.Max(maxInside, n);
            otherBlocked &= !gate.IsFree; // held while a song is analysed (probe without queueing a waiter)
            Interlocked.Decrement(ref inside);
            return true;
        }, () => true, _ => Task.CompletedTask, gate, CancellationToken.None, (done, _) => progress.Add(done));
        Assert.Empty(failed);
        Assert.Equal(1, maxInside);
        Assert.True(otherBlocked);
        Assert.Equal(new[] { 1, 2, 3 }, progress);
    }

    [Fact] // [AC-background-compute-stats-6] [EDGE-background-compute-stats-15]
    public async Task StatsDetectLoop_WaitsUntilIdleBeforeEachSong()
    {
        var idle = new TaskCompletionSource();
        int detects = 0;
        var run = StatsDetectLoop.RunAsync(["a"], _ => { detects++; return true; }, () => true, _ => idle.Task,
            new LibraryReadGate(), CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(0, detects);
        idle.SetResult();
        await run;
        Assert.Equal(1, detects);
    }

    [Fact] // [AC-background-compute-stats-6] [EDGE-background-compute-stats-40]
    public async Task StatsDetectLoop_RechecksIdleAfterGate_ReleasesAndWaits()
    {
        var gate = new LibraryReadGate();
        int idleChecks = 0, waits = 0;
        bool gateFreeWhileBusy = false;
        var failed = await StatsDetectLoop.RunAsync(["a"], _ => true,
            () => ++idleChecks > 1,
            _ => { waits++; if (waits == 2) gateFreeWhileBusy = gate.IsFree; return Task.CompletedTask; },
            gate, CancellationToken.None);
        Assert.Empty(failed);
        Assert.Equal(2, waits);
        Assert.True(gateFreeWhileBusy, "the gate is released while waiting again");
    }

    [Fact] // [AC-background-compute-stats-5] [EDGE-background-compute-stats-17] [EDGE-background-compute-stats-18] [EDGE-background-compute-stats-32]
    public async Task StatsDetectLoop_CancelStopsBeforeNextSong_ReleasesGate()
    {
        var gate = new LibraryReadGate();
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StatsDetectLoop.RunAsync(["a", "b", "c"],
            rel => { seen.Add(rel); cts.Cancel(); return true; }, () => true, _ => Task.CompletedTask, gate, cts.Token));
        Assert.Equal(new[] { "a" }, seen);
        Assert.True(gate.IsFree);

        // blocked waiting for the gate or for idle
        using var cts2 = new CancellationTokenSource();
        await gate.EnterAsync(CancellationToken.None);
        var blocked = StatsDetectLoop.RunAsync(["x"], _ => true, () => true, _ => Task.CompletedTask, gate, cts2.Token);
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        gate.Exit();
        using var cts3 = new CancellationTokenSource();
        var idleWait = StatsDetectLoop.RunAsync(["x"], _ => true, () => true, ct => Task.Delay(Timeout.Infinite, ct), gate, cts3.Token);
        cts3.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idleWait);
        Assert.True(gate.IsFree);
    }

    [Fact] // [EDGE-background-compute-stats-4] [EDGE-background-compute-stats-31]
    public async Task StatsDetectLoop_FailuresCountedAndLoopContinues()
    {
        var done = new List<string>();
        var failed = await StatsDetectLoop.RunAsync(["a", "bad", "boom", "d"], rel =>
        {
            done.Add(rel);
            if (rel == "boom") throw new InvalidOperationException("x");
            return rel != "bad";
        }, () => true, _ => Task.CompletedTask, new LibraryReadGate(), CancellationToken.None);
        Assert.Equal(new[] { "bad", "boom" }, failed);
        Assert.Equal(new[] { "a", "bad", "boom", "d" }, done);
    }

    [Fact] // [AC-background-compute-stats-6] [EDGE-background-compute-stats-21] [EDGE-background-compute-stats-22] [EDGE-background-compute-stats-30]
    public async Task LibraryReadGate_FifoHandover_NoBarging()
    {
        var gate = new LibraryReadGate();
        await gate.EnterAsync(CancellationToken.None); // A
        var order = new List<string>();
        var b = Task.Run(async () => { await gate.EnterAsync(CancellationToken.None); order.Add("B"); gate.Exit(); });
        await Task.Delay(100);
        gate.Exit();
        var aAgain = gate.EnterAsync(CancellationToken.None).ContinueWith(_ => { order.Add("A"); gate.Exit(); });
        await Task.WhenAll(b, aAgain);
        Assert.Equal(new[] { "B", "A" }, order);

        int turnsA = 0, turnsB = 0;
        async Task Loop(Action turn) { for (int i = 0; i < 20; i++) { await gate.EnterAsync(CancellationToken.None); turn(); await Task.Yield(); gate.Exit(); } }
        await Task.WhenAll(Task.Run(() => Loop(() => turnsA++)), Task.Run(() => Loop(() => turnsB++)));
        Assert.Equal(20, turnsA);
        Assert.Equal(20, turnsB);
    }

    // ── Names ──

    [Fact] // [AC-background-compute-stats-4] [EDGE-background-compute-stats-34] [EDGE-background-compute-stats-53]
    public void StatsNameParts_Parse_RecognisesOnlyKeyBrackets()
    {
        foreach (var k in new[] { "8A Am", "Am", "8A", "3B C# Major", "11A F# Minor" }) Assert.True(StatsNameParts.IsKeyBracket(k), k);
        foreach (var k in new[] { "Official Video", "Remix", "Live", "12C", "" }) Assert.False(StatsNameParts.IsKeyBracket(k), k);

        var p = StatsNameParts.Parse("Song (128 BPM) [8A Am] (00:03:00)");
        Assert.Equal("Song", p.Base);
        Assert.Equal(" (128 BPM)", p.BpmText);
        Assert.Equal(" [8A Am]", p.KeyText);
        Assert.Equal(" (00:03:00)", p.LengthText);

        var remix = StatsNameParts.Parse("Song (120 BPM) [Remix]");
        Assert.Equal("Song (120 BPM) [Remix]", remix.Base);
        Assert.True(remix.HasBpm);
        Assert.False(remix.HasKey);
        Assert.False(remix.HasLength);
        Assert.Equal("Song (120 BPM) [Remix] [8A Am] (00:03:00)",
            StatsNameParts.AddMissing(remix, 126, "Am", TimeSpan.FromMinutes(3), All));
        Assert.True(StatsNameParts.Parse("Song [8A Am] [Live]").HasKey);
    }

    [Fact] // [AC-background-compute-stats-4] [EDGE-background-compute-stats-28] [EDGE-background-compute-stats-33] [EDGE-background-compute-stats-51]
    public void StatsRename_Additive_AddsOnlyMissingParts_KeepsExisting()
    {
        var e = new StatsEntry("P", "p/a.scd", "p\\a.scd", "Song (128 BPM)", false);
        string Expect(StatsEntry entry, CachedStats c, OptionStatsNaming.Parts parts) =>
            StatsRename.ExpectedNames([entry], _ => c, parts, StatsRenameMode.Additive)[entry];
        var full = new CachedStats(126, "Am", TimeSpan.FromMinutes(3));
        Assert.Equal("Song (128 BPM) [8A Am] (00:03:00)", Expect(e, full, All)); // hand-set 128 kept
        Assert.Equal("Song (128 BPM)", Expect(e, full, None));
        Assert.Equal("Song (128 BPM) (00:03:00)", Expect(e, new CachedStats(null, null, TimeSpan.FromMinutes(3)), All));
        var bare = e with { Name = "Song" };
        Assert.Equal("Song (126 BPM) [8A Am] (00:03:00)", Expect(bare, full, All));
    }

    [Fact] // [AC-background-compute-stats-4] [EDGE-background-compute-stats-34]
    public void StatsRename_Additive_NonKeyBracketKept_PartsAppendedAfter()
    {
        var e = new StatsEntry("P", "p/a.scd", "p\\a.scd", "Song [Remix]", false);
        string name = StatsRename.ExpectedNames([e], _ => new CachedStats(120, "Am", TimeSpan.FromMinutes(3)), All, StatsRenameMode.Additive)[e];
        Assert.Equal("Song [Remix] (120 BPM) [8A Am] (00:03:00)", name);
        Assert.Equal("Song [Remix]", OptionStatsNaming.StripSuffix(name));
    }

    [Fact] // [EDGE-background-compute-stats-35]
    public void StatsRename_NoCachedStats_NameUntouched_BothModes()
    {
        var e = new StatsEntry("P", "p/a.scd", "p\\a.scd", "Song (120 BPM) [8A Am]", false);
        foreach (var mode in Enum.GetValues<StatsRenameMode>())
        {
            var expected = StatsRename.ExpectedNames([e], _ => default, All, mode);
            Assert.Empty(expected);
            var o = Song(e.Name, e.Rel);
            var pl = new Playlist { Name = "P", Options = [o] };
            var (changes, dirty, _) = StatsRename.ApplyOne(pl, expected, RelOf, false);
            Assert.Empty(changes);
            Assert.False(dirty);
            Assert.Equal("Song (120 BPM) [8A Am]", o.Name);
        }
    }

    [Fact] // [AC-background-compute-stats-2] [EDGE-background-compute-stats-28] [EDGE-background-compute-stats-51]
    public void StatsRename_ManualRebuild_EqualsUpdateName()
    {
        var c = new CachedStats(126, "Am", TimeSpan.FromMinutes(3));
        foreach (var name in new[] { "Song", "Song (128 BPM)", "Song [Remix]", "Song (1 BPM) [1A Abm] (00:00:01)" })
            foreach (var parts in new[] { All, None, new OptionStatsNaming.Parts(true, false, true, false) })
            {
                var e = new StatsEntry("P", "r", "r", name, false);
                var scratch = new Option { Name = name };
                OptionStatsNaming.UpdateName(scratch, c.Bpm, c.Key, c.Length, parts);
                Assert.Equal(scratch.Name, StatsRename.ExpectedNames([e], _ => c, parts, StatsRenameMode.Rebuild)[e]);
            }
    }

    private static readonly CachedStats Full = new(120, "Am", TimeSpan.FromMinutes(3));

    private static Dictionary<StatsEntry, string> ExpectedFor(params StatsEntry[] entries) =>
        StatsRename.ExpectedNames(entries, _ => Full, All, StatsRenameMode.Additive);

    [Fact] // [AC-background-compute-stats-7] [EDGE-background-compute-stats-6] [EDGE-background-compute-stats-7] [EDGE-background-compute-stats-8] [EDGE-background-compute-stats-36] [EDGE-background-compute-stats-37]
    public void StatsRename_Apply_SkipsRenamedDeletedMovedAndSuffixOnlyRename()
    {
        var entries = new[]
        {
            new StatsEntry("P", "p/a.scd", "p\\a.scd", "A", false),
            new StatsEntry("P", "p/b.scd", "p\\b.scd", "B", false),
            new StatsEntry("P", "p/c.scd", "p\\c.scd", "C", false),
            new StatsEntry("P", "p/d.scd", "p\\d.scd", "D (99 BPM)", false),
            new StatsEntry("P", "p/e.scd", "p\\e.scd", "E", false),
        };
        var expected = ExpectedFor(entries);
        var renamed = Song("A by me", "p/a.scd");      // renamed
        var moved = Song("C", "q/c.scd");              // moved
        var suffixOnly = Song("D", "p/d.scd");          // the user removed the stats by hand
        var caseVariant = Song("E", "P/E.scd");         // same file, other spelling: not this entry
        var pl = new Playlist { Name = "P", Options = [renamed, moved, suffixOnly, caseVariant] }; // B deleted
        var (changes, dirty, matched) = StatsRename.ApplyOne(pl, expected, RelOf, false);
        Assert.Empty(changes);
        Assert.False(dirty);
        Assert.Equal(0, matched);
        Assert.Equal(new[] { "A by me", "C", "D", "E" }, pl.Options.Select(o => o.Name));
    }

    [Fact] // [EDGE-background-compute-stats-9] [EDGE-background-compute-stats-10] [EDGE-background-compute-stats-37]
    public void StatsRename_Apply_FreshObjects_ExactOrBackfilledStripped()
    {
        var plain = new StatsEntry("P", "p/a.scd", "p\\a.scd", "A", false);
        var backfilled = new StatsEntry("P", "p/b.scd", "p\\b.scd", "B (120 BPM) [8A Am] (00:03:00)", true);
        var notBackfilled = new StatsEntry("P", "p/c.scd", "p\\c.scd", "C (120 BPM)", false);
        var expected = ExpectedFor(plain, backfilled, notBackfilled);
        var a = Song("A", "p/a.scd");
        var b = Song("B", "p/b.scd");   // reloaded from disk: the undecorated name
        var c = Song("C", "p/c.scd");
        var pl = new Playlist { Name = "P", Options = [a, b, c] };
        StatsRename.ApplyOne(pl, expected, RelOf, false);
        Assert.Equal("A (120 BPM) [8A Am] (00:03:00)", a.Name);
        Assert.Equal("B (120 BPM) [8A Am] (00:03:00)", b.Name);
        Assert.Equal("C", c.Name);
    }

    [Fact] // [AC-background-compute-stats-4] [EDGE-background-compute-stats-3] [EDGE-background-compute-stats-10] [EDGE-background-compute-stats-47]
    public void StatsRename_Dirty_BackfilledOnlyWithMatchedEntry()
    {
        var done = new StatsEntry("P", "p/a.scd", "p\\a.scd", "A (120 BPM) [8A Am] (00:03:00)", true);
        var expected = ExpectedFor(done);
        Assert.False(StatsRename.ApplyOne(new Playlist { Name = "P", Options = [Song(done.Name, done.Rel)] }, expected, RelOf, false).Dirty);
        Assert.True(StatsRename.ApplyOne(new Playlist { Name = "P", Options = [Song(done.Name, done.Rel)] }, expected, RelOf, true).Dirty);
        Assert.False(StatsRename.ApplyOne(new Playlist { Name = "Q", Options = [Song("Other", "q/x.scd")] }, expected, RelOf, true).Dirty);
        var change = new StatsEntry("P", "p/b.scd", "p\\b.scd", "B", false);
        Assert.True(StatsRename.ApplyOne(new Playlist { Name = "P", Options = [Song("B", "p/b.scd")] }, ExpectedFor(change), RelOf, false).Dirty);
    }

    [Fact] // [EDGE-background-compute-stats-11]
    public void StatsRename_SharedRel_BothPlaylistsRenamed_PreferSamePlaylist()
    {
        var inP = new StatsEntry("P", "s/x.scd", "s\\x.scd", "X", false);
        var inQ = new StatsEntry("Q", "s/x.scd", "s\\x.scd", "X", false);
        var expected = new Dictionary<StatsEntry, string> { [inP] = "X from P", [inQ] = "X from Q" };
        var p = new Playlist { Name = "P", Options = [Song("X", "s/x.scd")] };
        var q = new Playlist { Name = "Q", Options = [Song("X", "s/x.scd")] };
        StatsRename.ApplyOne(p, expected, RelOf, false);
        StatsRename.ApplyOne(q, expected, RelOf, false);
        Assert.Equal("X from P", p.Options[0].Name);
        Assert.Equal("X from Q", q.Options[0].Name);
    }

    [Fact] // [AC-background-compute-stats-7] [EDGE-background-compute-stats-11] a manual run on a selection renames only there
    public void StatsRename_RequireSamePlaylist_LeavesOtherPlaylistsAlone()
    {
        var inP = new StatsEntry("P", "s/x.scd", "s\\x.scd", "X", false);
        var expected = new Dictionary<StatsEntry, string> { [inP] = "X (120 BPM)" };
        var q = new Playlist { Name = "Q", Options = [Song("X", "s/x.scd")] };
        var (changes, dirty, _) = StatsRename.ApplyOne(q, expected, RelOf, false, requireSamePlaylist: true);
        Assert.Empty(changes);
        Assert.False(dirty);
        Assert.Equal("X", q.Options[0].Name);
        StatsRename.ApplyOne(q, expected, RelOf, false); // the automatic run follows the file
        Assert.Equal("X (120 BPM)", q.Options[0].Name);
    }

    [Theory] // [AC-background-compute-stats-2] [EDGE-background-compute-stats-51] the name format, pinned
    [InlineData(true, true, true, true, "Song (126 BPM) [8A Am] (01:02:03)")]
    [InlineData(false, true, false, false, "Song [Am]")]
    [InlineData(false, false, true, false, "Song [8A]")]
    [InlineData(true, false, false, true, "Song (126 BPM) (01:02:03)")]
    [InlineData(false, false, false, false, "Song")]
    public void UpdateName_GoldenFormat(bool bpm, bool key, bool camelot, bool length, string expected)
    {
        var o = new Option { Name = "Song (99 BPM) [1A Abm] (00:00:01)" };
        OptionStatsNaming.UpdateName(o, 126, "Am", new TimeSpan(1, 2, 3), new OptionStatsNaming.Parts(bpm, key, camelot, length));
        Assert.Equal(expected, o.Name);
        Assert.Equal("Song", OptionStatsNaming.StripSuffix(o.Name));
    }

    [Fact] // [EDGE-background-compute-stats-26] "Off"/"Default" in any case are never analysed or renamed
    public void StatsPlan_SkipsOffAndDefault_AnyCase()
    {
        var pl = new Playlist { Name = "P" };
        var entries = StatsPlan.Snapshot([(pl, Song("off", "p/o.scd")), (pl, Song("DEFAULT", "p/d.scd")), (pl, Song("Song", "p/s.scd"))], RelOf, _ => false);
        Assert.Equal(new[] { "Song" }, entries.Select(e => e.Name));
    }

    [Fact] // [EDGE-background-compute-stats-5]
    public void StatsRename_Revert_RestoresExactOldNames()
    {
        var e = new StatsEntry("P", "p/a.scd", "p\\a.scd", "A", false);
        var o = Song("A", "p/a.scd");
        var (changes, _, _) = StatsRename.ApplyOne(new Playlist { Name = "P", Options = [o] }, ExpectedFor(e), RelOf, false);
        Assert.NotEqual("A", o.Name);
        StatsRename.Revert(changes);
        Assert.Equal("A", o.Name);
    }

    [Fact] // [AC-background-compute-stats-10]
    public void BackgroundStatus_StatsSlotWins_ThenAi_ThenEmpty()
    {
        Assert.Equal(("stats", 10), BackgroundStatus.Pick(("stats", 10), ("ai", 50)));
        Assert.Equal(("ai", 50), BackgroundStatus.Pick(null, ("ai", 50)));
        Assert.Equal(("ai", 50), BackgroundStatus.Pick(("", 0), ("ai", 50)));
        Assert.Null(BackgroundStatus.Pick(null, null));
    }

    [Fact] // [EDGE-background-compute-stats-46]
    public void ThreadPriorityScope_SuspendBackground_TogglesOnlyInsideBackground()
    {
        Exception? error = null;
        ThreadPriority after = default;
        var seen = new List<(string Where, bool Background)>();
        void Note(string where) => seen.Add((where, ThreadPriorityScope.IsBackgroundMode));
        var t = new Thread(() =>
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
                Note("start");
                using (ThreadPriorityScope.SuspendBackground()) Note("suspend outside"); // a no-op
                using (ThreadPriorityScope.Background())
                {
                    Note("background");
                    using (ThreadPriorityScope.SuspendBackground()) Note("suspended");
                    Note("restored");
                    try { using (ThreadPriorityScope.SuspendBackground()) { Note("suspended, throwing"); throw new InvalidOperationException(); } }
                    catch (InvalidOperationException) { }
                    Note("restored after throw");
                }
                Note("end");
                after = Thread.CurrentThread.Priority;
            }
            catch (Exception ex) { error = ex; }
        });
        t.Start();
        t.Join();
        Assert.Null(error);
        Assert.Equal(ThreadPriority.AboveNormal, after);
        // Background mode is a Windows feature; if the call isn't available the scope falls back to BelowNormal.
        bool available = seen.Single(s => s.Where == "background").Background;
        var expected = new (string, bool)[]
        {
            ("start", false), ("suspend outside", false), ("background", available), ("suspended", false),
            ("restored", available), ("suspended, throwing", false), ("restored after throw", available), ("end", false),
        };
        Assert.Equal(expected, seen);
        Assert.True(available, "Windows background thread mode should be available on the dev machine");
    }

    [Fact] // [EDGE-background-compute-stats-18] [EDGE-background-compute-stats-39]
    public void StatsCacheWrites_HoldForExit_BlocksOtherWriters_ReleaseResumes()
    {
        try
        {
            Assert.True(StatsCacheWrites.HoldForExit());
            Assert.True(StatsCacheWrites.HoldForExit()); // idempotent
            bool wrote = false;
            var writer = Task.Run(() => StatsCacheWrites.Write(() => wrote = true));
            Assert.False(writer.Wait(200));
            StatsCacheWrites.ReleaseExitHold();
            Assert.True(writer.Wait(5000));
            Assert.True(wrote);

            // another thread holds the lock past the limit: not held, and releasing doesn't throw
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() => { lock (StatsCacheWrites.Lock) { held.Set(); release.Wait(10_000); } });
            holder.Start();
            held.Wait();
            try
            {
                Assert.False(StatsCacheWrites.HoldForExit());
                Assert.Null(Record.Exception(StatsCacheWrites.ReleaseExitHold));
            }
            finally { release.Set(); holder.Join(); }
        }
        finally { StatsCacheWrites.ReleaseExitHold(); }
    }

    [Fact] // [AC-background-compute-stats-1] [AC-background-compute-stats-8] [EDGE-background-compute-stats-4]
    public void Strings_NewKeys_InAllSevenLocales()
    {
        var dirs = Directory.GetDirectories(Path.Combine(RuntimeTargetTests.RepoRoot(), "Strings"));
        Assert.Equal(7, dirs.Length);
        Dictionary<string, string> Values(string d) => XDocument.Load(Path.Combine(d, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");
        var en = Values(dirs.Single(d => d.EndsWith("en-US")));
        foreach (var d in dirs)
        {
            var v = Values(d);
            foreach (var key in new[] { "Prog_ComputingStatsCount", "Prog_StatsWaitingToApply", "Stats_AlreadyRunning", "Stats_AnalyseFailed" })
            {
                Assert.True(v.TryGetValue(key, out var s) && s.Length > 0, $"{Path.GetFileName(d)} lacks {key}");
                if (!d.EndsWith("en-US")) Assert.NotEqual(en[key], s);
            }
            Assert.Single(Regex.Matches(v["Prog_ComputingStatsCount"], @"\{0\}"));
            Assert.Single(Regex.Matches(v["Prog_ComputingStatsCount"], @"\{1\}"));
            Assert.Single(Regex.Matches(v["Stats_AnalyseFailed"], @"\{0\}"));
        }
    }
}
