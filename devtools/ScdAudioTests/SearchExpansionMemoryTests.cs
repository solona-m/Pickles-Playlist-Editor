using Pickles_Playlist_Editor;
using Xunit;

namespace ScdAudioTests;

public class SearchExpansionMemoryTests
{
    private static Dictionary<string, bool> States(params (string Name, bool Expanded)[] items) =>
        items.ToDictionary(i => i.Name, i => i.Expanded);

    private static void Pollute(Dictionary<string, bool> states)
    {
        foreach (var k in states.Keys.ToList()) states[k] = true;
    }

    private static void AssertStates(Dictionary<string, bool> actual, params (string Name, bool Expanded)[] expected)
    {
        Assert.Equal(expected.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => $"{e.Name}={e.Expanded}"),
            actual.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
    }

    [Fact] // [AC-search-expand-restore-1]
    public void SearchThenClear_RestoresPreSearchStates()
    {
        var states = States(("A", true), ("B", false), ("C", false));
        string[] existing = ["A", "B", "C"];
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("a", states, existing);
        Assert.True(mem.Active);
        Pollute(states);
        mem.OnFilterChanged("", states, existing);

        Assert.False(mem.Active);
        AssertStates(states, ("A", true), ("B", false), ("C", false));
    }

    [Fact] // [AC-search-expand-restore-2] [EDGE-search-expand-restore-1]
    public void RefiningFilter_KeepsFirstSnapshot()
    {
        var states = States(("A", true), ("B", false));
        string[] existing = ["A", "B"];
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("a", states, existing);
        Pollute(states);
        mem.OnFilterChanged("ab", states, existing);
        states["B"] = false;
        states["A"] = false;
        mem.OnFilterChanged("a", states, existing);
        Pollute(states);
        mem.OnFilterChanged("xyz", states, existing);
        mem.OnFilterChanged("", states, existing);

        AssertStates(states, ("A", true), ("B", false));
    }

    [Fact] // [AC-search-expand-restore-3]
    public void ToggleDuringSearch_PreSearchWins()
    {
        var states = States(("A", true), ("B", false));
        string[] existing = ["A", "B"];
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, existing);
        states["B"] = true;
        states["A"] = false;
        mem.OnFilterChanged("", states, existing);

