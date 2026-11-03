using NAudio.Wave;

namespace ScdAudioTests;

/// <summary>Synthetic OGGs made with the bundled ffmpeg, in a per-test temp folder.</summary>
public sealed class AudioFixtures : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "ogg-" + Guid.NewGuid().ToString("N"));

    public AudioFixtures() => Directory.CreateDirectory(Dir);

    /// <summary>A 1 kHz click on every beat.</summary>
    public string MakeClickOgg(int bpm, double seconds, int channels = 2, int rate = 44100)
    {
        string path = Path.Combine(Dir, $"click{bpm}-{Guid.NewGuid():N}.ogg");
        string period = (60.0 / bpm).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        AdpcmScdFixture.RunFfmpeg($"-y -v error -f lavfi -i \"aevalsrc='if(lt(mod(t,{period}),0.03),0.8*sin(2*PI*1000*t),0)':s={rate}:d={seconds}\" -ac {channels} -c:a libvorbis -q:a 5 \"{path}\"");
        return path;
    }

    public string MakeSilenceOgg(double seconds)
    {
        string path = Path.Combine(Dir, $"silence-{Guid.NewGuid():N}.ogg");
        AdpcmScdFixture.RunFfmpeg($"-y -v error -f lavfi -i anullsrc=r=44100:cl=stereo -t {seconds} -c:a libvorbis \"{path}\"");
        return path;
    }

    public string MakeSineOgg(double seconds)
    {
        string path = Path.Combine(Dir, $"sine-{Guid.NewGuid():N}.ogg");
        AdpcmScdFixture.RunFfmpeg($"-y -v error -f lavfi -i sine=frequency=440:duration={seconds} -ac 2 -c:a libvorbis \"{path}\"");
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { }
    }
}

/// <summary>Stands in for the sound device: the test pulls samples and decides when Stopped fires.</summary>
internal sealed class FakeAudioOutput : Pickles_Playlist_Editor.Utils.IAudioOutput
{
    public ISampleProvider? Source;
    public bool Playing, Paused, Disposed;
    public int StopCalls;
    public event Action<Exception?>? Stopped;

    public void Init(ISampleProvider source) => Source = source;
    public void Play() { Playing = true; Paused = false; }
    public void Pause() { Paused = true; }
    public void Stop() { StopCalls++; Playing = false; }
    public void Dispose() => Disposed = true;

    /// <summary>Reads until the source ends, then raises Stopped(null) on another thread, as WaveOutEvent does.</summary>
    public void DrainAndEnd()
    {
        var buf = new float[8192];
        while (Source!.Read(buf, 0, buf.Length) > 0) { }
        FireStopped(null);
    }

    public void FireStopped(Exception? ex)
    {
        var t = new Thread(() => Stopped?.Invoke(ex));
        t.Start();
        t.Join();
    }

    public Thread FireStoppedAsync(Exception? ex)
    {
        var t = new Thread(() => Stopped?.Invoke(ex)) { IsBackground = true };
        t.Start();
        return t;
    }
}
