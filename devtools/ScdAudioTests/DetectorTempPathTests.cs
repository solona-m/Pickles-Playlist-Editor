using Xunit;

namespace ScdAudioTests;

public class DetectorTempPathTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Pickles Playlist Editor.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }

    // The detectors open persistent caches in the user's Documents, so they are checked at source level:
    // a shared fixed temp name lets a slow ADPCM transcode in one call be read half-written by another.
    [Theory] // [EDGE-adpcm-songs-14]
    [InlineData("Utils/BPMDetector.cs")]
    [InlineData("Utils/KeyDetector.cs")]
    public void DetectorTempPaths_AreUniquePerCall(string file)
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), file));

        Assert.DoesNotContain("\"temp_extracted.ogg\"", source);
        Assert.DoesNotContain("\"temp_extracted_key.ogg\"", source);
        Assert.Contains("Guid.NewGuid()", source);
    }
}
