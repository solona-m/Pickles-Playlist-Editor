using Pickles_Playlist_Editor.Utils.Tex;

namespace TexHarness;

/// <summary>
/// Writes compressed blocks and this codec's decode of them, so an independent decoder can be held
/// against it pixel for pixel.
///
/// A block codec that is merely self-consistent proves nothing: encode-then-decode agrees with
/// itself just as happily when both halves share a mistake. Random blocks reach every mode and every
/// partition, which eyeballing a real texture never would — the one real BC7 texture in these mods
/// turns out to use two modes out of eight.
///
/// compare_against_reference.py is the other half of this; it decodes the same blocks with an
/// unrelated decoder and diffs every pixel.
/// </summary>
internal static class CodecCheck
{
    /// <summary>
    /// Checks the anchor tables against the partition tables, which must agree.
    ///
    /// The anchor of a subset is a texel OF that subset, and the three anchors of a block are three
    /// different texels — otherwise the decoder reads the wrong number of index bits and walks off
    /// the end of the block. That makes this a check the two tables can settle between themselves,
    /// with no reference decoder involved, and it localises a bad entry to a single partition.
    /// </summary>
    public static int AnchorsAgreeWithPartitions()
    {
        int bad = 0;

        for (int p = 0; p < 64; p++)
        {
            int anchor = Bc7Tables.Anchor2[p];
            if (anchor == 0 || Bc7Tables.Partition2[p, anchor] != 1)
            {
                Console.WriteLine($"   FAIL 2-subset partition {p}: anchor {anchor} is in subset "
                    + $"{(anchor == 0 ? 0 : Bc7Tables.Partition2[p, anchor])}, not 1");
                bad++;
            }
        }

        for (int p = 0; p < 64; p++)
        {
            int first = Bc7Tables.Anchor3First[p];
            int second = Bc7Tables.Anchor3Second[p];

            if (first == 0 || Bc7Tables.Partition3[p, first] != 1)
            {
                Console.WriteLine($"   FAIL 3-subset partition {p}: first anchor {first} is in subset "
                    + $"{(first == 0 ? 0 : Bc7Tables.Partition3[p, first])}, not 1");
                bad++;
            }
            if (second == 0 || Bc7Tables.Partition3[p, second] != 2)
            {
                Console.WriteLine($"   FAIL 3-subset partition {p}: second anchor {second} is in subset "
                    + $"{(second == 0 ? 0 : Bc7Tables.Partition3[p, second])}, not 2");
                bad++;
            }
            if (first == second)
            {
                Console.WriteLine($"   FAIL 3-subset partition {p}: both anchors are texel {first}");
                bad++;
            }
        }

        // Every subset a partition claims to have must actually contain a texel, or its endpoints
        // describe nothing and the index bit budget is wrong again.
        for (int p = 0; p < 64; p++)
        {
            int seen2 = 0, seen3 = 0;
            for (int i = 0; i < 16; i++)
            {
                seen2 |= 1 << Bc7Tables.Partition2[p, i];
                seen3 |= 1 << Bc7Tables.Partition3[p, i];
            }
            if (seen2 != 0b11)
            {
                Console.WriteLine($"   FAIL 2-subset partition {p}: subsets present {seen2:b}");
                bad++;
            }
            if (seen3 != 0b111)
            {
                Console.WriteLine($"   FAIL 3-subset partition {p}: subsets present {seen3:b}");
                bad++;
            }
        }

        Console.WriteLine(bad == 0
            ? "   ok   anchor tables agree with the partition tables"
            : $"   {bad} table disagreements");
        return bad;
    }

