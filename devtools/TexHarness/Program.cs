using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Tex;

namespace TexHarness;

/// <summary>
/// Runs the offline format checks over real .atex files.
///
/// Usage: TexHarness [modRoot] [--picture file.bmp] [--dump outDir]
///
/// The checks that matter are the ones a unit test on a fixture this repo wrote itself could not
/// make: that a paste leaves every block it did not cover byte-identical, and that the encoder is
/// good enough on real deck art and real photographs rather than on a gradient.
/// </summary>
internal static class Program
{
    private const string DefaultRoot = @"e:\Penumbradt\DJ Pickles Base Mod Pack For All V5.0.7 (2)";

    private sealed record Surface(string Label, string Relative, TexRect Rect, int QuarterTurns);

    private static readonly Surface[] Surfaces =
    {
        new("table panel", @"common\4\eq55.atex", new TexRect(165, 398, 440, 289), 2),
        new("laptop screen", @"common\7\eq3.atex", new TexRect(2993, 2759, 387, 656), 1),
        new("lid badge", @"common\7\eq3.atex", new TexRect(3109, 3660, 196, 196), 1),
    };

    private static int _checked, _failed;

    private static int Main(string[] args)
    {
        // Positional, and only in first place: anywhere else it is indistinguishable from the value
        // belonging to the flag in front of it.
        string root = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : DefaultRoot;
        string? picture = ValueAfter(args, "--picture");
        string? dump = ValueAfter(args, "--dump");
        string? codecOut = ValueAfter(args, "--codeccheck");
        string? scanRoot = ValueAfter(args, "--scan");

        if (scanRoot != null) return Scan(scanRoot);

        string? fitDemo = ValueAfter(args, "--fitdemo");
        if (fitDemo != null && dump != null) return FitDemo(fitDemo, dump);

        if (codecOut != null)
        {
            // The table cross-check first: if it fails, the decode that follows would walk off the
            // end of a block and the exception would say nothing about which entry is wrong.
            if (CodecCheck.AnchorsAgreeWithPartitions() != 0) return 1;
            if (CodecCheck.PictureLoadPlanIsSane() != 0) return 1;
            if (CodecCheck.FitModesDiffer(codecOut) != 0) return 1;
            if (CodecCheck.Bc3EncoderOrdersEndpoints(20000, seed: 20260924) != 0) return 1;

            CodecCheck.EmitBc3(codecOut, 20000, seed: 20260924);
            CodecCheck.EmitBc7(codecOut, 20000, seed: 20260924);
            Console.WriteLine($"   now run compare_against_reference.py {codecOut}");
            return 0;
        }

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"No such mod folder: {root}");
            return 2;
        }

        Console.WriteLine($"Mod: {root}");
        if (dump != null) Directory.CreateDirectory(dump);

        foreach (var surface in Surfaces)
        {
            string path = Path.Combine(root, surface.Relative);
            if (!File.Exists(path))
            {
                Console.WriteLine($"  SKIP {surface.Label}: {surface.Relative} is not there");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"== {surface.Label} ({surface.Relative})");
            Check(surface, File.ReadAllBytes(path), picture, dump);
        }

        SharedFileSurvivesBothEdits(root);

        Console.WriteLine();
        Console.WriteLine($"{_checked} checks, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Writes what each fit mode does to a real picture at each panel's real size.
    ///
    /// For answering "the fit control does not seem to change anything" with something you can look
    /// at rather than an assurance that the code is fine.
    /// </summary>
    private static int FitDemo(string picture, string outputDirectory)
    {
        if (!File.Exists(picture))
        {
            Console.Error.WriteLine($"No such picture: {picture}");
            return 2;
        }

        Directory.CreateDirectory(outputDirectory);
        var source = Bmp.Read(picture);
        Console.WriteLine($"source {source.Width}x{source.Height} "
            + $"(aspect {(double)source.Width / source.Height:0.00})");

        foreach (var surface in Surfaces)
        {
            int w = surface.QuarterTurns % 2 == 0 ? surface.Rect.Width : surface.Rect.Height;
            int h = surface.QuarterTurns % 2 == 0 ? surface.Rect.Height : surface.Rect.Width;
            Console.WriteLine($"\n{surface.Label}: panel {w}x{h} (aspect {(double)w / h:0.00})");

            foreach (var mode in new[] { FitMode.Fill, FitMode.Fit, FitMode.Stretch })
            {
                var fitted = ImageOps.FitTo(source, w, h, mode);
                string name = surface.Label.Replace(' ', '-') + "-" + mode + ".bmp";
                Bmp.Write(Path.Combine(outputDirectory, name), fitted);

                // How much of the panel this mode leaves black tells you whether Whole will read as
                // letterboxing or as an imperceptible nudge.
                long black = 0;
                for (int i = 0; i < fitted.Pixels.Length; i += 4)
                    if (fitted.Pixels[i] < 8 && fitted.Pixels[i + 1] < 8 && fitted.Pixels[i + 2] < 8) black++;
                Console.WriteLine($"   {mode,-8} {100.0 * black / (w * h),5:0.#}% of the panel is black");
            }
        }
        return 0;
    }

    /// <summary>
    /// Runs the dialog's own mod detection over a real Penumbra folder and prints what it finds.
    ///
    /// This is the check that was missing. Every other check here starts from a path this file
    /// hard-codes, so all of them passed while the dialog — which has to FIND the file first —
    /// reported that no mod anywhere had a DJ table.
    /// </summary>
    private static int Scan(string penumbraRoot)
    {
        if (!Directory.Exists(penumbraRoot))
        {
            Console.Error.WriteLine($"No such Penumbra folder: {penumbraRoot}");
            return 2;
        }

        Console.WriteLine($"Penumbra: {penumbraRoot}");
        int mods = Directory.EnumerateDirectories(penumbraRoot).Count();

        var candidates = DjTextureScan.FindCandidates(penumbraRoot, preferredFolder: null);
        Console.WriteLine($"{mods:N0} mods scanned, {candidates.Count} with a DJ table.");
        Console.WriteLine();

        foreach (var candidate in candidates)
        {
            Console.WriteLine($"== {candidate.Name}  [{candidate.Folder}]");
            var targets = DjTextureScan.Locate(
                Path.Combine(penumbraRoot, candidate.Folder), candidate.Folder, out var problems);

            foreach (var target in targets)
                Console.WriteLine($"   ok   {target.Surface.Key,-7} {target.Relative}");
            foreach (var problem in problems)
                Console.WriteLine($"   --   {problem.Surface.Key,-7} {problem.Reason}");
        }

        Assert(candidates.Count > 0, "at least one mod has a DJ table");
        Console.WriteLine();
        Console.WriteLine($"{_checked} checks, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(Surface surface, byte[] original, string? picture, string? dump)
    {
        var header = TexHeader.TryRead(original, out string error);
        if (!Assert(header != null, $"header parses ({error})") || header == null) return;

        Console.WriteLine($"   {header} — {original.Length:N0} bytes");

        var codec = BlockCodec.For(header.Format, out string unsupported);
        if (!Assert(codec != null, $"format is one this can edit ({unsupported})") || codec == null) return;
        Assert(surface.Rect.FitsInside(header.Width, header.Height), "panel fits the texture");

        var before = TexPaste.ReadRect(original, surface.Rect, out error);
        if (!Assert(before != null, $"panel reads back ({error})") || before == null) return;

        if (dump != null)
            Bmp.Write(Path.Combine(dump, Name(surface, "before")), ImageOps.Rotate(before, surface.QuarterTurns));

        var content = picture != null && File.Exists(picture)
            ? FitPicture(Bmp.Read(picture), surface, before)
            : TestPattern(surface.Rect.Width, surface.Rect.Height);

        RoundTripQuality(before, codec, $"re-encoding the panel as it stands ({header.Format})");

        var pasted = TexPaste.PasteRect(original, surface.Rect, content, out error);
        if (!Assert(pasted != null, $"paste succeeds ({error})") || pasted == null) return;

        Assert(pasted.Length == original.Length, "file length is unchanged");
        Assert(pasted.AsSpan(0, TexHeader.Size).SequenceEqual(original.AsSpan(0, TexHeader.Size)),
            "header is byte-identical");

        UntouchedBlocksSurvive(header, surface.Rect, original, pasted);

        var after = TexPaste.ReadRect(pasted, surface.Rect, out error);
        if (!Assert(after != null, $"pasted panel reads back ({error})") || after == null) return;

        double psnr = Psnr(content, after);
        Assert(psnr > 30, $"pasted panel survives compression (PSNR {psnr:0.0} dB)");

        if (dump != null)
            Bmp.Write(Path.Combine(dump, Name(surface, "after")), ImageOps.Rotate(after, surface.QuarterTurns));

        // Pasting the same picture twice must land on the same bytes: the app always composites from
        // its pristine copy, and a paste that drifted would mean re-applying quietly degraded the art.
        var again = TexPaste.PasteRect(original, surface.Rect, content, out _);
        Assert(again != null && again.AsSpan().SequenceEqual(pasted), "paste is deterministic");
    }

    /// <summary>
    /// Two surfaces in one texture must not tread on each other.
    ///
    /// The laptop screen and the lid badge are both islands of eq3.atex, so an apply to one has to
    /// leave the other alone, and restoring one has to leave the other's picture in place. This is
    /// the mechanic the per-file backup model was rewritten for; the bookkeeping around it decides
    /// WHICH bytes get pasted, but this is what decides whether the result is right.
    /// </summary>
    private static void SharedFileSurvivesBothEdits(string root)
    {
        var screen = Surfaces.First(s => s.Label == "laptop screen");
        var badge = Surfaces.First(s => s.Label == "lid badge");
        string path = Path.Combine(root, screen.Relative);
        if (!File.Exists(path)) return;

        Console.WriteLine();
        Console.WriteLine("== two surfaces sharing one texture");

        byte[] original = File.ReadAllBytes(path);
        var screenPicture = TestPattern(screen.Rect.Width, screen.Rect.Height);
        var badgePicture = TestPattern(badge.Rect.Width, badge.Rect.Height);

        // Apply one, then the other on top of the result, exactly as two applies would.
        var afterScreen = TexPaste.PasteRect(original, screen.Rect, screenPicture, out string error);
        if (!Assert(afterScreen != null, $"screen applies ({error})") || afterScreen == null) return;

        var afterBoth = TexPaste.PasteRect(afterScreen, badge.Rect, badgePicture, out error);
        if (!Assert(afterBoth != null, $"badge applies on top ({error})") || afterBoth == null) return;

        var screenNow = TexPaste.ReadRect(afterBoth, screen.Rect, out _);
        Assert(screenNow != null && Psnr(screenPicture, screenNow) > 30,
            "the screen survives the badge being applied after it");

        // Restore just the badge: take its rectangle from the pristine file and paste it back.
        var pristineBadge = TexPaste.ReadRect(original, badge.Rect, out _);
        var afterRestore = pristineBadge == null ? null
            : TexPaste.PasteRect(afterBoth, badge.Rect, pristineBadge, out error);
        if (!Assert(afterRestore != null, $"badge restores ({error})") || afterRestore == null) return;

        var badgeNow = TexPaste.ReadRect(afterRestore, badge.Rect, out _);
        Assert(badgeNow != null && pristineBadge != null && Psnr(pristineBadge, badgeNow) > 40,
            "the badge comes back as it shipped");

        var screenAfterRestore = TexPaste.ReadRect(afterRestore, screen.Rect, out _);
        Assert(screenAfterRestore != null && Psnr(screenPicture, screenAfterRestore) > 30,
            "restoring the badge leaves the screen picture alone");

        // And the rectangles must not overlap in the first place.
        bool overlaps = screen.Rect.X < badge.Rect.Right && badge.Rect.X < screen.Rect.Right
                     && screen.Rect.Y < badge.Rect.Bottom && badge.Rect.Y < screen.Rect.Bottom;
        Assert(!overlaps, "the two rectangles do not overlap");
    }

    /// <summary>Every block the rectangle does not cover must come through untouched.</summary>
    private static void UntouchedBlocksSurvive(TexHeader header, TexRect rect, byte[] original, byte[] pasted)
    {
        long changed = 0, outside = 0;

        for (int level = 0; level < header.MipCount; level++)
        {
            int x = rect.X >> level, y = rect.Y >> level;
            int width = Math.Max(1, rect.Width >> level), height = Math.Max(1, rect.Height >> level);
            int mipWidth = header.MipWidth(level), mipHeight = header.MipHeight(level);
            if (x >= mipWidth || y >= mipHeight) continue;
            width = Math.Min(width, mipWidth - x);
            height = Math.Min(height, mipHeight - y);

            int blocksAcross = header.BlocksAcross(level), blocksDown = header.BlocksDown(level);
            int offset = header.MipOffsets[level];

            for (int by = 0; by < blocksDown; by++)
            {
                for (int bx = 0; bx < blocksAcross; bx++)
                {
                    bool touches = bx * 4 < x + width && (bx + 1) * 4 > x
                                && by * 4 < y + height && (by + 1) * 4 > y;
                    if (touches) continue;

                    outside++;
                    int at = offset + (by * blocksAcross + bx) * 16;
                    if (!original.AsSpan(at, 16).SequenceEqual(pasted.AsSpan(at, 16)))
                        changed++;
                }
            }
        }

        Assert(changed == 0, $"{outside:N0} blocks outside the panel are untouched ({changed:N0} changed)");
    }

    /// <summary>How much the encoder loses on content it has already compressed once.</summary>
    private static void RoundTripQuality(BgraImage image, IBlockCodec codec, string label)
    {
        var tile = new byte[64];
        var block = new byte[codec.BlockBytes];
        var result = new BgraImage(image.Width, image.Height);

        for (int by = 0; by * 4 < image.Height; by++)
        {
            for (int bx = 0; bx * 4 < image.Width; bx++)
            {
                for (int ty = 0; ty < 4; ty++)
                {
                    int y = Math.Min(by * 4 + ty, image.Height - 1);
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int x = Math.Min(bx * 4 + tx, image.Width - 1);
                        Array.Copy(image.Pixels, (y * image.Width + x) * 4, tile, (ty * 4 + tx) * 4, 4);
                    }
                }

                codec.EncodeBlock(tile, block);
                codec.DecodeBlock(block, tile);

                for (int ty = 0; ty < 4; ty++)
                {
                    int y = by * 4 + ty;
                    if (y >= image.Height) break;
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int x = bx * 4 + tx;
                        if (x >= image.Width) break;
                        Array.Copy(tile, (ty * 4 + tx) * 4, result.Pixels, (y * image.Width + x) * 4, 4);
                    }
                }
            }
        }

        double psnr = Psnr(image, result);
        Assert(psnr > 30, $"{label}: PSNR {psnr:0.0} dB");
    }

    private static BgraImage FitPicture(BgraImage picture, Surface surface, BgraImage panel)
    {
        // Upright first, the way a user sees it, then back into the texture's own orientation.
        var upright = ImageOps.FitTo(picture,
            surface.QuarterTurns % 2 == 0 ? panel.Width : panel.Height,
            surface.QuarterTurns % 2 == 0 ? panel.Height : panel.Width,
            FitMode.Fill);
        return ImageOps.Rotate(upright, -surface.QuarterTurns);
    }

    private static BgraImage TestPattern(int width, int height)
    {
        var image = new BgraImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool checker = ((x / 16) + (y / 16)) % 2 == 0;
                image.Pixels[i] = (byte)(checker ? 255 - x * 255 / width : 40);
                image.Pixels[i + 1] = (byte)(y * 255 / height);
                image.Pixels[i + 2] = (byte)(checker ? x * 255 / width : 200);
                image.Pixels[i + 3] = 255;
            }
        }
        return image;
    }

    private static double Psnr(BgraImage a, BgraImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 0;

        double sum = 0;
        int count = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                double difference = a.Pixels[i + channel] - b.Pixels[i + channel];
                sum += difference * difference;
                count++;
            }
        }
        double mse = sum / count;
        return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static string Name(Surface surface, string suffix) =>
        surface.Label.Replace(' ', '-') + "-" + suffix + ".bmp";

    private static string? ValueAfter(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool Assert(bool condition, string what)
    {
        _checked++;
        if (!condition) _failed++;
        Console.WriteLine($"   {(condition ? "ok  " : "FAIL")} {what}");
        return condition;
    }
}
