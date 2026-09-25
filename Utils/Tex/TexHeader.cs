using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>The pixel encodings a .tex/.atex surface can be in. Only BC3 is written here.</summary>
    internal enum TexFormat : uint
    {
        Unknown = 0,
        Bgra8 = 0x1450,
        Bc1 = 0x3420,
        Bc2 = 0x3430,
        Bc3 = 0x3431,
        Bc5 = 0x6230,
        Bc7 = 0x6432,
    }

    /// <summary>
    /// The 80-byte header every FFXIV .tex and .atex file starts with.
    ///
    /// Read-only on purpose. Every write this feature makes keeps the texture at its existing size,
    /// format and mip count, so the header never changes and the new file is the old one with some
    /// blocks swapped. That is not laziness — it removes the entire class of "the file loads but the
    /// game renders garbage" bugs that a hand-built header invites, and it means a texture whose
    /// layout this code does not understand is simply refused rather than rewritten wrongly.
    /// </summary>
    internal sealed class TexHeader
    {
        public const int Size = 80;

        /// <summary>Where the per-mip surface offsets start; thirteen slots, unused ones zero.</summary>
        private const int SurfaceTableOffset = 0x1C;

        private const int MaxMips = 13;

        public uint Attribute { get; private init; }
        public TexFormat Format { get; private init; }
        public int Width { get; private init; }
        public int Height { get; private init; }
        public int Depth { get; private init; }

        /// <summary>Absolute file offset of each mip level, largest first.</summary>
        public int[] MipOffsets { get; private init; } = Array.Empty<int>();

        public int MipCount => MipOffsets.Length;

        public int MipWidth(int level) => Math.Max(1, Width >> level);
        public int MipHeight(int level) => Math.Max(1, Height >> level);

        /// <summary>
        /// Parses the header, or returns null with a reason when the file is not one this can edit.
        ///
        /// Never throws. The caller is holding a file that some stranger packed into a mod, and the
        /// honest answer for anything unexpected is to leave it alone and say why.
        /// </summary>
        public static TexHeader? TryRead(byte[] file, out string error) =>
            TryRead(file, file.LongLength, out error);

        /// <summary>
        /// The same, for a caller holding only the first <see cref="Size"/> bytes.
        ///
        /// The total length has to come in separately or the mip offset check reads as a failure for
        /// every texture there is: the first mip starts at byte 80, which is exactly where an
        /// 80-byte header buffer ends. That mistake cost this feature an afternoon — the dialog
        /// reported no mod in the entire Penumbra folder had a DJ table, while the offline harness,
        /// which reads whole files, passed every check.
        /// </summary>
        public static TexHeader? TryRead(byte[] file, long fileLength, out string error)
        {
            error = string.Empty;

            if (file.Length < Size)
            {
                error = "The texture file is too short to have a header.";
                return null;
            }

            int width = BitConverter.ToUInt16(file, 8);
            int height = BitConverter.ToUInt16(file, 10);
            int depth = BitConverter.ToUInt16(file, 12);

            // A byte, not the ushort that sits here: the high half is ArraySize, and reading both as
            // one number turns an ordinary texture array into a mip count of several hundred.
            int mipCount = file[14];

            if (width <= 0 || height <= 0)
            {
                error = "The texture reports no size.";
                return null;
            }
            if (mipCount is < 1 or > MaxMips)
            {
                error = $"The texture reports {mipCount} mip levels, which is not a number this can read.";
                return null;
            }

            var offsets = new int[mipCount];
            for (int level = 0; level < mipCount; level++)
            {
                long offset = BitConverter.ToUInt32(file, SurfaceTableOffset + level * 4);
                if (offset < Size || offset >= fileLength)
                {
                    error = $"Mip level {level} points outside the file.";
                    return null;
                }
                if (level > 0 && offset <= offsets[level - 1])
                {
                    error = "The mip levels are not stored in order.";
                    return null;
                }
                offsets[level] = (int)offset;
            }

            return new TexHeader
            {
                Attribute = BitConverter.ToUInt32(file, 0),
                Format = (TexFormat)BitConverter.ToUInt32(file, 4),
                Width = width,
                Height = height,
                Depth = depth == 0 ? 1 : depth,
                MipOffsets = offsets,
            };
        }

        /// <summary>Bytes one mip level occupies, for a block-compressed format.</summary>
        public int MipByteLength(int level, int bytesPerBlock) =>
            BlocksAcross(level) * BlocksDown(level) * bytesPerBlock;

        public int BlocksAcross(int level) => Math.Max(1, (MipWidth(level) + 3) / 4);
        public int BlocksDown(int level) => Math.Max(1, (MipHeight(level) + 3) / 4);

        public override string ToString() => $"{Width}x{Height} {Format} x{MipCount}";
    }
}
