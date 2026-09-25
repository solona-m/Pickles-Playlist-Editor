using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>
    /// BC3 (DXT5) blocks: sixteen bytes covering a 4x4 tile, eight for alpha and eight for colour.
    ///
    /// Written out here rather than taken from a compression library for one reason that shapes the
    /// whole feature: because this codec works a block at a time, a paste into part of a texture can
    /// re-encode only the blocks it actually covers and copy every other block through byte for byte.
    /// The DJ deck art around the laptop screen therefore survives an edit completely untouched,
    /// which no whole-image decode-and-recompress can promise.
    /// </summary>
    internal static class Bc3
    {
        public const int BlockBytes = 16;

        /// <summary>Bytes in the 4x4 BGRA tile the encode and decode sides trade in.</summary>
        public const int TileBytes = 4 * 4 * 4;

        // ---- decode ----------------------------------------------------------------------------

        /// <summary>Expands one block into a 4x4 BGRA tile.</summary>
        public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> tile)
        {
            Span<byte> alphas = stackalloc byte[8];
            BuildAlphaPalette(block[0], block[1], alphas);

            ulong alphaBits = block[2] | ((ulong)block[3] << 8) | ((ulong)block[4] << 16)
                | ((ulong)block[5] << 24) | ((ulong)block[6] << 32) | ((ulong)block[7] << 40);

            ushort c0 = (ushort)(block[8] | (block[9] << 8));
            ushort c1 = (ushort)(block[10] | (block[11] << 8));
            uint colourBits = (uint)(block[12] | (block[13] << 8) | (block[14] << 16) | (block[15] << 24));

            Span<int> palette = stackalloc int[12];
            BuildColourPalette(c0, c1, palette);

            for (int i = 0; i < 16; i++)
            {
                int colour = (int)((colourBits >> (i * 2)) & 3) * 3;
                int alpha = (int)((alphaBits >> (i * 3)) & 7);

                tile[i * 4] = (byte)palette[colour + 2];      // blue
                tile[i * 4 + 1] = (byte)palette[colour + 1];  // green
                tile[i * 4 + 2] = (byte)palette[colour];      // red
                tile[i * 4 + 3] = alphas[alpha];
            }
        }

        // ---- encode ----------------------------------------------------------------------------

        /// <summary>Compresses a 4x4 BGRA tile into one block.</summary>
        public static void EncodeBlock(ReadOnlySpan<byte> tile, Span<byte> block)
        {
            EncodeAlpha(tile, block);
            EncodeColour(tile, block[8..]);
        }

        private static void EncodeAlpha(ReadOnlySpan<byte> tile, Span<byte> block)
        {
            int low = 255, high = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = tile[i * 4 + 3];
                if (a < low) low = a;
                if (a > high) high = a;
            }

            block[0] = (byte)high;
            block[1] = (byte)low;

            if (low == high)
            {
                // A flat tile, which is what these VFX panels are: their alpha is opaque throughout.
                block[2] = block[3] = block[4] = block[5] = block[6] = block[7] = 0;
                return;
            }

            Span<byte> palette = stackalloc byte[8];
            BuildAlphaPalette((byte)high, (byte)low, palette);

            ulong bits = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = tile[i * 4 + 3];
                int best = 0, bestError = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int error = a - palette[k];
                    error *= error;
                    if (error >= bestError) continue;
                    bestError = error;
                    best = k;
                }
                bits |= (ulong)best << (i * 3);
            }

            for (int i = 0; i < 6; i++) block[2 + i] = (byte)(bits >> (i * 8));
        }

        /// <summary>
        /// The colour half: two RGB565 endpoints and sixteen two-bit indices into the line between
        /// them.
        ///
        /// Endpoints come from the tile's principal colour axis rather than its per-channel bounding
        /// box, then get two rounds of least-squares refinement against the indices they produce.
        /// The bounding box alone is noticeably worse on exactly the content this feature pastes —
        /// photographs and album art, where the colours run along a diagonal the box does not follow.
        /// Both candidates are scored and the better one kept, so the cheap fit still wins where it
        /// deserves to.
        /// </summary>
        private static void EncodeColour(ReadOnlySpan<byte> tile, Span<byte> block)
        {
            Span<int> reds = stackalloc int[16];
            Span<int> greens = stackalloc int[16];
            Span<int> blues = stackalloc int[16];

            bool flat = true;
            for (int i = 0; i < 16; i++)
            {
                blues[i] = tile[i * 4];
                greens[i] = tile[i * 4 + 1];
                reds[i] = tile[i * 4 + 2];
                if (i > 0 && (reds[i] != reds[0] || greens[i] != greens[0] || blues[i] != blues[0]))
                    flat = false;
            }

            if (flat)
            {
                ushort flatColour = Pack565(reds[0], greens[0], blues[0]);
                Write565(block, flatColour, flatColour, 0);
                return;
            }

            PrincipalAxisEndpoints(reds, greens, blues, out var axisLow, out var axisHigh);
            BoundingBoxEndpoints(reds, greens, blues, out var boxLow, out var boxHigh);

            Refine(reds, greens, blues, ref axisLow, ref axisHigh);

            long axisError = ScoreEndpoints(reds, greens, blues, axisLow, axisHigh, out uint axisIndices);
            long boxError = ScoreEndpoints(reds, greens, blues, boxLow, boxHigh, out uint boxIndices);

            ushort best0 = axisError <= boxError ? axisLow : boxLow;
            ushort best1 = axisError <= boxError ? axisHigh : boxHigh;
            uint indices = axisError <= boxError ? axisIndices : boxIndices;

            // BC3 has no three-colour mode, but a decoder that special-cases c0 <= c1 the way BC1 does
            // would read the palette backwards. Ordering the endpoints removes the question entirely.
            if (best0 < best1)
            {
                (best0, best1) = (best1, best0);
                indices = SwapEndpointIndices(indices);
            }
            else if (best0 == best1)
            {
                indices = 0;
            }

            Write565(block, best0, best1, indices);
        }

        /// <summary>Endpoints at the extremes of the tile's dominant colour direction.</summary>
        private static void PrincipalAxisEndpoints(ReadOnlySpan<int> reds, ReadOnlySpan<int> greens,
            ReadOnlySpan<int> blues, out ushort low, out ushort high)
        {
            double meanR = 0, meanG = 0, meanB = 0;
            for (int i = 0; i < 16; i++) { meanR += reds[i]; meanG += greens[i]; meanB += blues[i]; }
            meanR /= 16; meanG /= 16; meanB /= 16;

            double rr = 0, rg = 0, rb = 0, gg = 0, gb = 0, bb = 0;
            for (int i = 0; i < 16; i++)
            {
                double dr = reds[i] - meanR, dg = greens[i] - meanG, db = blues[i] - meanB;
                rr += dr * dr; rg += dr * dg; rb += dr * db;
                gg += dg * dg; gb += dg * db; bb += db * db;
            }

            // Power iteration from the widest row of the covariance matrix, which is a seed that
            // cannot be orthogonal to the axis it is looking for.
            double axisR = rr, axisG = rg, axisB = rb;
            if (gg > rr && gg >= bb) { axisR = rg; axisG = gg; axisB = gb; }
            else if (bb > rr) { axisR = rb; axisG = gb; axisB = bb; }

            for (int iteration = 0; iteration < 6; iteration++)
            {
                double nextR = axisR * rr + axisG * rg + axisB * rb;
                double nextG = axisR * rg + axisG * gg + axisB * gb;
                double nextB = axisR * rb + axisG * gb + axisB * bb;

                double magnitude = Math.Max(Math.Abs(nextR), Math.Max(Math.Abs(nextG), Math.Abs(nextB)));
                if (magnitude < 1e-9) break;

                axisR = nextR / magnitude;
                axisG = nextG / magnitude;
                axisB = nextB / magnitude;
            }

            if (Math.Abs(axisR) + Math.Abs(axisG) + Math.Abs(axisB) < 1e-9)
            {
                // A tile with no colour variance at all still has to produce two endpoints.
                axisR = 0.299; axisG = 0.587; axisB = 0.114;
            }

            double lowest = double.MaxValue, highest = double.MinValue;
            int lowIndex = 0, highIndex = 0;
            for (int i = 0; i < 16; i++)
            {
                double projection = reds[i] * axisR + greens[i] * axisG + blues[i] * axisB;
                if (projection < lowest) { lowest = projection; lowIndex = i; }
                if (projection > highest) { highest = projection; highIndex = i; }
            }

            low = Pack565(reds[lowIndex], greens[lowIndex], blues[lowIndex]);
            high = Pack565(reds[highIndex], greens[highIndex], blues[highIndex]);
        }

        private static void BoundingBoxEndpoints(ReadOnlySpan<int> reds, ReadOnlySpan<int> greens,
            ReadOnlySpan<int> blues, out ushort low, out ushort high)
        {
            int lowR = 255, lowG = 255, lowB = 255, highR = 0, highG = 0, highB = 0;
            for (int i = 0; i < 16; i++)
            {
                lowR = Math.Min(lowR, reds[i]); highR = Math.Max(highR, reds[i]);
                lowG = Math.Min(lowG, greens[i]); highG = Math.Max(highG, greens[i]);
                lowB = Math.Min(lowB, blues[i]); highB = Math.Max(highB, blues[i]);
            }
            low = Pack565(lowR, lowG, lowB);
            high = Pack565(highR, highG, highB);
        }

        /// <summary>Two rounds of: assign indices, then least-squares solve the endpoints back.</summary>
        private static void Refine(ReadOnlySpan<int> reds, ReadOnlySpan<int> greens,
            ReadOnlySpan<int> blues, ref ushort low, ref ushort high)
        {
            for (int round = 0; round < 2; round++)
            {
                ScoreEndpoints(reds, greens, blues, low, high, out uint indices);

                double aa = 0, ab = 0, bb = 0;
                double axR = 0, axG = 0, axB = 0, bxR = 0, bxG = 0, bxB = 0;

                for (int i = 0; i < 16; i++)
                {
                    double weight = ((indices >> (i * 2)) & 3) switch
                    {
                        0 => 1.0,
                        1 => 0.0,
                        2 => 2.0 / 3.0,
                        _ => 1.0 / 3.0,
                    };
                    double other = 1.0 - weight;

                    aa += weight * weight;
                    bb += other * other;
                    ab += weight * other;
                    axR += weight * reds[i]; axG += weight * greens[i]; axB += weight * blues[i];
                    bxR += other * reds[i]; bxG += other * greens[i]; bxB += other * blues[i];
                }

                double determinant = aa * bb - ab * ab;
                if (Math.Abs(determinant) < 1e-9) return;

                low = Pack565(
                    Round((axR * bb - bxR * ab) / determinant),
                    Round((axG * bb - bxG * ab) / determinant),
                    Round((axB * bb - bxB * ab) / determinant));
                high = Pack565(
                    Round((bxR * aa - axR * ab) / determinant),
                    Round((bxG * aa - axG * ab) / determinant),
                    Round((bxB * aa - axB * ab) / determinant));
            }
        }

        /// <summary>Squared error of the best index assignment for these endpoints.</summary>
        private static long ScoreEndpoints(ReadOnlySpan<int> reds, ReadOnlySpan<int> greens,
            ReadOnlySpan<int> blues, ushort low, ushort high, out uint indices)
        {
            Span<int> palette = stackalloc int[12];
            BuildColourPalette(low, high, palette);

            indices = 0;
            long total = 0;

            for (int i = 0; i < 16; i++)
            {
                int best = 0;
                long bestError = long.MaxValue;
                for (int k = 0; k < 4; k++)
                {
                    long dr = reds[i] - palette[k * 3];
                    long dg = greens[i] - palette[k * 3 + 1];
                    long db = blues[i] - palette[k * 3 + 2];
                    long error = dr * dr + dg * dg + db * db;
                    if (error >= bestError) continue;
                    bestError = error;
                    best = k;
                }
                indices |= (uint)best << (i * 2);
                total += bestError;
            }
            return total;
        }

        // ---- bit fiddling ----------------------------------------------------------------------

        /// <summary>The eight alpha levels a block interpolates between, in index order.</summary>
        private static void BuildAlphaPalette(byte a0, byte a1, Span<byte> palette)
        {
            palette[0] = a0;
            palette[1] = a1;

            if (a0 > a1)
            {
                for (int k = 1; k <= 6; k++)
                    palette[k + 1] = (byte)(((7 - k) * a0 + k * a1) / 7);
            }
            else
            {
                for (int k = 1; k <= 4; k++)
                    palette[k + 1] = (byte)(((5 - k) * a0 + k * a1) / 5);
                palette[6] = 0;
                palette[7] = 255;
            }
        }

        /// <summary>
        /// The four colours a block interpolates between, as r,g,b triples.
        ///
        /// Always the four-colour layout, which is what D3D specifies for BC2 and BC3: only BC1 reads
        /// c0 &lt;= c1 as a three-colour mode with a punchthrough black.
        ///
        /// Worth knowing that this is the one place independent decoders disagree — some reuse their
        /// BC1 path for BC3 — and that real textures do contain such blocks: 95% of the blocks in
        /// these DJ packs have c0 &lt;= c1. It turns out not to matter, because almost all of them are
        /// flat black, where both readings agree to the pixel; decoding a whole shipped texture both
        /// ways differs in 0 of 1,048,576 pixels. The encoder side orders its endpoints anyway, so
        /// nothing this app writes is ambiguous.
        /// </summary>
        private static void BuildColourPalette(ushort c0, ushort c1, Span<int> palette)
        {
            Unpack565(c0, out int r0, out int g0, out int b0);
            Unpack565(c1, out int r1, out int g1, out int b1);

            palette[0] = r0; palette[1] = g0; palette[2] = b0;
            palette[3] = r1; palette[4] = g1; palette[5] = b1;
            palette[6] = (2 * r0 + r1) / 3; palette[7] = (2 * g0 + g1) / 3; palette[8] = (2 * b0 + b1) / 3;
            palette[9] = (r0 + 2 * r1) / 3; palette[10] = (g0 + 2 * g1) / 3; palette[11] = (b0 + 2 * b1) / 3;
        }

        private static uint SwapEndpointIndices(uint indices)
        {
            uint swapped = 0;
            for (int i = 0; i < 16; i++)
            {
                uint index = (indices >> (i * 2)) & 3;
                swapped |= (index switch { 0u => 1u, 1u => 0u, 2u => 3u, _ => 2u }) << (i * 2);
            }
            return swapped;
        }

        private static void Write565(Span<byte> block, ushort c0, ushort c1, uint indices)
        {
            block[0] = (byte)c0;
            block[1] = (byte)(c0 >> 8);
            block[2] = (byte)c1;
            block[3] = (byte)(c1 >> 8);
            block[4] = (byte)indices;
            block[5] = (byte)(indices >> 8);
            block[6] = (byte)(indices >> 16);
            block[7] = (byte)(indices >> 24);
        }

        private static ushort Pack565(int r, int g, int b) => (ushort)(
            ((Math.Clamp(r, 0, 255) * 31 + 127) / 255) << 11 |
            ((Math.Clamp(g, 0, 255) * 63 + 127) / 255) << 5 |
            ((Math.Clamp(b, 0, 255) * 31 + 127) / 255));

        private static void Unpack565(ushort colour, out int r, out int g, out int b)
        {
            int r5 = (colour >> 11) & 31, g6 = (colour >> 5) & 63, b5 = colour & 31;
            r = (r5 << 3) | (r5 >> 2);
            g = (g6 << 2) | (g6 >> 4);
            b = (b5 << 3) | (b5 >> 2);
        }

        private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
