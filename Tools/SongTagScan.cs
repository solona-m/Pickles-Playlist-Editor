using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Pickles_Playlist_Editor.Tools
{
    internal readonly record struct SongTagScanResult(int Read, int Tagged, int Unreadable, int AlreadyKnown, int Refused);

    /// <summary>Reads tags for every path the cache doesn't know yet, one at a time. Stores survive a cancel.</summary>
    internal static class SongTagScan
    {
        public const int FlushEvery = 100;

        public static SongTagScanResult Run(IReadOnlyList<string> fullPaths, SongTagCache cache, Func<string, SongTags?> read,
            CancellationToken ct, Action<int>? onStored = null)
        {
            int stored = 0, tagged = 0, unreadable = 0, known = 0, refused = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var p in fullPaths)
                {
                    if (!seen.Add(AiScoreCache.Key(p))) continue;
                    if (cache.TryGet(p) != null) { known++; continue; }
                    ct.ThrowIfCancellationRequested();
                    long gen = cache.Generation(p);
                    SongTags? t;
                    try { t = read(p); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { unreadable++; continue; }
                    if (t is not { } tags) { unreadable++; continue; }
                    if (!cache.StoreIfUnchanged(p, tags, gen)) { refused++; continue; }
                    stored++;
                    if (tags.HasArtistOrAlbum) tagged++;
                    onStored?.Invoke(stored);
                    if (stored % FlushEvery == 0) cache.Flush();
                }
            }
            finally { cache.Flush(); }
            return new SongTagScanResult(stored, tagged, unreadable, known, refused);
        }
    }
}
