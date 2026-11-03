using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PersistentCollection;
using Xunit;

namespace ScdAudioTests;

public class ArchTargetTests
{
    private static string RepoRoot() => RuntimeTargetTests.RepoRoot();

    internal enum PeKind { NotPe, Managed, NativeX86, NativeX64, NativeOther }

    internal static PeKind Classify(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            var h = pe.PEHeaders;
            if (h.CorHeader != null) return PeKind.Managed;
            return h.CoffHeader.Machine switch
            {
                Machine.I386 => PeKind.NativeX86,
                Machine.Amd64 => PeKind.NativeX64,
                _ => PeKind.NativeOther,
            };
        }
        catch (BadImageFormatException) { return PeKind.NotPe; }
    }

    [Fact] // [AC-x64-naudio-1]
    public void TestHost_Is64Bit() => Assert.True(Environment.Is64BitProcess);

    [Theory] // [AC-x64-naudio-1]
    [InlineData("Pickles Playlist Editor.csproj")]
    [InlineData("devtools/ScdAudioTests/ScdAudioTests.csproj")]
    public void Projects_TargetX64(string file)
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), file));
        Assert.Contains("<PlatformTarget>x64</PlatformTarget>", text);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", text);
        Assert.DoesNotContain("x86", Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline));
        Assert.DoesNotContain("LargeAddressAware", text);
        Assert.DoesNotContain("libzplay", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // [AC-x64-naudio-1] [EDGE-x64-naudio-21]
    public void LibZPlay_IsGone()
    {
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "libzplay.dll")));
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "Utils", "libZPlay.cs")));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "libzplay.dll")));
    }

    [Fact] // [EDGE-x64-naudio-25]
    public void PeClassifier_Controls()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(PeKind.NativeX86, Classify(Path.Combine(win, "SysWOW64", "whoami.exe")));
        Assert.Equal(PeKind.NativeX64, Classify(Path.Combine(win, "System32", "whoami.exe")));
        Assert.Equal(PeKind.Managed, Classify(Path.Combine(AppContext.BaseDirectory, "Newtonsoft.Json.dll")));
        Assert.Equal(PeKind.Managed, Classify(typeof(object).Assembly.Location));
    }

    // Test-platform binaries ship with the test runner, not with the app.
    private static readonly Regex s_testPlatform = new(@"^(testhost|Microsoft\.TestPlatform|Microsoft\.VisualStudio|xunit|Xunit|ScdAudioTests)", RegexOptions.IgnoreCase);

    [Fact] // [AC-x64-naudio-1] [EDGE-x64-naudio-30]
    public void TestOutput_HasNoX86NativeBinaries()
    {
        var x86 = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(f => !s_testPlatform.IsMatch(Path.GetFileName(f)))
            .Where(f => Classify(f) == PeKind.NativeX86)
            .Select(f => Path.GetRelativePath(AppContext.BaseDirectory, f)).ToList();
        Assert.True(x86.Count == 0, "32-bit native files: " + string.Join(", ", x86));
    }

    [Fact] // [AC-x64-naudio-9]
    public void Notices_ShipLicenseTexts()
    {
        string root = RepoRoot();
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));
        Assert.Contains("SoundTouch.Net", notices);
        Assert.Contains("LGPL-2.1-or-later", notices);
        Assert.Contains("unmodified", notices);
        Assert.Contains("NAudio", notices);
        Assert.Contains("NVorbis", notices);
        Assert.Contains("MIT", notices);

        var st = WindowsAppSdkTargetTests.Package("SoundTouch.Net");
        Assert.Equal(File.ReadAllBytes(Path.Combine(st.Dir, "LICENSE", "LICENSE")), File.ReadAllBytes(Path.Combine(root, "licenses", "LGPL-2.1.txt")));
        Assert.True(File.Exists(Path.Combine(root, "licenses", "NAudio-MIT.txt")));
        Assert.True(File.Exists(Path.Combine(root, "licenses", "NVorbis-MIT.txt")));
        Assert.Contains("Andrew Ward", File.ReadAllText(Path.Combine(root, "licenses", "NAudio.Vorbis-MIT.txt")));
        Assert.Contains("NAudio.Vorbis-MIT.txt", notices);

        string csproj = File.ReadAllText(Path.Combine(root, "Pickles Playlist Editor.csproj"));
        Assert.Contains(@"<None Update=""THIRD-PARTY-NOTICES.md"">", csproj);
        Assert.Contains(@"<None Include=""licenses\*.txt"" CopyToOutputDirectory=""PreserveNewest"" />", csproj);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "licenses", "LGPL-2.1.txt")));
    }

    [Fact] // [AC-x64-naudio-9] [EDGE-x64-naudio-20]
    public void Readme_States64BitAndPointsToNotices()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        Assert.Contains("64-bit Windows 10 or 11", readme);
        Assert.Contains("THIRD-PARTY-NOTICES.md", readme);
    }

    [Theory] // [AC-x64-naudio-2] [EDGE-x64-naudio-18] caches written by the x86 build still read on x64
    [InlineData("bpm_cache.dat", 128)]
    [InlineData("duration_cache.dat", 213)]
    public void X86Caches_StillRead(string file, int expected)
    {
        string copy = CopyFixture(file);
        var d = new PersistentDictionary<string, int>(copy);
        Assert.Equal(expected, d[@"C:\fixture\a.scd"]);
    }

    [Fact] // [AC-x64-naudio-2] [EDGE-x64-naudio-18]
    public void X86KeyCache_StillReads()
    {
        string copy = CopyFixture("key_cache.dat");
        var d = new PersistentDictionary<string, string>(copy);
        Assert.Equal("Am", d[@"C:\fixture\a.scd"]);
    }

    [SkippableFact] // [AC-x64-naudio-2] [EDGE-x64-naudio-19] HKCU\Software is shared by the 32- and 64-bit views.
    public void Settings_SameIn32And64BitRegistryViews()
    {
        static string[]? Values(RegistryView view)
        {
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = hkcu.OpenSubKey(@"SOFTWARE\ScdConverter");
            return key?.GetValueNames().OrderBy(n => n).Select(n => $"{n}={key.GetValue(n)}").ToArray();
        }
        var x86 = Values(RegistryView.Registry32);
        Skip.If(x86 == null, "no saved settings on this machine");
        Assert.Equal(x86, Values(RegistryView.Registry64));
    }

    private static string CopyFixture(string file)
    {
        string dir = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, file);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "x86-cache", file), copy);
        return copy;
    }
}