        AssertStates(states, ("A", true), ("B", false));
    }

    [Fact] // [AC-search-expand-restore-4] [EDGE-search-expand-restore-6]
    public void CreatedDuringSearch_KeepsCurrentState()
    {
        var states = States(("A", false));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["A"]);
        states["A"] = true;
        states["N"] = true;
        mem.OnFilterChanged("", states, ["A", "N"]);

        AssertStates(states, ("A", false), ("N", true));
    }

    [Fact] // [AC-search-expand-restore-5] [EDGE-search-expand-restore-7]
    public void RenameDuringSearch_RestoresUnderNewName()
    {
        var states = States(("A", false), ("rock", true), ("X", false));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["A", "rock", "X"]);
        mem.OnRenamed("A", "A2");
        mem.OnRenamed("X", "Y");
        mem.OnRenamed("Y", "Z");
        mem.OnRenamed("rock", "Rock");
        states.Clear();
        states["A2"] = true; states["Rock"] = false; states["Z"] = true;
        mem.OnFilterChanged("", states, ["A2", "Rock", "Z"]);

        AssertStates(states, ("A2", false), ("Rock", true), ("Z", false));
    }

    [Fact] // [EDGE-search-expand-restore-7]
    public void RenameExistingOntoFreedName_TakesRenamedValue()
    {
        var states = States(("A", true), ("B", false));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["A", "B"]);
        mem.OnRenamed("A", "B"); // B was deleted first
        states.Remove("A");
        states["B"] = false;
        mem.OnFilterChanged("", states, ["B"]);

        AssertStates(states, ("B", true));
    }

    [Fact] // [AC-search-expand-restore-4] [EDGE-search-expand-restore-14]
    public void DeleteThenCreateSameName_NewKeepsCurrent()
    {
        var states = States(("A", false));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["A"]);
        mem.Forget("A");
        states["A"] = true;
        mem.OnFilterChanged("", states, ["A"]);

        AssertStates(states, ("A", true));
    }

    [Fact] // [EDGE-search-expand-restore-14]
    public void RenameNewOntoFreedName_KeepsCurrent()
    {
        var states = States(("B", false));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["B"]);
        mem.OnRenamed("N", "B"); // N created during the search, B deleted
        states.Remove("N");
        states["B"] = true;
        mem.OnFilterChanged("", states, ["B"]);

        AssertStates(states, ("B", true));
    }

    [Fact] // [EDGE-search-expand-restore-14]
    public void ImportIntoExistingName_RestoresPreSearch()
    {
        var states = States(("A", false));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["A"]);
        states["A"] = true; // LoadPlaylistsAndExpand after the import; existed == true, so no Forget
        mem.OnFilterChanged("", states, ["A"]);

        AssertStates(states, ("A", false));
    }

    [Fact] // [EDGE-search-expand-restore-15]
    public void CreatedButHiddenDuringSearch_ComesBackCollapsed()
    {
        var states = States(("A", true));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["A"]);
        mem.Forget("Y");
        mem.OnFilterChanged("", states, ["A", "Y"]);

        AssertStates(states, ("A", true));
    }

    [Fact] // [EDGE-search-expand-restore-14] [EDGE-search-expand-restore-8]
    public void ForgetAndRename_WhenInactive_AreNoOps()
    {
        var states = States(("A", false), ("B", true));
        var mem = new SearchExpansionMemory();

        mem.Forget("A");
        mem.OnRenamed("A", "C");
        Assert.False(mem.Active);
        AssertStates(states, ("A", false), ("B", true));

        mem.OnFilterChanged("x", states, ["A", "B"]);
        Pollute(states);
        mem.OnFilterChanged("", states, ["A", "B"]);
        AssertStates(states, ("A", false), ("B", true));
    }

    [Fact] // [AC-search-expand-restore-6] [EDGE-search-expand-restore-4] [EDGE-search-expand-restore-5]
    public void DeletedOrMergedDuringSearch_Dropped()
    {
        var states = States(("D", true), ("T", false), ("S", false));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["D", "T", "S"]);
        Pollute(states); // D deleted, S merged into T; their stale keys stay in the map
        mem.OnFilterChanged("", states, ["T"]);

        AssertStates(states, ("T", false));
    }

    [Fact] // [EDGE-search-expand-restore-9]
    public void MoveTargetForcedDuringSearch_RestoresPreSearch()
    {
        var states = States(("T", false));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["T"]);
        states["T"] = true;
        mem.OnFilterChanged("", states, ["T"]);

        AssertStates(states, ("T", false));
    }

    [Fact] // [AC-search-expand-restore-7] [EDGE-search-expand-restore-13]
    public void SecondSearch_SnapshotsFreshState()
    {
        var states = States(("A", true), ("B", false));
        string[] existing = ["A", "B"];
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, existing);
        Pollute(states);
        mem.OnFilterChanged("", states, existing);
        states["A"] = false;
        states["B"] = true;

        mem.OnFilterChanged("y", states, existing);
        Pollute(states);
        states["B"] = false;
        mem.OnFilterChanged("", states, existing);

        AssertStates(states, ("A", false), ("B", true));
    }

    [Fact] // [AC-search-expand-restore-8] [EDGE-search-expand-restore-2]
    public void WhitespaceOnly_IsNotASearch()
    {
        var states = States(("A", false));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("   ", states, ["A"]);
        Assert.False(mem.Active);
        AssertStates(states, ("A", false));

        mem.OnFilterChanged("x", states, ["A"]);
        Pollute(states);
        mem.OnFilterChanged("  ", states, ["A"]);
        Assert.False(mem.Active);
        AssertStates(states, ("A", false));
    }

    [Fact] // [EDGE-search-expand-restore-3]
    public void ClearWithoutSearch_LeavesStatesUntouched()
    {
        var states = States(("A", true), ("Gone", true));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("", states, ["A"]);
        mem.OnFilterChanged(null, states, ["A"]);

        Assert.False(mem.Active);
        AssertStates(states, ("A", true), ("Gone", true));
    }

    [Fact] // [EDGE-search-expand-restore-10]
    public void UnfilteredReloadMidSearch_SnapshotSurvives()
    {
        var states = States(("A", false), ("B", true));
        string[] existing = ["A", "B"];
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, existing);
        Pollute(states);
        Assert.True(mem.Active);
        mem.OnFilterChanged("", states, existing);

        AssertStates(states, ("A", false), ("B", true));
    }

    [Fact] // [EDGE-search-expand-restore-11]
    public void UnrecordedPlaylistAtStart_CapturedCollapsed()
    {
        var states = States(("A", true));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["A", "E"]);
        states["E"] = true;
        mem.OnFilterChanged("", states, ["A", "E"]);

        AssertStates(states, ("A", true), ("E", false));
    }

    [Fact] // [EDGE-search-expand-restore-12]
    public void PlaylistSetChanged_MergesByName()
    {
        var states = States(("A", true), ("B", false));
        var mem = new SearchExpansionMemory();

        mem.OnFilterChanged("x", states, ["A", "B"]);
        states.Clear();
        states["B"] = true; states["Z"] = true;
        mem.OnFilterChanged("", states, ["B", "Z"]);

        AssertStates(states, ("B", false), ("Z", true));
    }
}
