using System.Diagnostics;
using NAudio.Wave;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;

namespace ScdAudioTests;

/// <summary>Builds ADPCM .scd fixtures from ffmpeg-generated audio in a per-test temp folder.</summary>
public sealed class AdpcmScdFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "scd-audio-tests", Guid.NewGuid().ToString("N"));

    public AdpcmScdFixture()
    {
        // FFMpeg.Run finds "ffmpeg.exe" and ScdFile.Import finds "default.scd" relative to these.
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        Directory.CreateDirectory(Dir);
    }

    public static string DefaultScd => Path.Combine(AppContext.BaseDirectory, "default.scd");

    public string PathFor(string name) => Path.Combine(Dir, name);

    /// <summary>The fmt chunk body and data of a sine (or noise) encoded as MS-ADPCM by ffmpeg.</summary>
    public (byte[] Fmt, byte[] Data) MakeAdpcm(int channels, int rate, double seconds = 3, bool noise = false)
    {
        string wav = PathFor($"src-{Guid.NewGuid():N}.wav");
        string dur = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Noise encodes to many Vorbis pages; a pure sine fits in one or two.
        string source = noise ? $"anoisesrc=d={dur}:a=0.3" : $"sine=frequency=440:duration={dur}";
        RunFfmpeg($"-y -f lavfi -i {source} " +
                  $"-ac {channels} -ar {rate} -c:a adpcm_ms \"{wav}\"");
        return ReadChunks(File.ReadAllBytes(wav));
    }

    public static (byte[] Fmt, byte[] Data) ReadChunks(byte[] riff)
    {
        using var br = new BinaryReader(new MemoryStream(riff));
        br.ReadBytes(12);
        byte[]? fmt = null, data = null;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            byte[] body = br.ReadBytes(size);
            if ((size & 1) == 1 && br.BaseStream.Position < br.BaseStream.Length) br.ReadByte();
            if (id == "fmt ") fmt = body;
            else if (id == "data") data = body;
        }
        return (fmt!, data!);
    }

    /// <summary>A fresh audio entry built on default.scd's first entry (for in-memory edge cases).</summary>
    public static (ScdFile Scd, ScdAudioEntry Old) LoadDefault()
    {
        string copy = CopyOfDefault();
        try
        {
            var scd = ScdFile.Import(copy);
            return (scd, scd.Audio[0]);
        }
        finally { File.Delete(copy); }
    }

    public static ScdAudioEntry AdpcmEntry(ScdAudioEntry old, byte[] fmt, byte[] data, int channels, int rate,
        int loopStart = 0, int loopEnd = 0, byte[]? waveHeaderOverride = null, int? dataLength = null)
    {
        var entry = new ScdAudioEntry(old, dataLength ?? data.Length, channels, rate, SscfWaveFormat.MsAdPcm);
        using var br = new BinaryReader(new MemoryStream(fmt));
        entry.Data = new ScdAdpcm(WaveFormat.FromFormatChunk(br, fmt.Length), waveHeaderOverride ?? fmt, data, entry);
        entry.LoopStart = loopStart;
        entry.LoopEnd = loopEnd;
        return entry;
    }

    /// <summary>Writes an .scd whose only audio entry is the given ADPCM audio.</summary>
    public string WriteAdpcmScd(byte[] fmt, byte[] data, int channels, int rate, int loopStart = 0, int loopEnd = 0)
    {
        var (scd, old) = LoadDefault();
        scd.Replace(old, AdpcmEntry(old, fmt, data, channels, rate, loopStart, loopEnd));
        string path = PathFor($"adpcm-{Guid.NewGuid():N}.scd");
        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
            scd.Write(bw);
        return path;
    }

    public string MakeAdpcmScd(int channels = 2, int rate = 44100, int loopStart = 0, int loopEnd = 0)
    {
        var (fmt, data) = MakeAdpcm(channels, rate);
        return WriteAdpcmScd(fmt, data, channels, rate, loopStart, loopEnd);
    }

    public static ushort BlockAlign(byte[] fmt) => BitConverter.ToUInt16(fmt, 12);

    public static ushort SamplesPerBlock(byte[] fmt) => BitConverter.ToUInt16(fmt, 18);

    private static string CopyOfDefault()
    {
        string copy = Path.Combine(Path.GetTempPath(), "scd-audio-tests", $"default-{Guid.NewGuid():N}.scd");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(DefaultScd, copy);
        return copy;
    }

    private static void RunFfmpeg(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        })!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("fixture ffmpeg failed: " + err);
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { }
    }
}
