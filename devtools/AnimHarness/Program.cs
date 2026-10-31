using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;

namespace AnimHarness;

/// <summary>
/// Offline checks for the animated table: AVFX surgery, GIF decoding, and the apply/restore
/// write ordering, over temporary copies of the Pickles base and DJ Moon Base packs.
///
/// Usage: AnimHarness [--pickles root] [--moon .pmp] [--only tag] [--scan penumbraRoot]
///                    [--calibration out.gif]
/// </summary>
internal static partial class Program
{
    private sealed record Test(string Name, string[] Tags, Action Body);

    private static readonly List<Test> AllTests = new();
    private static int _failedChecks;
    private static int _checks;

    private static void Add(string name, string tags, Action body) =>
        AllTests.Add(new Test(name, tags.Split(' ', StringSplitOptions.RemoveEmptyEntries), body));

    private static async Task<int> Main(string[] args)
    {
        AnimationText.Format = (message, arg) => $"{message}|{arg}";

        if (Value(args, "--pickles") is { } p) Fixtures.PicklesRoot = p;
        if (Value(args, "--moon") is { } m) Fixtures.MoonPack = m;
        if (Value(args, "--calibration") is { } cal)
        {
            File.WriteAllBytes(cal, GifWriter.Calibration());
            Console.WriteLine($"Wrote {cal}");
            return 0;
        }
        if (Value(args, "--scan") is { } scanRoot) return ScanPenumbra(scanRoot);
        if (Value(args, "--calibration-formats") is { } calDir) return CalibrationFormats(calDir);

        string? only = Value(args, "--only");
        Register();
        RegisterGif();
        RegisterMedia();

        int failed = 0, skipped = 0, passed = 0;
        foreach (var test in AllTests)
        {
            if (only != null && !test.Name.Contains(only, StringComparison.OrdinalIgnoreCase)
                && !test.Tags.Any(t => t.Contains(only, StringComparison.OrdinalIgnoreCase))) continue;

            int before = _failedChecks;
            string verdict;
            try
            {
                test.Body();
                verdict = _failedChecks == before ? "PASS" : "FAIL";
            }
            catch (SkipException ex) { verdict = "SKIP (" + ex.Message + ")"; }
            catch (AggregateException ex) when (ex.InnerException is SkipException s) { verdict = "SKIP (" + s.Message + ")"; }
            catch (Exception ex)
            {
                verdict = "FAIL";
                _failedChecks++;
                Console.WriteLine($"   threw: {ex}");
            }

            if (verdict == "PASS") passed++;
            else if (verdict.StartsWith("SKIP")) skipped++;
            else failed++;
            Console.WriteLine($"{verdict,-5} {test.Name} {string.Join(" ", test.Tags.Select(t => "[" + t + "]"))}");
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped; {_checks} checks.");
        await Task.CompletedTask;
        return failed == 0 && skipped == 0 ? 0 : 1;
    }

    private static bool Check(bool condition, string what)
    {
        _checks++;
        if (!condition)
        {
            _failedChecks++;
            Console.WriteLine($"   FAIL {what}");
        }
        return condition;
    }

    private static string? Value(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ---- shared steps ---------------------------------------------------------------------------

    private static bool ApplyFrames(TempMod mod, int n, out string error, TempModHost? host = null,
        DjTableAnimationSite? site = null, int delayCs = 10)
    {
        host ??= mod.Host;
        site ??= mod.Scan(host);
        var ok = DjTableAnimationCore.Apply(host, site, Fixtures.Frames(n), Fixtures.Timeline(n, delayCs),
            FitMode.Fill, null, out _, out error);
        return ok;
    }

    private static bool Restore(TempMod mod, out IReadOnlyList<string> warnings, out string error, TempModHost? host = null)
    {
        host ??= mod.Host;
        return DjTableAnimationCore.Restore(host, mod.Scan(host), out warnings, out error);
    }

    private static void CheckPristine(TempMod mod, Dictionary<string, byte[]> avfx, List<Newtonsoft.Json.Linq.JObject> groups, string label)
    {
        foreach (var (rel, bytes) in avfx)
            if (File.Exists(mod.Full(rel)))
                Check(mod.Read(rel).AsSpan().SequenceEqual(bytes), $"{label}: {rel} byte-identical");
        var now = mod.Groups();
        Check(now.Count == groups.Count && now.Zip(groups).All(z => Newtonsoft.Json.Linq.JToken.DeepEquals(z.First, z.Second)),
            $"{label}: groups JSON-equal");
        Check(!Directory.Exists(mod.Full(DjAnimationScan.FrameFolder)), $"{label}: no frame folder");
        Check(!Directory.Exists(mod.StateFolder), $"{label}: no state folder");
    }

    private static Dictionary<string, byte[]> AvfxBytes(TempMod mod, IEnumerable<string> rels) =>
        rels.Where(r => File.Exists(mod.Full(r))).ToDictionary(r => r, r => mod.Read(r));

    /// <summary>The 0–9 digit calibration as WebP, APNG and MP4, for the in-game speed check.</summary>
    private static int CalibrationFormats(string dir)
    {
        Directory.CreateDirectory(dir);
        string gif = Path.Combine(dir, "calibration.gif");
        File.WriteAllBytes(gif, GifWriter.Calibration());
        MediaFixtures.Ffmpeg("-i", gif, "-c:v", "libwebp_anim", "-lossless", "1", "-loop", "0", Path.Combine(dir, "calibration.webp"));
        MediaFixtures.Ffmpeg("-i", gif, "-plays", "0", "-f", "apng", Path.Combine(dir, "calibration.apng"));
        MediaFixtures.Ffmpeg("-i", gif, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-r", "10", Path.Combine(dir, "calibration.mp4"));
        Console.WriteLine($"Wrote calibration.webp/.apng/.mp4 to {dir}");
        return 0;
    }

        private static int ScanPenumbra(string root)
    {
        if (!Directory.Exists(root)) { Console.WriteLine("SKIP no Penumbra root"); return 0; }
        int bad = 0;
        foreach (var candidate in DjTextureScan.FindCandidates(root, null))
        {
            string modRoot = Path.Combine(root, candidate.Folder);
            var table = DjTextureScan.Locate(modRoot, candidate.Folder, out _).FirstOrDefault(t => t.Surface.Key == "table");
            if (table == null) continue;
            try
            {
                var site = DjAnimationScan.Inspect(modRoot, table, Path.Combine(Path.GetTempPath(), "AnimHarness", "nostate"));
                var status = DjTableAnimationCore.Status(site);
                Console.WriteLine($"{candidate.Name}: {status.Kind} mode={site.Mode} capacity={site.Capacity} {site.Problem}");
            }
            catch (Exception ex) { bad++; Console.WriteLine($"{candidate.Name}: scan threw {ex.Message}"); }
        }
        return bad == 0 ? 0 : 1;
    }
}