    /// <summary>
    /// Checks the picture load plan, which is where the EXIF shear bug lived.
    ///
    /// The loader itself needs a real image decoder and can only run inside the app, so the
    /// arithmetic was pulled into a pure function to get it under test. What this cannot prove is
    /// the platform's ordering — that BitmapTransform scales before the EXIF rotation — which still
    /// wants one drag of a genuinely rotated photograph.
    /// </summary>
    public static int PictureLoadPlanIsSane()
    {
        // rawW, rawH, orientedW, maxEdge, expected scaled, expected output
        var cases = new (uint RawW, uint RawH, uint OrientedW, uint Max,
                         uint ScaledW, uint ScaledH, int OutW, int OutH, string What)[]
        {
            (4032, 3024, 3024, 2048, 2048, 1536, 1536, 2048, "portrait phone photo, quarter turn"),
            (3024, 4032, 3024, 2048, 1536, 2048, 1536, 2048, "portrait photo, no rotation"),
            (4032, 3024, 4032, 2048, 2048, 1536, 2048, 1536, "landscape photo, no rotation"),
            (4032, 3024, 4032, 2048, 2048, 1536, 2048, 1536, "landscape, half turn (axes unchanged)"),
            (800, 1000, 1000, 2048, 800, 1000, 1000, 800, "small, quarter turn, under the cap"),
            (1000, 800, 1000, 2048, 1000, 800, 1000, 800, "small, no rotation, under the cap"),
            (4000, 4000, 4000, 2048, 2048, 2048, 2048, 2048, "square, rotation undetectable"),
            (1, 1, 1, 2048, 1, 1, 1, 1, "one pixel"),
        };

        int bad = 0;
        foreach (var c in cases)
        {
            var plan = PictureLoadPlan.For(c.RawW, c.RawH, c.OrientedW, c.Max);
            bool ok = plan.ScaledWidth == c.ScaledW && plan.ScaledHeight == c.ScaledH
                   && plan.OutputWidth == c.OutW && plan.OutputHeight == c.OutH;
            if (!ok)
            {
                Console.WriteLine($"   FAIL {c.What}: scaled {plan.ScaledWidth}x{plan.ScaledHeight} "
                    + $"output {plan.OutputWidth}x{plan.OutputHeight}, expected "
                    + $"{c.ScaledW}x{c.ScaledH} and {c.OutW}x{c.OutH}");
                bad++;
            }
        }

        // The aspect ratio must survive: a squashed frame was the visible half of the shear.
        var photo = PictureLoadPlan.For(4032, 3024, 3024, 2048);
        double rawAspect = 4032.0 / 3024.0, scaledAspect = (double)photo.ScaledWidth / photo.ScaledHeight;
        if (Math.Abs(rawAspect - scaledAspect) > 0.01)
        {
            Console.WriteLine($"   FAIL aspect ratio drifted: {rawAspect:0.000} became {scaledAspect:0.000}");
            bad++;
        }

        Console.WriteLine(bad == 0
            ? $"   ok   picture load plan correct for all {cases.Length} cases"
            : $"   {bad} picture load plan failures");
        return bad;
    }

