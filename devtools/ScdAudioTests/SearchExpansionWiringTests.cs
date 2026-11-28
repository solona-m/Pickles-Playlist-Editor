using System.Text.RegularExpressions;
using Xunit;

namespace ScdAudioTests;

public class SearchExpansionWiringTests
{
    private static string Src(string rel) => File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), rel));

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

    private static int At(string s, string needle, int from = 0)
    {
        int i = s.IndexOf(needle, from, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + needle);
        return i;
    }

    private static int Count(string s, string needle) => Regex.Matches(s, Regex.Escape(needle)).Count;

    [Fact] // [AC-search-expand-restore-1] [AC-search-expand-restore-2]
    public void TextChanged_CallsMemoryBeforeLoad()
    {
        string body = Method(Src("ui/MainWindow.cs"), "private void SearchTextBox_TextChanged");
        Assert.True(At(body, "_searchExpansion.OnFilterChanged(filter, _playlistExpandedStates, Playlists.Keys);")
            < At(body, "LoadPlaylists(filter);"));
    }

    [Fact] // [EDGE-search-expand-restore-10]
    public void OnFilterChanged_OnlyCalledFromSearchBox()
    {
        string ui = Path.Combine(RuntimeTargetTests.RepoRoot(), "ui");
        int total = Directory.GetFiles(ui, "*.cs").Sum(f => Count(File.ReadAllText(f), "_searchExpansion.OnFilterChanged("));
        Assert.Equal(1, total);
        Assert.DoesNotContain("_searchExpansion", Method(Src("ui/MainWindowCore.cs"), "private void LoadPlaylists(string filter, string? forceExpandedPlaylistName)"));
    }

    [Fact] // [AC-search-expand-restore-1]
    public void Field_Declared()
    {
        Assert.Contains("private readonly SearchExpansionMemory _searchExpansion = new();", Src("ui/MainWindowCore.cs"));
    }

    [Fact] // [AC-search-expand-restore-5] [EDGE-search-expand-restore-8]
    public void Rename_RekeysOnlyOnSuccessfulRename()
    {
        string body = Method(Src("ui/MainWindow.cs"), "private async Task RenamePlaylistAsync");
        const string call = "if (playlist.Rename(newName)) _searchExpansion.OnRenamed(currentName, newName);";
        Assert.Equal(1, Count(body, call));
        Assert.True(At(body, "try") < At(body, call));
        Assert.True(At(body, call) < At(body, "LoadPlaylistsAndExpand(newName)"));
        Assert.DoesNotMatch(new Regex(@"^\s*playlist\.Rename\(newName\);", RegexOptions.Multiline), body);
    }

    [Fact] // [AC-search-expand-restore-4] [EDGE-search-expand-restore-14]
    public void CreatePaths_ForgetName()
    {
        string add = Method(Src("ui/MainWindowCore.cs"), "public void AddCreatedPlaylist(string playlistName, bool existed)");
        Assert.True(At(add, "if (!existed) _searchExpansion.Forget(playlistName);") < At(add, "if (IsFilterActive)"));
        Assert.Equal(1, Count(add, "_searchExpansion.Forget("));

        string yt = Src("ui/MainWindow.YouTube.cs");
        Assert.True(At(yt, "Playlist.Create(playlistName") < At(yt, "_searchExpansion.Forget(playlistName);"));
        Assert.True(At(yt, "Playlist.Create(targetPlaylist") < At(yt, "_searchExpansion.Forget(targetPlaylist);"));
        Assert.Equal(2, Count(yt, "_searchExpansion.Forget("));
    }

    [Fact] // [EDGE-search-expand-restore-14]
    public void NewPlaylistDialog_PassesExistedFlag()
    {
        string body = Method(Src("ui/NewPlaylistDialog.xaml.cs"), "private void GoButton_Click");
        Assert.True(At(body, "existed = Playlist.GetAll().ContainsKey(playlistName);") < At(body, "Playlist.Create(playlistName"));
        Assert.True(At(body, "Playlist.Create(") < At(body, "AddCreatedPlaylist(playlistName, existed)"));
        Assert.DoesNotContain("AddCreatedPlaylist(playlistName)", body);
    }

    [Fact] // guard: move targets fall back to their pre-search state (option 1)
    public void MovePaths_DoNotTouchMemory()
    {
        Assert.DoesNotContain("_searchExpansion", Src("ui/MainWindow.ContextMenu.cs"));
        Assert.DoesNotContain("_searchExpansion", Src("ui/MainWindow.DragDrop.cs"));
    }
}
