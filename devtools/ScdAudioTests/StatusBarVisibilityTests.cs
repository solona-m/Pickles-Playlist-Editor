using System.Text.RegularExpressions;
using Xunit;

namespace ScdAudioTests;

public class StatusBarVisibilityTests
{
    private static string Root() => RuntimeTargetTests.RepoRoot();
    private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel)).Replace("\r\n", "\n");

    private static string Method(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + signature);
        int open = src.IndexOf('{', i), depth = 0;
        for (int j = open; j < src.Length; j++)
        {
            if (src[j] == '{') depth++;
            else if (src[j] == '}' && --depth == 0) return src[i..(j + 1)];
        }
        return src[i..];
    }

    private const string Write = @"(?<![\w.])(ProgressLabel\.Text|ProgressBar1\.Value)\s*=(?!=)";

    [Fact]
    public void StatusGrid_IsNamed_AndStartsCollapsed()
    {
        string xaml = Src("ui/MainWindow.xaml");
        Assert.Contains("<Grid x:Name=\"StatusBarGrid\" Grid.Row=\"4\" Margin=\"4,2,4,4\" Visibility=\"Collapsed\">", xaml);
        int grid = xaml.IndexOf("x:Name=\"StatusBarGrid\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("</Grid>", grid, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProgressBar1\"", xaml[grid..end]);
        Assert.Contains("x:Name=\"ProgressLabel\"", xaml[grid..end]);
    }

    [Fact]
    public void Helper_CollapsesOnEmptyTextAndZeroValue()
    {
        string helper = Method(Src("ui/MainWindowCore.cs"), "private void UpdateStatusBarVisibility()");
        Assert.Contains("string.IsNullOrEmpty(ProgressLabel.Text) && ProgressBar1.Value == 0", helper);
        Assert.Contains("StatusBarGrid.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;", helper);
    }

    [Theory]
    [InlineData("ui/MainWindowCore.cs", "public void SetProgressBarPercent(int percent)")]
    [InlineData("ui/MainWindowCore.cs", "public void SetProgressBarText(string text)")]
    [InlineData("ui/MainWindowCore.cs", "public void ClearProgressDisplay()")]
    [InlineData("ui/MainWindow.Stats.cs", "private void RenderBackgroundProgress()")]
    public void Writer_CallsHelper_AfterLastWrite(string file, string signature)
    {
        string body = Method(Src(file), signature);
        var writes = Regex.Matches(body, Write);
        Assert.NotEmpty(writes);
        int lastWrite = writes[^1].Index;
        Assert.True(body.IndexOf("UpdateStatusBarVisibility();", lastWrite, StringComparison.Ordinal) > lastWrite,
            signature + " must call UpdateStatusBarVisibility() after writing the status bar");
    }

    [Fact]
    public void EveryWriter_IsPinned()
    {
        // Only the four pinned methods may write the main window's status bar controls.
        foreach (var f in Directory.GetFiles(Path.Combine(Root(), "ui"), "*.cs"))
        {
            string rel = Path.GetRelativePath(Root(), f).Replace('\\', '/');
            if (rel is "ui/MainWindowCore.cs" or "ui/MainWindow.Stats.cs" or "ui/YouTubeDownloadDialog.xaml.cs") continue;
            Assert.True(Regex.Matches(File.ReadAllText(f), Write).Count == 0, rel + " writes the status bar; route it through UpdateStatusBarVisibility");
        }
        Assert.Equal(4, Regex.Matches(Src("ui/MainWindowCore.cs"), Write).Count);
        Assert.Equal(2, Regex.Matches(Src("ui/MainWindow.Stats.cs"), Write).Count);
    }
}
