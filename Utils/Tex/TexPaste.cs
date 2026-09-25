using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>A window on a texture, in mip-0 texels, top-left origin.</summary>
    internal readonly record struct TexRect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;

        public bool FitsInside(int width, int height) =>
            X >= 0 && Y >= 0 && Width > 0 && Height > 0 && Right <= width && Bottom <= height;

        public override string ToString() => $"{Width}x{Height} at {X},{Y}";
    }

    /// <summary>
    /// Reading and replacing one rectangle of a BC3 texture, mip chain included.
    ///
    /// The file that comes back is the file that went in with some blocks swapped: same length, same
    /// header, same bytes everywhere the rectangle does not reach. That is what makes replacing the
    /// laptop screen safe on a 4096-wide atlas whose other three-quarters is the DJ deck — none of
    /// that art is decoded, recompressed or even read.
    /// </summary>
    internal static class TexPaste
    {
        /// <summary>Bytes in the 4x4 BGRA tile the codecs trade in. The same for every format here.</summary>
        private const int TileBytes = 4 * 4 * 4;

        /// <summary>The rectangle as it looks now, at full resolution.</summary>
        public static BgraImage? ReadRect(byte[] file, TexRect rect, out string error)
        {
            var header = TexHeader.TryRead(file, out error);
            if (header == null) return null;

            var codec = BlockCodec.For(header.Format, out error);
            if (codec == null) return null;

            if (!rect.FitsInside(header.Width, header.Height))
            {
                error = $"The panel {rect} does not fit a {header.Width}x{header.Height} texture.";
                return null;
            }
            if (!MipFits(header, file, 0, codec.BlockBytes, out error)) return null;

            var result = new BgraImage(rect.Width, rect.Height);
            Span<byte> tile = stackalloc byte[TileBytes];

            int blocksAcross = header.BlocksAcross(0);
            int offset = header.MipOffsets[0];

            for (int by = rect.Y / 4; by <= (rect.Bottom - 1) / 4; by++)
            {
                for (int bx = rect.X / 4; bx <= (rect.Right - 1) / 4; bx++)
                {
                    int blockOffset = offset + (by * blocksAcross + bx) * codec.BlockBytes;
                    codec.DecodeBlock(file.AsSpan(blockOffset, codec.BlockBytes), tile);

                    for (int ty = 0; ty < 4; ty++)
                    {
                        int y = by * 4 + ty;
                        if (y < rect.Y || y >= rect.Bottom) continue;

                        for (int tx = 0; tx < 4; tx++)
                        {
                            int x = bx * 4 + tx;
                            if (x < rect.X || x >= rect.Right) continue;

                            int destination = ((y - rect.Y) * rect.Width + (x - rect.X)) * 4;
                            tile.Slice((ty * 4 + tx) * 4, 4).CopyTo(result.Pixels.AsSpan(destination, 4));
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// What the rectangle would look like with this content on it, without building a new file.
        ///
        /// For previews. <see cref="PasteRect"/> clones the whole texture and walks the mip chain,
        /// which for the 22 MB laptop atlas is a large-object allocation every time the user nudges
        /// the fit control — on a 32-bit build that churn is worth avoiding. This encodes and decodes
        /// only the blocks the rectangle covers at mip 0, which is the level a preview shows anyway,
        /// and allocates nothing bigger than the rectangle.
        /// </summary>
        public static BgraImage? RoundTripRect(byte[] file, TexRect rect, BgraImage content,
            out string error)
        {
            var header = TexHeader.TryRead(file, out error);
            if (header == null) return null;

            var codec = BlockCodec.For(header.Format, out error);
            if (codec == null) return null;

            if (!rect.FitsInside(header.Width, header.Height))
            {
                error = $"The panel {rect} does not fit a {header.Width}x{header.Height} texture.";
                return null;
            }
            if (content.Width != rect.Width || content.Height != rect.Height)
            {
                error = $"The picture is {content.Width}x{content.Height} but the panel is {rect}.";
                return null;
            }
            if (!MipFits(header, file, 0, codec.BlockBytes, out error)) return null;

            var result = new BgraImage(rect.Width, rect.Height);
            Span<byte> tile = stackalloc byte[TileBytes];
            Span<byte> block = stackalloc byte[codec.BlockBytes];

            int blocksAcross = header.BlocksAcross(0);
            int offset = header.MipOffsets[0];

            for (int by = rect.Y / 4; by <= (rect.Bottom - 1) / 4; by++)
            {
                for (int bx = rect.X / 4; bx <= (rect.Right - 1) / 4; bx++)
                {
                    int blockOffset = offset + (by * blocksAcross + bx) * codec.BlockBytes;
                    codec.DecodeBlock(file.AsSpan(blockOffset, codec.BlockBytes), tile);

                    Composite(tile, content, rect.X, rect.Y, rect.Width, rect.Height, bx, by);

                    // Through the codec and back, so the preview carries the compression the game
                    // will see rather than the picture as chosen.
                    codec.EncodeBlock(tile, block);
                    codec.DecodeBlock(block, tile);

                    for (int ty = 0; ty < 4; ty++)
                    {
                        int y = by * 4 + ty;
                        if (y < rect.Y || y >= rect.Bottom) continue;

                        for (int tx = 0; tx < 4; tx++)
                        {
                            int x = bx * 4 + tx;
                            if (x < rect.X || x >= rect.Right) continue;

                            int destination = ((y - rect.Y) * rect.Width + (x - rect.X)) * 4;
                            tile.Slice((ty * 4 + tx) * 4, 4).CopyTo(result.Pixels.AsSpan(destination, 4));
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Overwrites a tile's colour channels from the content, keeping its alpha.
        ///
        /// Shared by the preview and the real paste, which have to agree texel for texel or the
        /// preview stops being a preview. The window is passed in rather than taken from a rect
        /// because the paste works in mip-level coordinates, where both the origin and the content
        /// have been halved.
        /// </summary>
        private static void Composite(Span<byte> tile, BgraImage content,
            int originX, int originY, int width, int height, int bx, int by)
        {
            for (int ty = 0; ty < 4; ty++)
            {
                int y = by * 4 + ty;
                if (y < originY || y >= originY + height) continue;

                for (int tx = 0; tx < 4; tx++)
                {
                    int x = bx * 4 + tx;
                    if (x < originX || x >= originX + width) continue;

                    int source = ((y - originY) * width + (x - originX)) * 4;
                    int destination = (ty * 4 + tx) * 4;
                    tile[destination] = content.Pixels[source];
                    tile[destination + 1] = content.Pixels[source + 1];
                    tile[destination + 2] = content.Pixels[source + 2];
                }
            }
        }

        /// <summary>
        /// Replaces the rectangle with <paramref name="content"/>, which must already be the
        /// rectangle's size and orientation, and rebuilds that part of every mip level.
        ///
        /// Only the colour channels are replaced; each texel keeps the alpha it had. These are VFX
        /// textures, and their alpha is a mask the effect shapes itself with rather than a part of
        /// the picture — a user pasting a PNG with a transparent background would otherwise punch a
        /// hole through the table instead of putting a picture on it.
        /// </summary>
        public static byte[]? PasteRect(byte[] file, TexRect rect, BgraImage content, out string error)
        {
            var header = TexHeader.TryRead(file, out error);
            if (header == null) return null;

            var codec = BlockCodec.For(header.Format, out error);
            if (codec == null) return null;

            if (!rect.FitsInside(header.Width, header.Height))
            {
                error = $"The panel {rect} does not fit a {header.Width}x{header.Height} texture.";
                return null;
            }
            if (content.Width != rect.Width || content.Height != rect.Height)
            {
                error = $"The picture is {content.Width}x{content.Height} but the panel is {rect}.";
                return null;
            }

            var result = (byte[])file.Clone();
            Span<byte> tile = stackalloc byte[TileBytes];

            for (int level = 0; level < header.MipCount; level++)
            {
                if (!MipFits(header, file, level, codec.BlockBytes, out error)) return null;

                // Each level halves the rectangle along with the texture. Flooring the origin and the
                // size keeps the shrinking rectangle inside the one above it, so a coarse mip never
                // bleeds the new picture over a texel the full-resolution paste left alone.
                int x = rect.X >> level, y = rect.Y >> level;
                int width = Math.Max(1, rect.Width >> level), height = Math.Max(1, rect.Height >> level);

                int mipWidth = header.MipWidth(level), mipHeight = header.MipHeight(level);
                if (x >= mipWidth || y >= mipHeight) continue;
                width = Math.Min(width, mipWidth - x);
                height = Math.Min(height, mipHeight - y);

                var scaled = ImageOps.Resize(content, width, height);

                int blocksAcross = header.BlocksAcross(level);
                int offset = header.MipOffsets[level];

                for (int by = y / 4; by <= (y + height - 1) / 4; by++)
                {
                    for (int bx = x / 4; bx <= (x + width - 1) / 4; bx++)
                    {
                        int blockOffset = offset + (by * blocksAcross + bx) * codec.BlockBytes;
                        codec.DecodeBlock(result.AsSpan(blockOffset, codec.BlockBytes), tile);

                        Composite(tile, scaled, x, y, width, height, bx, by);

                        codec.EncodeBlock(tile, result.AsSpan(blockOffset, codec.BlockBytes));
                    }
                }
            }

            error = string.Empty;
            return result;
        }

        private static bool MipFits(TexHeader header, byte[] file, int level, int blockBytes,
            out string error)
        {
            long end = (long)header.MipOffsets[level] + header.MipByteLength(level, blockBytes);
            if (end <= file.Length)
            {
                error = string.Empty;
                return true;
            }

            error = $"The texture is truncated: mip level {level} runs past the end of the file.";
            return false;
        }
    }
}
