using System.Text;

namespace Pickles_Playlist_Editor.Tools;

/// <summary>Turns an SCD's MS-ADPCM entry (raw fmt chunk body + block data) back into a RIFF .wav.</summary>
public static class AdpcmWav
{
    public static byte[] Build(byte[] fmtChunk, byte[] data)
    {
        int fmtPad = fmtChunk.Length & 1;
        int dataPad = data.Length & 1;
        int riffSize = 4 + 8 + fmtChunk.Length + fmtPad + 8 + data.Length + dataPad;

        using var ms = new MemoryStream(riffSize + 8);
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(riffSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(fmtChunk.Length);
            bw.Write(fmtChunk);
            if (fmtPad == 1) bw.Write((byte)0);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(data.Length);
            bw.Write(data);
            if (dataPad == 1) bw.Write((byte)0);
        }
        return ms.ToArray();
    }

    // SCD loop points for ADPCM are byte offsets into the data; ogg loop tags are sample frames.
    public static long BytesToSamples(byte[] fmtChunk, int byteOffset)
    {
        if (fmtChunk.Length < 16) throw new InvalidDataException("ADPCM fmt chunk is too short.");
        int channels = BitConverter.ToUInt16(fmtChunk, 2);
        int blockAlign = BitConverter.ToUInt16(fmtChunk, 12);
        if (channels == 0 || blockAlign == 0) throw new InvalidDataException("ADPCM fmt chunk has no channels or block size.");

        int headerBytes = 7 * channels;
        bool hasSamplesPerBlock = fmtChunk.Length >= 20 && BitConverter.ToUInt16(fmtChunk, 16) >= 2;
        long samplesPerBlock = hasSamplesPerBlock
            ? BitConverter.ToUInt16(fmtChunk, 18)
            : (blockAlign - headerBytes) * 2L / channels + 2;

        long blocks = byteOffset / blockAlign;
        int rem = byteOffset % blockAlign;
        long partial = rem >= headerBytes ? (rem - headerBytes) * 2L / channels + 2 : 0;
        return blocks * samplesPerBlock + partial;
    }
}
