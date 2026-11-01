using Pickles_Playlist_Editor.Utils;

namespace AnimHarness;

internal static partial class Program
{
    private static string MTag(string ids) =>
        string.Join(" ", ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(id =>
            $"{(id.StartsWith("A-") ? "AC" : "EDGE")}-issue-14-remember-dj-mod-{id[2..]}"));

    /// <summary>An in-memory settings store that counts each setter call.</summary>
    private sealed class FakeDjMods : IDjModSettings
    {
        private string _dance = string.Empty, _group = string.Empty, _pictures = string.Empty;
        public int DanceWrites, GroupWrites, PicturesWrites;
        public int Writes => DanceWrites + GroupWrites + PicturesWrites;

        public string DanceModName { get => _dance; set { _dance = value; DanceWrites++; } }
        public string DanceGroupId { get => _group; set { _group = value; GroupWrites++; } }
        public string PicturesModName { get => _pictures; set { _pictures = value; PicturesWrites++; } }

        public static FakeDjMods With(string? dance = null, string? group = null, string? pictures = null) =>
            new() { _dance = dance ?? string.Empty, _group = group ?? string.Empty, _pictures = pictures ?? string.Empty };
    }

    /// <summary>A temp Penumbra root holding the named mod folders, deleted afterwards.</summary>
    private static void WithRoot(Action<string> body, params string[] folders)
    {
        string root = Path.Combine(Path.GetTempPath(), "AnimHarness", "modmemory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var f in folders) Directory.CreateDirectory(Path.Combine(root, f));
            body(root);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void RegisterModMemory()
    {
        Add("PicturesReopensOnPicked", MTag("A-1"), () => WithRoot(root =>
        {
            var s = FakeDjMods.With(dance: "A");
            Check(DjModMemory.PicturesLoaded(s, "B", picked: true, targetCount: 2), "picked load wrote");
            Check(DjModMemory.PicturesOpening(root, s) == "B", $"reopens on B ({DjModMemory.PicturesOpening(root, s)})");
        }, "A", "B"));

        Add("PicturesPickLeavesDances", MTag("A-2 E-11"), () => WithRoot(root =>
        {
            var s = FakeDjMods.With(dance: "A", group: "g");
            DjModMemory.PicturesLoaded(s, "B", true, 1);
            Check(s.DanceWrites == 0 && s.GroupWrites == 0 && s.DanceModName == "A" && s.DanceGroupId == "g", "dance settings untouched");
            Check(DjModMemory.DancesOpening(root, s) == "A", "dances still open on A");
            int pictures = s.PicturesWrites;
            Check(DjModMemory.DancesPicked(s, "C", "h"), "dances pick accepted");
            Check(s.PicturesWrites == pictures && s.PicturesModName == "B", "dances pick leaves pictures");
        }, "A", "B", "C"));

        Add("PicturesFallsBackToDances", MTag("A-3 E-2"), () => WithRoot(root =>
        {
            foreach (var p in new[] { null, "", "   " })
            {
                var s = FakeDjMods.With(dance: "A", pictures: p);
                Check(DjModMemory.PicturesOpening(root, s) == "A", $"pictures '{p}' falls back to A");
            }
        }, "A"));

        Add("PicturesMissingFolderFallsBack", MTag("E-1"), () => WithRoot(root =>
        {
            var s = FakeDjMods.With(dance: "A", pictures: "Gone");
            Check(DjModMemory.PicturesOpening(root, s) == "A", "missing pictures folder: A");
            Check(s.PicturesModName == "Gone" && s.Writes == 0, "setting kept, nothing written");
        }, "A"));

        Add("PicturesNothingExistsGivesPicker", MTag("A-4 E-3"), () => WithRoot(root =>
        {
            Check(DjModMemory.PicturesOpening(root, FakeDjMods.With()) == null, "both unset: picker");
            Check(DjModMemory.PicturesOpening(root, FakeDjMods.With(dance: "X", pictures: "Y")) == null, "both missing: picker");
        }));

        Add("BlankPenumbraRootGivesPicker", MTag("E-4"), () =>
        {
            var s = FakeDjMods.With(dance: "A", pictures: "A");
            string missing = Path.Combine(Path.GetTempPath(), "AnimHarness", "modmemory-none", Guid.NewGuid().ToString("N"));
            foreach (var root in new[] { " ", missing })
                Check(DjModMemory.PicturesOpening(root, s) == null && DjModMemory.DancesOpening(root, s) == null, $"root '{root}': picker");

            // A blank root must not resolve the bare folder name against the working folder.
            string cwd = Environment.CurrentDirectory;
            string temp = Path.Combine(Path.GetTempPath(), "AnimHarness", "modmemory-cwd", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(temp, "A"));
                Environment.CurrentDirectory = temp;
                Check(DjModMemory.PicturesOpening("", s) == null && DjModMemory.DancesOpening("", s) == null, "root '': picker, not the working folder");
            }
            finally
            {
                Environment.CurrentDirectory = cwd;
                try { Directory.Delete(temp, true); } catch { }
            }
        });

        Add("DancesReopensOnPicked", MTag("A-5"), () => WithRoot(root =>
        {
            var s = FakeDjMods.With();
            Check(DjModMemory.DancesPicked(s, "A", "g"), "pick accepted");
            Check(DjModMemory.DancesOpening(root, s) == "A" && s.DanceGroupId == "g", "reopens on A with its group");
            Directory.Move(Path.Combine(root, "A"), Path.Combine(root, "A2"));
            Check(DjModMemory.DancesOpening(root, s) == null, "renamed: picker");
            Directory.Delete(Path.Combine(root, "A2"));
            Check(DjModMemory.DancesOpening(root, s) == null, "deleted: picker");
        }, "A"));

        Add("PicturesPreferredOrder", MTag("A-6"), () =>
        {
            Check(DjModMemory.PicturesPreferred(FakeDjMods.With(dance: "A", pictures: "B")) == "B", "pictures first");
            Check(DjModMemory.PicturesPreferred(FakeDjMods.With(dance: "A", pictures: " ")) == "A", "blank pictures: dance");
            Check(DjModMemory.PicturesPreferred(FakeDjMods.With()) == string.Empty, "both blank: empty");
        });

        Add("PicturesLoadedGate", MTag("E-5 E-6"), () => WithRoot(root =>
        {
            foreach (var (picked, count) in new[] { (false, 0), (false, 3), (true, 0) })
            {
                var s = FakeDjMods.With(dance: "A", pictures: "X");
                Check(!DjModMemory.PicturesLoaded(s, "B", picked, count) && s.Writes == 0 && s.PicturesModName == "X",
                    $"picked={picked} targets={count}: nothing saved");
            }
            var ok = FakeDjMods.With(dance: "A", pictures: "X");
            Check(DjModMemory.PicturesLoaded(ok, "B", true, 3) && ok.PicturesModName == "B" && ok.PicturesWrites == 1, "picked with targets: saved");

            foreach (var s in new[] { FakeDjMods.With(), FakeDjMods.With(dance: "A"), FakeDjMods.With(dance: "A", pictures: "B"), FakeDjMods.With(pictures: "Gone") })
            {
                DjModMemory.PicturesOpening(root, s);
                DjModMemory.DancesOpening(root, s);
                DjModMemory.PicturesPreferred(s);
                Check(s.Writes == 0, "resolvers never write");
            }
        }, "A", "B"));

        Add("PicturesKeepsPickAfterDancesChange", MTag("E-7"), () => WithRoot(root =>
        {
            var s = FakeDjMods.With(dance: "A");
            DjModMemory.PicturesLoaded(s, "A", true, 1);
            DjModMemory.DancesPicked(s, "C", "h");
            Check(DjModMemory.PicturesOpening(root, s) == "A", "pictures stays on its own pick");
        }, "A", "C"));

        Add("DancesNoGroupNotSaved", MTag("E-9"), () =>
        {
            var s = FakeDjMods.With(dance: "A", group: "g");
            Check(!DjModMemory.DancesPicked(s, "B", null) && s.Writes == 0 && s.DanceModName == "A" && s.DanceGroupId == "g", "no group: refused, nothing written");
            Check(DjModMemory.DancesPicked(s, "B", "") && s.DanceModName == "B" && s.DanceGroupId == "", "group with no id: saved as empty id");
        });

        Add("FolderCaseInsensitive", MTag("E-10"), () => WithRoot(root =>
            Check(DjModMemory.PicturesOpening(root, FakeDjMods.With(pictures: "dj pack")) != null, "case differs: resolves"), "DJ Pack"));

        Add("PicturesOpenNeverWritesSetting", MTag("E-12"), () => WithRoot(root =>
        {
            // The dialog's catch returns before PicturesLoaded; the opening itself never writes.
            var s = FakeDjMods.With(dance: "A", pictures: "B");
            Check(DjModMemory.PicturesOpening(root, s) == "B" && s.PicturesModName == "B" && s.PicturesWrites == 0, "kept after a failed open");
        }, "A", "B"));

        Add("RepickSameValueNoWrite", MTag("E-13"), () =>
        {
            var s = FakeDjMods.With(pictures: "B");
            Check(!DjModMemory.PicturesLoaded(s, "B", true, 2) && s.Writes == 0, "same pictures mod: no write");
            var c = FakeDjMods.With(pictures: "b");
            Check(DjModMemory.PicturesLoaded(c, "B", true, 2) && c.PicturesModName == "B", "case fix is stored");
            var d = FakeDjMods.With(dance: "A", group: "g");
            Check(DjModMemory.DancesPicked(d, "A", "g") && d.Writes == 0, "same dances pick: accepted, no write");
            Check(DjModMemory.DancesPicked(d, "A", "h") && d.DanceWrites == 0 && d.GroupWrites == 1 && d.DanceGroupId == "h", "group change: only the group written");
        });
    }
}
