using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pickles_Playlist_Editor.Tools;
using Xunit;

namespace ScdAudioTests;

public class WindowsAppSdkTargetTests
{
    private static string RepoRoot() => RuntimeTargetTests.RepoRoot();

    // <config>\<tfm>\<rid>\ of this test run, so the obj reads match the build that was tested.
    private static string BuildTail()
    {
        string baseDir = AppContext.BaseDirectory;
        int i = baseDir.LastIndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        Assert.True(i >= 0, "test output is not under bin: " + baseDir);
        return baseDir[(i + 5)..];
    }

    private static string AppCsproj() => File.ReadAllText(Path.Combine(RepoRoot(), "Pickles Playlist Editor.csproj"));

    private static Version WinAppSdkVersion()
    {
        var m = Regex.Matches(AppCsproj(), @"Include=""Microsoft\.WindowsAppSDK""\s+Version=""([^""]+)""");
        Assert.Single(m);
        return Version.Parse(m[0].Groups[1].Value);
    }

    private static string MajorMinor() => $"{WinAppSdkVersion().Major}.{WinAppSdkVersion().Minor}";

    private static (string Version, string Dir) RuntimePackage()
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "obj", "project.assets.json")));
        string folder = assets.RootElement.GetProperty("packageFolders").EnumerateObject().First().Name;
        var lib = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Single(l => l.Name.StartsWith("Microsoft.WindowsAppSDK.Runtime/", StringComparison.OrdinalIgnoreCase));
        return (lib.Name.Split('/')[1], Path.Combine(folder, lib.Value.GetProperty("path").GetString()!));
    }

    private static List<int> MinVersionBuilds(XDocument manifest) =>
        manifest.Descendants().Where(e => e.Name.LocalName == "TargetDeviceFamily")
            .Select(e => Version.Parse(e.Attribute("MinVersion")!.Value).Build).ToList();

    [Fact] // [AC-winappsdk2-1] [EDGE-winappsdk2-8]
    public void App_References_WindowsAppSdk_2_x_AtLeast_2_6_4()
    {
        var v = WinAppSdkVersion();
        Assert.Equal(2, v.Major);
        Assert.True(v >= new Version(2, 6, 4), $"WindowsAppSDK {v} is below 2.6.4");
    }

    [Fact] // [AC-winappsdk2-1]
    public void Restore_Resolved_WindowsAppSdkRuntime_MatchesCsproj() =>
        Assert.Equal(WinAppSdkVersion(), Version.Parse(RuntimePackage().Version));

    [Theory] // [AC-winappsdk2-1] [EDGE-winappsdk2-5] [EDGE-winappsdk2-12]
    [InlineData("Microsoft.WindowsAppRuntime.dll")]
    [InlineData("Microsoft.ui.xaml.dll")]
    public void TestOutput_Bundles_WindowsAppRuntime_OfReferencedVersion(string dll)
    {
        string path = Path.Combine(AppContext.BaseDirectory, dll);
        Assert.True(File.Exists(path), dll + " not bundled");
        var info = FileVersionInfo.GetVersionInfo(path);
        Assert.Equal(MajorMinor(), $"{info.ProductMajorPart}.{info.ProductMinorPart}");
    }

    [Fact] // [AC-winappsdk2-2] [EDGE-winappsdk2-9] [EDGE-winappsdk2-10]
    public void SelfContained_Kept_NoRedistInstall()
    {
        string root = RepoRoot();
        var projects = new[] { "Pickles Playlist Editor.csproj", "devtools/ScdAudioTests/ScdAudioTests.csproj" }
            .Select(f => Path.Combine(root, f)).ToList();
        foreach (var csproj in projects)
        {
            string text = File.ReadAllText(csproj);
            Assert.Contains("<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>", text);
            Assert.Contains("<WindowsPackageType>None</WindowsPackageType>", text);
        }
        var workflows = Directory.GetFiles(Path.Combine(root, ".github", "workflows"))
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
        foreach (var file in projects.Concat(workflows))
        {
            string text = File.ReadAllText(file);
            foreach (var installer in new[] { "WindowsAppRuntimeInstall", "WindowsAppRuntime.Redist", "Add-AppxPackage" })
                Assert.False(text.Contains(installer, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} mentions {installer}");
        }
    }

    [Fact] // [AC-winappsdk2-3] [EDGE-winappsdk2-7]
    public void AppAssembly_SupportedOS_IsWindows10_19041() =>
        Assert.Equal("Windows10.0.19041.0", typeof(ScdOggExtractor).Assembly.GetCustomAttribute<SupportedOSPlatformAttribute>()?.PlatformName);

    // The runtime manifest the build extracted, i.e. what actually ships next to the exe.
    [Fact] // [AC-winappsdk2-3] [EDGE-winappsdk2-5] [EDGE-winappsdk2-12]
    public void BundledRuntime_ExtractedManifest_SupportsWindows10()
    {
        string path = Path.Combine(RepoRoot(), "obj", BuildTail(), "MsixContent", "AppxManifest.xml");
        Assert.True(File.Exists(path), "no extracted runtime manifest at " + path);
        var manifest = XDocument.Load(path);
        string name = manifest.Descendants().First(e => e.Name.LocalName == "Identity").Attribute("Name")!.Value;
        Assert.Matches($@"^Microsoft\.WindowsAppRuntime\.{Regex.Escape(MajorMinor())}(-.+)?$", name);
        var builds = MinVersionBuilds(manifest);
        Assert.NotEmpty(builds);
        Assert.All(builds, b => Assert.True(b < 22000, $"bundled runtime needs build {b}"));
    }

    [Fact] // [AC-winappsdk2-3] [EDGE-winappsdk2-7] [EDGE-winappsdk2-8] [EDGE-winappsdk2-12]
    public void RuntimePackage_AllMsix_SupportWindows10()
    {
        var msix = Directory.GetFiles(Path.Combine(RuntimePackage().Dir, "tools", "MSIX", "win10-x86"), "Microsoft.WindowsAppRuntime*.msix");
        Assert.NotEmpty(msix);
        foreach (var file in msix)
        {
            using var zip = ZipFile.OpenRead(file);
            using var stream = zip.GetEntry("AppxManifest.xml")!.Open();
            var builds = MinVersionBuilds(XDocument.Load(stream));
            Assert.NotEmpty(builds);
            Assert.All(builds, b => Assert.True(b < 22000, $"{Path.GetFileName(file)} needs build {b}"));
        }
    }

    [Fact] // [AC-winappsdk2-4] [EDGE-winappsdk2-2]
    public void AppEntryPoint_IsOwnProgramMain()
    {
        var entry = typeof(ScdOggExtractor).Assembly.EntryPoint;
        Assert.NotNull(entry);
        Assert.Equal("Pickles_Playlist_Editor.Program", entry.DeclaringType?.FullName);
        Assert.Equal("Main", entry.Name);
    }

    [Fact] // [AC-winappsdk2-4] [EDGE-winappsdk2-2]
    public void Csproj_Keeps_DisableXamlGeneratedMain() =>
        Assert.Matches(@"<DefineConstants>[^<]*DISABLE_XAML_GENERATED_MAIN[^<]*</DefineConstants>", AppCsproj());

    // Program.Main replaces the generated main, so it must make every call the generated one makes.
    [Fact] // [AC-winappsdk2-4] [EDGE-winappsdk2-14]
    public void ProgramMain_HasEveryGeneratedMainCall()
    {
        string generated = File.ReadAllText(Path.Combine(RepoRoot(), "obj", BuildTail(), "ui", "App.g.i.cs"));
        var start = Regex.Match(generated, @"static\s+void\s+(?:XamlGenerated)?Main\s*\([^)]*\)\s*\{");
        Assert.True(start.Success, "no generated main in App.g.i.cs");
        int depth = 0, end = start.Index + start.Length - 1;
        for (; end < generated.Length; end++)
        {
            if (generated[end] == '{') depth++;
            else if (generated[end] == '}' && --depth == 0) break;
        }
        string body = generated[start.Index..end];
        var calls = Regex.Matches(body, @"global::(?:\w+\.)*?(\w+\.\w+)\s*\(").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(calls);
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs"));
        Assert.All(calls, c => Assert.Contains(c, program));
    }

    [Fact] // [EDGE-winappsdk2-6]
    public void WindowsSdkPackageVersion_SameInAppAndTests()
    {
        static string? Pin(string text) =>
            Regex.Match(text, @"<WindowsSdkPackageVersion>([^<]+)</WindowsSdkPackageVersion>") is { Success: true } m ? m.Groups[1].Value : null;
        Assert.Equal(Pin(AppCsproj()), Pin(File.ReadAllText(Path.Combine(RepoRoot(), "devtools", "ScdAudioTests", "ScdAudioTests.csproj"))));
    }
}
