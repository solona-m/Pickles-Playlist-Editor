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

    // A restored package of the app: its resolved version and folder.
    internal static (string Version, string Dir) Package(string id)
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "obj", "project.assets.json")));
        string folder = assets.RootElement.GetProperty("packageFolders").EnumerateObject().First().Name;
        var lib = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Single(l => l.Name.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase));
        return (lib.Name.Split('/')[1], Path.Combine(folder, lib.Value.GetProperty("path").GetString()!));
    }

    private static (string Version, string Dir) RuntimePackage() => Package("Microsoft.WindowsAppSDK.Runtime");

    private static string MsixFolder() => Path.Combine(RuntimePackage().Dir, "tools", "MSIX", "win10-x64");

    private static XDocument MsixManifest(string msix)
    {
        using var zip = ZipFile.OpenRead(msix);
        using var stream = zip.GetEntry("AppxManifest.xml")!.Open();
        return XDocument.Load(stream);
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

    [Fact] // [AC-winappsdk2-1] [EDGE-winappsdk2-5] [EDGE-winappsdk2-12]
    public void TestOutput_Bundles_WindowsAppRuntime_OfReferencedVersion()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.dll");
        Assert.True(File.Exists(path), "Microsoft.WindowsAppRuntime.dll not bundled");
        var info = FileVersionInfo.GetVersionInfo(path);
        Assert.Equal(MajorMinor(), $"{info.ProductMajorPart}.{info.ProductMinorPart}");
    }

    // WinUI versions itself (3.x in SDK 2.6), so the bundled copy must be the resolved WinUI package's own file.
    [Fact] // [AC-winappsdk2-1] [EDGE-winappsdk2-5] [EDGE-winappsdk2-12]
    public void TestOutput_Bundles_WinUI_FromResolvedPackage()
    {
        var winui = Package("Microsoft.WindowsAppSDK.WinUI");
        Assert.Equal(WinAppSdkVersion(), Version.Parse(winui.Version));
        string shipped = Path.Combine(winui.Dir, "runtimes-framework", "win-x64", "native", "Microsoft.ui.xaml.dll");
        string bundled = Path.Combine(AppContext.BaseDirectory, "Microsoft.ui.xaml.dll");
        Assert.True(File.Exists(bundled), "Microsoft.ui.xaml.dll not bundled");
        Assert.Equal(File.ReadAllBytes(shipped), File.ReadAllBytes(bundled));
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

    // The framework package of the referenced version: Microsoft's declared OS floor for the binaries we bundle.
    [Fact] // [AC-winappsdk2-3] [EDGE-winappsdk2-5] [EDGE-winappsdk2-12]
    public void FrameworkMsix_OfReferencedVersion_SupportsWindows10()
    {
        var v = WinAppSdkVersion();
        var framework = Directory.GetFiles(MsixFolder(), "Microsoft.WindowsAppRuntime.*.msix")
            .Select(MsixManifest)
            .Select(m => m.Descendants().First(e => e.Name.LocalName == "Identity"))
            .Where(i => Regex.IsMatch(i.Attribute("Name")!.Value, $@"^Microsoft\.WindowsAppRuntime\.{v.Major}(\.{v.Minor})?$"))
            .ToList();
        var identity = Assert.Single(framework);
        Assert.StartsWith($"{v.Major}.{v.Minor}.{v.Build}.", identity.Attribute("Version")!.Value);
        var builds = MinVersionBuilds(identity.Document!);
        Assert.NotEmpty(builds);
        Assert.All(builds, b => Assert.True(b < 22000, $"framework package needs build {b}"));
    }

    [Fact] // [AC-winappsdk2-3] [EDGE-winappsdk2-7] [EDGE-winappsdk2-8] [EDGE-winappsdk2-12]
    public void RuntimePackage_AllMsix_SupportWindows10()
    {
        var msix = Directory.GetFiles(MsixFolder(), "Microsoft.WindowsAppRuntime*.msix");
        Assert.NotEmpty(msix);
        foreach (var file in msix)
        {
            var builds = MinVersionBuilds(MsixManifest(file));
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
        // The header sits between #if/#else lines (renamed XamlGeneratedMain since SDK 2.3), so start at the next brace.
        var start = Regex.Match(generated, @"static\s+void\s+(?:XamlGenerated)?Main\s*\([^)]*\)");
        Assert.True(start.Success, "no generated main in App.g.i.cs");
        int depth = 0, end = generated.IndexOf('{', start.Index);
        for (; end < generated.Length; end++)
        {
            if (generated[end] == '{') depth++;
            else if (generated[end] == '}' && --depth == 0) break;
        }
        string body = generated[start.Index..end];
        body = body[(body.IndexOf('{'))..];
        // Qualified (X.Y) and bare calls; "new T(" is a construction, checked as a call to T.
        var calls = Regex.Matches(body, @"(?<![\w.])((?:\w+\.)*\w+)\s*\(").Select(m => m.Groups[1].Value)
            .Select(c => c.Contains('.') ? string.Join('.', c.Split('.')[^2..]) : c).Distinct().ToList();
        Assert.NotEmpty(calls);
        string program = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs")));
        foreach (var call in calls)
        {
            if (call == "App.XamlGeneratedCreateApplicationInstance")
            {
                // Both generated layers must only construct App, which Program.Main does itself.
                string wrapper = StripComments(generated);
                Assert.Equal("_XamlGeneratedCreateApplicationInstance();", Body(wrapper, @"void\s+XamlGeneratedCreateApplicationInstance"));
                string app = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "obj", BuildTail(), "ui", "App.g.cs")));
                Assert.Equal("new App();", Body(app, @"void\s+_XamlGeneratedCreateApplicationInstance"));
                Assert.Matches(@"new\s+App\s*\(\s*\)", program);
            }
            else Assert.Matches($@"(?<!\w){Regex.Escape(call)}\s*\(", program);
        }
    }

    private static string StripComments(string code) =>
        Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//.*", "");

    private static string Body(string code, string header)
    {
        var m = Regex.Match(code, header + @"\s*\(\s*\)\s*\{([^}]*)\}");
        Assert.True(m.Success, "no generated body for " + header);
        return Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
    }

    [Fact] // [EDGE-winappsdk2-6]
    public void WindowsSdkPackageVersion_SameInAppAndTests()
    {
        static string? Pin(string text) =>
            Regex.Match(text, @"<WindowsSdkPackageVersion>([^<]+)</WindowsSdkPackageVersion>") is { Success: true } m ? m.Groups[1].Value : null;
        Assert.Equal(Pin(AppCsproj()), Pin(File.ReadAllText(Path.Combine(RepoRoot(), "devtools", "ScdAudioTests", "ScdAudioTests.csproj"))));
    }
}
