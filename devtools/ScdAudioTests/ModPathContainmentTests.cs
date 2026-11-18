using System.Text.RegularExpressions;
using Pickles_Playlist_Editor;
using Pickles_Playlist_Editor.Utils;
using Xunit;

namespace ScdAudioTests;

// Paths from a mod's own JSON must never steer a delete/move/write outside that mod's folder.
// Destructive tests run in a per-test sandbox under %TEMP%\<guid> and never touch Settings or the
// *.Default caches; escape candidates never climb above the sandbox, even against unfixed code.
public sealed class ModPathContainmentTests : IDisposable
{
    private readonly string _guid = Guid.NewGuid().ToString("N");
    private readonly string _sandbox;
    private readonly string _penumbra;
    private readonly string _mod;
    private readonly string _victim;
    private readonly string _victim2;

    public ModPathContainmentTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), _guid);
        _penumbra = Path.Combine(_sandbox, "Penumbra");
        _mod = Path.Combine(_penumbra, "Mod");
        _victim = Path.Combine(_penumbra, "Victim");
        _victim2 = Path.Combine(_sandbox, "Victim2");
        Directory.CreateDirectory(_mod);
        Directory.CreateDirectory(_victim);
        Directory.CreateDirectory(_victim2);
        File.WriteAllText(Path.Combine(_victim, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(_victim2, "keep.txt"), "keep");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true); } catch { }
    }

    private void AssertSandbox(string modRoot)
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), _guid, "Penumbra", "Mod"), modRoot);
        string? grand = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(modRoot)));
        Assert.True(Guid.TryParseExact(grand, "N", out _), "sandbox root is not a GUID folder");
        Assert.True(Directory.Exists(modRoot));
    }

    private void AssertVictimsIntact()
    {
        Assert.True(File.Exists(Path.Combine(_victim, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_victim2, "keep.txt")));
        Assert.True(Directory.Exists(_penumbra));
        Assert.True(Directory.Exists(_mod));
    }

    private string ModFile(string rel, string text = "x")
    {
        string full = Path.Combine(_mod, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private static Option Song(string name, string rel)
    {
        var o = new Option { Name = name };
        o.Files["sound/bpmloop.scd"] = rel;
        return o;
    }

    private static Playlist Pl(string name, params Option[] songs) => new() { Name = name, Options = songs.ToList() };

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

    private static void Reclaim(string root, string?[] candidates, string?[]? refs = null, bool onlyIfEmpty = false) =>
        Playlist.ReclaimUnusedAudioDirectories(root, candidates, refs ?? Array.Empty<string?>(), onlyIfEmpty);

    // ── Reclaim (playlist delete / merge) ──

    [Fact] // [AC-mod-path-containment-1] [AC-mod-path-containment-5]
    public void Reclaim_NeverDeletesFoldersOutsideMod()
    {
        AssertSandbox(_mod);
        Reclaim(_mod, new[] { "..\\Victim", "..", "pl\\..\\..\\Victim", _victim2 });
        AssertVictimsIntact();
    }

    [Fact] // [AC-mod-path-containment-1]
    public void Reclaim_StillDeletesUnusedInModFolder()
    {
        AssertSandbox(_mod);
        ModFile("oldpl\\a.scd");
        Reclaim(_mod, new[] { "oldpl" });
        Assert.False(Directory.Exists(Path.Combine(_mod, "oldpl")));
        AssertVictimsIntact();
    }

    [Theory] // [AC-mod-path-containment-1] [EDGE-mod-path-containment-14]
    [InlineData(".\\Chill\\a.scd")]
    [InlineData("pl\\..\\Chill\\a.scd")]
    [InlineData("ABS")]
    public void Reclaim_KeepsFolderReferencedViaDotSpellings(string reference)
    {
        AssertSandbox(_mod);
        string file = ModFile("Chill\\a.scd");
        if (reference == "ABS") reference = file;
        Reclaim(_mod, new[] { "Chill" }, new[] { reference });
        Assert.True(File.Exists(file));
    }

    [Fact] // [AC-mod-path-containment-1]
    public void DeleteAndMerge_UseObservedFolderAndContainedReclaim()
    {
        string src = Src("Playlist.cs");
        string delete = Method(src, "public void Delete()");
        Assert.Contains("GetObservedScdDirectory()", delete);
        // A folder guessed from the name is only ever removed when empty; the observed one only when observed.
        Assert.Matches(@"if \(scdDir != null\)\s*ReclaimUnusedAudioDirectories\(modRoot, new\[\] \{ scdDir \}\);", delete);
        Assert.Matches(@"ReclaimUnusedAudioDirectories\(modRoot, new\[\] \{ GetDefaultPlaylistDirectoryName\(Name\) \}, onlyIfEmpty: true\)", delete);
        Assert.Equal(2, Regex.Matches(delete, @"ReclaimUnusedAudioDirectories\(").Count);
        Assert.DoesNotContain("GetCurrentScdDirectory", src);

        string pure = Method(src, "internal static void ReclaimUnusedAudioDirectories(");
        Assert.DoesNotContain("Path.Combine(Settings.PenumbraLocation, Settings.ModName, dir)", src);
        Assert.True(At(pure, "ModPath.ResolveInside") < At(pure, "Directory.Delete("));
    }

    [Fact] // [AC-mod-path-containment-2]
    public void Reclaim_NeverDeletesModRoot()
    {
        AssertSandbox(_mod);
        File.WriteAllText(Path.Combine(_mod, "marker.txt"), "m");
        // "\" is covered without touching disk by ResolveInside_RootItself_ReturnsNull.
        Reclaim(_mod, new[] { ".", "pl\\..", "" });
        Assert.True(File.Exists(Path.Combine(_mod, "marker.txt")));
        AssertVictimsIntact();
    }

    [Fact] // [AC-mod-path-containment-2] [EDGE-mod-path-containment-18]
    public void Reclaim_OnlyIfEmpty_RemovesEmptyNameFolderOnly()
    {
        AssertSandbox(_mod);
        Directory.CreateDirectory(Path.Combine(_mod, "MyList"));
        string deep = ModFile("Other\\sub\\x.txt");
        Reclaim(_mod, new[] { "MyList" }, onlyIfEmpty: true);
        Reclaim(_mod, new[] { "Other" }, onlyIfEmpty: true);
        Assert.False(Directory.Exists(Path.Combine(_mod, "MyList")));
        Assert.True(File.Exists(deep));
    }

    [Fact] // [AC-mod-path-containment-2] [EDGE-mod-path-containment-18]
    public void Reclaim_OnlyIfEmpty_NeverRecursive()
    {
        string pure = Method(Src("Playlist.cs"), "internal static void ReclaimUnusedAudioDirectories(");
        int branch = At(pure, "if (onlyIfEmpty)");
        string tail = pure[branch..];
        int recursive = tail.IndexOf("Directory.Delete(folder, true)", StringComparison.Ordinal);
        int single = At(tail, "Directory.Delete(folder)");
        Assert.True(At(tail, "EnumerateFileSystemEntries") < single);
        Assert.True(recursive < 0 || recursive > single, "the onlyIfEmpty branch must not recurse");
    }

    [Theory] // [AC-mod-path-containment-2]
    [InlineData(".")]
    [InlineData("pl\\..")]
    [InlineData("\\")]
    public void ResolveInside_RootItself_ReturnsNull(string rel) => Assert.Null(ModPath.ResolveInside(_mod, rel));

    // ── Song delete ──

    [Fact] // [AC-mod-path-containment-3] [EDGE-mod-path-containment-20]
    public void DeleteSongAudio_OutsidePath_LeavesFile()
    {
        AssertSandbox(_mod);
        string x = Path.Combine(_victim, "x.scd");
        File.WriteAllText(x, "x");
        Assert.Null(Playlist.DeleteSongAudio(_mod, "..\\Victim\\x.scd"));
        Assert.Null(Playlist.DeleteSongAudio(_mod, Path.Combine(_victim2, "keep.txt")));
        Assert.True(File.Exists(x));
        AssertVictimsIntact();
    }

    [Fact] // [AC-mod-path-containment-3]
    public void DeleteSongAudio_InsidePath_DeletesAndReturnsPath()
    {
        AssertSandbox(_mod);
        string x = ModFile("pl\\x.scd");
        string y = ModFile("pl\\y.scd");
        Assert.Equal(x, Playlist.DeleteSongAudio(_mod, "pl\\x.scd"), StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(Playlist.DeleteSongAudio(_mod, ".\\pl\\y.scd"));
        Assert.False(File.Exists(x));
        Assert.False(File.Exists(y));
    }

    // ── Rename ──

    [Fact] // [AC-mod-path-containment-4] [EDGE-mod-path-containment-22]
    public void Rename_UsesObservedFolderResolvedMovesAndRepointedRel()
    {
        string rename = Method(Src("Playlist.cs"), "public bool Rename(string newName)");
        Assert.Contains("GetObservedScdDirectory()", rename);
        Assert.Contains("RepointedRel(", rename);
        Assert.DoesNotContain("Path.Combine(modDirectory, NormalizeRelativeModPath(", rename);
        Assert.Matches(@"oldFolder\s*=\s*ModPath\.ResolveInside\(modDirectory", rename);
        Assert.Matches(@"newFolder\s*=\s*ModPath\.ResolveInside\(modDirectory", rename);
        Assert.Matches(@"Directory\.Move\(oldFolder!?, newFolder!?\)", rename);
        Assert.Matches(@"Directory\.Move\(newFolder!?, oldFolder!?\)", rename);
        // No observed folder, or one that won't resolve, disables the move before it can happen.
        Assert.Matches(@"bool scdDirChanged = oldScdDir != null &&", rename);
        int guard = At(rename, "oldFolder == null || newFolder == null");
        Assert.True(guard < At(rename, "Directory.Move("));
        Assert.Contains("scdDirChanged = false", rename[guard..At(rename, "Directory.Move(")]);

        // The shared-folder check compares full paths, however other playlists spell theirs.
        string pl = Src("Playlist.cs");
        int s = At(pl, "private bool IsScdDirectoryShared(");
        string shared = pl[s..pl.IndexOf(';', s)]; // expression-bodied
        Assert.Contains("ModPath.IsStrictlyInside(folder, ModPath.FullUnder(modDirectory, rel))", shared);
        Assert.DoesNotContain(".StartsWith(", shared);
    }

    [Fact] // [AC-mod-path-containment-4] [EDGE-mod-path-containment-12]
    public void ObservedScdDirectory_IgnoresOutsideAndCanonicalizes()
    {
        var p = Pl("P", Song("a", "..\\a.scd"), Song("b", "C:\\b\\b.scd"), Song("c", ".\\pl\\c.scd"), Song("d", "pl\\d.scd"));
        Assert.Equal("pl", p.GetObservedScdDirectory());
    }

    [Fact] // [AC-mod-path-containment-4]
    public void RepointedRel_MovesInFolderOnly()
    {
        Assert.Equal("New\\a.scd", Playlist.RepointedRel(".\\pl\\a.scd", "pl", "New"));
        Assert.Null(Playlist.RepointedRel("other\\a.scd", "pl", "New"));
        Assert.Null(Playlist.RepointedRel("..\\pl\\a.scd", "pl", "New"));
    }

    [Fact] // [EDGE-mod-path-containment-28]
    public void CaseDifferingVote_RepointsBoth()
    {
        var p = Pl("P", Song("a", "pl\\a.scd"), Song("b", "PL\\b.scd"));
        string dir = p.GetObservedScdDirectory()!;
        Assert.Equal("pl", dir, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("New\\a.scd", Playlist.RepointedRel("pl\\a.scd", dir, "New"));
        Assert.Equal("New\\b.scd", Playlist.RepointedRel("PL\\b.scd", dir, "New"));
    }

    // ── Merge ──

    [Fact] // [AC-mod-path-containment-5]
    public void Merge_CarriesOutsideSongsUnmoved()
    {
        string merge = Method(Src("Playlist.cs"), "internal MergeResult MergeFrom(");
        Assert.DoesNotContain("Path.Combine(modAtStart, oldRel)", merge);
        Assert.Contains("ModPath.ResolveInside(modAtStart", merge);
        Assert.Contains("GetObservedScdDirectory()", merge);
        int outside = At(merge, "oldFull == null");
        int move = At(merge, "File.Move(oldFull, newFull)");
        Assert.True(outside < move);
        Assert.Matches(@"else if \([^)]*oldFull != null", merge);
    }

    [Fact] // [AC-mod-path-containment-5] [EDGE-mod-path-containment-29]
    public void Merge_RootMatchGuardsDeletesAndReclaim()
    {
        string merge = Method(Src("Playlist.cs"), "internal MergeResult MergeFrom(");
        int save = merge.LastIndexOf("Save();", At(merge, "source.DeleteGroupOnly()"), StringComparison.Ordinal);
        int cmp = At(merge, "string.Equals(Path.GetFullPath(");
        int del = At(merge, "source.DeleteGroupOnly()");
        int reclaim = At(merge, "ReclaimUnusedAudioDirectories(modAtStart");
        Assert.True(save < cmp && cmp < del && cmp < reclaim);
        string guard = merge[cmp..del];
        Assert.Contains("OrdinalIgnoreCase", guard);
        Assert.Contains("catch", guard);
        Assert.Contains("return", guard);
    }

    // ── Cleanup ──

    [Fact] // [AC-mod-path-containment-6] [EDGE-mod-path-containment-19]
    public void Cleanup_SkipsOutsideSongsBeforeMissingCheck()
    {
        string cleanup = Method(Src("Playlist.cs"), "public void Cleanup(");
        Assert.DoesNotContain("Path.GetFullPath(Path.Combine(modAtStart, oldRelNorm))", cleanup);
        int resolve = At(cleanup, "ModPath.ResolveInside(modAtStart");
        int exists = At(cleanup, "File.Exists(oldPath)");
        int drop = At(cleanup, "optionsToRemove.Add(");
        Assert.True(resolve < exists && exists < drop);
        Assert.Contains("continue", cleanup[resolve..exists]);
    }

    // ── Repair: strip _N suffixes ──

    [Fact] // [AC-mod-path-containment-7]
    public void Unsuffix_OutsideFile_NotRenamed()
    {
        AssertSandbox(_mod);
        string f = Path.Combine(_victim, "song_1.scd");
        File.WriteAllText(f, "x");
        var log = new List<string>();
        var (rel, moves) = Library.UnsuffixOnDisk(_mod, "..\\Victim\\song_1.scd", log);
        Assert.Equal("..\\Victim\\song_1.scd", rel);
        Assert.Empty(moves);
        Assert.True(File.Exists(f));
        Assert.False(File.Exists(Path.Combine(_victim, "song.scd")));
        Assert.Contains(log, l => l.Contains("SKIP (outside mod folder)"));
    }

    [Fact] // [AC-mod-path-containment-7]
    public void Unsuffix_InsideFile_Renamed()
    {
        AssertSandbox(_mod);
        ModFile("pl\\song_1.scd");
        var (rel, moves) = Library.UnsuffixOnDisk(_mod, "pl\\song_1.scd", new List<string>());
        Assert.Equal("pl\\song.scd", rel);
        Assert.Single(moves);
        Assert.True(File.Exists(Path.Combine(_mod, "pl", "song.scd")));
        Assert.False(File.Exists(Path.Combine(_mod, "pl", "song_1.scd")));
    }

    [Fact] // [AC-mod-path-containment-7]
    public void StripSuffixes_DelegatesAndBracesMigrations()
    {
        string strip = Method(Src("Library.cs"), "private static List<string> StripRedundantScdSuffixes(");
        Assert.Contains("UnsuffixOnDisk(", strip);
        Assert.DoesNotContain("File.Move(", strip);
        Assert.Matches(@"foreach \([^)]*\)\s*\{[^}]*AiScoreCache\.Default\.UpdateCacheForSCD", strip);
    }

    // ── Import / create ──

    [Fact] // [AC-mod-path-containment-8] [EDGE-mod-path-containment-13]
    public void ImportFolder_FallsBackInsideMod()
    {
        var outside = Pl("Mix", Song("a", "..\\x.scd"), Song("b", "C:\\y\\z.scd"));
        Assert.Null(outside.GetObservedScdDirectory());
        Assert.Null(Pl("Empty").GetObservedScdDirectory());
        string? dir = ModPath.ResolveInside(_mod, outside.GetScdDirectoryForNewFiles());
        Assert.NotNull(dir);
        Assert.True(ModPath.IsStrictlyInside(_mod, dir));
    }

    [Theory] // [AC-mod-path-containment-8] [EDGE-mod-path-containment-24]
    [InlineData("..")]
    [InlineData(". .")]
    [InlineData(".")]
    [InlineData("a\\..\\b")]
    [InlineData("C:")]
    [InlineData("..\\..\\x")]
    public void PlaylistNames_MapToOneSafeSegment(string name)
    {
        string seg = new Playlist { Name = name }.GetScdDirectoryForNewFiles();
        Assert.DoesNotContain('\\', seg);
        Assert.DoesNotContain('/', seg);
        string? full = ModPath.ResolveInside(_mod, seg);
        Assert.NotNull(full);
        Assert.Equal(Path.GetFullPath(_mod), Path.GetDirectoryName(full), StringComparer.OrdinalIgnoreCase);
    }

    [Fact] // [AC-mod-path-containment-8]
    public void CreateAndAddFiles_ReCheckContainment()
    {
        string src = Src("Playlist.cs");
        string addFiles = Method(src, "static Option AddFiles(");
        Assert.DoesNotContain("Path.Combine(Settings.PenumbraLocation, Settings.ModName, playlistScdDirectory)", addFiles);
        Assert.Contains("ModPath.ResolveInside", addFiles);
        string create = Method(src, "public static void Create(string playlistName");
        Assert.DoesNotContain("Path.Combine(Settings.PenumbraLocation, Settings.ModName,", create);
        int upsert = At(create, "group.Upsert();");
        Assert.True(At(create[upsert..], "ModPath.ResolveInside(") > 0);
        Assert.True(create.IndexOf("Directory.CreateDirectory(ModPath.ResolveInside(", upsert, StringComparison.Ordinal) > upsert);
    }

    // ── Normalize / EQ / Convert / Drag ──

    [Fact] // [AC-mod-path-containment-9] [EDGE-mod-path-containment-23]
    public void Normalize_Eq_Convert_Drag_NoRawJoins()
    {
        string menu = Src("ui/MainWindow.ContextMenu.cs");
        string norm = Method(menu, "private static void NormalizeSongAudio(");
        string eq = Method(menu, "private static void ApplyEqualizerSettingsToSongAudio(");
        Assert.Contains("ModPath.ResolveInside(modRoot, Playlist.GetScdPath(option))",
            Method(menu, "private static string ContainedScdPath("));
        foreach (var body in new[] { norm, eq })
        {
            Assert.DoesNotContain("Path.Combine(Settings.PenumbraLocation, Settings.ModName, Playlist.GetScdPath(", body);
            Assert.Contains("string? modRoot", body);
            Assert.True(At(body, "ContainedScdPath(option, modRoot)") < At(body, "File.Exists("));
            Assert.True(At(body, "ContainedScdPath(option, modRoot)") < At(body, "FileMode.Create"));
        }

        string convert = Method(Src("Library.cs"), "public static List<string> ConvertToStereo(");
        Assert.DoesNotContain("Path.Combine(Settings.PenumbraLocation, Settings.ModName, rel)", convert);
        Assert.True(At(convert, "ModPath.ResolveInside") < At(convert, "FileMode.Create"));
        Assert.Contains("outside the mod folder", convert);

        string drag = Method(Src("ui/MainWindow.DragDrop.cs"), "private static void MoveSongAcrossPlaylists(");
        Assert.DoesNotContain("Path.Combine(Settings.PenumbraLocation, Settings.ModName, oldRel)", drag);
        Assert.Matches(@"bool moveFile = oldFullPath != null", drag);
        // Every file move sits inside the brace-balanced body of an if (moveFile) block.
        var blocks = Regex.Matches(drag, @"if \(moveFile\)").Select(b => (b.Index, Body: Method(drag[b.Index..], "if (moveFile)"))).ToList();
        var moves = Regex.Matches(drag, @"File\.Move\(").ToList();
        Assert.NotEmpty(moves);
        foreach (Match m in moves)
            Assert.Contains(blocks, b => m.Index > b.Index && m.Index < b.Index + b.Body.Length);
    }

    [Fact] // [EDGE-mod-path-containment-30]
    public void ModFolderUnset_ReportedDistinctly()
    {
        string convert = Method(Src("Library.cs"), "public static List<string> ConvertToStereo(");
        Assert.True(At(convert, "Mod folder is not set.") < At(convert, "foreach (var option in allOptions)"));

        string menu = Src("ui/MainWindow.ContextMenu.cs");
        string helper = Method(menu, "private static string ContainedScdPath(");
        Assert.True(At(helper, "Mod folder is not set.") < At(helper, "ModPath.ResolveInside"));
        foreach (var sig in new[] { "private async void NormalizeAudioMenuItem_Click(", "private async Task ApplyEqualizerSettingsToTargetsAsync(" })
        {
            string body = Method(menu, sig);
            Assert.True(At(body, "SongTagCache.ModRootOrNull(") < At(body, "foreach (var (playlist, option)"));
        }
    }

    // ── Dances / textures ──

    [Fact] // [AC-mod-path-containment-10]
    public void DancePathsOutsideMod_FlagsEscapingRoot()
    {
        Assert.Single(DanceModWrites.PathsOutsideMod(_mod, "..\\x\\slug", new[] { "..\\x\\slug\\a.pap" }));
        Assert.Single(DanceModWrites.PathsOutsideMod(_mod, "C:\\x\\slug", new[] { "C:\\x\\slug\\a.pap" }));
        Assert.Contains("PathsOutsideMod(", Method(Src("Utils/DanceModWrites.cs"), "public static AddDancePlan PlanAdd("));
    }

    [Fact] // [EDGE-mod-path-containment-21]
    public void DancePathsOutsideMod_AllowsTopLevelAndDances()
    {
        Assert.Empty(DanceModWrites.PathsOutsideMod(_mod, "slug", new[] { "slug\\a.pap" }));
        Assert.Empty(DanceModWrites.PathsOutsideMod(_mod, "dances\\slug", new[] { "dances\\slug\\a.pap" }));
    }

    [Fact] // [AC-mod-path-containment-10]
    public void DanceAdd_ReChecksBeforeWriteAndDelete()
    {
        string add = Method(Src("Utils/DanceModWrites.cs"), "public static DanceWriteResult Add(");
        int resolve = At(add, "ModPath.ResolveInside");
        Assert.True(resolve < At(add, "PenumbraMeta.AtomicWrite("));
        Assert.True(resolve < At(add, "Directory.Delete("));
        Assert.DoesNotContain("Path.Combine(modRoot, relative.Replace(", add);
    }

    [Fact] // [AC-mod-path-containment-10]
    public void DanceRepairScan_SkipsOutsidePaths()
    {
        AssertSandbox(_mod);
        string pap = Path.Combine(_victim, "x.pap");
        byte[] bytes = { 1, 2, 3, 4 };
        File.WriteAllBytes(pap, bytes);
        File.WriteAllText(Path.Combine(_mod, "meta.json"),
            "{\"FileVersion\":3,\"Groups\":[{\"Name\":\"G\",\"Options\":[{\"Name\":\"d\",\"Files\":{\"chara/human/c0101/animation/a0001/bt_common/emote/dance_male_loop.pap\":\"..\\\\Victim\\\\x.pap\"}}]}]}");
        var notes = new List<string>();
        var found = DanceRepair.Scan(_mod, notes);
        Assert.Empty(found);
        Assert.Contains(notes, n => n.Contains("outside"));
        Assert.Equal(bytes, File.ReadAllBytes(pap));
    }

    [Fact] // [AC-mod-path-containment-10]
    public void DjTextureLocate_RefusesOutsideTarget()
    {
        AssertSandbox(_mod);
        File.WriteAllText(Path.Combine(_victim, "eq55.atex"), "not a texture");
        File.WriteAllText(Path.Combine(_mod, "meta.json"),
            "{\"FileVersion\":3,\"Groups\":[{\"Name\":\"G\",\"Options\":[{\"Name\":\"t\",\"Files\":{\"vfx/atex/eq55.atex\":\"..\\\\Victim\\\\eq55.atex\"}}]}]}");
        var found = DjTextureScan.Locate(_mod, "Mod", out var problems);
        Assert.DoesNotContain(found, t => t.Surface.Key == "table");
        Assert.Contains(problems, p => p.Surface.Key == "table" && p.Reason.Contains("outside"));
    }

    // ── ModPath (pure) ──

    [Fact] // [EDGE-mod-path-containment-1]
    public void ResolveInside_Rejects_AbsolutePaths()
    {
        Assert.Null(ModPath.ResolveInside(_mod, "C:\\Users\\x\\x.scd"));
        Assert.Null(ModPath.ResolveInside(_mod, Path.Combine(_victim2, "keep.txt")));
    }

    [Theory] // [EDGE-mod-path-containment-2]
    [InlineData("C:x.scd")]
    [InlineData("pl\\x.scd:evil")]
    public void ModPath_Rejects_Colon(string rel)
    {
        Assert.Null(ModPath.CanonicalRelative(rel));
        Assert.Null(ModPath.ResolveInside(_mod, rel));
    }

    [Fact] // [EDGE-mod-path-containment-3]
    public void ResolveInside_Rejects_DevicePath() => Assert.Null(ModPath.ResolveInside(_mod, "\\\\?\\C:\\x.scd"));

    [Fact] // [EDGE-mod-path-containment-4]
    public void ResolveInside_LeadingSeparator_StaysInside()
    {
        Assert.Equal(Path.Combine(_mod, "server", "share", "x.scd"), ModPath.ResolveInside(_mod, "\\\\server\\share\\x.scd"));
        Assert.Equal(Path.Combine(_mod, "pl", "x.scd"), ModPath.ResolveInside(_mod, "/pl/x.scd"));
    }

    [Fact] // [EDGE-mod-path-containment-5]
    public void CanonicalRelative_ResolvesInTreeDots()
    {
        Assert.Equal("Chill\\a.scd", ModPath.CanonicalRelative(".\\Chill\\a.scd"));
        Assert.Equal("Chill\\a.scd", ModPath.CanonicalRelative("pl\\..\\Chill\\a.scd"));
        Assert.Equal("a\\b\\d.scd", ModPath.CanonicalRelative("a\\b\\.\\c\\..\\d.scd"));
        Assert.Equal(Path.Combine(_mod, "Chill", "a.scd"), ModPath.ResolveInside(_mod, ".\\Chill\\a.scd"));
    }

    [Theory] // [EDGE-mod-path-containment-6]
    [InlineData(".")]
    [InlineData("pl\\..")]
    [InlineData("..\\x")]
    [InlineData("pl\\..\\..\\x")]
    [InlineData("...\\x.scd")]
    [InlineData(". .\\x.scd")]
    public void ModPath_RootEscapeAndDotSegments_Null(string rel)
    {
        Assert.Null(ModPath.CanonicalRelative(rel));
        Assert.Null(ModPath.ResolveInside(_mod, rel));
    }

    [Fact] // [EDGE-mod-path-containment-7]
    public void ResolveInside_Accepts_MixedAndDoubledSeparators()
    {
        Assert.Equal(Path.Combine(_mod, "pl", "sub", "x.scd"), ModPath.ResolveInside(_mod, "pl/sub\\x.scd"));
        Assert.Equal(Path.Combine(_mod, "pl", "x.scd"), ModPath.ResolveInside(_mod, "pl\\\\x.scd"));
    }

    [Fact] // [EDGE-mod-path-containment-8]
    public void IsStrictlyInside_SiblingPrefixAndRoot_False()
    {
        Assert.False(ModPath.IsStrictlyInside(_mod, _mod + "Evil\\x"));
        Assert.False(ModPath.IsStrictlyInside(_mod, _mod));
        Assert.False(ModPath.IsStrictlyInside(_mod, _mod + "\\"));
        Assert.True(ModPath.IsStrictlyInside(_mod, _mod + "\\x"));
    }

    [Fact] // [EDGE-mod-path-containment-9]
    public void ResolveInside_RootTrailingSeparatorAndCase()
    {
        string? a = ModPath.ResolveInside(_mod + "\\", "pl\\x.scd");
        string? b = ModPath.ResolveInside(_mod.ToUpperInvariant(), "pl\\x.scd");
        Assert.NotNull(a);
        Assert.Equal(a, b, StringComparer.OrdinalIgnoreCase);
    }

    [Fact] // [EDGE-mod-path-containment-10]
    public void ModPath_NullOrBlank_ReturnsNull()
    {
        foreach (var blank in new[] { null, "", "   " })
        {
            Assert.Null(ModPath.ResolveInside(blank, "pl\\x.scd"));
            Assert.Null(ModPath.ResolveInside(_mod, blank));
            Assert.Null(ModPath.CanonicalRelative(blank));
            Assert.Null(ModPath.FullUnder(blank, "pl\\x.scd"));
            Assert.Null(ModPath.FullUnder(_mod, blank));
        }
        Assert.Null(Playlist.DeleteSongAudio(null, Guid.NewGuid().ToString("N") + "\\x.scd"));
    }

    [Theory] // [EDGE-mod-path-containment-11]
    [InlineData("pl\\x\0.scd")]
    [InlineData("pl\\x|.scd")]
    [InlineData("pl\\x*.scd")]
    [InlineData("pl\\x?.scd")]
    [InlineData("pl\\x\".scd")]
    [InlineData("pl\\x<.scd")]
    public void ModPath_InvalidChars_ReturnsNull(string rel)
    {
        Assert.Null(ModPath.CanonicalRelative(rel));
        Assert.Null(ModPath.ResolveInside(_mod, rel));
    }

    [Fact] // [EDGE-mod-path-containment-15]
    public void Reclaim_KeepsPlainlyReferencedFolder()
    {
        AssertSandbox(_mod);
        string f = ModFile("pl\\a.scd");
        Reclaim(_mod, new[] { "pl" }, new[] { "pl\\a.scd" });
        Assert.True(File.Exists(f));
    }

    [Fact] // [EDGE-mod-path-containment-16]
    public void Reclaim_MissingCandidate_NoThrow()
    {
        AssertSandbox(_mod);
        Reclaim(_mod, new[] { "ghost" });
        AssertVictimsIntact();
    }

    [Fact] // [EDGE-mod-path-containment-17]
    public void Reclaim_DuplicateCandidates_RemovedWithoutThrow()
    {
        AssertSandbox(_mod);
        ModFile("oldpl\\a.scd");
        Reclaim(_mod, new[] { "oldpl", "OLDPL", "oldpl/" });
        Assert.False(Directory.Exists(Path.Combine(_mod, "oldpl")));
    }

    [Fact] // [EDGE-mod-path-containment-25]
    public void ModPathKey_CollapsesInModSpellings()
    {
        string k = ModPath.Key("pl\\a.scd");
        Assert.Equal(k, ModPath.Key(".\\pl\\a.scd"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(k, ModPath.Key("pl/a.scd"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(k, ModPath.Key("PL\\a.scd"), StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(ModPath.Key("a.scd"), ModPath.Key("..\\a.scd"), StringComparer.OrdinalIgnoreCase);

        string src = Src("Playlist.cs");
        string counts = Method(src, "internal static Dictionary<string, int> ScdReferenceCounts(");
        Assert.Contains("ModPath.Key", counts);
        Assert.DoesNotContain(".Select(NormalizeRelativeModPath)", counts);
        Assert.Contains("ModPath.Key", Method(src, "private static HashSet<string> ReferencedScdPaths()"));

        var bodies = new[]
        {
            Method(src, "internal MergeResult MergeFrom("),
            Method(src, "public void Cleanup("),
            Method(Src("ui/MainWindow.DragDrop.cs"), "private static void MoveSongAcrossPlaylists("),
        };
        foreach (var body in bodies)
        {
            Assert.DoesNotMatch(@"==\s*oldRel(Norm)?\b", body);
            var follows = Regex.Matches(body, @"string\.Equals\((Utils\.)?ModPath\.Key\([^;]*");
            Assert.NotEmpty(follows);
            foreach (Match m in follows)
                Assert.Contains("OrdinalIgnoreCase", m.Value);
        }
    }

    [Theory] // [EDGE-mod-path-containment-26]
    [InlineData("..\\pickles-canonical-root\\x.scd")]
    [InlineData("..\\PICKLES-CANONICAL-ROOT\\x.scd")]
    [InlineData("..\\anything\\x.scd")]
    public void CanonicalRelative_NoEscapeAndReturnAliasing(string rel)
    {
        Assert.Null(ModPath.CanonicalRelative(rel));
        Assert.Null(ModPath.ResolveInside(_mod, rel));
        Assert.NotEqual(ModPath.Key("x.scd"), ModPath.Key(rel), StringComparer.OrdinalIgnoreCase);
    }

    [Theory] // [EDGE-mod-path-containment-31] QA: whitespace Windows keeps must not be trimmed away
    [InlineData("Chill\u3000")]
    [InlineData("Chill\u00A0")]
    [InlineData(" Chill")]
    [InlineData("\u3000Chill")]
    public void PlaylistFolderWhitespace_WrittenWhereRecorded(string name)
    {
        // AddFiles/Create/drag write to ResolveInside(dir) but record Path.Combine(dir, file): both must agree.
        string dir = new Playlist { Name = name }.GetScdDirectoryForNewFiles();
        string? written = ModPath.ResolveInside(_mod, dir);
        Assert.NotNull(written);
        Assert.Equal(Path.Combine(_mod, dir), written);
        Assert.Equal(Path.Combine(dir, "song.scd"), ModPath.CanonicalRelative(Path.Combine(dir, "song.scd")));
        Assert.Equal(Path.Combine(_mod, "pl", "x.scd\u00A0"), ModPath.ResolveInside(_mod, "pl\\x.scd\u00A0"));
    }

    [Fact] // [EDGE-mod-path-containment-31] Key keeps whitespace too, so key-resolving sinks agree with Windows
    public void Key_DoesNotTrimWhitespace()
    {
        Assert.Equal(Path.Combine(_mod, " pl", "a.scd"), ModPath.ResolveInside(_mod, ModPath.Key(" pl\\a.scd")));
        Assert.Null(ModPath.ResolveInside(_mod, ModPath.Key(" pl\\a.scd ")));
        Assert.NotEqual(ModPath.Key("pl\\a.scd"), ModPath.Key("pl\\a.scd "), StringComparer.OrdinalIgnoreCase);
    }

    [Fact] // [EDGE-mod-path-containment-32] QA: a name cut at the length cap still maps to a usable folder
    public void LongPlaylistName_CutBeforeTrailingDotTrim()
    {
        string dir = new Playlist { Name = new string('a', 199) + ".b" }.GetScdDirectoryForNewFiles();
        Assert.False(dir.EndsWith('.'));
        Assert.NotNull(ModPath.ResolveInside(_mod, dir));
    }

    [Theory] // [EDGE-mod-path-containment-27]
    [InlineData("pl.\\x.scd")]
    [InlineData("pl \\x.scd")]
    public void CanonicalRelative_RejectsTrailingDotOrSpaceSegments(string rel)
    {
        Assert.Null(ModPath.CanonicalRelative(rel));
        Assert.NotEqual(ModPath.Key("pl\\x.scd"), ModPath.Key(rel), StringComparer.OrdinalIgnoreCase);
    }
}
