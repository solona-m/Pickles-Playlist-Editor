using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>
    /// BC7 blocks: sixteen bytes covering a 4x4 tile, in one of eight quite different layouts.
    ///
    /// Reading handles all eight modes, because a texture someone else packed uses whichever its
    /// encoder chose. Writing emits mode 6 only: one subset, full RGBA endpoints and four-bit
    /// indices, which is the finest index precision BC7 offers and suits the photographs and album
    /// art this feature pastes. Blocks are independently moded, so a mode 6 block sits perfectly
    /// happily among blocks of other modes — the ones this never touches keep whatever they were.
    /// </summary>
    internal static class Bc7
    {
        public const int BlockBytes = 16;

        /// <summary>Bytes in the 4x4 BGRA tile the encode and decode sides trade in.</summary>
        public const int TileBytes = 4 * 4 * 4;

        private readonly record struct ModeInfo(
            int Subsets, int PartitionBits, int RotationBits, int IndexSelectionBits,
            int ColourBits, int AlphaBits, int EndpointPBits, int SharedPBits,
            int IndexBits, int Index2Bits);

        private static readonly ModeInfo[] Modes =
        {
            new(3, 4, 0, 0, 4, 0, 1, 0, 3, 0),
            new(2, 6, 0, 0, 6, 0, 0, 1, 3, 0),
            new(3, 6, 0, 0, 5, 0, 0, 0, 2, 0),
            new(2, 6, 0, 0, 7, 0, 1, 0, 2, 0),
            new(1, 0, 2, 1, 5, 6, 0, 0, 2, 3),
            new(1, 0, 2, 0, 7, 8, 0, 0, 2, 2),
            new(1, 0, 0, 0, 7, 7, 1, 0, 4, 0),
            new(2, 6, 0, 0, 5, 5, 1, 0, 2, 0),
        };

        // ---- decode ----------------------------------------------------------------------------

        /// <summary>Expands one block into a 4x4 BGRA tile.</summary>
        public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> tile)
        {
            int mode = ModeOf(block);
            if (mode < 0)
            {
                // No mode bit set at all is a reserved encoding, and hardware returns transparent
                // black for it rather than refusing the draw.
                tile.Clear();
                return;
            }

            var info = Modes[mode];
            int at = mode + 1;

            int rotation = ReadBits(block, ref at, info.RotationBits);
            int indexSelection = ReadBits(block, ref at, info.IndexSelectionBits);
            int partition = ReadBits(block, ref at, info.PartitionBits);

            int endpointCount = info.Subsets * 2;
            Span<int> red = stackalloc int[6];
            Span<int> green = stackalloc int[6];
            Span<int> blue = stackalloc int[6];
            Span<int> alpha = stackalloc int[6];

            for (int e = 0; e < endpointCount; e++) red[e] = ReadBits(block, ref at, info.ColourBits);
            for (int e = 0; e < endpointCount; e++) green[e] = ReadBits(block, ref at, info.ColourBits);
            for (int e = 0; e < endpointCount; e++) blue[e] = ReadBits(block, ref at, info.ColourBits);
            if (info.AlphaBits > 0)
                for (int e = 0; e < endpointCount; e++) alpha[e] = ReadBits(block, ref at, info.AlphaBits);

            Span<int> parity = stackalloc int[6];
            int extra = 0;
            if (info.EndpointPBits > 0)
            {
                extra = 1;
                for (int e = 0; e < endpointCount; e++) parity[e] = ReadBits(block, ref at, 1);
            }
            else if (info.SharedPBits > 0)
            {
                extra = 1;
                for (int s = 0; s < info.Subsets; s++)
                {
                    int shared = ReadBits(block, ref at, 1);
                    parity[s * 2] = parity[s * 2 + 1] = shared;
                }
            }

            for (int e = 0; e < endpointCount; e++)
            {
                red[e] = Expand(red[e], info.ColourBits, extra, parity[e]);
                green[e] = Expand(green[e], info.ColourBits, extra, parity[e]);
                blue[e] = Expand(blue[e], info.ColourBits, extra, parity[e]);
                alpha[e] = info.AlphaBits > 0 ? Expand(alpha[e], info.AlphaBits, extra, parity[e]) : 255;
            }

            int anchorOne = info.Subsets == 2 ? Bc7Tables.Anchor2[partition]
                : info.Subsets == 3 ? Bc7Tables.Anchor3First[partition] : -1;
            int anchorTwo = info.Subsets == 3 ? Bc7Tables.Anchor3Second[partition] : -1;

            Span<int> indices = stackalloc int[16];
            Span<int> indices2 = stackalloc int[16];

            for (int i = 0; i < 16; i++)
            {
                bool anchor = i == 0 || i == anchorOne || i == anchorTwo;
                indices[i] = ReadBits(block, ref at, anchor ? info.IndexBits - 1 : info.IndexBits);
            }
            if (info.Index2Bits > 0)
            {
                for (int i = 0; i < 16; i++)
                {
                    bool anchor = i == 0 || i == anchorOne || i == anchorTwo;
                    indices2[i] = ReadBits(block, ref at, anchor ? info.Index2Bits - 1 : info.Index2Bits);
                }
            }

            var colourWeights = Bc7Tables.Weights[info.IndexBits];
            var alphaWeights = info.Index2Bits > 0 ? Bc7Tables.Weights[info.Index2Bits] : colourWeights;

            for (int i = 0; i < 16; i++)
            {
                int subset = info.Subsets == 1 ? 0
                    : info.Subsets == 2 ? Bc7Tables.Partition2[partition, i]
                    : Bc7Tables.Partition3[partition, i];

                int colourWeight, alphaWeight;
                if (info.Index2Bits == 0)
                {
                    colourWeight = alphaWeight = colourWeights[indices[i]];
                }
                else if (indexSelection == 0)
                {
                    colourWeight = colourWeights[indices[i]];
                    alphaWeight = alphaWeights[indices2[i]];
                }
                else
                {
                    // The selection bit swaps which array drives colour and which drives alpha, so
                    // the weight table has to follow the array rather than the channel.
                    colourWeight = alphaWeights[indices2[i]];
                    alphaWeight = colourWeights[indices[i]];
                }

                int e0 = subset * 2, e1 = e0 + 1;
                int r = Interpolate(red[e0], red[e1], colourWeight);
                int g = Interpolate(green[e0], green[e1], colourWeight);
                int b = Interpolate(blue[e0], blue[e1], colourWeight);
                int a = Interpolate(alpha[e0], alpha[e1], alphaWeight);

                switch (rotation)
                {
                    case 1: (a, r) = (r, a); break;
                    case 2: (a, g) = (g, a); break;
                    case 3: (a, b) = (b, a); break;
                }

                tile[i * 4] = (byte)b;
                tile[i * 4 + 1] = (byte)g;
                tile[i * 4 + 2] = (byte)r;
                tile[i * 4 + 3] = (byte)a;
            }
        }

        private static int ModeOf(ReadOnlySpan<byte> block)
        {
            for (int bit = 0; bit < 8; bit++)
                if ((block[0] & (1 << bit)) != 0) return bit;
            return -1;
        }

        private static int ReadBits(ReadOnlySpan<byte> block, ref int at, int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++, at++)
                value |= ((block[at >> 3] >> (at & 7)) & 1) << i;
            return value;
        }

        /// <summary>Widens a stored endpoint to eight bits, replicating its own high bits.</summary>
        private static int Expand(int value, int bits, int extraBits, int parity)
        {
            int total = bits + extraBits;
            if (extraBits > 0) value = (value << 1) | parity;
            if (total >= 8) return value & 0xFF;

            // Replication, not a shift: the low bits have to come from the value's own high bits or
            // white stops being white. No mode gets below five bits here, so the right shift is safe.
            return (value << (8 - total)) | (value >> Math.Max(0, 2 * total - 8));
        }

        private static int Interpolate(int a, int b, int weight) =>
            ((64 - weight) * a + weight * b + 32) >> 6;

        // ---- encode ----------------------------------------------------------------------------

        /// <summary>Compresses a 4x4 BGRA tile into one mode 6 block.</summary>
        public static void EncodeBlock(ReadOnlySpan<byte> tile, Span<byte> block)
        {
            Span<int> channels = stackalloc int[64];
            bool flat = true;
            for (int i = 0; i < 16; i++)
            {
                channels[i * 4] = tile[i * 4 + 2];      // red
                channels[i * 4 + 1] = tile[i * 4 + 1];  // green
                channels[i * 4 + 2] = tile[i * 4];      // blue
                channels[i * 4 + 3] = tile[i * 4 + 3];  // alpha
                for (int c = 0; c < 4 && flat; c++)
                    if (channels[i * 4 + c] != channels[c]) flat = false;
            }

            Span<int> best0 = stackalloc int[4];
            Span<int> best1 = stackalloc int[4];
            Span<int> indices = stackalloc int[16];

            if (flat)
            {
                // Both endpoints on the colour, every index at zero. Cheap, and exact whenever the
                // colour survives the shared low bit, which for a flat tile it usually does.
                for (int c = 0; c < 4; c++) best0[c] = best1[c] = channels[c];
                QuantizePair(best0, best1, channels[0] & 1, channels[0] & 1, best0, best1);
                indices.Clear();
                Pack(block, best0, best1, channels[0] & 1, channels[0] & 1, indices);
                return;
            }

            PrincipalAxisEndpoints(channels, out var startLow, out var startHigh);

            long bestError = long.MaxValue;
            int bestParity0 = 0, bestParity1 = 0;

            // Mode 6 endpoints are seven bits plus a parity bit shared by all four channels, so the
            // reachable eight-bit values depend on that bit. Four combinations is few enough to try
            // all of them rather than guess, and guessing was measurably worse on dark content.
            Span<int> low = stackalloc int[4];
            Span<int> high = stackalloc int[4];
            Span<int> candidateIndices = stackalloc int[16];

            for (int parity0 = 0; parity0 < 2; parity0++)
            {
                for (int parity1 = 0; parity1 < 2; parity1++)
                {
                    startLow.CopyTo(low);
                    startHigh.CopyTo(high);
                    QuantizePair(low, high, parity0, parity1, low, high);

                    for (int round = 0; round < 2; round++)
                    {
                        AssignIndices(channels, low, high, candidateIndices);
                        if (!SolveEndpoints(channels, candidateIndices, low, high)) break;
                        QuantizePair(low, high, parity0, parity1, low, high);
                    }

                    long error = AssignIndices(channels, low, high, candidateIndices);
                    if (error >= bestError) continue;

                    bestError = error;
                    bestParity0 = parity0;
                    bestParity1 = parity1;
                    low.CopyTo(best0);
                    high.CopyTo(best1);
                    candidateIndices.CopyTo(indices);
                }
            }

            // The first texel's index is stored a bit short, so its top bit must be zero. Swapping the
            // endpoints and inverting every index says the same thing the other way round.
            if (indices[0] >= 8)
            {
                for (int c = 0; c < 4; c++) (best0[c], best1[c]) = (best1[c], best0[c]);
                (bestParity0, bestParity1) = (bestParity1, bestParity0);
                for (int i = 0; i < 16; i++) indices[i] = 15 - indices[i];
            }

            Pack(block, best0, best1, bestParity0, bestParity1, indices);
        }

        /// <summary>Endpoints at the extremes of the tile's dominant direction through RGBA.</summary>
        private static void PrincipalAxisEndpoints(ReadOnlySpan<int> channels,
            out int[] low, out int[] high)
        {
            Span<double> mean = stackalloc double[4];
            for (int i = 0; i < 16; i++)
                for (int c = 0; c < 4; c++) mean[c] += channels[i * 4 + c];
            for (int c = 0; c < 4; c++) mean[c] /= 16;

            Span<double> covariance = stackalloc double[16];
            Span<double> deviation = stackalloc double[4];
            for (int i = 0; i < 16; i++)
            {
                for (int c = 0; c < 4; c++) deviation[c] = channels[i * 4 + c] - mean[c];
                for (int a = 0; a < 4; a++)
                    for (int b = 0; b < 4; b++) covariance[a * 4 + b] += deviation[a] * deviation[b];
            }

            // Seeded from the widest row, which cannot be orthogonal to the axis it is looking for.
            int widest = 0;
            for (int c = 1; c < 4; c++)
                if (covariance[c * 4 + c] > covariance[widest * 4 + widest]) widest = c;

            Span<double> axis = stackalloc double[4];
            for (int c = 0; c < 4; c++) axis[c] = covariance[widest * 4 + c];

            Span<double> next = stackalloc double[4];
            for (int iteration = 0; iteration < 6; iteration++)
            {
                next.Clear();
                for (int a = 0; a < 4; a++)
                    for (int b = 0; b < 4; b++) next[a] += axis[b] * covariance[a * 4 + b];

                double magnitude = 0;
                for (int c = 0; c < 4; c++) magnitude = Math.Max(magnitude, Math.Abs(next[c]));
                if (magnitude < 1e-9) break;
                for (int c = 0; c < 4; c++) axis[c] = next[c] / magnitude;
            }

            double smallest = double.MaxValue, largest = double.MinValue;
            int lowIndex = 0, highIndex = 0;
            for (int i = 0; i < 16; i++)
            {
                double projection = 0;
                for (int c = 0; c < 4; c++) projection += channels[i * 4 + c] * axis[c];
                if (projection < smallest) { smallest = projection; lowIndex = i; }
                if (projection > largest) { largest = projection; highIndex = i; }
            }

            low = new int[4];
            high = new int[4];
            for (int c = 0; c < 4; c++)
            {
                low[c] = channels[lowIndex * 4 + c];
                high[c] = channels[highIndex * 4 + c];
            }
        }

        /// <summary>Rounds both endpoints to the eight-bit values their parity bit can reach.</summary>
        private static void QuantizePair(ReadOnlySpan<int> low, ReadOnlySpan<int> high,
            int parity0, int parity1, Span<int> outLow, Span<int> outHigh)
        {
            for (int c = 0; c < 4; c++)
            {
                outLow[c] = (Math.Clamp((low[c] - parity0 + 1) >> 1, 0, 127) << 1) | parity0;
                outHigh[c] = (Math.Clamp((high[c] - parity1 + 1) >> 1, 0, 127) << 1) | parity1;
            }
        }

        private static long AssignIndices(ReadOnlySpan<int> channels, ReadOnlySpan<int> low,
            ReadOnlySpan<int> high, Span<int> indices)
        {
            var weights = Bc7Tables.Weights[4];
            Span<int> palette = stackalloc int[16 * 4];
            for (int k = 0; k < 16; k++)
                for (int c = 0; c < 4; c++)
                    palette[k * 4 + c] = Interpolate(low[c], high[c], weights[k]);

            long total = 0;
            for (int i = 0; i < 16; i++)
            {
                int best = 0;
                long bestError = long.MaxValue;
                for (int k = 0; k < 16; k++)
                {
                    long error = 0;
                    for (int c = 0; c < 4; c++)
                    {
                        long difference = channels[i * 4 + c] - palette[k * 4 + c];
                        error += difference * difference;
                    }
                    if (error >= bestError) continue;
                    bestError = error;
                    best = k;
                }
                indices[i] = best;
                total += bestError;
            }
            return total;
        }

        /// <summary>Least-squares endpoints for the index assignment just made.</summary>
        private static bool SolveEndpoints(ReadOnlySpan<int> channels, ReadOnlySpan<int> indices,
            Span<int> low, Span<int> high)
        {
            var weights = Bc7Tables.Weights[4];
            double aa = 0, ab = 0, bb = 0;
            Span<double> ax = stackalloc double[4];
            Span<double> bx = stackalloc double[4];

            for (int i = 0; i < 16; i++)
            {
                double beta = weights[indices[i]] / 64.0;
                double alpha = 1.0 - beta;

                aa += alpha * alpha;
                bb += beta * beta;
                ab += alpha * beta;
                for (int c = 0; c < 4; c++)
                {
                    ax[c] += alpha * channels[i * 4 + c];
                    bx[c] += beta * channels[i * 4 + c];
                }
            }

            double determinant = aa * bb - ab * ab;
            if (Math.Abs(determinant) < 1e-9) return false;

            for (int c = 0; c < 4; c++)
            {
                low[c] = (int)Math.Round((ax[c] * bb - bx[c] * ab) / determinant);
                high[c] = (int)Math.Round((bx[c] * aa - ax[c] * ab) / determinant);
            }
            return true;
        }

        private static void Pack(Span<byte> block, ReadOnlySpan<int> low, ReadOnlySpan<int> high,
            int parity0, int parity1, ReadOnlySpan<int> indices)
        {
            block.Clear();
            int at = 0;

            WriteBits(block, ref at, 1 << 6, 7);        // mode 6: six zeroes then a one
            for (int c = 0; c < 4; c++)
            {
                WriteBits(block, ref at, low[c] >> 1, 7);
                WriteBits(block, ref at, high[c] >> 1, 7);
            }
            WriteBits(block, ref at, parity0, 1);
            WriteBits(block, ref at, parity1, 1);

            WriteBits(block, ref at, indices[0], 3);    // the anchor, one bit short
            for (int i = 1; i < 16; i++) WriteBits(block, ref at, indices[i], 4);
        }

        private static void WriteBits(Span<byte> block, ref int at, int value, int count)
        {
            for (int i = 0; i < count; i++, at++)
                if (((value >> i) & 1) != 0) block[at >> 3] |= (byte)(1 << (at & 7));
        }
    }
}
