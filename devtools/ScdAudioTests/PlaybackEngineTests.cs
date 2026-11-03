using System.Collections.Concurrent;
using System.Reflection;
using NAudio.Wave;
using Pickles_Playlist_Editor.Utils;
using Xunit;
using Xunit.Abstractions;

namespace ScdAudioTests;

[Collection("ffmpeg")]
public sealed class PlaybackEngineTests : IDisposable
{
    private readonly AudioFixtures _fx = new();
    private readonly ITestOutputHelper _log;
    private readonly List<FakeAudioOutput> _outputs = new();
    private readonly ConcurrentQueue<string> _warnings = new();

    public PlaybackEngineTests(ITestOutputHelper log)
    {
        _log = log;
        Environment.CurrentDirectory = AppContext.BaseDirectory;
    }

    public void Dispose() => _fx.Dispose();

    private PlaybackEngine NewEngine() => new(() => { var o = new FakeAudioOutput(); lock (_outputs) _outputs.Add(o); return o; }, w => _warnings.Enqueue(w));

    private FakeAudioOutput Last { get { lock (_outputs) return _outputs[^1]; } }

    private static bool WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > ms) return false; Thread.Sleep(10); }
        return true;
    }

    [Fact] // [AC-x64-naudio-3]
    public void TrackEnd_InvokesOnEndedOnce()
    {
        var engine = NewEngine();
        var ended = new ConcurrentQueue<long>();
        string ogg = _fx.MakeSineOgg(1);

        long genA = engine.Open(ogg, g => ended.Enqueue(g));
        Last.DrainAndEnd();
        Assert.True(WaitFor(() => ended.Count == 1));
        Thread.Sleep(200);
        Assert.Equal(new[] { genA }, ended.ToArray());
        Assert.False(engine.IsPlaying);
        Assert.True(engine.IsCurrent(genA));

        // A later session reports its own generation, never a stale or fresh read.
        long genB = engine.Open(ogg, g => ended.Enqueue(g));
        Assert.NotEqual(genA, genB);
        Last.DrainAndEnd();
        Assert.True(WaitFor(() => ended.Count == 2));
        Assert.Equal(new[] { genA, genB }, ended.ToArray());
    }

    [Fact] // [AC-x64-naudio-3]
    public void Stop_NeverInvokesOnEnded()
    {
        var engine = NewEngine();
        int ended = 0;
        engine.Open(_fx.MakeSineOgg(2), _ => Interlocked.Increment(ref ended));
        var output = Last;
        engine.Stop();
        output.FireStopped(null); // the device reports the stop after the fact
        Thread.Sleep(300);
        Assert.Equal(0, ended);
        Assert.False(engine.IsPlaying);
        Assert.True(output.Disposed);
    }

    [Fact] // [AC-x64-naudio-3] [EDGE-x64-naudio-24]
    public void TrackEnd_ThenOpenOrStop_BeforeQueuedAdvance_IsNotCurrent()
    {
        string ogg = _fx.MakeSineOgg(1);
        foreach (var variant in new[] { "open", "stop", "none" })
        {
            var engine = NewEngine();
            var queue = new ConcurrentQueue<long>(); // stands in for DispatcherQueue.TryEnqueue
            long genA = engine.Open(ogg, g => queue.Enqueue(g));
            Last.DrainAndEnd();
            Assert.True(WaitFor(() => queue.Count == 1));
            Assert.False(engine.IsPlaying);

            long genB = 0;
            if (variant == "open") genB = engine.Open(ogg, _ => { });
            if (variant == "stop") engine.Stop();

            Assert.True(queue.TryDequeue(out long queued));
            Assert.Equal(genA, queued);
            Assert.Equal(variant == "none", engine.IsCurrent(queued));
            if (variant == "open") Assert.True(engine.IsCurrent(genB));
            engine.Stop();
        }
    }

    [Fact] // [AC-x64-naudio-3] [EDGE-x64-naudio-31]
    public void StoppedA_DuringOpenB_AfterGenBump_NoOnEndedForA()
    {
        string ogg = _fx.MakeSineOgg(2);
        var engine = NewEngine();
        int endedA = 0;
        long genA = engine.Open(ogg, _ => Interlocked.Increment(ref endedA));
        var outA = Last;

        using var atHook = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        engine.TestHook = p => { if (p == "open-after-gen") { atHook.Set(); release.Wait(); } };
        long genB = 0;
        var t1 = new Thread(() => genB = engine.Open(ogg, _ => { }));
        t1.Start();
        Assert.True(atHook.Wait(5000));
        engine.TestHook = null;

        outA.FireStopped(null);
        release.Set();
        t1.Join();

        Thread.Sleep(300);
        Assert.Equal(0, endedA);
        Assert.True(engine.IsPlaying);
        Assert.True(engine.IsCurrent(genB));
        Assert.False(engine.IsCurrent(genA));
    }

    [Fact] // [AC-x64-naudio-3] [EDGE-x64-naudio-31]
    public void StoppedA_HeldBetweenCheckAndWrite_OpenBBlocks_BEndsPlaying()
    {
        string ogg = _fx.MakeSineOgg(2);
        var engine = NewEngine();
        var endedA = new ConcurrentQueue<long>();
        long genA = engine.Open(ogg, g => endedA.Enqueue(g));
        var outA = Last;
        int factoryCallsBefore;
        lock (_outputs) factoryCallsBefore = _outputs.Count;

        using var atHook = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        engine.TestHook = p => { if (p == "stopped-after-check") { atHook.Set(); release.Wait(); } };
        var t2 = outA.FireStoppedAsync(null);
        Assert.True(atHook.Wait(5000));

        long genB = 0;
        var t1 = new Thread(() => genB = engine.Open(ogg, _ => { }));
        t1.Start();
        Thread.Sleep(200);
        lock (_outputs) Assert.Equal(factoryCallsBefore, _outputs.Count); // Open(B) waits on the state lock

        release.Set();
        t2.Join();
        t1.Join();
        engine.TestHook = null;

        Assert.True(WaitFor(() => endedA.Count == 1));
        Assert.Equal(genA, endedA.Single());
        Assert.False(engine.IsCurrent(genA));
        Assert.True(engine.IsPlaying);
        Assert.True(engine.IsCurrent(genB));
    }

    [Fact] // [AC-x64-naudio-3]
    public void StopDuringOpenIo_OpenDoesNotSetPlaying()
    {
        string ogg = _fx.MakeSineOgg(2);
        string copy = Path.Combine(_fx.Dir, "deletable.ogg");
        File.Copy(ogg, copy);
        var engine = NewEngine();
        using var atHook = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        engine.TestHook = p => { if (p == "open-after-gen") { atHook.Set(); release.Wait(); } };
        var t1 = new Thread(() => engine.Open(copy, _ => { }));
        t1.Start();
        Assert.True(atHook.Wait(5000));
        engine.Stop();
        release.Set();
        t1.Join();

        Assert.False(engine.IsPlaying);
        Assert.True(Last.Disposed);
        File.Delete(copy); // the reader was closed
        Assert.False(File.Exists(copy));
    }

    [Fact] // [EDGE-x64-naudio-9]
    public void SeekWhilePaused_UpdatesPosition_StaysPaused()
    {
        var engine = NewEngine();
        engine.Open(_fx.MakeSineOgg(6), _ => { });
        engine.TogglePause();
        Assert.True(engine.IsPaused);
        engine.Seek(TimeSpan.FromSeconds(3));
        Assert.InRange(engine.Position.TotalMilliseconds, 2999, 3001);
        Assert.True(engine.IsPaused);
        engine.TogglePause();
        Assert.False(engine.IsPaused);
        Assert.True(engine.IsPlaying);
    }

    [Fact] // [EDGE-x64-naudio-9]
    public void Seek_IsClampedShortOfTheEnd()
    {
        var engine = NewEngine();
        engine.Open(_fx.MakeSineOgg(4), _ => { });
        engine.Seek(TimeSpan.FromSeconds(99));
        Assert.InRange(engine.Position.TotalSeconds, engine.Duration.TotalSeconds - 0.51, engine.Duration.TotalSeconds - 0.49);
        engine.Seek(TimeSpan.FromSeconds(-5));
        Assert.Equal(TimeSpan.Zero, engine.Position);
    }

    [Fact] // [AC-x64-naudio-4]
    public void Duration_AndVolume()
    {
        var engine = NewEngine();
        engine.Volume = 150;
        Assert.Equal(100, engine.Volume);
        engine.Volume = 40;
        engine.Open(_fx.MakeSineOgg(3), _ => { });
        Assert.InRange(engine.Duration.TotalSeconds, 2.9, 3.1);
        Assert.Equal(40, engine.Volume);
        engine.Stop();
        Assert.Equal(TimeSpan.Zero, engine.Duration);
    }

    [Fact] // [AC-x64-naudio-4]
    public void Open_MissingFile_ThrowsDecoderMessage()
    {
        var engine = NewEngine();
        var ex = Assert.Throws<InvalidOperationException>(() => engine.Open(Path.Combine(_fx.Dir, "nope.ogg"), _ => { }));
        Assert.StartsWith("The audio decoder could not open this track", ex.Message);
        Assert.False(engine.IsPlaying);
    }

    [Fact] // [EDGE-x64-naudio-26]
    public void ReadAfterStop_Silent_DecoderFailure_WarnsOnce()
    {
        var engine = NewEngine();
        engine.Open(_fx.MakeSineOgg(2), _ => { });
        var source = Last.Source!;
        engine.Stop();
        Assert.Equal(0, source.Read(new float[1024], 0, 1024));
        Assert.Empty(_warnings);

        // NVorbis skips corrupt pages without throwing, so the failure comes from a reader that throws mid-stream.
        var failing = new PlaybackEngine(() => { var o = new FakeAudioOutput(); lock (_outputs) _outputs.Add(o); return o; },
            w => _warnings.Enqueue(w), path => new ThrowingReader(path, readsBeforeFailure: 3));
        int ended = 0;
        failing.Open(_fx.MakeSineOgg(5), _ => Interlocked.Increment(ref ended));
        Last.DrainAndEnd();
        Assert.Equal(0, Last.Source!.Read(new float[1024], 0, 1024));
        Assert.True(WaitFor(() => ended == 1));
        Assert.Single(_warnings, w => w.StartsWith("Playback decoder failure"));
        Assert.Single(_warnings);
    }

    [Fact] // [EDGE-x64-naudio-27]
    public void WaveOutAudioOutput_CapturesNoSyncContext()
    {
        var saved = SynchronizationContext.Current;
        var recording = new RecordingContext();
        SynchronizationContext.SetSynchronizationContext(recording);
        try
        {
            using var output = new WaveOutAudioOutput();
            var inner = typeof(WaveOutAudioOutput).GetField("_out", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(output)!;
            var ctx = typeof(WaveOutEvent).GetField("syncContext", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.Null(ctx.GetValue(inner));
            Assert.Same(recording, SynchronizationContext.Current);
        }
        finally { SynchronizationContext.SetSynchronizationContext(saved); }
    }

    [SkippableFact] // [AC-x64-naudio-4] [EDGE-x64-naudio-27]
    public void RealOutput_PlaysAndStops_WhenDeviceExists()
    {
        Skip.If(WaveOut.DeviceCount == 0, "no audio output device");
        var saved = SynchronizationContext.Current;
        var recording = new RecordingContext();
        SynchronizationContext.SetSynchronizationContext(recording);
        try
        {
            var engine = new PlaybackEngine(() => new WaveOutAudioOutput(), w => _warnings.Enqueue(w));
            engine.Volume = 0;
            int ended = 0;
            engine.Open(_fx.MakeSineOgg(1), _ => Interlocked.Increment(ref ended));
            Assert.True(WaitFor(() => ended == 1, 10000));
            Thread.Sleep(200);
            Assert.Equal(1, ended);
            Assert.Equal(0, recording.Posts);

            engine.Open(_fx.MakeSineOgg(2), _ => { });
            engine.Stop();
            Assert.False(engine.IsPlaying);
            _log.WriteLine("Ran on a real audio device.");
        }
        finally { SynchronizationContext.SetSynchronizationContext(saved); }
    }

    private sealed class ThrowingReader : WaveStream, ISampleProvider
    {
        private readonly NAudio.Vorbis.VorbisWaveReader _inner;
        private int _left;
        public ThrowingReader(string path, int readsBeforeFailure) { _inner = new(path); _left = readsBeforeFailure; }
        public override WaveFormat WaveFormat => _inner.WaveFormat;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public int Read(float[] buffer, int offset, int count)
        {
            if (_left-- <= 0) throw new InvalidDataException("bad packet");
            return _inner.Read(buffer, offset, count);
        }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Posts); base.Post(d, state); }
        public override void Send(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Posts); base.Send(d, state); }
    }
}
