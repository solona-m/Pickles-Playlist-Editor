using System.Security.Cryptography;
using NVorbis;
using Pickles_Playlist_Editor;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;
using Xunit;

namespace ScdAudioTests;

[CollectionDefinition("ffmpeg", DisableParallelization = true)]
public class FfmpegCollection { }

// Every UI path (Play, Compute Stats, Extract Audio, Equalize, Normalize) consumes ExtractOgg's .ogg,
// so these check that output. Equalize/Normalize re-create the ContextMenu flow and never write Settings.
[Collection("ffmpeg")]
public sealed class ScdOggExtractorTests : IDisposable
{
    private readonly AdpcmScdFixture fx = new();

    public void Dispose() => fx.Dispose();

    private static (int Channels, int Rate, double Seconds, double Rms) Probe(string oggPath) => Probe(File.ReadAllBytes(oggPath));

    private static (int Channels, int Rate, double Seconds, double Rms) Probe(byte[] ogg)
    {
        using var reader = new VorbisReader(new MemoryStream(ogg), true);
        var buffer = new float[reader.Channels * 4096];
        double sum = 0; long count = 0; int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++) sum += buffer[i] * buffer[i];
            count += read;
        }
        return (reader.Channels, reader.SampleRate, reader.TotalTime.TotalSeconds, count == 0 ? 0 : Math.Sqrt(sum / count));
    }

    private static (string? Start, string? End) LoopTags(string oggPath)
    {
        using var reader = new VorbisReader(oggPath);
        string? s = reader.Tags.GetTagSingle("LoopStart");
        string? e = reader.Tags.GetTagSingle("LoopEnd");
        return (string.IsNullOrEmpty(s) ? null : s, string.IsNullOrEmpty(e) ? null : e);
    }

    private static string[] TempFiles(string pattern) => Directory.GetFiles(Path.GetTempPath(), pattern);

    [Fact]
    public void ExtractOgg_Adpcm_ProducesPlayableOgg() // [AC-adpcm-songs-1]
    {
        string scd = fx.MakeAdpcmScd(2, 44100);
        string ogg = fx.PathFor("out.ogg");

        ScdOggExtractor.ExtractOgg(scd, ogg);

        var p = Probe(ogg);
        Assert.Equal(2, p.Channels);
        Assert.Equal(44100, p.Rate);
        Assert.InRange(p.Seconds, 2.9, 3.1);
        Assert.True(p.Rms > 0.05, $"RMS {p.Rms}");
    }

    [Fact]
    public void ExtractOgg_Adpcm_DurationMatchesSource() // [AC-adpcm-songs-2]
    {
        var (fmt, data) = fx.MakeAdpcm(2, 44100);
        string scd = fx.WriteAdpcmScd(fmt, data, 2, 44100);
        string ogg = fx.PathFor("out.ogg");
        double sourceSeconds = AdpcmWav.BytesToSamples(fmt, data.Length) / 44100.0;

        ScdOggExtractor.ExtractOgg(scd, ogg);

        double seconds = Probe(ogg).Seconds;
        Assert.True(seconds > 0);
        Assert.InRange(seconds, sourceSeconds - 0.1, sourceSeconds + 0.1); // no 5 s padding
    }

    [Fact]
    public void ExtractOgg_Adpcm_ExportsIntoNewFolder() // [AC-adpcm-songs-3] [EDGE-adpcm-songs-3]
    {
        string scd = fx.MakeAdpcmScd();
        string ogg = Path.Combine(fx.Dir, "new", "sub", "Song.ogg");

        ScdOggExtractor.ExtractOgg(scd, ogg);

        Assert.Equal(2, Probe(ogg).Channels);
    }

    private string ReimportAsVorbis(string adpcmScd, Action<string> process, out ScdAudioEntry newEntry)
    {
        string ogg = fx.PathFor($"proc-{Guid.NewGuid():N}.ogg");
        ScdOggExtractor.ExtractOgg(adpcmScd, ogg);
        process(ogg);

        string copy = fx.PathFor($"copy-{Guid.NewGuid():N}.scd");
        File.Copy(adpcmScd, copy);
        var scd = ScdFile.Import(copy);
        var old = scd.Audio[0];
        newEntry = ScdVorbis.ImportOgg(ogg, old);
        scd.Replace(old, newEntry);
        string result = fx.PathFor($"result-{Guid.NewGuid():N}.scd");
        using (var fs = File.Create(result))
        using (var bw = new BinaryWriter(fs))
            scd.Write(bw);
        return result;
    }

    private void AssertVorbisAndPlayable(string scdPath)
    {
        string copy = fx.PathFor($"check-{Guid.NewGuid():N}.scd");
        File.Copy(scdPath, copy);
        Assert.IsType<ScdVorbis>(ScdFile.Import(copy).Audio[0].Data);

        string ogg = fx.PathFor($"check-{Guid.NewGuid():N}.ogg");
        ScdOggExtractor.ExtractOgg(scdPath, ogg);
        Assert.InRange(Probe(ogg).Seconds, 2.5, 9.0); // ImportOgg may normalize; duration stays near 3 s
    }

    [Fact]
    public void EqualizePath_Adpcm_ConvertsToVorbisAndStillExtracts() // [AC-adpcm-songs-4]
    {
        string scd = fx.MakeAdpcmScd();
        var eq = new EqualizerSettings { BassGain = 3, TrebleGain = -2 };

        string result = ReimportAsVorbis(scd, ogg => FFMpeg.Equalize(ogg, eq.ToFilterChain()), out _);

        AssertVorbisAndPlayable(result);
    }

    [Fact]
    public void NormalizePath_Adpcm_ConvertsToVorbis() // [AC-adpcm-songs-4]
    {
        string scd = fx.MakeAdpcmScd();

        string result = ReimportAsVorbis(scd, FFMpeg.NormalizeVolume, out _);

        AssertVorbisAndPlayable(result);
    }

    [Fact]
    public void ExtractOgg_Adpcm_LeavesScdUntouched() // [AC-adpcm-songs-5]
    {
        string scd = fx.MakeAdpcmScd();
        byte[] before = SHA256.HashData(File.ReadAllBytes(scd));

        ScdOggExtractor.ExtractOgg(scd, fx.PathFor("out.ogg"));
        ScdOggExtractor.GetOggBytes(scd);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(scd)));
        string copy = fx.PathFor("copy.scd");
        File.Copy(scd, copy);
        Assert.IsType<ScdAdpcm>(ScdFile.Import(copy).Audio[0].Data);
    }

    private byte[] StoredVorbisOfDefault()
    {
        string copy = fx.PathFor($"default-{Guid.NewGuid():N}.scd");
        File.Copy(AdpcmScdFixture.DefaultScd, copy);
        return ((ScdVorbis)ScdFile.Import(copy).Audio[0].Data).Data;
    }

    [Fact]
    public void ExtractOgg_Vorbis_IsByteIdenticalToStoredStream() // [AC-adpcm-songs-6]
    {
        string ogg = fx.PathFor("vorbis.ogg");

        ScdOggExtractor.ExtractOgg(AdpcmScdFixture.DefaultScd, ogg);

        Assert.Equal(StoredVorbisOfDefault(), File.ReadAllBytes(ogg));
    }

    [Fact]
    public void ExtractOgg_UnsupportedFormat_NamesFormat() // [AC-adpcm-songs-7]
    {
        var (_, old) = AdpcmScdFixture.LoadDefault();
        var xma = new ScdAudioEntry(old, 1000, 2, 44100, SscfWaveFormat.Xma);

        var ex = Assert.Throws<InvalidOperationException>(() => ScdOggExtractor.WriteEntryAsOgg(xma, fx.PathFor("x.ogg")));

        Assert.Contains("Xma", ex.Message);
        Assert.Contains("isn't supported", ex.Message);
    }

    [Fact]
    public void GetOggBytes_Adpcm_ReturnsPlayableOgg() // [AC-adpcm-songs-8]
    {
        string scd = fx.MakeAdpcmScd(2, 44100);
        var before = TempFiles("pickles-ogg-*.ogg");

        byte[] bytes = ScdOggExtractor.GetOggBytes(scd);

        Assert.NotEmpty(bytes);
        var p = Probe(bytes);
        Assert.Equal(2, p.Channels);
        Assert.Equal(44100, p.Rate);
        Assert.Empty(TempFiles("pickles-ogg-*.ogg").Except(before));
    }

    [Fact]
    public void GetOggBytes_Vorbis_MatchesStoredStream() // [AC-adpcm-songs-8]
    {
        Assert.Equal(StoredVorbisOfDefault(), ScdOggExtractor.GetOggBytes(AdpcmScdFixture.DefaultScd));
    }

    [Fact]
    public void ExtractOgg_AdpcmMono22050_ProducesPlayableOgg() // [EDGE-adpcm-songs-1]
    {
        string scd = fx.MakeAdpcmScd(1, 22050);
        string ogg = fx.PathFor("mono.ogg");

        ScdOggExtractor.ExtractOgg(scd, ogg);

        var p = Probe(ogg);
        Assert.Equal(1, p.Channels);
        Assert.Equal(22050, p.Rate);
        Assert.InRange(p.Seconds, 2.9, 3.1);
        Assert.True(p.Rms > 0.05, $"RMS {p.Rms}");
    }

    [Fact]
    public async Task ExtractOgg_Adpcm_OverwritesExistingDestination() // [EDGE-adpcm-songs-2]
    {
        string scd = fx.MakeAdpcmScd();
        string ogg = fx.PathFor("existing.ogg");
        File.WriteAllText(ogg, "junk");

        var work = Task.Run(() => ScdOggExtractor.ExtractOgg(scd, ogg));
        var done = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(work, done);
        await work;
        Assert.Equal(2, Probe(ogg).Channels);
    }

    private static ScdAudioEntry UndecodableEntry(AdpcmScdFixture fx)
    {
        var (fmt, data) = fx.MakeAdpcm(2, 44100);
        var (_, old) = AdpcmScdFixture.LoadDefault();
        return AdpcmScdFixture.AdpcmEntry(old, fmt, data, 2, 44100, waveHeaderOverride: UndecodableHeader(fmt));
    }

    private static byte[] UndecodableHeader(byte[] fmt)
    {
        byte[] bad = (byte[])fmt.Clone();
        bad[0] = 0xFF; bad[1] = 0xFF; // format tag 0xFFFF: ffmpeg has no decoder for it
        return bad;
    }

    [Fact]
    public void ExtractOgg_UndecodableAdpcm_FailsCleanly() // [EDGE-adpcm-songs-4]
    {
        var entry = UndecodableEntry(fx);
        string ogg = fx.PathFor("bad.ogg");

        var ex = Assert.Throws<InvalidOperationException>(() => ScdOggExtractor.WriteEntryAsOgg(entry, ogg));

        Assert.StartsWith("Couldn't convert this ADPCM track", ex.Message);
        Assert.False(File.Exists(ogg));
    }

    [Fact]
    public void ExtractOgg_Adpcm_LeavesNoTempFiles() // [EDGE-adpcm-songs-5]
    {
        string scd = fx.MakeAdpcmScd();
        var (fmt, data) = fx.MakeAdpcm(2, 44100);
        string badScd = fx.WriteAdpcmScd(fmt, data, 2, 44100, waveHeaderOverride: UndecodableHeader(fmt));
        var wavBefore = TempFiles("pickles-adpcm-*.wav");
        var scdBefore = TempFiles("pickles-scd-*.scd");

        ScdOggExtractor.ExtractOgg(scd, fx.PathFor("ok.ogg"));
        var ex = Assert.Throws<InvalidOperationException>(() => ScdOggExtractor.ExtractOgg(badScd, fx.PathFor("bad.ogg")));

        Assert.StartsWith("Couldn't convert this ADPCM track", ex.Message);
        Assert.Empty(TempFiles("pickles-adpcm-*.wav").Except(wavBefore));
        Assert.Empty(TempFiles("pickles-scd-*.scd").Except(scdBefore));
    }

    [Fact]
    public void ExtractOgg_Adpcm_ConcurrentCallsSucceed() // [EDGE-adpcm-songs-5]
    {
        string scd = fx.MakeAdpcmScd();
        string[] outs = Enumerable.Range(0, 4).Select(i => fx.PathFor($"par{i}.ogg")).ToArray();

        Parallel.ForEach(outs, o => ScdOggExtractor.ExtractOgg(scd, o));

        foreach (string o in outs) Assert.InRange(Probe(o).Seconds, 2.9, 3.1);
    }

    [Fact]
    public void ExtractOgg_TruncatedAdpcmData_DecodesWhatIsPresent() // [EDGE-adpcm-songs-7]
    {
        var (fmt, data) = fx.MakeAdpcm(2, 44100);
        var (_, old) = AdpcmScdFixture.LoadDefault();
        int keep = AdpcmScdFixture.BlockAlign(fmt) * 40;
        var entry = AdpcmScdFixture.AdpcmEntry(old, fmt, data.Take(keep).ToArray(), 2, 44100, dataLength: data.Length);
        string ogg = fx.PathFor("trunc.ogg");

        ScdOggExtractor.WriteEntryAsOgg(entry, ogg);

        double seconds = Probe(ogg).Seconds;
        Assert.True(seconds > 0 && seconds < 3.0, $"{seconds} s");
    }

    [Fact]
    public void ExtractOgg_EmptyAdpcmData_Throws() // [EDGE-adpcm-songs-8]
    {
        var (fmt, _) = fx.MakeAdpcm(2, 44100);
        var (_, old) = AdpcmScdFixture.LoadDefault();
        var entry = AdpcmScdFixture.AdpcmEntry(old, fmt, [], 2, 44100, dataLength: 1000);

        var ex = Assert.Throws<InvalidOperationException>(() => ScdOggExtractor.WriteEntryAsOgg(entry, fx.PathFor("e.ogg")));

        Assert.Equal("ADPCM data is empty.", ex.Message);
    }

    [Fact]
    public void ExtractOgg_NoAudioData_Throws() // [EDGE-adpcm-songs-9]
    {
        var (_, old) = AdpcmScdFixture.LoadDefault();
        var entry = new ScdAudioEntry(old, 0, 2, 44100, SscfWaveFormat.MsAdPcm);

        var ex = Assert.Throws<InvalidOperationException>(() => ScdOggExtractor.WriteEntryAsOgg(entry, fx.PathFor("n.ogg")));

        Assert.Equal("This track has no audio data.", ex.Message);
    }

    [Fact]
    public void ExtractOgg_AdpcmWithLoop_WritesSampleLoopTags() // [EDGE-adpcm-songs-10]
    {
        const int ch = 2;
        var (fmt, data) = fx.MakeAdpcm(ch, 44100);
        int align = AdpcmScdFixture.BlockAlign(fmt);
        int spb = AdpcmScdFixture.SamplesPerBlock(fmt);
        string scd = fx.WriteAdpcmScd(fmt, data, ch, 44100, loopStart: 2 * align, loopEnd: 100 * align + 7 * ch + 4);
        string ogg = fx.PathFor("loop.ogg");

        ScdOggExtractor.ExtractOgg(scd, ogg);

        var (start, end) = LoopTags(ogg);
        Assert.Equal((2 * spb).ToString(), start);
        Assert.Equal((100 * spb + 4 * 2 / ch + 2).ToString(), end);
    }

    [Fact]
    public void ExtractOgg_AdpcmWithoutLoop_WritesNoLoopTags() // [EDGE-adpcm-songs-10]
    {
        string scd = fx.MakeAdpcmScd();
        string ogg = fx.PathFor("noloop.ogg");

        ScdOggExtractor.ExtractOgg(scd, ogg);

        Assert.Equal((null, null), LoopTags(ogg));
    }

    [Fact]
    public void EqualizePath_AdpcmWithLoop_KeepsLoop() // [EDGE-adpcm-songs-11]
    {
        var (fmt, data) = fx.MakeAdpcm(2, 44100, seconds: 6, noise: true);
        int align = AdpcmScdFixture.BlockAlign(fmt);
        int spb = AdpcmScdFixture.SamplesPerBlock(fmt);
        int Blocks(double s) => (int)(s * 44100 / spb);
        // Existing re-import floors loops to whole seconds and snaps them to a ~1 s seek table,
        // so a start under 2 s would land on byte 0 whatever this change does.
        string scd = fx.WriteAdpcmScd(fmt, data, 2, 44100, loopStart: Blocks(2.5) * align, loopEnd: Blocks(4.5) * align);
        var eq = new EqualizerSettings { BassGain = 2 };
        (string? Start, string? End) tagsAfterEq = default;

        ReimportAsVorbis(scd, ogg =>
        {
            FFMpeg.Equalize(ogg, eq.ToFilterChain());
            tagsAfterEq = LoopTags(ogg);
        }, out var entry);

        Assert.Equal((AdpcmWav.BytesToSamples(fmt, Blocks(2.5) * align).ToString(),
                      AdpcmWav.BytesToSamples(fmt, Blocks(4.5) * align).ToString()), tagsAfterEq);
        Assert.True(entry.LoopStart > 0, $"LoopStart {entry.LoopStart}");
        Assert.True(entry.LoopEnd > entry.LoopStart, $"LoopEnd {entry.LoopEnd}");
    }

    [Fact]
    public void ExtractOgg_AdpcmBadBlockAlignNoLoop_StillAttemptsDecode() // [EDGE-adpcm-songs-12]
    {
        var (fmt, data) = fx.MakeAdpcm(2, 44100);
        var (_, old) = AdpcmScdFixture.LoadDefault();
        byte[] bad = (byte[])fmt.Clone();
        bad[12] = 0; bad[13] = 0; // nBlockAlign = 0
        var entry = AdpcmScdFixture.AdpcmEntry(old, fmt, data, 2, 44100, waveHeaderOverride: bad);
        string ogg = fx.PathFor("ba.ogg");

        var ex = Record.Exception(() => ScdOggExtractor.WriteEntryAsOgg(entry, ogg));

        if (ex == null) Assert.True(new FileInfo(ogg).Length > 0);
        else Assert.StartsWith("Couldn't convert this ADPCM track", Assert.IsType<InvalidOperationException>(ex).Message);
    }

    [Fact]
    public void WithExtractedOgg_UsesUniqueTempAndDeletesIt() // [EDGE-adpcm-songs-14]
    {
        string scd = fx.MakeAdpcmScd();
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 4, _ => ScdOggExtractor.WithExtractedOgg(scd, ogg =>
        {
            seen.Add(ogg);
            Assert.InRange(Probe(ogg).Seconds, 2.9, 3.1);
            return 0;
        }));

        Assert.Equal(4, seen.Distinct().Count());
        Assert.All(seen, p => Assert.False(File.Exists(p)));
    }

    [Fact]
    public void WithExtractedOgg_DeletesTempWhenCallbackThrows() // [EDGE-adpcm-songs-14]
    {
        string scd = fx.MakeAdpcmScd();
        string? used = null;

        Assert.Throws<InvalidDataException>(() => ScdOggExtractor.WithExtractedOgg<int>(scd, ogg =>
        {
            used = ogg;
            throw new InvalidDataException("boom");
        }));

        Assert.NotNull(used);
        Assert.False(File.Exists(used));
    }

    [Fact]
    public void ExtractOgg_IndexOutOfRange_Throws() // [EDGE-adpcm-songs-13]
    {
        string scd = fx.MakeAdpcmScd();
        Assert.Throws<ArgumentOutOfRangeException>(() => ScdOggExtractor.ExtractOgg(scd, fx.PathFor("i.ogg"), 5));
    }

    [Fact]
    public void GetOggBytes_IndexOutOfRange_Throws() // [EDGE-adpcm-songs-13]
    {
        string scd = fx.MakeAdpcmScd();
        Assert.Throws<ArgumentOutOfRangeException>(() => ScdOggExtractor.GetOggBytes(scd, 5));
    }

    [Fact]
    public void ExtractOgg_MissingFile_Throws() // [EDGE-adpcm-songs-13]
    {
        Assert.Throws<FileNotFoundException>(() => ScdOggExtractor.ExtractOgg(fx.PathFor("nope.scd"), fx.PathFor("m.ogg")));
    }
}
