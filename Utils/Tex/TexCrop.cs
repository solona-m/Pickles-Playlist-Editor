using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>
    /// A small power-of-two window cut out of a block-compressed atlas, mip chain included, as a
    /// texture of its own. Levels whose window origin is block-aligned are copied block for block;
    /// the rest are decoded from the atlas's own mip and re-encoded.
    /// </summary>
    internal static class TexCrop
    {
        private const int TileBytes = 4 * 4 * 4;
        private const int SurfaceTableOffset = 0x1C;
        private const int MaxMips = 13;

        /// <summary>
        /// A power-of-two window holding the rect with 4 px to spare on every side, placed on the
        /// 64 px grid when it can be, or null when none fits inside the atlas.
        /// </summary>
        public static TexRect? Window(int atlasWidth, int atlasHeight, TexRect rect, int maxSize = 512)
        {
            int w = Pow2AtLeast(rect.Width + 8), h = Pow2AtLeast(rect.Height + 8);
            if (w > maxSize || h > maxSize) return null;
            int? x = Place(rect.X, rect.Width, w, atlasWidth), y = Place(rect.Y, rect.Height, h, atlasHeight);
            return x == null || y == null ? null : new TexRect(x.Value, y.Value, w, h);
        }

        private static int? Place(int start, int length, int size, int limit)
        {
            int lo = Math.Max(0, start + length + 4 - size), hi = Math.Min(start - 4, limit - size);
            if (lo > hi) return null;
            int centred = (int)Math.Round((start + length / 2.0 - size / 2.0) / 64.0) * 64;
            return Math.Clamp(centred, lo, hi);
        }

        private static int Pow2AtLeast(int value)
        {
            int p = 4;
            while (p < value) p <<= 1;
            return p;
        }

        /// <summary>The window as a texture: same format, a fresh header, mips down to 4 px.</summary>
        public static byte[]? Crop(byte[] atlas, TexRect window, out string error)
        {
            var header = TexHeader.TryRead(atlas, out error);
            if (header == null) return null;
            var codec = BlockCodec.For(header.Format, out error);
            if (codec == null) return null;
            if (header.Depth > 1 || atlas[15] > 1) { error = "Volume and array textures cannot be cropped."; return null; }
            if (!window.FitsInside(header.Width, header.Height) || window.X % 4 != 0 || window.Y % 4 != 0
                || window.Width % 4 != 0 || window.Height % 4 != 0)
            {
                error = $"The window {window} does not fit a {header.Width}x{header.Height} texture on whole blocks.";
                return null;
            }

            int levels = 1;
            while (levels < header.MipCount && Math.Min(window.Width, window.Height) >> levels >= 4) levels++;

            int bytes = codec.BlockBytes;
            var offsets = new int[levels];
            int total = TexHeader.Size;
            for (int l = 0; l < levels; l++)
            {
                offsets[l] = total;
                total += (window.Width >> l) / 4 * ((window.Height >> l) / 4) * bytes;
            }
            for (int l = 0; l < levels; l++)
                if ((long)header.MipOffsets[l] + header.MipByteLength(l, bytes) > atlas.Length)
                {
                    error = $"The texture is truncated: mip level {l} runs past the end of the file.";
                    return null;
                }

            var result = new byte[total];
            Array.Copy(atlas, result, TexHeader.Size);
            BitConverter.GetBytes((ushort)window.Width).CopyTo(result, 8);
            BitConverter.GetBytes((ushort)window.Height).CopyTo(result, 10);
            result[14] = (byte)levels;
            for (int i = 0; i < 3; i++)
            {
                uint lod = BitConverter.ToUInt32(atlas, 0x10 + i * 4);
                BitConverter.GetBytes(Math.Min(lod, (uint)(levels - 1))).CopyTo(result, 0x10 + i * 4);
            }
            for (int l = 0; l < MaxMips; l++)
                BitConverter.GetBytes(l < levels ? offsets[l] : 0).CopyTo(result, SurfaceTableOffset + l * 4);

            Span<byte> tile = stackalloc byte[TileBytes];
            for (int l = 0; l < levels; l++)
            {
                int across = (window.Width >> l) / 4, down = (window.Height >> l) / 4;
                int atlasAcross = header.BlocksAcross(l);
                int x = window.X >> l, y = window.Y >> l;
                bool aligned = window.X % (4 << l) == 0 && window.Y % (4 << l) == 0;

                for (int by = 0; by < down; by++)
                {
                    int dest = offsets[l] + by * across * bytes;
                    if (aligned)
                    {
                        int src = header.MipOffsets[l] + ((y / 4 + by) * atlasAcross + x / 4) * bytes;
                        Array.Copy(atlas, src, result, dest, across * bytes);
                        continue;
                    }
                    for (int bx = 0; bx < across; bx++)
                    {
                        for (int ty = 0; ty < 4; ty++)
                            for (int tx = 0; tx < 4; tx++)
                                Texel(atlas, header, codec, l, x + bx * 4 + tx, y + by * 4 + ty)
                                    .CopyTo(tile.Slice((ty * 4 + tx) * 4, 4));
                        codec.EncodeBlock(tile, result.AsSpan(dest + bx * bytes, bytes));
                    }
                }
            }

            var check = TexHeader.TryRead(result, out error);
            if (check == null || check.MipCount != levels) { error = "The cropped header does not read back: " + error; return null; }
            error = string.Empty;
            return result;
        }

        private static byte[] Texel(byte[] file, TexHeader header, IBlockCodec codec, int level, int x, int y)
        {
            Span<byte> tile = stackalloc byte[TileBytes];
            int offset = header.MipOffsets[level] + ((y / 4) * header.BlocksAcross(level) + x / 4) * codec.BlockBytes;
            codec.DecodeBlock(file.AsSpan(offset, codec.BlockBytes), tile);
            return tile.Slice(((y % 4) * 4 + x % 4) * 4, 4).ToArray();
        }

        /// <summary>
        /// True when every mip-0 texel outside the rect's block-aligned hull has no colour above the
        /// threshold, so hiding everything but the rect loses nothing visible.
        /// </summary>
        public static bool BlackOutside(byte[] atlas, TexRect rect, int threshold = 8)
        {
            var header = TexHeader.TryRead(atlas, out _);
            var codec = header == null ? null : BlockCodec.For(header.Format, out _);
            if (header == null || codec == null
                || (long)header.MipOffsets[0] + header.MipByteLength(0, codec.BlockBytes) > atlas.Length) return false;

            int x0 = rect.X / 4, x1 = (rect.Right + 3) / 4, y0 = rect.Y / 4, y1 = (rect.Bottom + 3) / 4;
            Span<byte> tile = stackalloc byte[TileBytes];
            for (int by = 0; by < header.BlocksDown(0); by++)
                for (int bx = 0; bx < header.BlocksAcross(0); bx++)
                {
                    if (bx >= x0 && bx < x1 && by >= y0 && by < y1) continue;
                    int offset = header.MipOffsets[0] + (by * header.BlocksAcross(0) + bx) * codec.BlockBytes;
                    codec.DecodeBlock(atlas.AsSpan(offset, codec.BlockBytes), tile);
                    for (int i = 0; i < TileBytes; i += 4)
                        if (Math.Max(tile[i], Math.Max(tile[i + 1], tile[i + 2])) > threshold) return false;
                }
            return true;
        }
    }
}
