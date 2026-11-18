using Pickles_Playlist_Editor;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using Xunit;

namespace ScdAudioTests;

[CollectionDefinition("stats-timing", DisableParallelization = true)]
public class StatsTimingCollection { }

// Timer-based: run alone, so a busy suite can't push a fast song past the slow threshold.
[Collection("stats-timing")]
public class StatsSongTimeoutTests
{
    private static string Src(string rel) => File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), rel)).Replace("\r\n", "\n");

    private static string Method(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + signature);
        int open = src.IndexOf('{', i), depth = 0;
        for (int j = open; j < src.Length; j++)
        {
            if (src[j] == '{') depth++;
            else if (src[j] == '}' && --depth == 0) return src[i..(j + 1)];
        }
        return src[i..];
    }

    private static int At(string s, string needle)
    {
        int i = s.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + needle);
        return i;
    }

    private static string Stats() => Src("ui/MainWindow.Stats.cs");
    private static string Run() => Method(Stats(), "private async Task RunStatsPassAsync");

    private static readonly StatsDetectLimits Tiny = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400), 3);

    private sealed class Recorder
    {
        public readonly List<(string Rel, TimeSpan Elapsed, StatsSongNote Kind)> Notes = new();
        public readonly List<StatsDetectStop> Stops = new();
        public readonly List<(int Index, TimeSpan Elapsed)> Waits = new();
        public StatsDetectHooks Hooks => new(
            Note: (r, t, k) => { lock (Notes) Notes.Add((r, t, k)); },
            Stopped: s => Stops.Add(s),
            Waiting: (i, t) => Waits.Add((i, t)));
    }

    private static Task<List<string>> Loop(IReadOnlyList<string> rels, Func<string, bool> detect, LibraryReadGate gate,
        Recorder rec, CancellationToken ct = default, Func<CancellationToken, Task>? wait = null, List<int>? progress = null,
        StatsDetectLimits? limits = null) =>
        StatsDetectLoop.RunAsync(rels, detect, () => true, wait ?? (_ => Task.CompletedTask), gate, ct,
            progress == null ? null : (d, _) => progress.Add(d), limits ?? Tiny, rec.Hooks);

    // ── Per-song limit ──

    [Fact] // [AC-stats-song-timeout-1] [EDGE-stats-song-timeout-1]
    public async Task StatsDetectLoop_StuckSong_TimesOut_GateFreed_NextSongRuns()
    {
        var gate = new LibraryReadGate();
        using var release = new ManualResetEventSlim();
        var detected = new List<string>();
        bool heldForC = false;
        var progress = new List<int>();
        try
        {
            var failed = await Loop(["a", "stuck", "c"], rel =>
            {
                if (rel == "stuck") { release.Wait(); return true; }
                lock (detected) detected.Add(rel);
                if (rel == "c") heldForC = !gate.IsFree;
                return true;
            }, gate, new Recorder(), progress: progress);
            Assert.Equal(new[] { "stuck" }, failed);
            Assert.Equal(new[] { "a", "c" }, detected);
            Assert.Equal(new[] { 1, 2, 3 }, progress);
            Assert.True(heldForC);
            Assert.True(gate.IsFree);
        }
        finally { release.Set(); }
    }

    [Fact] // [AC-stats-song-timeout-1] [AC-stats-song-timeout-2]
    public async Task StatsDetectLoop_StuckSong_NotesTimedOut()
    {
        using var release = new ManualResetEventSlim();
        var rec = new Recorder();
        try
        {
            await Loop(["a", "stuck", "c"], rel => { if (rel == "stuck") release.Wait(); return true; }, new LibraryReadGate(), rec);
            Assert.Equal(2, rec.Notes.Count);
            Assert.Equal(("stuck", StatsSongNote.StillRunning), (rec.Notes[0].Rel, rec.Notes[0].Kind));
            Assert.True(rec.Notes[0].Elapsed >= TimeSpan.FromMilliseconds(95));
            Assert.Equal(("stuck", Tiny.Timeout, StatsSongNote.TimedOut), rec.Notes[1]);
            Assert.Empty(rec.Stops);
        }
        finally { release.Set(); }
    }

    [Fact] // [AC-stats-song-timeout-3] [EDGE-stats-song-timeout-2]
    public async Task StatsDetectLoop_SlowSong_NotesStillRunningThenFinished()
    {
        var rec = new Recorder();
        bool returned = false, stillBeforeReturn = false;
        var limits = new StatsDetectLimits(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(2), 3);
        var hooks = new StatsDetectHooks(Note: (r, t, k) =>
        {
            lock (rec.Notes) rec.Notes.Add((r, t, k));
            if (k == StatsSongNote.StillRunning) stillBeforeReturn = !Volatile.Read(ref returned);
        });
        var failed = await StatsDetectLoop.RunAsync(["slow"], _ => { Thread.Sleep(150); Volatile.Write(ref returned, true); return true; },
            () => true, _ => Task.CompletedTask, new LibraryReadGate(), CancellationToken.None, null, limits, hooks);
        Assert.Empty(failed);
        Assert.Equal(new[] { StatsSongNote.StillRunning, StatsSongNote.FinishedSlow }, rec.Notes.Select(n => n.Kind));
        Assert.True(stillBeforeReturn);
        Assert.True(rec.Notes[1].Elapsed >= TimeSpan.FromMilliseconds(140));
    }

    [Fact] // [AC-stats-song-timeout-3] [EDGE-stats-song-timeout-3] [EDGE-stats-song-timeout-17] [EDGE-stats-song-timeout-23]
    public async Task StatsDetectLoop_FastSongs_NoNotes()
    {
        var rec = new Recorder();
        Assert.Empty(await Loop(["a", "b", "c"], _ => true, new LibraryReadGate(), rec));
        Assert.Empty(await Loop([], _ => throw new InvalidOperationException("no songs"), new LibraryReadGate(), rec));
        Assert.Empty(rec.Notes);
        Assert.Empty(rec.Stops);
        Assert.Empty(rec.Waits);
    }

    [Fact] // [AC-stats-song-timeout-3] [EDGE-stats-song-timeout-2]
    public async Task StatsDetectLoop_SlowSongThatThrows_StillClosedOut()
    {
        var rec = new Recorder();
        var limits = new StatsDetectLimits(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(2), 3);
        var failed = await StatsDetectLoop.RunAsync(["slowbad"], _ => { Thread.Sleep(150); throw new InvalidOperationException("x"); },
            () => true, _ => Task.CompletedTask, new LibraryReadGate(), CancellationToken.None, null, limits, rec.Hooks);
        Assert.Equal(new[] { "slowbad" }, failed);
        Assert.Equal(new[] { StatsSongNote.StillRunning, StatsSongNote.FinishedSlow }, rec.Notes.Select(n => n.Kind));
    }

    [Fact] // [AC-stats-song-timeout-5] [EDGE-stats-song-timeout-8]
    public async Task StatsDetectLoop_TimeoutsCountedAcrossTheRun_NotJustInARow()
    {
        using var release = new ManualResetEventSlim();
        var rec = new Recorder();
        var called = new List<string>();
        try
        {
            var failed = await Loop(["x1", "ok1", "x2", "ok2", "x3", "ok3"], rel =>
            {
                lock (called) called.Add(rel);
                if (rel.StartsWith('x')) release.Wait();
                return true;
            }, new LibraryReadGate(), rec);
            Assert.Equal(new[] { "x1", "ok1", "x2", "ok2", "x3" }, called);
            Assert.Equal(new[] { "x1", "x2", "x3", "ok3" }, failed);
            Assert.Equal(new[] { new StatsDetectStop(3, 1) }, rec.Stops);
        }
        finally { release.Set(); }
    }

    [Fact] // [EDGE-stats-song-timeout-16]
    public async Task StatsDetectLoop_ThrowingHooks_NeverLeakTheGate()
    {
        var gate = new LibraryReadGate();
        using var release = new ManualResetEventSlim();
        var boom = new StatsDetectHooks(
            Note: (_, _, _) => throw new InvalidOperationException("note"),
            Stopped: _ => throw new InvalidOperationException("stop"),
            Waiting: (_, _) => throw new InvalidOperationException("wait"));
        try
        {
            var failed = await StatsDetectLoop.RunAsync(["a", "stuck"], rel => { if (rel == "stuck") release.Wait(); return true; },
                () => true, _ => Task.Delay(200), gate, CancellationToken.None, null, Tiny, boom);
            Assert.Equal(new[] { "stuck" }, failed);
            Assert.True(gate.IsFree);

            // A throwing Stopped still returns the full list; a throwing FinishedSlow doesn't fail a good song.
            var capped = await StatsDetectLoop.RunAsync(["s1", "s2", "s3", "d"], _ => { release.Wait(); return true; },
                () => true, _ => Task.CompletedTask, gate, CancellationToken.None, null, Tiny, boom);
            Assert.Equal(new[] { "s1", "s2", "s3", "d" }, capped);
            var slowOk = await StatsDetectLoop.RunAsync(["slow"], _ => { Thread.Sleep(200); return true; },
                () => true, _ => Task.CompletedTask, gate, CancellationToken.None, null,
                new StatsDetectLimits(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2), 3), boom);
            Assert.Empty(slowOk);
            Assert.True(gate.IsFree);
        }
        finally { release.Set(); }
    }

    [Fact] // [EDGE-stats-song-timeout-4]
    public async Task StatsDetectLoop_QuickFailures_NoNotes()
    {
        var rec = new Recorder();
        var failed = await Loop(["a", "bad", "boom"], rel => rel == "boom" ? throw new InvalidOperationException("x") : rel != "bad",
            new LibraryReadGate(), rec);
        Assert.Equal(new[] { "bad", "boom" }, failed);
        Assert.Empty(rec.Notes);
    }

    [Fact] // [AC-stats-song-timeout-5] [EDGE-stats-song-timeout-8]
    public async Task StatsDetectLoop_StopsAfterMaxTimeouts_RestFailed_StoppedSignalled()
    {
        var gate = new LibraryReadGate();
        using var release = new ManualResetEventSlim();
        var called = new List<string>();
        var rec = new Recorder();
        try
        {
            var failed = await Loop(["s1", "s2", "s3", "d", "e"], rel => { lock (called) called.Add(rel); release.Wait(); return true; }, gate, rec);
            Assert.Equal(new[] { "s1", "s2", "s3", "d", "e" }, failed);
            Assert.Equal(new[] { "s1", "s2", "s3" }, called);
            Assert.Equal(new[] { new StatsDetectStop(3, 2) }, rec.Stops);
            Assert.Equal(3, rec.Notes.Count(n => n.Kind == StatsSongNote.TimedOut));
            Assert.True(gate.IsFree);
        }
        finally { release.Set(); }
    }

    [Fact] // [AC-stats-song-timeout-5] [EDGE-stats-song-timeout-9]
    public async Task StatsDetectLoop_CapOnLastSong_StoppedWithZeroSkipped()
    {
        using var release = new ManualResetEventSlim();
        var rec = new Recorder();
        try
        {
            var failed = await Loop(["s1", "s2", "s3"], _ => { release.Wait(); return true; }, new LibraryReadGate(), rec);
            Assert.Equal(new[] { "s1", "s2", "s3" }, failed);
            Assert.Equal(new[] { new StatsDetectStop(3, 0) }, rec.Stops);
        }
        finally { release.Set(); }
    }

    [Fact] // [EDGE-stats-song-timeout-5]
    public async Task StatsDetectLoop_CancelDuringSlowSong_WaitsForSong_ThenStops()
    {
        var gate = new LibraryReadGate();
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();
        bool aReturned = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Loop(["a", "b"], rel =>
        {
            lock (seen) seen.Add(rel);
            cts.Cancel();
            Thread.Sleep(100);
            Volatile.Write(ref aReturned, true);
            return true;
        }, gate, new Recorder(), cts.Token, limits: new StatsDetectLimits(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 3)));
        Assert.True(Volatile.Read(ref aReturned));
        Assert.Equal(new[] { "a" }, seen);
        Assert.True(gate.IsFree);
    }

    [Fact] // [EDGE-stats-song-timeout-6]
    public async Task StatsDetectLoop_AbandonedWorkerFault_IsObserved()
    {
        string marker = "abandoned-" + Guid.NewGuid().ToString("N");
        bool unobserved = false;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message == marker)) unobserved = true;
        };
        TaskScheduler.UnobservedTaskException += handler;
        using var release = new ManualResetEventSlim();
        using var thrown = new ManualResetEventSlim();
        try
        {
            await Loop(["stuck"], _ =>
            {
                release.Wait();
                thrown.Set();
                throw new InvalidOperationException(marker);
            }, new LibraryReadGate(), new Recorder());
            release.Set();
            Assert.True(thrown.Wait(TimeSpan.FromSeconds(5)));
            await Task.Delay(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(unobserved);
        }
        finally
        {
            release.Set();
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [Fact] // [EDGE-stats-song-timeout-7]
    public async Task StatsDetectLoop_AbandonedWorkerFinishesLate_ResultUnchanged()
    {
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var failed = await Loop(["late"], _ => { release.Wait(); finished.Set(); return true; }, new LibraryReadGate(), new Recorder());
        release.Set();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "late" }, failed);
    }

    [Fact] // [EDGE-stats-song-timeout-24]
    public async Task StatsDetectLoop_SlowAfterAtLeastTimeout_NoThrow()
    {
        using var release = new ManualResetEventSlim();
        var rec = new Recorder();
        try
        {
            var failed = await Loop(["stuck"], _ => { release.Wait(); return true; }, new LibraryReadGate(), rec,
                limits: new StatsDetectLimits(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), 3));
            Assert.Equal(new[] { "stuck" }, failed);
            Assert.Equal(new[] { StatsSongNote.StillRunning, StatsSongNote.TimedOut }, rec.Notes.Select(n => n.Kind));
        }
        finally { release.Set(); }
    }

    [Fact] // [AC-stats-song-timeout-6]
    public void StatsDetectLimits_Defaults()
    {
        Assert.Equal(new StatsDetectLimits(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), 3), StatsDetectLimits.Default);
    }

    // ── Wait phase ──

    [Fact] // [AC-stats-song-timeout-8] [EDGE-stats-song-timeout-16]
    public async Task StatsDetectLoop_LongWait_NotesWaitingOnce()
    {
        var rec = new Recorder();
        int detects = 0;
        var failed = await Loop(["a"], _ => { detects++; return true; }, new LibraryReadGate(), rec,
            wait: _ => Task.Delay(300));
        Assert.Empty(failed);
        Assert.Equal(1, detects);
        var w = Assert.Single(rec.Waits);
        Assert.Equal(0, w.Index);
        Assert.True(w.Elapsed >= TimeSpan.FromMilliseconds(95));
    }

    [Fact] // [AC-stats-song-timeout-8] [EDGE-stats-song-timeout-16]
    public async Task StatsDetectLoop_GateHeldByOther_NotesWaiting_HolderVisible()
    {
        var gate = new LibraryReadGate();
        await gate.EnterAsync(CancellationToken.None, "ai");
        string? holderSeen = null;
        bool heldSeen = false;
        var hooks = new StatsDetectHooks(Waiting: (_, _) => { holderSeen = gate.Holder; heldSeen = !gate.IsFree; });
        var run = StatsDetectLoop.RunAsync(["a"], _ => true, () => true, _ => Task.CompletedTask, gate, CancellationToken.None,
            null, Tiny, hooks);
        await Task.Delay(300);
        Assert.Equal("ai", holderSeen);
        Assert.True(heldSeen);
        gate.Exit();
        Assert.Empty(await run);
        Assert.True(gate.IsFree);
    }

    [Fact] // [EDGE-stats-song-timeout-18]
    public async Task StatsDetectLoop_CancelDuringLongWait_NoNote_GateFree()
    {
        var gate = new LibraryReadGate();
        var rec = new Recorder();
        using var cts = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Loop(["a"], _ => true, gate, rec, cts.Token,
            wait: ct => Task.Delay(Timeout.Infinite, ct), limits: new StatsDetectLimits(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), 3)));
        await Task.Delay(400);
        Assert.Empty(rec.Waits);
        Assert.True(gate.IsFree);
    }

    [Fact] // [EDGE-stats-song-timeout-20]
    public async Task LibraryReadGate_Holder_SetOnEnter_ClearedOnExit_FollowsHandover()
    {
        var gate = new LibraryReadGate();
        Assert.Null(gate.Holder);
        await gate.EnterAsync(CancellationToken.None, "a");
        Assert.Equal("a", gate.Holder);
        var b = gate.EnterAsync(CancellationToken.None, "b");
        Assert.False(b.IsCompleted);
        gate.Exit();
        await b;
        Assert.Equal("b", gate.Holder);
        gate.Exit();
        Assert.Null(gate.Holder);
        gate.Enter(CancellationToken.None, "tags");
        Assert.Equal("tags", gate.Holder);
        gate.Exit();
        Assert.True(gate.IsFree);
    }

    [Fact] // [AC-stats-song-timeout-8] [EDGE-stats-song-timeout-19]
    public void StatsWaitReason_Describe_Table()
    {
        Assert.Equal("nothing found busy", StatsWaitReason.Describe(false, false, false, false, null));
        Assert.Equal("busy overlay", StatsWaitReason.Describe(true, false, false, false, null));
        Assert.Equal("dialog open", StatsWaitReason.Describe(false, true, false, false, null));
        Assert.Equal("dialog gate held", StatsWaitReason.Describe(false, false, true, false, null));
        Assert.Equal("library gate held by ai", StatsWaitReason.Describe(false, false, false, true, "ai"));
        Assert.Equal("library gate held by unknown", StatsWaitReason.Describe(false, false, false, true, null));
        Assert.Equal("busy overlay, dialog open, dialog gate held, library gate held by tags",
            StatsWaitReason.Describe(true, true, true, true, "tags"));
    }

    // ── Auto-run rules ──

    [Fact] // [AC-stats-song-timeout-5] [EDGE-stats-song-timeout-11]
    public void AutoStatsRules_StoppedEarly_KeepsAttempt_NoMarker()
    {
        Assert.True(AutoStatsRules.KeepsAttempt(StatsPassEnd.Finished, 0, stoppedEarly: true));
        Assert.False(AutoStatsRules.ShouldWriteMarker(StatsPassEnd.Finished, 0, 746, 37, stoppedEarly: true));
        string? attempt = null;
        for (int i = 0; i < AutoStatsRules.MaxAttempts; i++)
        {
            Assert.True(AutoStatsRules.ShouldRun(null, attempt, "3.0.2", true));
            attempt = AutoStatsRules.NextAttempt(attempt, "3.0.2");
        }
        Assert.False(AutoStatsRules.ShouldRun(null, attempt, "3.0.2", true));
    }

    [Fact] // [AC-stats-song-timeout-6] [EDGE-stats-song-timeout-10]
    public void AutoStatsRules_IsolatedTimeouts_StillWriteMarker()
    {
        Assert.True(AutoStatsRules.ShouldWriteMarker(StatsPassEnd.Finished, 0, 746, 37));
        Assert.False(AutoStatsRules.KeepsAttempt(StatsPassEnd.Finished, 0));
        string run = Run();
        int marker = At(run, "AutoStatsRules.ShouldWriteMarker(");
        string cond = run[marker..run.IndexOf('\n', marker)];
        Assert.DoesNotContain("failed", cond);
        Assert.DoesNotContain("timedOut", cond);
    }

    // ── ffmpeg ──

    [Fact] // [AC-stats-song-timeout-7] [EDGE-stats-song-timeout-22]
    public void TranscodeTimeoutFor_FloorOrTwiceLength()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), FFMpeg.TranscodeTimeoutFor(null));
        Assert.Equal(TimeSpan.FromMinutes(2), FFMpeg.TranscodeTimeoutFor(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromMinutes(2), FFMpeg.TranscodeTimeoutFor(TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromMinutes(20), FFMpeg.TranscodeTimeoutFor(TimeSpan.FromMinutes(10)));
        Assert.Contains("timeout ?? TranscodeTimeout", Method(Src("Utils/FFMpeg.cs"), "public static void TranscodeToOgg("));
    }

    [Fact] // [AC-stats-song-timeout-7] [EDGE-stats-song-timeout-21]
    public void TranscodeToOgg_TimesOut()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pickles-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string wav = Path.Combine(dir, "in.wav"), ogg = Path.Combine(dir, "out.ogg");
        try
        {
            File.WriteAllBytes(wav, SilentWav(44100 * 5));
            Assert.Throws<TimeoutException>(() => FFMpeg.TranscodeToOgg(wav, ogg, null, null, TimeSpan.FromMilliseconds(1)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static byte[] SilentWav(int frames)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int data = frames * 2;
        bw.Write("RIFF"u8); bw.Write(36 + data); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(44100); bw.Write(88200);
        bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(data); bw.Write(new byte[data]);
        return ms.ToArray();
    }

    [Fact] // [AC-stats-song-timeout-7] [EDGE-stats-song-timeout-22]
    public void AdpcmWav_Duration()
    {
        // MS-ADPCM mono, 44100 Hz, blockAlign 256, 500 samples per block.
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write((ushort)2); bw.Write((ushort)1); bw.Write(44100); bw.Write(44100 * 256 / 500);
            bw.Write((ushort)256); bw.Write((ushort)4); bw.Write((ushort)32); bw.Write((ushort)500);
        }
        byte[] fmt = ms.ToArray();
        var d = AdpcmWav.Duration(fmt, 882 * 256);
        Assert.NotNull(d);
        Assert.Equal(882 * 500 / 44100.0, d!.Value.TotalSeconds, 3);
        Assert.Null(AdpcmWav.Duration(fmt[..6], 1000));
        byte[] zeroRate = (byte[])fmt.Clone();
        BitConverter.GetBytes(0).CopyTo(zeroRate, 4);
        Assert.Null(AdpcmWav.Duration(zeroRate, 1000));
        byte[] noChannels = (byte[])fmt.Clone();
        BitConverter.GetBytes((ushort)0).CopyTo(noChannels, 2);
        Assert.Null(AdpcmWav.Duration(noChannels, 1000)); // BytesToSamples throws; Duration says unknown
    }

    [Fact] // [AC-stats-song-timeout-7] [EDGE-stats-song-timeout-21]
    public void TranscodeAdpcm_LengthTimeout_WrapsTimeout()
    {
        string m = Method(Src("Tools/ScdOggExtractor.cs"), "private static void TranscodeAdpcm(");
        Assert.Contains("FFMpeg.TranscodeTimeoutFor(AdpcmWav.Duration(adpcm.WaveHeader, adpcm.Data.Length))", m);
        int c = At(m, "ex is InvalidOperationException or TimeoutException");
        Assert.True(At(m, "File.Delete(outOggPath)") > c);
    }

    // ── Wiring ──

    [Fact] // [AC-stats-song-timeout-2] [AC-stats-song-timeout-3] [EDGE-stats-song-timeout-13] [EDGE-stats-song-timeout-15]
    public void StatsPass_SongNotes_LogNameRelStepSize_NoFullPath()
    {
        string run = Run();
        Assert.Contains("StatsDetectLimits.Default", run);
        Assert.True(At(run, "new StatsDetectHooks(") > At(run, "StatsDetectLoop.RunAsync("));
        string stats = Stats();
        string note = Method(stats, "private void LogSongNote(");
        Assert.Contains("still analysing {Song} ({Rel}, {SizeKb} KB) at step {Step}", note);
        Assert.Contains("{Song} ({Rel}) took {Seconds} s", note);
        Assert.Contains("{Song} ({Rel}, {SizeKb} KB) timed out at step {Step}", note);
        Assert.Contains(".TryAdd(", run);
        string size = Method(stats, "private static string SizeKb(");
        Assert.True(At(size, "try") < At(size, "new FileInfo("));
        Assert.Contains("\"?\"", size);
        Assert.DoesNotContain("PenumbraLocation", note);
    }

    [Fact] // [AC-stats-song-timeout-2] [EDGE-stats-song-timeout-14]
    public void DetectOne_SetsStepPerRel()
    {
        string d = Method(Stats(), "private static bool DetectOne");
        Assert.True(At(d, "new DetectStep(rel, \"BPM\"") < At(d, "BPMDetector.GetBPMFromSCD(rel);"));
        Assert.True(At(d, "new DetectStep(rel, \"Key\"") < At(d, "KeyDetector.GetKeyFromSCD(rel);"));
        Assert.True(At(d, "new DetectStep(rel, \"Duration\"") < At(d, "BPMDetector.GetDuration(rel);"));
        Assert.Contains("return HasAllStats(rel);", d);
        Assert.True(At(d, "finally") > At(d, "BPMDetector.GetDuration(rel);"));
        Assert.Contains("if (s_detectStep?.Rel == rel) s_detectStep = null;", d);
        Assert.True(At(d, "new DetectStep(rel, \"size\"") < At(d, "SizeKb(rel)"));
        Assert.Contains(".Rel == rel", Method(Stats(), "private void LogSongNote("));
    }

    [Fact] // [AC-stats-song-timeout-4] [EDGE-stats-song-timeout-23]
    public void StatsPass_LogsSongCountAtStart()
    {
        string run = Run();
        int start = At(run, "Stats pass {Kind} started: {ToDetect} song(s) to analyse of {Entries}");
        Assert.True(At(run, "StatsPlan.Build(") < start);
        Assert.True(start < At(run, "StatsDetectLoop.RunAsync("));
        Assert.Contains("plan.ToDetect.Count, entryCount", run);
    }

    [Fact] // [AC-stats-song-timeout-5]
    public void StatsPass_StoppedEarly_Wired()
    {
        string run = Run();
        Assert.Contains("stoppedEarly = true", run[At(run, "Stopped:")..]);
        Assert.Contains("stoppedEarly)", run[At(run, "AutoStatsRules.ShouldWriteMarker(")..]);
        Assert.Contains("AutoStatsRules.KeepsAttempt(end, saveErrors.Count, stoppedEarly)", run);
        Assert.Contains("timed out; analysis stopped", run);
        Assert.Contains("{TimedOut} timed out", run);
    }

    [Fact] // [EDGE-stats-song-timeout-12]
    public void StatsPass_TimedOutAndSkipped_ReachManualSummary()
    {
        string run = Run();
        int list = At(run, "song(s) not analysed: {Songs}");
        Assert.True(At(run, "failedRels = failed.Count;") < list);
        Assert.Contains("failed.Take(10)", run);
        Assert.Contains("failedSet.Contains(e.Rel)", run);
        Assert.Contains("failedSongs.Select(AppStrings.StatsAnalyseFailed)", run);
    }

    [Fact] // [AC-stats-song-timeout-8]
    public void StatsPass_WaitNote_Wired()
    {
        string run = Run();
        string wait = run[At(run, "Waiting:")..];
        Assert.Contains("StatsWaitReason.Describe(_busyOverlayVisible, ", wait);
        Assert.Contains("_dialogGate.CurrentCount == 0", wait);
        Assert.Contains("!LibraryReadGate.Shared.IsFree, LibraryReadGate.Shared.Holder", wait);
        Assert.Contains("LibraryReadGate.Shared.Enter(ct, \"tags\");", Src("ui/MainWindow.SongTags.cs"));
        Assert.Contains("LibraryReadGate.Shared.Enter(ct, \"ai\");", Src("ui/MainWindow.AiScore.cs"));
        Assert.Contains("await gate.EnterAsync(ct, \"stats\");", Src("Tools/StatsDetectLoop.cs"));
    }

    [Fact] // [EDGE-stats-song-timeout-25]
    public void Detect_AbandonedWorker_IsThreadPool()
    {
        string loop = Src("Tools/StatsDetectLoop.cs");
        Assert.Contains("Task.Run(() => { using (ThreadPriorityScope.Background()) return detect(rel); })", loop);
        Assert.DoesNotContain("new Thread", loop);
        Assert.DoesNotContain("LongRunning", loop);
        Assert.Contains("StatsCacheWrites.HoldForExit();", Method(Stats(), "internal void PrepareForExit"));
    }
}
