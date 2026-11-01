using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Pickles_Playlist_Editor.Tools;
using Xunit;

namespace ScdAudioTests;

public class RuntimeTargetTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Pickles Playlist Editor.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }

    [Fact] // [AC-dotnet10-upgrade-1]
    public void TestHost_RunsOnDotNet10() => Assert.Equal(10, Environment.Version.Major);

    [Fact] // [AC-dotnet10-upgrade-1]
    public void AppAssembly_TargetsNet10() =>
        Assert.Equal(".NETCoreApp,Version=v10.0",
            typeof(ScdOggExtractor).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);

    [Theory] // [AC-dotnet10-upgrade-4] [EDGE-dotnet10-upgrade-10]
    [InlineData("Pickles Playlist Editor.csproj", "net10.0-windows10.0.19041.0")]
    [InlineData("devtools/AnimHarness/AnimHarness.csproj", "net10.0-windows10.0.19041.0")]
    [InlineData("devtools/ScdAudioTests/ScdAudioTests.csproj", "net10.0-windows10.0.19041.0")]
    [InlineData("devtools/TexHarness/TexHarness.csproj", "net10.0")]
    [InlineData("devtools/TmbHarness/TmbHarness.csproj", "net10.0")]
    public void Projects_TargetNet10(string file, string expected)
    {
        var values = Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot(), file)), @"<TargetFrameworks?>([^<]*)</TargetFrameworks?>")
            .Select(m => m.Groups[1].Value.Trim()).ToList();
        Assert.Equal(new[] { expected }, values);
    }

    // Every workflow that installs .NET, not a fixed list, so a new one can't bring back an old version.
    public static TheoryData<string> Workflows() =>
        new(Directory.GetFiles(Path.Combine(RepoRoot(), ".github", "workflows"))
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => File.ReadAllText(f).Contains("dotnet-version:")).Select(Path.GetFileName)!);

    [Fact] // [AC-dotnet10-upgrade-5]
    public void Workflows_SomeInstallDotNet() => Assert.True(Workflows().Count >= 3);

    [Theory] // [AC-dotnet10-upgrade-5] [EDGE-dotnet10-upgrade-10]
    [MemberData(nameof(Workflows))]
    public void Workflows_InstallDotNet10(string file)
    {
        var values = Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", file)), @"dotnet-version:\s*(\S+)")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.Equal("10.0.x", v));
    }

    [Fact] // [AC-dotnet10-upgrade-9]
    public void Readme_NeedsNoSeparateRuntime()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        Assert.Contains("no separate .NET install is required", readme);
        Assert.DoesNotContain("Desktop Runtime", readme);
        Assert.DoesNotContain("aka.ms/dotnet", readme);
    }
}
