using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>One frame of a WebP: its rect on the canvas, timing, flags, and a standalone still file.</summary>
    internal sealed record WebpFrame(int X, int Y, int Width, int Height, int DurationMs, bool Blend,
        bool DisposeToBackground, byte[] Still);

    internal sealed record WebpFile(int CanvasWidth, int CanvasHeight, IReadOnlyList<WebpFrame> Frames);

    /// <summary>
    /// The WebP RIFF container, read the way libwebp's demuxer reads it. Each animation frame is
    /// rewrapped as a standalone still so ffmpeg's still-only decoder can decode it.
    /// </summary>
    internal static class WebpContainer
    {
        public static WebpFile Parse(byte[] file)
        {
            if (file.Length < 12 || Tag(file, 0) != "RIFF" || Tag(file, 8) != "WEBP") throw Bad("not a WebP file");
            long riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
            if (riffEnd > file.Length) throw Bad("the RIFF size runs past the end of the file");

            byte[]? vp8x = null;
            byte[]? image = null;
            byte[]? alph = null;
            var anmf = new List<byte[]>();

            foreach (var (tag, data) in Chunks(file, 12, (int)riffEnd))
            {
                switch (tag)
                {
                    case "VP8X": vp8x = data; break;
                    case "ANMF": anmf.Add(data); break;
                    case "ALPH": alph ??= data; break;
                    case "VP8 ":
                    case "VP8L": image ??= Chunk(tag, data); break;
                }
            }

            if (anmf.Count == 0)
            {
                if (image == null) throw Bad("no image data");
                var (w, h) = vp8x != null ? CanvasOf(vp8x) : BitstreamSize(image);
                if (w <= 0 || h <= 0) throw Bad("zero-sized image");
                return new WebpFile(w, h, new[] { new WebpFrame(0, 0, w, h, 0, false, false, file) });
            }

            if (vp8x == null || vp8x.Length < 10) throw Bad("an animation without VP8X");
            var (cw, ch) = CanvasOf(vp8x);
            var frames = new List<WebpFrame>();
            foreach (var data in anmf)
            {
                if (data.Length < 16) throw Bad("a truncated frame header");
                int x = U24(data, 0) * 2, y = U24(data, 3) * 2;
                int fw = U24(data, 6) + 1, fh = U24(data, 9) + 1;
                int duration = U24(data, 12);
                byte flags = data[15];
                if (x + fw > cw || y + fh > ch) throw Bad("a frame outside the canvas");

                byte[]? frameImage = null, frameAlpha = null;
                foreach (var (tag, sub) in Chunks(data, 16, data.Length))
                {
                    if (tag == "ALPH") frameAlpha ??= sub;
                    else if ((tag == "VP8 " || tag == "VP8L") && frameImage == null) frameImage = Chunk(tag, sub);
                }
                if (frameImage == null) throw Bad("a frame without image data");
                var (bw, bh) = BitstreamSize(frameImage);
                if (bw != fw || bh != fh) throw Bad("a frame whose image size does not match its rect");

                frames.Add(new WebpFrame(x, y, fw, fh, duration, Blend: (flags & 2) == 0,
                    DisposeToBackground: (flags & 1) != 0, Wrap(fw, fh, frameImage, frameAlpha)));
            }
            return new WebpFile(cw, ch, frames);
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static IEnumerable<(string Tag, byte[] Data)> Chunks(byte[] buffer, int start, int end)
        {
            int o = start;
            while (o + 8 <= end)
            {
                string tag = Tag(buffer, o);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(o + 4));
                if (o + 8 + size > end) throw Bad("a chunk runs past its container");
                yield return (tag, buffer.AsSpan(o + 8, (int)size).ToArray());
                o += 8 + (int)size + (int)(size & 1);
            }
        }

        /// <summary>A standalone still: VP8X + ALPH + VP8 when the frame has alpha, else the image chunk alone.</summary>
        private static byte[] Wrap(int width, int height, byte[] imageChunk, byte[]? alpha)
        {
            using var body = new MemoryStream();
            if (alpha != null)
            {
                var x = new byte[10];
                x[0] = 0x10;
                WriteU24(x, 4, width - 1);
                WriteU24(x, 7, height - 1);
                body.Write(Chunk("VP8X", x));
                body.Write(Chunk("ALPH", alpha));
            }
            body.Write(imageChunk);

            using var file = new MemoryStream();
            file.Write(Encoding.ASCII.GetBytes("RIFF"));
            file.Write(BitConverter.GetBytes((uint)(4 + body.Length)));
            file.Write(Encoding.ASCII.GetBytes("WEBP"));
            body.Position = 0;
            body.CopyTo(file);
            return file.ToArray();
        }

        private static byte[] Chunk(string tag, byte[] data)
        {
            var chunk = new byte[8 + data.Length + (data.Length & 1)];
            Encoding.ASCII.GetBytes(tag).CopyTo(chunk, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
            data.CopyTo(chunk, 8);
            return chunk;
        }

        /// <summary>Width and height from a VP8 key-frame header or a VP8L header (chunk bytes, header included).</summary>
        private static (int W, int H) BitstreamSize(byte[] chunk)
        {
            string tag = Tag(chunk, 0);
            var data = chunk.AsSpan(8);
            if (tag == "VP8 ")
            {
                if (data.Length < 10 || data[3] != 0x9d || data[4] != 0x01 || data[5] != 0x2a) throw Bad("a bad VP8 header");
                return (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3fff,
                        BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3fff);
            }
            if (data.Length < 5 || data[0] != 0x2f) throw Bad("a bad VP8L header");
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
            return ((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
        }

        private static (int W, int H) CanvasOf(byte[] vp8x) => (U24(vp8x, 4) + 1, U24(vp8x, 7) + 1);

        private static int U24(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);

        private static void WriteU24(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
        }

        private static string Tag(byte[] b, int o) => Encoding.ASCII.GetString(b, o, 4);

        private static InvalidDataException Bad(string why) => new($"Malformed WebP: {why}.");
    }
}