    /// <summary>
    /// Checks that Fill, Whole and Stretch actually produce different pictures.
    ///
    /// They are the one control in the dialog whose whole job is visible change, so "it looks the
    /// same whichever I pick" is the symptom worth having a check for.
    /// </summary>
    public static int FitModesDiffer(string? dumpDirectory)
    {
        // Deliberately the wrong shape for the panel: tall and thin against a wide panel, so every
        // mode has something to do.
        var source = new BgraImage(300, 900);
        for (int y = 0; y < 900; y++)
        {
            for (int x = 0; x < 300; x++)
            {
                int i = (y * 300 + x) * 4;
                source.Pixels[i] = (byte)(x * 255 / 300);
                source.Pixels[i + 1] = (byte)(y * 255 / 900);
                source.Pixels[i + 2] = (byte)((x / 30 + y / 30) % 2 == 0 ? 230 : 40);
                source.Pixels[i + 3] = 255;
            }
        }

        const int W = 440, H = 289;
        var results = new Dictionary<FitMode, BgraImage>();
        foreach (var mode in new[] { FitMode.Fill, FitMode.Fit, FitMode.Stretch })
        {
            var fitted = ImageOps.FitTo(source, W, H, mode);
            results[mode] = fitted;
            if (fitted.Width != W || fitted.Height != H)
                Console.WriteLine($"   FAIL {mode} produced {fitted.Width}x{fitted.Height}, expected {W}x{H}");
            if (dumpDirectory != null)
            {
                Directory.CreateDirectory(dumpDirectory);
                Bmp.Write(Path.Combine(dumpDirectory, $"fit-{mode}.bmp"), fitted);
            }
        }

        int bad = 0;
        foreach (var (a, b) in new[]
                 {
                     (FitMode.Fill, FitMode.Fit),
                     (FitMode.Fill, FitMode.Stretch),
                     (FitMode.Fit, FitMode.Stretch),
                 })
        {
            long differing = 0;
            for (int i = 0; i < results[a].Pixels.Length; i += 4)
                if (results[a].Pixels[i] != results[b].Pixels[i]
                    || results[a].Pixels[i + 1] != results[b].Pixels[i + 1]
                    || results[a].Pixels[i + 2] != results[b].Pixels[i + 2]) differing++;

            double share = 100.0 * differing / (W * H);
            if (differing == 0)
            {
                Console.WriteLine($"   FAIL {a} and {b} produced identical pictures");
                bad++;
            }
            else
            {
                Console.WriteLine($"   ok   {a} vs {b}: {share:0.#}% of pixels differ");
            }
        }

        // Whole must letterbox: the panel is wider than the source is, so the sides go black.
        var whole = results[FitMode.Fit];
        bool leftEdgeBlack = true;
        for (int y = 0; y < H; y++)
        {
            int i = (y * W) * 4;
            if (whole.Pixels[i] != 0 || whole.Pixels[i + 1] != 0 || whole.Pixels[i + 2] != 0)
                leftEdgeBlack = false;
        }
        if (!leftEdgeBlack)
        {
            Console.WriteLine("   FAIL Whole did not letterbox a source narrower than the panel");
            bad++;
        }
        else
        {
            Console.WriteLine("   ok   Whole letterboxes a source of the wrong shape");
        }

        // And the readout beside the control has to agree with what the modes actually did.
        foreach (var (mode, kind) in new[]
                 {
                     (FitMode.Fill, FitEffectKind.Cropped),
                     (FitMode.Fit, FitEffectKind.Letterboxed),
                     (FitMode.Stretch, FitEffectKind.Squashed),
                 })
        {
            var effect = ImageOps.DescribeFit(300, 900, W, H, mode);
            if (effect.Kind != kind || effect.Percent <= 0)
            {
                Console.WriteLine($"   FAIL {mode} described as {effect.Kind} {effect.Percent}%");
                bad++;
            }
        }

        // Whole's number is the share of the panel left black, so it must match the measurement.
        var whole16x9 = ImageOps.DescribeFit(1920, 1080, 656, 387, FitMode.Fit);
        if (whole16x9.Kind != FitEffectKind.Letterboxed || Math.Abs(whole16x9.Percent - 5) > 2)
        {
            Console.WriteLine($"   FAIL 16:9 on the laptop panel described as "
                + $"{whole16x9.Kind} {whole16x9.Percent}%, measured 4.7% black");
            bad++;
        }

        // A picture already the right shape must not claim an effect it does not have.
        var exact = ImageOps.DescribeFit(880, 578, 440, 289, FitMode.Fill);
        if (exact.Kind != FitEffectKind.Exact)
        {
            Console.WriteLine($"   FAIL a correctly shaped picture described as {exact.Kind}");
            bad++;
        }

        if (bad == 0) Console.WriteLine("   ok   the fit readout matches what the modes do");
        return bad;
    }

