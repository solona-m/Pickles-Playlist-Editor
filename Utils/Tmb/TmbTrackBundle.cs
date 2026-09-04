using System;
using System.Collections.Generic;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils.Tmb
{
    /// <summary>
    /// A run of tracks lifted out of one timeline, ready to be spliced into another.
    ///
    /// For this app that run is always the DJ's music-and-effects block: the tracks whose C012 items
    /// fire <c>vfx/bpmloop-others.avfx</c> and the DJ's other emitters. Appending it to a downloaded
    /// dance is what makes that dance react to the DJ's music, and it is the one thing the manual
    /// workflow used a hand-built <c>.tmbtrack</c> file for.
    ///
    /// The bundle is deliberately extracted from the user's OWN mod rather than shipped with the app.
    /// Every DJ pack names its own effects — Pickles' set is not Lu's — so a hardcoded block would be
    /// wrong for everyone but one person, and would rot the moment a pack was updated.
    ///
    /// It carries the donor's ENTIRE pool rather than just the slice its own entries reference. That
    /// wastes a couple of kilobytes in a file measured in megabytes, and in exchange every offset in
    /// the bundle can be rebased by one constant — which is the difference between arithmetic that
    /// can be checked by eye and a compaction pass that would have to understand every entry type.
    /// </summary>
    internal sealed class TmbTrackBundle
    {
        /// <summary>The selected entries, concatenated, in donor order.</summary>
        public byte[] Entries { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// What each selected entry points at, resolved out of the donor, in the same order as
        /// <see cref="DonorEntries"/>.
        ///
        /// Values rather than offsets, because the splice rebuilds the destination's pool from
        /// scratch rather than concatenating two of them — see <see cref="TmbSplice"/> for why the
        /// pool layout is not free-form.
        /// </summary>
        public IReadOnlyList<IReadOnlyDictionary<int, TmbPoolItem>> Items { get; init; }
            = Array.Empty<IReadOnlyDictionary<int, TmbPoolItem>>();

        /// <summary>
        /// The selected entries as walked in the DONOR, in the same order as <see cref="Entries"/>.
        /// Their <c>Offset</c> is a donor offset; <see cref="EntryOffsets"/> gives the bundle-local one.
        /// </summary>
        public IReadOnlyList<TmbEntry> DonorEntries { get; init; } = Array.Empty<TmbEntry>();

        /// <summary>Offset of each entry within <see cref="Entries"/>.</summary>
        public IReadOnlyList<int> EntryOffsets { get; init; } = Array.Empty<int>();

        /// <summary>The ids of the TMTR entries, which the destination actor must be told about.</summary>
        public IReadOnlyList<short> TrackIds { get; init; } = Array.Empty<short>();

        /// <summary>
        /// How many leading bytes of <see cref="Entries"/> are TMTR entries.
        ///
        /// The track entries and the item entries have to be spliced in at two DIFFERENT places —
        /// every real timeline keeps all its TMTR together right after the actor, and its items after
        /// those — so the bundle has to be splittable. Donor order already puts the tracks first,
        /// which is why this is a byte count rather than a reshuffle.
        /// </summary>
        public int TrackEntryBytes { get; init; }

        public int TrackEntryCount => TrackIds.Count;

        /// <summary>Every effect path the bundle fires, for display and for sanity checks.</summary>
        public IReadOnlyList<string> EffectPaths { get; init; } = Array.Empty<string>();

        public int TrackCount => TrackIds.Count;
        public int EntryCount => DonorEntries.Count;
        public int EntryBytes => Entries.Length;

        public override string ToString() =>
            $"{TrackCount} tracks, {EntryCount} entries, {EffectPaths.Count} effects";

        // ---- extraction ------------------------------------------------------------------------

        /// <summary>
        /// Lifts the given tracks, and every item they reference, out of a donor timeline.
        ///
        /// Entries keep their donor order. That matters: in every real file the TMTR entries precede
        /// the items they point at, and preserving the order preserves that property without this
        /// having to know it is a property.
        /// </summary>
        public static TmbTrackBundle Extract(TmbLayout donor, IReadOnlyList<TmbEntry> tracks)
        {
            if (tracks == null || tracks.Count == 0)
                throw new TmbFormatException("Refusing to build an empty track bundle.");

            var byId = new Dictionary<short, TmbEntry>();
            foreach (var entry in donor.Entries)
                byId[entry.Id] = entry;

            var wanted = new HashSet<int>();
            var trackIds = new List<short>();

            foreach (var track in tracks)
            {
                if (track.Magic != TmbBinary.Tmtr)
                    throw new TmbFormatException($"{track} is not a track.");

                wanted.Add(track.Offset);
                trackIds.Add(track.Id);

                foreach (short id in TmbBinary.ResolveInt16List(donor, track, track.Fields[0]))
                {
                    if (!byId.TryGetValue(id, out var item))
                        throw new TmbFormatException(
                            $"{track} references item id {id}, which this timeline does not contain.");
                    wanted.Add(item.Offset);
                }
            }

            var selected = donor.Entries.Where(e => wanted.Contains(e.Offset)).ToList();

            // A track whose items are themselves tracks would make the copy order meaningless and is
            // not something any real dance does. Refuse rather than produce a subtly wrong bundle.
            foreach (var entry in selected)
            {
                if (entry.Magic != TmbBinary.Tmtr && TmbBinary.IsSkeleton(entry.Magic))
                    throw new TmbFormatException(
                        $"{entry} is part of the timeline skeleton and cannot be bundled.");
            }

            // A donor entry whose own path does not resolve would be copied into every dance added
            // from here on, so it is refused at the point it is picked up rather than at each write.
            // Scoped to the SELECTED entries deliberately: a dance elsewhere in the same donor file
            // being broken is a reason to repair that dance, not a reason this block cannot be copied.
            var picked = new HashSet<int>(selected.Select(e => e.Offset));
            foreach (var broken in TmbBinary.BrokenPaths(donor))
            {
                if (!picked.Contains(broken.Entry.Offset)) continue;
                throw new TmbFormatException(
                    $"{broken.Entry} {broken.Problem}, so this effect block cannot be copied into " +
                    "another dance.");
            }

            // Tracks first, then items. Donor order already satisfies this in every real file; sorting
            // makes it true by construction, because the splice relies on being able to cut the
            // bundle in two at TrackEntryBytes.
            selected = selected.OrderBy(e => e.Magic == TmbBinary.Tmtr ? 0 : 1).ToList();

            int totalBytes = selected.Sum(e => e.Size);
            var entryBytes = new byte[totalBytes];
            var offsets = new List<int>(selected.Count);
            int at = 0, trackBytes = 0;
            foreach (var entry in selected)
            {
                offsets.Add(at);
                Buffer.BlockCopy(donor.Bytes, entry.Offset, entryBytes, at, entry.Size);
                at += entry.Size;
                if (entry.Magic == TmbBinary.Tmtr) trackBytes = at;
            }

            var items = selected
                .Select(e => (IReadOnlyDictionary<int, TmbPoolItem>)TmbBinary.ResolveItems(donor, e))
                .ToList();

            var effects = new List<string>();
            foreach (var entry in selected)
            {
                foreach (var field in entry.Fields)
                {
                    if (field.Kind != TmbFieldKind.String) continue;
                    string value = TmbBinary.ResolveString(donor, entry, field);
                    if (value.Length > 0 && !effects.Contains(value)) effects.Add(value);
                }
            }

            return new TmbTrackBundle
            {
                Entries = entryBytes,
                Items = items,
                DonorEntries = selected,
                EntryOffsets = offsets,
                TrackIds = trackIds,
                TrackEntryBytes = trackBytes,
                EffectPaths = effects,
            };
        }

        // ---- finding the DJ block ---------------------------------------------------------------

        /// <summary>
        /// The effect paths the prepped dances in a mod share: those carried by at least half of them.
        ///
        /// A majority vote rather than an intersection, because intersecting is far too brittle to
        /// survive real data. Measured on a real pack of 21 prepped dances, the DJ block is twenty
        /// emitters — but one dance is missing <c>vfx/pickles2.avfx</c> and one carries only the first
        /// eight of the twenty, and those two files alone drag a strict intersection down to SEVEN,
        /// which then matches no dance's tracks at all. A vote ignores that drift while still dropping
        /// the genuinely per-dance effects, which appear exactly once each: one dance's
        /// <c>vfx/mikulivec.avfx</c>, another's <c>vfx/axie/shuffle.avfx</c>.
        ///
        /// Only effect strings are counted. The C009 animation name and the C010 facial expressions
        /// are shared just as widely and are not effects.
        /// </summary>
        public static HashSet<string> DjStringSet(IEnumerable<TmbLayout> preppedDances)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int dances = 0;

            foreach (var dance in preppedDances)
            {
                dances++;
                foreach (string value in EffectStrings(dance))
                {
                    counts.TryGetValue(value, out int n);
                    counts[value] = n + 1;
                }
            }

            int threshold = (dances + 1) / 2;
            var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (value, count) in counts)
                if (count >= threshold) shared.Add(value);
            return shared;
        }

        /// <summary>
        /// Whether these dances look like a DJ pack rather than an ordinary dance mod.
        ///
        /// The distinction is SHARING. A DJ pack gives every dance the same effect block, so the block
        /// shows up again and again; an ordinary dance mod gives each dance its own effects, which
        /// show up once each. <see cref="DjStringSet"/> is a majority vote, and a vote over one or two
        /// dances is unanimous by construction — so on a mod with a single dance every effect it
        /// happens to use was promoted to a "DJ effect", and the mod was then offered as somewhere to
        /// install dances into. Picking it would have built every future dance's block from that one
        /// dance's personal effects, giving animations that play and never react to the music.
        ///
        /// Three requirements, all about sharing rather than about any particular effect name. Naming
        /// is not usable: of the DJ packs measured, some anchor on <c>vfx/bpmloop-others.avfx</c>,
        /// one uses <c>vfx/bpmloop.avfx</c>, and one carries 159 effects with no bpmloop at all.
        /// </summary>
        public static bool LooksLikeDjPack(IReadOnlyList<TmbLayout> dances)
        {
            // A vote needs enough voters to mean anything.
            if (dances.Count < MinDancesForDjBlock) return false;

            var shared = DjStringSet(dances);
            if (shared.Count < MinSharedEffects) return false;

            // And the block has to be genuinely common property, not one dance's set that the vote
            // waved through: at least two dances must carry the whole of it.
            int carryAll = dances.Count(d => CoverageOf(d, shared).Count == shared.Count);
            return carryAll >= 2;
        }

        /// <summary>Fewer dances than this and the shared-effect vote is unanimous by construction.</summary>
        private const int MinDancesForDjBlock = 4;

        /// <summary>A block smaller than this is more plausibly one dance's own effects.</summary>
        private const int MinSharedEffects = 5;

        /// <summary>
        /// Whether an entry is one that fires an .avfx.
        ///
        /// C173 belongs here as much as C012 does. Leaving it out is why a donor's two C173 paths were
        /// never enumerated and so never copied into the pack: the animation was rebuilt to point at
        /// <c>vfx/makeyoumineloop.avfx</c> and that file was nowhere under the mod.
        /// </summary>
        public static bool IsEffectEntry(string magic) =>
            magic == TmbBinary.EffectEntry || magic == TmbBinary.AsyncEffectEntry;

        /// <summary>Every distinct effect path in a timeline.</summary>
        private static HashSet<string> EffectStrings(TmbLayout timeline)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in timeline.Entries)
            {
                if (!IsEffectEntry(entry.Magic)) continue;
                foreach (var field in entry.Fields)
                {
                    if (field.Kind != TmbFieldKind.String) continue;
                    string value = TmbBinary.ResolveString(timeline, entry, field);
                    if (value.Length > 0) found.Add(value);
                }
            }
            return found;
        }

        /// <summary>
        /// The prepped dance whose DJ block is the most complete, and that block.
        ///
        /// Donors are not equal: of 21 prepped dances in a real pack, one carries only 8 of the 20 DJ
        /// effects and another is missing one. Copying from either would quietly hand every future
        /// dance a partial block, so the donor is chosen on coverage rather than taken as given.
        /// </summary>
        public static (TmbLayout Donor, List<TmbEntry> Tracks) ChooseDonor(
            IReadOnlyList<TmbLayout> preppedDances, ISet<string> djStrings)
        {
            var candidates = new List<(TmbLayout Donor, List<TmbEntry> Tracks, int Coverage)>();
            foreach (var dance in preppedDances)
            {
                var tracks = FindDjTracks(dance, djStrings);
                if (tracks.Count == 0) continue;
                candidates.Add((dance, tracks, CoverageOf(dance, djStrings).Count));
            }

            if (candidates.Count == 0)
                throw new TmbFormatException(
                    "None of this mod's dances carry a recognisable DJ effect block to copy from.");

            int bestCoverage = candidates.Max(c => c.Coverage);
            var complete = candidates.Where(c => c.Coverage == bestCoverage).ToList();

            // Among donors that carry the same effects, prefer the track SHAPE most of the mod uses.
            // On a real pack 18 of 21 dances split the block across three tracks while one merges two
            // of them; both carry all twenty effects, so coverage alone picks the odd one out roughly
            // at random. Copying the shape the mod overwhelmingly ships is the more conservative
            // choice, because that is the arrangement known to work in game.
            int modalTrackCount = complete
                .GroupBy(c => c.Tracks.Count)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .First().Key;

            var chosen = complete.First(c => c.Tracks.Count == modalTrackCount);
            return (chosen.Donor, chosen.Tracks);
        }

        /// <summary>How much of the DJ block a timeline's trailing tracks actually carry.</summary>
        public static HashSet<string> CoverageOf(TmbLayout timeline, ISet<string> djStrings)
        {
            var byId = new Dictionary<short, TmbEntry>();
            foreach (var entry in timeline.Entries) byId[entry.Id] = entry;

            var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in FindDjTracks(timeline, djStrings))
            {
                foreach (short id in TrackItemIds(timeline, track))
                {
                    if (!byId.TryGetValue(id, out var item)) continue;
                    foreach (var field in item.Fields)
                    {
                        if (field.Kind != TmbFieldKind.String) continue;
                        carried.Add(TmbBinary.ResolveString(timeline, item, field));
                    }
                }
            }
            return carried;
        }

        /// <summary>
        /// The trailing run of tracks on the first actor whose items are all C012 effects drawn from
        /// <paramref name="djStrings"/>.
        ///
        /// Anchored at the END of the actor's track list because that is where the manual workflow
        /// appends it, and because a dance's own effect track sits before it — taking a leading or
        /// unanchored match would sweep that up too.
        /// </summary>
        public static List<TmbEntry> FindDjTracks(TmbLayout timeline, ISet<string> djStrings)
        {
            var found = new List<TmbEntry>();
            if (djStrings.Count == 0) return found;

            var byId = new Dictionary<short, TmbEntry>();
            foreach (var entry in timeline.Entries) byId[entry.Id] = entry;

            var actorTracks = ActorTracks(timeline);
            for (int i = actorTracks.Count - 1; i >= 0; i--)
            {
                if (!IsDjTrack(timeline, actorTracks[i], byId, djStrings)) break;
                found.Insert(0, actorTracks[i]);
            }

            return found;
        }

        /// <summary>
        /// True when the timeline already ends in tracks made purely of DJ effects.
        ///
        /// Deliberately triggered by ANY such track rather than by a complete block. A dance carrying
        /// a partial block is one somebody has already prepared, badly or against an older effect set;
        /// appending a second block on top would leave it firing some effects twice, which is worse
        /// than refusing and saying so. <see cref="CoverageOf"/> reports how much it carries, so the
        /// caller can tell the user whether it is complete.
        /// </summary>
        public static bool IsAlreadyPrepped(TmbLayout timeline, ISet<string> djStrings) =>
            djStrings.Count > 0 && FindDjTracks(timeline, djStrings).Count > 0;

        private static bool IsDjTrack(TmbLayout timeline, TmbEntry track,
            Dictionary<short, TmbEntry> byId, ISet<string> djStrings)
        {
            var ids = TrackItemIds(timeline, track);
            if (ids.Length == 0) return false;

            foreach (short id in ids)
            {
                if (!byId.TryGetValue(id, out var item)) return false;

                // Any effect entry, not C012 alone. This is a TRAILING-RUN scan, so a single
                // unrecognised item does not merely skip its own track — it stops the walk and
                // silently shortens the whole extracted block.
                if (!IsEffectEntry(item.Magic)) return false;

                bool named = false;
                foreach (var field in item.Fields)
                {
                    if (field.Kind != TmbFieldKind.String) continue;
                    if (!djStrings.Contains(TmbBinary.ResolveString(timeline, item, field))) return false;
                    named = true;
                }
                if (!named) return false;
            }

            return true;
        }

        private static short[] TrackItemIds(TmbLayout timeline, TmbEntry track) =>
            track.Fields.Count > 0
                ? TmbBinary.ResolveInt16List(timeline, track, track.Fields[0])
                : Array.Empty<short>();

        /// <summary>
        /// The first actor's tracks, in the order that actor lists them.
        ///
        /// List order, not file order: the actor's own list is what the game walks, and this app
        /// appends to that list, so "trailing" has to mean trailing in it.
        /// </summary>
        public static List<TmbEntry> ActorTracks(TmbLayout timeline)
        {
            var tracks = new List<TmbEntry>();
            var actor = FirstActor(timeline);
            if (actor == null) return tracks;

            var byId = new Dictionary<short, TmbEntry>();
            foreach (var entry in timeline.Entries) byId[entry.Id] = entry;

            foreach (short id in TmbBinary.ResolveInt16List(timeline, actor, actor.Fields[0]))
            {
                if (byId.TryGetValue(id, out var track) && track.Magic == TmbBinary.Tmtr)
                    tracks.Add(track);
            }
            return tracks;
        }

        /// <summary>
        /// The actor the DJ block belongs to. Every real dance has exactly one, and the manual
        /// workflow always targets "Actor 0".
        /// </summary>
        public static TmbEntry? FirstActor(TmbLayout timeline)
        {
            foreach (var entry in timeline.Entries)
                if (entry.Magic == TmbBinary.Tmac && entry.Fields.Count > 0) return entry;
            return null;
        }
    }
}
