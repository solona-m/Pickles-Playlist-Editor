using Pickles_Playlist_Editor.Tools;
using System;
using System.Collections.Generic;

namespace Pickles_Playlist_Editor
{
    /// <summary>The Backfill tags from folder summary. Pure, so every line can be tested.</summary>
    internal static class BackfillText
    {
        public static string Summary(BackfillResult r, bool aiOn, Func<string, string> get)
        {
            if (r.Outcome == BackfillOutcome.TagsFileUnavailable) return get("Backfill_TagsFileUnavailable");
            if (r.Outcome == BackfillOutcome.FolderUnreadable) return string.Format(get("Backfill_FolderUnreadable"), r.FolderError);
            var lines = new List<string> { string.Format(get("Backfill_Filled"), r.Filled, r.Targets, r.FilesChecked) };
            void Add(string key, int n) { if (n > 0) lines.Add(string.Format(get(key), n)); }
            Add("Backfill_Ai", r.AiTagged);
            if (r.AiTagged > 0 && !aiOn) lines.Add(get("Backfill_AiHidden"));
            Add("Backfill_Already", r.AlreadyHad);
            Add("Backfill_NoMatch", r.NoMatch);
            Add("Backfill_Skipped", r.Skipped);
            Add("Backfill_NoTags", r.NoTags);
            Add("Backfill_CloudOnly", r.CloudOnly);
            return string.Join("\n", lines);
        }
    }
}