    /// <summary>
    /// Checks that the BC3 encoder never emits a block with c0 &lt;= c1.
    ///
    /// That ordering is out of spec for BC3 and is the one case where independent decoders
    /// legitimately disagree — BC1 reads it as a three-colour mode with a punchthrough black, BC2
    /// and BC3 have no such mode. Real shipped textures contain plenty of such blocks, but almost
    /// always flat black ones where both readings agree to the pixel. Making our own output
    /// unambiguous costs a swap and removes the question from anything this app writes.
    /// </summary>
    public static int Bc3EncoderOrdersEndpoints(int count, int seed)
    {
        var random = new Random(seed);
        var tile = new byte[Bc3.TileBytes];
        var block = new byte[Bc3.BlockBytes];
        int unordered = 0;

        for (int i = 0; i < count; i++)
        {
            random.NextBytes(tile);

            // Flat and near-flat tiles are the ones that tempt an encoder into equal endpoints.
            if (i % 3 == 0)
            {
                for (int t = 1; t < 16; t++) Array.Copy(tile, 0, tile, t * 4, 4);
            }

            Bc3.EncodeBlock(tile, block);
            int c0 = block[8] | (block[9] << 8);
            int c1 = block[10] | (block[11] << 8);
            if (c0 < c1) unordered++;
        }

        Console.WriteLine(unordered == 0
            ? $"   ok   BC3 encoder ordered the endpoints in all {count:N0} blocks"
            : $"   FAIL BC3 encoder emitted {unordered:N0} blocks with c0 < c1");
        return unordered;
    }

    /// <summary>
    /// Emits BC3 blocks and their decode.
    ///
    /// Every 16-byte pattern is a valid BC3 block — no modes, no reserved encodings — so plain
    /// random bytes exercise the whole format, including the second alpha interpolation mode that
    /// real textures almost never reach.
    /// </summary>
    public static void EmitBc3(string directory, int count, int seed)
    {
        Directory.CreateDirectory(directory);

        var random = new Random(seed);
        var blocks = new byte[count * Bc3.BlockBytes];
        random.NextBytes(blocks);

        var pixels = new byte[count * Bc3.TileBytes];
        Span<byte> tile = stackalloc byte[Bc3.TileBytes];
        for (int i = 0; i < count; i++)
        {
            Bc3.DecodeBlock(blocks.AsSpan(i * Bc3.BlockBytes, Bc3.BlockBytes), tile);
            tile.CopyTo(pixels.AsSpan(i * Bc3.TileBytes));
        }

        File.WriteAllBytes(Path.Combine(directory, "bc3-blocks.bin"), blocks);
        File.WriteAllBytes(Path.Combine(directory, "bc3-pixels.bin"), pixels);
        Console.WriteLine($"   wrote {count:N0} BC3 blocks and their decode");
    }

    /// <summary>
    /// Emits BC7 blocks and their decode.
    ///
    /// Deterministic, so a failure found here can be re-run and narrowed rather than chased.
    /// </summary>
    public static void EmitBc7(string directory, int count, int seed)
    {
        Directory.CreateDirectory(directory);

        var random = new Random(seed);
        var blocks = new byte[count * Bc7.BlockBytes];
        random.NextBytes(blocks);

        // Random bytes name a mode uniformly at random only by accident: the mode is a run of zero
        // bits, so mode 0 turns up half the time and mode 7 almost never. Forcing an even spread is
        // what actually exercises the wide modes and the three-subset partitions.
        for (int i = 0; i < count; i++)
        {
            int mode = i % 9;   // 8 is the reserved "no mode bit" encoding, which must decode to zero
            int at = i * Bc7.BlockBytes;
            if (mode == 8)
            {
                blocks[at] = 0;
            }
            else
            {
                blocks[at] = (byte)((blocks[at] & ~((1 << (mode + 1)) - 1)) | (1 << mode));
            }
        }

        var pixels = new byte[count * Bc7.TileBytes];
        Span<byte> tile = stackalloc byte[Bc7.TileBytes];
        for (int i = 0; i < count; i++)
        {
            Bc7.DecodeBlock(blocks.AsSpan(i * Bc7.BlockBytes, Bc7.BlockBytes), tile);
            tile.CopyTo(pixels.AsSpan(i * Bc7.TileBytes));
        }

        File.WriteAllBytes(Path.Combine(directory, "bc7-blocks.bin"), blocks);
        File.WriteAllBytes(Path.Combine(directory, "bc7-pixels.bin"), pixels);
        Console.WriteLine($"   wrote {count:N0} BC7 blocks and their decode");
    }

}
