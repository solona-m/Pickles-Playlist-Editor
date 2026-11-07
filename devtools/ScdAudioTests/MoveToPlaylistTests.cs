using System.Xml.Linq;
using Pickles_Playlist_Editor;
using Xunit;

namespace ScdAudioTests;

public class MoveToPlaylistTests
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

    private static int At(string s, string needle)
    {
        int i = s.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + needle);
        return i;
    }

    private static Option Song(string name, string rel = "")
    {
        var o = new Option { Name = name };
        if (rel.Length > 0) o.Files["sound/bpmloop.scd"] = rel;
        return o;
    }

    private static Playlist Pl(string name, params Option[] songs) => new() { Name = name, Options = songs.ToList() };

    // Fake of MoveSongAcrossPlaylists: insert into target, then remove from source.
    private sealed class Mover
    {
        public List<int> Indices { get; } = new();
        public HashSet<string> Throw { get; } = new();
        public HashSet<string> ThrowAfterInsert { get; } = new();
        public int Saves;
        public bool SaveThrows;

        public void Move(Playlist s, Playlist t, Option song, int i)
        {
            Indices.Add(i);
            if (Throw.Contains(song.Name)) throw new InvalidOperationException("boom");
            t.Options.Insert(Math.Clamp(i, 0, t.Options.Count), song);
            if (ThrowAfterInsert.Contains(song.Name)) throw new InvalidOperationException("half");
            s.Options.Remove(song);
        }

        public void Save(Playlist p)
        {
            Saves++;
            if (SaveThrows) throw new InvalidOperationException("save");
        }
    }

    private static SongMove.BatchResult Run(Playlist target, Option? anchor, InTargetPolicy policy, Mover m,
        params (Playlist, Option)[] moves) =>
        SongMove.Run(target, moves, anchor, policy, m.Move, m.Save);

    private static string Names(Playlist p) => string.Join(",", p.Options.Select(o => o.Name));
    private static string Names(IEnumerable<Option> o) => string.Join(",", o.Select(x => x.Name));

    // ── Run: menu (StayPut, append) ──

    [Fact] // [AC-move-to-playlist-2] [AC-move-to-playlist-4]
    public void Run_AppendStayPut_InTreeOrder()
    {
        Option a = Song("A"), b = Song("B");
        var s = Pl("S", Song("Off"), a, b);
        var t = Pl("T", Song("Off"), Song("X"));
        var m = new Mover();
        var r = Run(t, null, InTargetPolicy.StayPut, m, (s, a), (s, b));
        Assert.Equal(new[] { 2, 3 }, m.Indices);
        Assert.Equal("Off,X,A,B", Names(t));
        Assert.Equal("Off", Names(s));
        Assert.Equal("A,B", Names(r.Landed));
        Assert.Equal("A,B", Names(r.Moved));
        Assert.Equal(0, m.Saves);
    }

    [Fact] // [AC-move-to-playlist-5] [EDGE-move-to-playlist-9]
    public void Run_StayPut_InTargetKeepIndices()
    {
        Option a = Song("A"), b = Song("B"), x = Song("X"), y = Song("Y");
        var t = Pl("T", Song("Off"), a, b);
        var s = Pl("S", Song("Off"), x, y);
        var m = new Mover();
        var r = Run(t, null, InTargetPolicy.StayPut, m, (t, b), (s, x), (t, a), (s, y));
        Assert.Equal("Off,A,B,X,Y", Names(t));
        Assert.Equal("B,X,A,Y", Names(r.Landed));
        Assert.Equal("X,Y", Names(r.Moved));
        Assert.Equal(0, m.Saves);
    }

    // ── Run: drag (Reorder) keeps today's final order ──

    [Fact] // [AC-move-to-playlist-8]
    public void Run_Reorder_TargetSongAboveAnchor()
    {
        Option a = Song("A"), c = Song("C"), x = Song("X");
        var t = Pl("T", Song("Off"), a, Song("B"), c, Song("D"));
        var s = Pl("S", Song("Off"), x);
        var m = new Mover();
        var r = Run(t, c, InTargetPolicy.Reorder, m, (t, a), (s, x));
        Assert.Equal("Off,B,C,A,X,D", Names(t));
        Assert.Equal(new[] { 4 }, m.Indices);
        Assert.Equal(1, m.Saves);
        Assert.Equal("A,X", Names(r.Landed));
    }

    [Fact] // [AC-move-to-playlist-8]
    public void Run_Reorder_TargetSongBelowAnchor()
    {
        Option a = Song("A"), d = Song("D"), x = Song("X");
        var t = Pl("T", Song("Off"), a, Song("B"), Song("C"), d);
        var s = Pl("S", Song("Off"), x);
        var m = new Mover();
        Run(t, a, InTargetPolicy.Reorder, m, (t, d), (s, x));
        Assert.Equal("Off,A,D,X,B,C", Names(t));
        Assert.Equal(new[] { 2 }, m.Indices);
        Assert.Equal(1, m.Saves);
    }

    [Fact] // [AC-move-to-playlist-8]
    public void Run_Reorder_OnlyTargetSongs()
    {
        Option a = Song("A"), b = Song("B"), d = Song("D");
        var t = Pl("T", Song("Off"), a, b, Song("C"), d);
        var m = new Mover();
        Run(t, b, InTargetPolicy.Reorder, m, (t, a), (t, d));
        Assert.Equal("Off,B,A,D,C", Names(t));
        Assert.Empty(m.Indices);
        Assert.Equal(1, m.Saves);
    }

    [Fact] // [AC-move-to-playlist-8] [EDGE-move-to-playlist-7]
    public void Run_Reorder_MidBatchFailureWithAnchor()
    {
        Option a = Song("A"), x = Song("X"), y = Song("Y"), z = Song("Z");
        var t = Pl("T", Song("Off"), a, Song("B"));
        var s = Pl("S", Song("Off"), x, y, z);
        var m = new Mover();
        m.Throw.Add("Y");
        var r = Run(t, a, InTargetPolicy.Reorder, m, (s, x), (s, y), (s, z));
        Assert.Equal(new[] { 2, 3, 3 }, m.Indices);
        Assert.Equal("Off,A,X,Z,B", Names(t));
        Assert.Contains(y, s.Options);
        var e = Assert.Single(r.Errors);
        Assert.Same(s, e.Playlist);
        Assert.Same(y, e.Song);
        Assert.Equal("X,Z", Names(r.Landed));
    }

    [Fact] // [AC-move-to-playlist-8]
    public void Run_Reorder_NullAnchorAppends()
    {
        Option a = Song("A"), x = Song("X");
        var t = Pl("T", Song("Off"), a, Song("B"));
        var s = Pl("S", Song("Off"), x);
        var m = new Mover();
        Run(t, null, InTargetPolicy.Reorder, m, (t, a), (s, x));
        Assert.Equal("Off,B,A,X", Names(t));
    }

    [Fact] // [AC-move-to-playlist-8]
    public void Run_SkipsAnchorMove()
    {
        Option a = Song("A");
        var t = Pl("T", Song("Off"), a);
        var m = new Mover();
        var r = Run(t, a, InTargetPolicy.Reorder, m, (t, a));
        Assert.Empty(r.Landed);
        Assert.Equal(0, m.Saves);
        Assert.Equal("Off,A", Names(t));
    }

    [Fact] // [EDGE-move-to-playlist-17]
    public void Run_Reorder_SaveFailureReported()
    {
        Option a = Song("A");
        var t = Pl("T", Song("Off"), a, Song("B"));
        var m = new Mover { SaveThrows = true };
        var r = Run(t, null, InTargetPolicy.Reorder, m, (t, a));
        var e = Assert.Single(r.Errors);
        Assert.Same(t, e.Playlist);
        Assert.Null(e.Song);
        Assert.Equal("T: save", SongMove.Describe(e, ex => ex.Message));
    }

    [Fact] // [AC-move-to-playlist-7]
    public void Describe_MatchesDragText()
    {
        var s = Pl("S");
        Assert.Equal("S/Song: msg", SongMove.Describe(new SongMove.BatchError(s, Song("Song"), new Exception("msg")), ex => ex.Message));
        Assert.Equal("S: msg", SongMove.Describe(new SongMove.BatchError(s, null, new Exception("msg")), ex => ex.Message));
    }

    [Fact] // [EDGE-move-to-playlist-14]
    public void Run_DestructiveFailure_NextInsertsSameIndex()
    {
        Option x = Song("X"), y = Song("Y");
        var t = Pl("T", Song("Off"));
        var s = Pl("S", Song("Off"), x, y);
        var m = new Mover();
        m.ThrowAfterInsert.Add("X");
        var r = Run(t, null, InTargetPolicy.StayPut, m, (s, x), (s, y));
        Assert.Equal(new[] { 1, 1 }, m.Indices);
        Assert.Contains(x, s.Options);
        Assert.Contains(x, t.Options);
        Assert.Single(r.Errors);
    }

    [Fact] // [EDGE-move-to-playlist-8]
    public void Run_DuplicateNameInTarget_Allowed()
    {
        Option a2 = Song("A");
        var t = Pl("T", Song("Off"), Song("A"));
        var s = Pl("S", Song("Off"), a2);
        var r = Run(t, null, InTargetPolicy.StayPut, new Mover(), (s, a2));
        Assert.Equal("Off,A,A", Names(t));
        Assert.Empty(r.Errors);
    }

    // ── Targets, PickOption, placeholder, hidden count ──

    [Fact] // [AC-move-to-playlist-1] [EDGE-move-to-playlist-12]
    public void Targets_SingleSource_ExcludesSourceAndHidden_KeepsOrder()
    {
        var all = new[] { Pl("P1"), Pl("S"), Pl("V"), Pl("P2") };
        var r = SongMove.Targets(all, new[] { "S", "S" }, p => p.Name == "V");
        Assert.Equal(new[] { "P1", "P2" }, r.Select(p => p.Name));
    }

    [Fact] // [AC-move-to-playlist-5]
    public void Targets_MultiSource_ListsAll()
    {
        var all = new[] { Pl("P1"), Pl("S"), Pl("P2") };
        var r = SongMove.Targets(all, new[] { "S", "P1" }, _ => false);
        Assert.Equal(new[] { "P1", "S", "P2" }, r.Select(p => p.Name));
    }

    [Fact] // [EDGE-move-to-playlist-2]
    public void Targets_OnlySourceOrOnlyHidden_Empty()
    {
        Assert.Empty(SongMove.Targets(new[] { Pl("S") }, new[] { "S" }, _ => false));
        Assert.Empty(SongMove.Targets(new[] { Pl("S"), Pl("V") }, new[] { "S" }, p => p.Name == "V"));
    }

    [Fact] // [EDGE-move-to-playlist-4]
    public void PickOption_Table()
    {
        Option off = Song("Off"), a1 = Song("A", "s/a1.scd"), a2 = Song("A", "s/a2.scd"), b = Song("B", "s/b.scd");
        var opts = new List<Option> { off, a1, a2, b };
        var none = new HashSet<Option>();
        // Full playlist shown: by position.
        Assert.Same(a2, SongMove.PickOption(opts, 4, 2, "A", "s/a2.scd", none));
        // Filtered: name + scd.
        Assert.Same(a2, SongMove.PickOption(opts, 1, 0, "A", "S/A2.SCD", none));
        // Name-only fallback when scd is empty or doesn't match.
        Assert.Same(a1, SongMove.PickOption(opts, 1, 0, "A", "", none));
        Assert.Same(a1, SongMove.PickOption(opts, 1, 0, "A", "s/zz.scd", none));
        // Out-of-range index falls back.
        Assert.Same(b, SongMove.PickOption(opts, 4, 9, "B", "s/b.scd", none));
        Assert.Null(SongMove.PickOption(opts, 1, 0, "Nope", "", none));
        // Two identical rows resolve to distinct options.
        Option d1 = Song("D", "s/d.scd"), d2 = Song("D", "s/d.scd");
        var dup = new List<Option> { off, d1, d2 };
        Assert.Same(d2, SongMove.PickOption(dup, 2, 1, "D", "s/d.scd", new HashSet<Option> { d1 }));
    }

    [Fact] // [EDGE-move-to-playlist-1]
    public void IsPlaceholder_Table()
    {
        Assert.True(SongMove.IsPlaceholder("Off"));
        Assert.True(SongMove.IsPlaceholder("off"));
        Assert.True(SongMove.IsPlaceholder("OFF"));
        Assert.False(SongMove.IsPlaceholder("Office"));
        Assert.False(SongMove.IsPlaceholder(""));
        Assert.False(SongMove.IsPlaceholder(null));
    }

    [Fact] // [EDGE-move-to-playlist-6] [EDGE-move-to-playlist-15]
    public void CountHiddenBySearch_Table()
    {
        var songs = new[] { Song("A"), Song("chill out") };
        Assert.Equal(0, SongMove.CountHiddenBySearch("rock", "Rock Mix", songs, _ => null));
        Assert.Equal(1, SongMove.CountHiddenBySearch("chill", "Party", songs, _ => null));
    }

    // ── Wiring ──

    private static string Menu() => Src("ui/MainWindow.ContextMenu.cs");
    private static string MoveMethod() => Method(Menu(), "private async Task MoveSongsToPlaylistAsync");

    [Fact] // [AC-move-to-playlist-8]
    public void Drag_UsesSharedBatch()
    {
        string drag = Src("ui/MainWindow.DragDrop.cs");
        string m = Method(drag, "private async Task HandleInternalReorderAsync");
        Assert.Contains("SongMove.Run(targetPlaylist, moves, anchor, InTargetPolicy.Reorder", m);
        Assert.Contains("MoveSongAcrossPlaylists(", m);
        Assert.Contains("SongMove.Describe(", m);
        Assert.DoesNotContain("foreach (var (source, song) in moves)", m);
        Assert.DoesNotContain("InsertRange(", m);
        string r = Method(drag, "private static List<(Playlist source, Option song)> ResolveDraggedSongs");
        Assert.Contains("SongMove.PickOption(", r);
        Assert.Contains("seen)", r);
    }

    [Fact] // [AC-move-to-playlist-1] [AC-move-to-playlist-6]
    public void ContextMenu_HasMoveSubmenuAfterRename()
    {
        string m = Method(Menu(), "private MenuFlyout BuildContextMenu");
        int sub = At(m, "new MenuFlyoutSubItem");
        Assert.True(At(m, "AppStrings.Menu_MoveToPlaylist") > sub);
        int addRename = At(m, "flyout.Items.Add(rename)");
        int addMove = At(m, "flyout.Items.Add(_moveToMenuItem)");
        int sep = At(m, "flyout.Items.Add(new MenuFlyoutSeparator())");
        Assert.True(addRename < addMove && addMove < sep);
    }

    [Fact] // [AC-move-to-playlist-6] [EDGE-move-to-playlist-1] [EDGE-move-to-playlist-2] [EDGE-move-to-playlist-13]
    public void Populate_VisibilityAndEnabledRules()
    {
        string m = Method(Menu(), "private void PopulateMoveToMenu");
        Assert.Contains("Level == 2", m);
        Assert.Contains("SongMove.IsPlaceholder", m);
        Assert.Contains("Visibility.Collapsed", m);
        Assert.Contains("Visibility.Visible", m);
        Assert.Contains("IsEnabled = targets.Count > 0", m);
    }

    [Fact] // [AC-move-to-playlist-6]
    public void RightTapped_PopulatesAfterEnableLoop()
    {
        string m = Method(Src("ui/MainWindow.cs"), "private void PlaylistTreeView_RightTapped");
        int pop = At(m, "PopulateMoveToMenu()");
        Assert.True(At(m, "item.IsEnabled = valid") < pop);
        Assert.True(At(m, "_contextMenuNodes = SelectionOrNode") < pop);
        Assert.True(pop < At(m, "_treeContextMenu.ShowAt"));
    }

    [Fact] // [AC-move-to-playlist-3] [EDGE-move-to-playlist-10] [EDGE-move-to-playlist-12]
    public void Populate_UsesAllPlaylistsNotTree()
    {
        string m = Method(Menu(), "private void PopulateMoveToMenu");
        Assert.Contains("SongMove.Targets(Playlists.Values", m);
        Assert.Contains("IsVFXGroup", m);
        Assert.DoesNotContain("RootPlaylistItems", m);
        Assert.Contains("Tag = p.Name", m);
    }

    [Fact] // [AC-move-to-playlist-3] [EDGE-move-to-playlist-11] [EDGE-move-to-playlist-15]
    public void Move_RefreshKeepsFilterAndExpandsTarget()
    {
        string m = MoveMethod();
        int expand = At(m, "_playlistExpandedStates[target.Name] = true");
        int load = At(m, "LoadPlaylists(SearchTextBox.Text.Trim())");
        Assert.True(expand < load);
        Assert.True(At(m, "SyncPlaylistNode(") > load);
        Assert.Contains("IsExpanded = true", m);
        int restore = At(m, "RestoreSelection(");
        Assert.True(restore > At(m, "SyncPlaylistNode("));
        Assert.True(At(m, "SelectionKey(2, target.Name") > load);
    }

    [Fact] // [AC-move-to-playlist-2] [AC-move-to-playlist-4] [EDGE-move-to-playlist-5]
    public void Move_ReusesSafeMoveAppending()
    {
        string m = MoveMethod();
        int counts = At(m, "Playlist.ScdReferenceCounts(");
        int run = At(m, "SongMove.Run(target, moves, null, InTargetPolicy.StayPut");
        Assert.True(counts < run);
        Assert.Contains("MoveSongAcrossPlaylists(", m);
        Assert.DoesNotContain("Task.Run", m);
    }

    [Fact] // [EDGE-move-to-playlist-3] [EDGE-move-to-playlist-10]
    public void Move_ResolvesAtClickTime()
    {
        string m = MoveMethod();
        int get = At(m, "Playlists.TryGetValue(targetName");
        int missing = At(m, "new PlaylistSaveException(targetName)");
        int run = At(m, "SongMove.Run(");
        Assert.True(get < missing && missing < run);
        Assert.Contains("ResolveDraggedSongs(_contextMenuNodes", m);
        // The only await before the batch is in the missing-target branch, which returns.
        int firstAwaitAfterRun = m.IndexOf("await ", run, StringComparison.Ordinal);
        Assert.True(firstAwaitAfterRun > run);
        string between = m[(missing)..run];
        Assert.Contains("return;", between);
    }

    [Fact] // [AC-move-to-playlist-7] [EDGE-move-to-playlist-14]
    public void Move_RefreshesBeforeErrorDialog_Capped()
    {
        string m = MoveMethod();
        int dialog = m.LastIndexOf("ErrorMoveSongs(", StringComparison.Ordinal);
        Assert.True(At(m, "RestoreSelection(") < dialog);
        Assert.Contains("Take(10)", m);
        Assert.DoesNotContain("if (batch.Errors.Count == 0)", m);
    }

    [Fact] // [AC-move-to-playlist-7] [EDGE-move-to-playlist-16]
    public void Click_WrappedInTryCatch()
    {
        string c = Method(Menu(), "private async void MoveToPlaylistMenuItem_Click");
        Assert.True(At(c, "try") < At(c, "await MoveSongsToPlaylistAsync"));
        Assert.Contains("catch (Exception", c);
        Assert.Contains("ErrorMoveSongs(FormatExceptionMessage(", c);
        string p = Method(Menu(), "private void PopulateMoveToMenu");
        Assert.Contains("catch (Exception", p);
        Assert.Contains("Logger.LogError", p);
    }

    [Fact] // [AC-move-to-playlist-3] [EDGE-move-to-playlist-6]
    public void HiddenBySearch_StatusNotice()
    {
        string m = MoveMethod();
        int guard = At(m, "if (IsFilterActive)");
        Assert.True(m.LastIndexOf("SongMove.CountHiddenBySearch(", StringComparison.Ordinal) > guard);
        Assert.Contains("batch.Moved", m);
        Assert.Contains("ShowStatusNotice(AppStrings.MovedHiddenBySearch(", m);
        Assert.DoesNotContain("SetProgressBarText", m);
        string stats = Src("ui/MainWindow.Stats.cs");
        Assert.Contains("BackgroundStatus.Pick(_statusNotice, BackgroundStatus.Pick(_backfillProgress, BackgroundStatus.Pick(_statsProgress, _aiProgress)))",
            Method(stats, "private void RenderBackgroundProgress"));
        string n = Method(stats, "private void ShowStatusNotice");
        Assert.Contains("CreateTimer", n);
        Assert.Contains("IsRepeating = false", n);
        Assert.Contains("RenderBackgroundProgress()", n);
        Assert.DoesNotContain("ProgressLabel", n);
    }

    [Fact] // [AC-move-to-playlist-1] [AC-move-to-playlist-3] [AC-move-to-playlist-7]
    public void Strings_PresentInAllLanguages()
    {
        var dirs = Directory.GetDirectories(Path.Combine(RuntimeTargetTests.RepoRoot(), "Strings"));
        Assert.Equal(7, dirs.Length);
        foreach (var dir in dirs)
        {
            var v = XDocument.Load(Path.Combine(dir, "Resources.resw")).Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
            Assert.False(string.IsNullOrWhiteSpace(v["Menu_MoveToPlaylist"]), dir);
            Assert.Contains("{0}", v["Dlg_ErrorMoveSongs"]);
            Assert.Contains("{0}", v["Status_MovedHiddenBySearch"]);
            Assert.Contains("{1}", v["Status_MovedHiddenBySearch"]);
        }
        string s = Src("ui/AppStrings.cs");
        Assert.Contains("Menu_MoveToPlaylist =>", s);
        Assert.Contains("ErrorMoveSongs(", s);
        Assert.Contains("MovedHiddenBySearch(", s);
    }
}
