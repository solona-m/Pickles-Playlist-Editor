using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;

namespace Pickles_Playlist_Editor.Tools
{
    /// <param name="CachedDuration">From the stats cache, taken on the UI thread with the raw relative path.</param>
    internal readonly record struct BackfillSong(string ScdRel, string ScdFullPath, long Generation, bool InScope, TimeSpan? CachedDuration);

    internal enum BackfillOutcome { Done, FolderUnreadable, TagsFileUnavailable }

    internal readonly record struct BackfillResult(BackfillOutcome Outcome, int FilesChecked, int Targets, int Filled,
        int AiTagged, int AlreadyHad, int NoMatch, int Skipped, int NoTags, int CloudOnly, string FolderError = "");

    /// <summary>
    /// Backfill tags from folder: finds songs' original mp3/m4a/flac/wav files by the name import gave the SCD, and
    /// stores their tags as source tags for songs that show none. Matches need equal tags across candidates and a
    /// length that agrees with every library SCD on that name; anything unclear is skipped. SCDs are only read.
    /// </summary>
    internal static class SourceTagBackfill
    {
        public static readonly string[] Extensions = [".mp3", ".m4a", ".flac", ".wav"];
        public const int MaxCandidates = 8, FlushEvery = 100, MaxDepth = 64;
        // Import pads 5 s; Trim Silence (with normalize) shortens; cached durations are whole seconds.
        public static readonly TimeSpan MinDelta = TimeSpan.FromSeconds(-3), MaxDelta = TimeSpan.FromSeconds(7);
        public const string PhaseScanning = "scan", PhaseReading = "read";

        private static readonly Regex s_suffix = new(@"_\d+$", RegexOptions.CultureInvariant);

        /// <summary>The stem, then the stem with each trailing _N removed in turn (Song_1_1, Song_1, Song). Never empty keys.</summary>
        public static IEnumerable<string> StemKeys(string stem)
        {
            string s = stem;
            while (s.Length > 0)
            {
                yield return s;
                if (!s_suffix.IsMatch(s)) yield break;
                s = s_suffix.Replace(s, "");
            }
        }

        public static bool DurationMatches(TimeSpan? scd, TimeSpan? source)
        {
            if (scd is not { } a || source is not { } b) return false;
            var delta = a - b;
            return delta >= MinDelta && delta <= MaxDelta;
        }

        /// <summary>The cached length, else the Vorbis stream's length read in memory; null when unknown. Never throws.</summary>
        public static TimeSpan? ScdDuration(BackfillSong s)
        {
            if (s.CachedDuration is { } d) return d;
            try { return ScdDurationFromBytes(SongTagReader.ReadPrefix(s.ScdFullPath, int.MaxValue)); }
            catch { return null; }
        }

        /// <summary>Mirrors <see cref="SongTagReader.FromScdBytes"/>: null for a non-Vorbis or unparseable SCD.</summary>
        internal static TimeSpan? ScdDurationFromBytes(byte[] scd)
        {
            try
            {
                using var br = new BinaryReader(new MemoryStream(scd));
                var file = new ScdFile(br, false);
                if (file.Audio.Count == 0 || file.Audio[0].Data is not ScdVorbis v) return null;
                using var r = new NVorbis.VorbisReader(new MemoryStream(v.Data), true);
                var t = r.TotalTime;
                return t > TimeSpan.Zero ? t : null;
            }
            catch { return null; }
        }

