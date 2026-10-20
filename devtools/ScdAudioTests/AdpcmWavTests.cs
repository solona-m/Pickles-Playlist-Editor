using NAudio.Wave;
using Pickles_Playlist_Editor.Tools;
using Xunit;

namespace ScdAudioTests;

public class AdpcmWavTests
{
    // WAVEFORMATEX for MS-ADPCM; cbSize 32 covers wSamplesPerBlock, wNumCoef and the 7 standard coefficient pairs.
    private static byte[] AdpcmFmt(ushort channels, ushort blockAlign, ushort samplesPerBlock, bool withExtra = true)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)2);
        bw.Write(channels);
        bw.Write(44100);
        bw.Write(44100 * blockAlign / Math.Max(1, (int)samplesPerBlock));
        bw.Write(blockAlign);
        bw.Write((ushort)4);
        if (withExtra)
        {
            bw.Write((ushort)32);
            bw.Write(samplesPerBlock);
            bw.Write((ushort)7);
            short[] coefs = [256, 0, 512, -256, 0, 0, 192, 64, 240, 0, 460, -208, 392, -232];
            foreach (short c in coefs) bw.Write(c);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Build_OddLengthChunks_ArePadded() // [EDGE-adpcm-songs-6]
    {
        byte[] fmt = AdpcmFmt(1, 256, 500).Take(49).ToArray();
        byte[] data = [1, 2, 3, 4, 5];

        byte[] wav = AdpcmWav.Build(fmt, data);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(49, BitConverter.ToInt32(wav, 16));
        int dataHeader = 20 + 49 + 1;
        Assert.Equal(0, wav[20 + 49]);
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, dataHeader, 4));
        Assert.Equal(5, BitConverter.ToInt32(wav, dataHeader + 4));
        Assert.Equal(dataHeader + 8 + 5 + 1, wav.Length);
        Assert.Equal(0, wav[^1]);
    }

    [Fact]
    public void Build_RoundTripsFfmpegAdpcmWav() // [EDGE-adpcm-songs-6]
    {
        using var fx = new AdpcmScdFixture();
        var (fmt, data) = fx.MakeAdpcm(2, 44100);

        byte[] wav = AdpcmWav.Build(fmt, data);

        using var reader = new WaveFileReader(new MemoryStream(wav));
        Assert.Equal(WaveFormatEncoding.Adpcm, reader.WaveFormat.Encoding);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(44100, reader.WaveFormat.SampleRate);
        Assert.Equal(AdpcmScdFixture.BlockAlign(fmt), reader.WaveFormat.BlockAlign);
        Assert.Equal(data.Length, reader.Length);
    }

    [Theory] // [EDGE-adpcm-songs-10]
    [InlineData(1, 256, true)]
    [InlineData(2, 512, true)]
    [InlineData(1, 256, false)]
    [InlineData(2, 512, false)]
    public void BytesToSamples_UsesSamplesPerBlockOrDerives(int channels, int blockAlign, bool withExtra)
    {
        int spb = (blockAlign - 7 * channels) * 2 / channels + 2;
        byte[] fmt = AdpcmFmt((ushort)channels, (ushort)blockAlign, (ushort)spb, withExtra);

        Assert.Equal(0, AdpcmWav.BytesToSamples(fmt, 0));
        Assert.Equal(3L * spb, AdpcmWav.BytesToSamples(fmt, 3 * blockAlign));
        // Partial block: the 7-byte-per-channel header holds 2 samples, then 2 samples per byte across channels.
        Assert.Equal(3L * spb + 2 + 10 * 2 / channels, AdpcmWav.BytesToSamples(fmt, 3 * blockAlign + 7 * channels + 10));
        // Inside the block header: no complete samples yet.
        Assert.Equal(3L * spb, AdpcmWav.BytesToSamples(fmt, 3 * blockAlign + 3));
    }

    [Fact]
    public void BytesToSamples_ReadsSamplesPerBlockWhenPresent() // [EDGE-adpcm-songs-10]
    {
        // A header whose wSamplesPerBlock differs from the derived value must win.
        byte[] fmt = AdpcmFmt(1, 256, 400);
        Assert.Equal(800, AdpcmWav.BytesToSamples(fmt, 512));
    }

    [Fact]
    public void BytesToSamples_CbSizeZero_IgnoresOffset18() // [EDGE-adpcm-songs-10]
    {
        // 20-byte header with cbSize 0 and junk where wSamplesPerBlock would sit: must derive, not read it.
        byte[] fmt = AdpcmFmt(1, 256, 0, withExtra: false).Concat(new byte[] { 0, 0, 0x99, 0x01 }).ToArray();
        int derived = (256 - 7) * 2 + 2;
        Assert.Equal(2L * derived, AdpcmWav.BytesToSamples(fmt, 512));
    }

    [Fact]
    public void BytesToSamples_InvalidHeader_Throws() // [EDGE-adpcm-songs-12]
    {
        Assert.Throws<InvalidDataException>(() => AdpcmWav.BytesToSamples(new byte[10], 100));
        Assert.Throws<InvalidDataException>(() => AdpcmWav.BytesToSamples(AdpcmFmt(1, 0, 0), 100));
        Assert.Throws<InvalidDataException>(() => AdpcmWav.BytesToSamples(AdpcmFmt(0, 256, 500), 100));
    }
}
