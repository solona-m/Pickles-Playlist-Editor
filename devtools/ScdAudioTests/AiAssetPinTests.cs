using System.Text.Json;
using System.Text.RegularExpressions;
using Pickles_Playlist_Editor.Tools;
using Xunit;

namespace ScdAudioTests;

public class AiAssetPinTests
{
    private static string Root() => RuntimeTargetTests.RepoRoot();

    internal static JsonElement Manifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "devtools", "aiscan", "releases.json"))).RootElement;

    // Copy of the Proteus worker route (E:\repos\Proteus\worker\src\index.js, "/pickles/"); keep them in step.
    private static readonly Regex s_workerRoute = new(@"^\/pickles\/(aiscan-v[0-9]{1,6})\/([A-Za-z0-9_.-]{1,80}\.(?:exe|onnx|data))$");

    [Fact] // [AC-ai-assets-download-5] [EDGE-ai-assets-download-26] [EDGE-ai-assets-download-30]
    public void Pins_MatchReleasesJson()
    {
        var fromJson = Manifest().EnumerateObject()
            .SelectMany(t => t.Value.EnumerateArray().Select(e => new AiAsset(
                e.GetProperty("name").GetString()!, t.Name, e.GetProperty("bytes").GetInt64(), e.GetProperty("sha256").GetString()!)))
            .ToList();
        Assert.Equal(fromJson, AiAssets.Files);
        foreach (var a in AiAssets.Files)
        {
            Assert.Matches("^[0-9a-f]{64}$", a.Sha256);
            Assert.Matches(@"^aiscan-v\d+\z", a.Tag);
            Assert.Matches(@"^[A-Za-z0-9_.-]{1,80}\.(exe|onnx|data)$", a.Name);
            Assert.InRange(a.Bytes, 1, 512L * 1024 * 1024 - 1); // Cloudflare's cacheable object limit
            Assert.Matches(s_workerRoute, $"/pickles/{a.Tag}/{a.Name}");
        }
    }

    [Fact] // [EDGE-ai-assets-download-25]
    public void OnnxAndData_ShareTag()
    {
        var onnx = AiAssets.Files.Single(f => f.Name.EndsWith(".onnx"));
        Assert.Equal(onnx.Tag, AiAssets.Files.Single(f => f.Name == onnx.Name + ".data").Tag);
    }

    [Fact] // [AC-ai-assets-download-5]
    public void BaseUrls_MirrorThenGitHub()
    {
        Assert.Equal(new[]
        {
            "https://dl.solona.info/pickles/aiscan-v1/",
            "https://github.com/solona-m/Pickles-Playlist-Editor/releases/download/aiscan-v1/",
        }, AiAssets.BaseUrls("aiscan-v1"));
        Assert.Matches(s_workerRoute, new Uri(AiAssets.BaseUrls("aiscan-v1")[0] + "ffmpeg.exe").AbsolutePath);
    }

    [Fact] // [AC-ai-assets-download-8]
    public void DefaultRoot_IsLocalAppDataSiblingOfCurrent()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicklesPlaylistEditor", "aiscan");
        Assert.Equal(expected, AiAssets.DefaultRoot);
        Assert.DoesNotContain(Path.DirectorySeparatorChar + "current" + Path.DirectorySeparatorChar, AiAssets.DefaultRoot + Path.DirectorySeparatorChar);
    }

    [Fact] // [AC-ai-assets-download-8]
    public void NoAppCodeCallsTheDownloader()
    {
        string root = Root();
        var own = new[] { "AiAssetDownloadService.cs", "PinnedFileDownloader.cs", "AiAssets.cs" };
        var callers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(Path.GetRelativePath(root, f), @"^(devtools|bin|obj|Releases[^\\/]*)[\\/]", RegexOptions.IgnoreCase))
            .Where(f => !own.Contains(Path.GetFileName(f)))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\b(AiAssetDownloadService|PinnedFileDownloader)\b"))
            .ToList();
        Assert.Empty(callers);
    }

    [Fact] // [AC-ai-assets-download-2]
    public void Manifest_FfmpegEntryIsPinnedGyan902()
    {
        var e = Manifest().GetProperty("aiscan-v1").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "ffmpeg.exe");
        var s = e.GetProperty("source");
        Assert.Equal("gyan", s.GetProperty("kind").GetString());
        Assert.Equal("https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-full_build.7z", s.GetProperty("url").GetString());
        Assert.Equal(168971636, s.GetProperty("archiveBytes").GetInt64());
        Assert.Equal("f0e46253c70dfe902bac915dfb4224f0cf1b7c6eeab9da2ccc9a5581f9a71b13", s.GetProperty("archiveSha256").GetString());
        Assert.Equal("ffmpeg-9.0.2-full_build/bin/ffmpeg.exe", s.GetProperty("path").GetString());
        Assert.Equal(227823104, e.GetProperty("bytes").GetInt64());
        Assert.Equal("589e50b766d251afdf181dd664d40bd94407e200b019989fd7468c7d118a28d0", e.GetProperty("sha256").GetString());
        Assert.Equal("9.0.2-full_build-www.gyan.dev", s.GetProperty("version").GetString());
        Assert.Equal("https://github.com/FFmpeg/FFmpeg/commit/946fcce07b", s.GetProperty("sourceUrl").GetString());
    }
}
