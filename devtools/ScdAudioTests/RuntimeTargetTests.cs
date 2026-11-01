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

    [Theory] // [AC-dotnet10-upgrade-4]
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

    [Theory] // [AC-dotnet10-upgrade-5]
    [InlineData(".github/workflows/release.yml")]
    [InlineData(".github/workflows/testing-release.yml")]
    [InlineData(".github/workflows/dependabot-automerge.yml")]
    public void Workflows_InstallDotNet10(string file)
    {
        var values = Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot(), file)), @"dotnet-version:\s*(\S+)")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.Equal("10.0.x", v));
    }
}