        /// <param name="library">Every library song; <see cref="BackfillSong.InScope"/> marks the untagged ones to fill.</param>
        /// <param name="alreadyHadInUi">Scoped songs the caller already found tagged (not in <paramref name="library"/> as InScope).</param>
        public static BackfillResult Run(IReadOnlyList<BackfillSong> library, int alreadyHadInUi, string root, SongTagCache cache,
            Func<string, SourceRead> readSource, Func<string, SongTags?> readScdTags, Func<BackfillSong, TimeSpan?> scdDuration,
            CancellationToken ct, Action<string, int, int>? progress = null, Action<int>? onFilled = null)
        {
            if (!cache.CanLoadSource())
                return new BackfillResult(BackfillOutcome.TagsFileUnavailable, 0, 0, 0, 0, alreadyHadInUi, 0, 0, 0, 0);

            int filesChecked = 0, targetsCount = 0, filled = 0, aiTagged = 0, alreadyHad = alreadyHadInUi, noMatch = 0,
                skipped = 0, noTags = 0, cloudOnly = 0;
            BackfillResult Result() => new(BackfillOutcome.Done, filesChecked, targetsCount, filled, aiTagged, alreadyHad,
                noMatch, skipped, noTags, cloudOnly);
            try
            {
                // One entry per SCD; a song in scope anywhere counts as in scope. Missing SCDs are dropped.
                var songs = new Dictionary<string, BackfillSong>(StringComparer.Ordinal);
                foreach (var s in library)
                {
                    string key = AiScoreCache.Key(s.ScdFullPath);
                    if (songs.TryGetValue(key, out var prev)) { if (s.InScope && !prev.InScope) songs[key] = prev with { InScope = true }; }
                    else songs[key] = s;
                }
                foreach (var key in songs.Where(kv => !File.Exists(kv.Value.ScdFullPath)).Select(kv => kv.Key).ToList())
                    songs.Remove(key);

                // Songs whose SCD tags were never read: read them now, so tags they already carry aren't hidden.
                var targets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (key, s) in songs)
                {
                    if (!s.InScope) continue;
                    ct.ThrowIfCancellationRequested();
                    var current = cache.TryGet(s.ScdFullPath);
                    if (current == null && readScdTags(s.ScdFullPath) is { } read)
                    {
                        cache.StoreIfUnchanged(s.ScdFullPath, read, s.Generation);
                        current = read;
                    }
                    if (current is { } c && (c.HasArtistOrAlbum || c.IsAiTagged)) alreadyHad++;
                    else targets.Add(key);
                }
                targetsCount = targets.Count;
                if (targetsCount == 0) return Result();

                var stems = songs.ToDictionary(kv => kv.Key, kv => StemKeys(Path.GetFileNameWithoutExtension(kv.Value.ScdFullPath)).ToList());
                var wanted = new HashSet<string>(stems.Values.SelectMany(k => k), StringComparer.OrdinalIgnoreCase);
                var wantedExact = new HashSet<string>(stems.Values.Where(k => k.Count > 0).Select(k => k[0]), StringComparer.OrdinalIgnoreCase);

                progress?.Invoke(PhaseScanning, 0, 0);
                var walk = SourceFolderWalk.Walk(root, wanted, wantedExact, ct, n => progress?.Invoke(PhaseScanning, n, 0));
                filesChecked = walk.FilesChecked;
                Logger.LogInfo("Backfill: {Files} audio files checked, {Skipped} folders skipped", walk.FilesChecked, walk.DirsSkipped);
                if (walk.RootError != null)
                    return new BackfillResult(BackfillOutcome.FolderUnreadable, 0, targetsCount, 0, 0, alreadyHad, 0, 0, 0, 0, walk.RootError);

                // Match: exact name, then the SCD name stripped of _N, then the file name stripped (against the exact SCD name).
                var groups = new Dictionary<(bool Stripped, string Key), List<string>>();
                foreach (var (key, keys) in stems)
                {
                    (bool, string)? match = null;
                    foreach (string k in keys)
                        if (walk.Exact.ContainsKey(k)) { match = (false, k); break; }
                    if (match == null && keys.Count > 0 && walk.Stripped.ContainsKey(keys[0])) match = (true, keys[0]);
                    if (match is not { } m)
                    {
                        if (targets.Contains(key)) noMatch++;
                        continue;
                    }
                    if (!groups.TryGetValue(m, out var list)) groups[m] = list = new List<string>();
                    list.Add(key);
                }

                var work = groups.Where(g => g.Value.Any(targets.Contains))
                    .OrderBy(g => g.Key.Key, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key.Stripped).ToList();
                int done = 0, sinceFlush = 0;
                progress?.Invoke(PhaseReading, 0, work.Count);
                foreach (var (groupKey, members) in work)
                {
                    ct.ThrowIfCancellationRequested();
                    int groupTargets = members.Count(targets.Contains);
                    var candidates = (groupKey.Stripped ? walk.Stripped : walk.Exact)[groupKey.Key];
                    var verdict = Decide(candidates, members.Select(k => songs[k]).ToList(), readSource, scdDuration, out var tags);
                    switch (verdict)
                    {
                        case Verdict.CloudOnly: cloudOnly += groupTargets; break;
                        case Verdict.NoTags: noTags += groupTargets; break;
                        case Verdict.Skip: skipped += groupTargets; break;
                        default:
                            foreach (string key in members.Where(targets.Contains))
                            {
                                var song = songs[key];
                                if (!cache.StoreSourceIfUntagged(song.ScdFullPath, tags, song.Generation)) { skipped++; continue; }
                                filled++;
                                if (tags.IsAiTagged) aiTagged++;
                                Logger.LogInfo("Backfill: tags for {Scd} from {File}", Path.GetFileName(song.ScdFullPath),
                                    string.Join(", ", candidates.Select(c => Path.GetFileName(c.Path))));
                                onFilled?.Invoke(filled);
                                if (++sinceFlush >= FlushEvery) { cache.Flush(); sinceFlush = 0; }
                            }
                            break;
                    }
                    progress?.Invoke(PhaseReading, ++done, work.Count);
                }
                return Result();
            }
            catch (OperationCanceledException) { return Result(); } // closing: what's filled is kept
            finally { cache.Flush(); }
        }

        private enum Verdict { Fill, Skip, NoTags, CloudOnly }

        // Every candidate read, equal and non-empty; every SCD on the name within the length window of every candidate.
        private static Verdict Decide(List<SourceFile> candidates, List<BackfillSong> scds, Func<string, SourceRead> readSource,
            Func<BackfillSong, TimeSpan?> scdDuration, out SongTags tags)
        {
            tags = default;
            if (candidates.Any(c => c.CloudOnly)) return Verdict.CloudOnly;
            if (candidates.Count > MaxCandidates) return Verdict.Skip;
            var reads = candidates.Select(c => readSource(c.Path)).ToList();
            bool Usable(SourceRead r) => r.Tags is { IsEmpty: false };
            if (!reads.Any(Usable)) return Verdict.NoTags;
            if (!reads.All(Usable) || reads.Select(r => r.Tags!.Value).Distinct().Count() != 1) return Verdict.Skip;
            foreach (var scd in scds)
            {
                var length = scdDuration(scd);
                if (!reads.All(r => DurationMatches(length, r.Duration))) return Verdict.Skip;
            }
            tags = reads[0].Tags!.Value;
            return Verdict.Fill;
        }
    }
}
