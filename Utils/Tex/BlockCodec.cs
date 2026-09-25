using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>
    /// One block-compressed pixel format, as the paste needs to see it: decode a block to a 4x4
    /// BGRA tile, and put one back.
    ///
    /// The point of the interface is that <see cref="TexPaste"/> never branches on format. Both
    /// formats this supports pack a 4x4 tile into sixteen bytes and can be read and written one
    /// block at a time, which is the only property the paste actually relies on.
    /// </summary>
    internal interface IBlockCodec
    {
        int BlockBytes { get; }
        void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> tile);
        void EncodeBlock(ReadOnlySpan<byte> tile, Span<byte> block);
    }

    internal sealed class Bc3Codec : IBlockCodec
    {
        public static readonly Bc3Codec Instance = new();

        public int BlockBytes => Bc3.BlockBytes;
        public void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> tile) => Bc3.DecodeBlock(block, tile);
        public void EncodeBlock(ReadOnlySpan<byte> tile, Span<byte> block) => Bc3.EncodeBlock(tile, block);
    }

    internal sealed class Bc7Codec : IBlockCodec
    {
        public static readonly Bc7Codec Instance = new();

        public int BlockBytes => Bc7.BlockBytes;
        public void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> tile) => Bc7.DecodeBlock(block, tile);
        public void EncodeBlock(ReadOnlySpan<byte> tile, Span<byte> block) => Bc7.EncodeBlock(tile, block);
    }

    internal static class BlockCodec
    {
        /// <summary>The codec for this format, or null with a reason when there is none.</summary>
        public static IBlockCodec? For(TexFormat format, out string error)
        {
            error = string.Empty;
            switch (format)
            {
                case TexFormat.Bc3: return Bc3Codec.Instance;
                case TexFormat.Bc7: return Bc7Codec.Instance;
                default:
                    error = $"This texture is stored as {format}, which this cannot read.";
                    return null;
            }
        }
    }
}
