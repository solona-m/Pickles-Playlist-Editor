using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

namespace ScdAudioTests;

public class FfmpegBundleTests
{
    private const string EssentialsSha = "55a4c22d762979e6904af754a2488b63dd02c83364e3d401c4f50a811d4e3dd1";

    private static string Root() => RuntimeTargetTests.RepoRoot();

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    internal static string FirstLine(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        string line = p.StandardOutput.ReadLine() ?? "";
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return line;
    }

    [SkippableFact] // [AC-ai-assets-download-1]
    public void BundledFfmpeg_IsTheTrackedEssentialsBuild()
    {
        Assert.Equal(EssentialsSha, Sha(Path.Combine(Root(), "ffmpeg.exe")));
        Assert.Equal(EssentialsSha, Sha(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")));
        Assert.Contains("essentials_build", FirstLine(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"), "-hide_banner -version"));

        string csproj = File.ReadAllText(Path.Combine(Root(), "Pickles Playlist Editor.csproj"));
        Assert.Contains("<None Update=\"ffmpeg.exe\">", csproj);
        Assert.DoesNotContain("DownloadFile", csproj);
        Assert.DoesNotContain("<Exec", csproj);
        Assert.DoesNotContain(".7z", csproj);

        string git;
        try { git = Run("git", $"-C \"{Root()}\" ls-files ffmpeg.exe"); }
        catch (System.ComponentModel.Win32Exception) { throw new SkipException("git not available"); }
        Assert.Equal("ffmpeg.exe", git.Trim());
    }

    private static string Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        string s = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return s;
    }

    [Fact] // [AC-ai-assets-download-10]
    public void Notices_CoverBothFfmpegBuilds()
    {
        string notices = File.ReadAllText(Path.Combine(Root(), "THIRD-PARTY-NOTICES.md"));
        string bundled = FirstLine(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"), "-hide_banner -version");
        string build = bundled.Split(' ')[2]; // "ffmpeg version <build> Copyright ..."
        Assert.Contains(build, notices);
        foreach (var s in new[] { "9.0.2-full_build-www.gyan.dev", "commit/f5eb11a71d", "commit/946fcce07b", "GPL", "licenses/FFmpeg-GPL-3.0.txt", "gyan.dev" })
            Assert.Contains(s, notices);
    }

    [Fact] // [AC-ai-assets-download-10]
    public void FfmpegLicense_IsGplv3TextAndShips()
    {
        string lic = Path.Combine(Root(), "licenses", "FFmpeg-GPL-3.0.txt");
        Assert.Equal(35147, new FileInfo(lic).Length);
        Assert.Equal("8ceb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903", Sha(lic));
        string text = File.ReadAllText(lic);
        Assert.StartsWith("GNU GENERAL PUBLIC LICENSE", text.TrimStart());
        Assert.Contains("Version 3, 29 June 2007", text);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "licenses", "FFmpeg-GPL-3.0.txt")));
    }
}
