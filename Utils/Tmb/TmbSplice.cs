using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pickles_Playlist_Editor.Utils.Tmb
{
    /// <summary>One string field to repoint, addressed by the entry's offset in the timeline.</summary>
    internal readonly record struct TmbStringEdit(int EntryOffset, int FieldBodyOffset, string Value);

    /// <summary>
    /// Rebuilds a TMB timeline with an extra block of tracks in it.
    ///
    /// This does not patch the destination — it writes a new timeline from the destination's parts.
    /// That is because the format turns out to be far more rigid than "offsets say where things are",
    /// and every one of these rules was learned by producing a file that parsed perfectly, resolved
    /// perfectly, and was ignored by the game. Measured across 35 real timelines, all of them hold
    /// without exception:
    ///
    ///   ENTRY ORDER. Header, actor list, actor, then EVERY track, then every item. Tracks are never
    ///   interleaved with items.
    ///
    ///   ENTRY IDS ARE POSITIONS. They run 1, 2, 3, ... in file order across every entry that carries
    ///   one (see <see cref="TmbBinary.CarriesId"/>), with no gaps.
    ///
    ///   THE POOL IS THREE CONTIGUOUS SECTIONS, in this order and with no padding between them:
    ///   float blocks, then id lists, then strings, running from the end of the last entry to the end
    ///   of the file. Verified exactly on every real file: each section begins on the byte the
    ///   previous one ends.
    ///
    /// A timeline that satisfies all three works; one that satisfies only the first two still does
    /// not, which is why this rebuilds the pool rather than appending to it. The failure mode is
    /// silent and misleading — the dance still animates, because the animation comes from the .pap's
    /// havok data rather than from the timeline, so a rejected timeline looks like "the effects are
    /// broken" rather than like a bad file.
    ///
    /// Undecoded entries are copied verbatim and never interpreted. That is safe because
    /// <see cref="TmbBinary.PoolCoverage"/> accounts for every byte of every pool in all 35 real files
    /// using only the declared fields — including the one carrying 28 undecoded C042 entries — so
    /// nothing undecoded owns pool data that a rebuild would drop.
    /// </summary>
    internal static class TmbSplice
    {
        /// <summary>Where new TMTR entries go: directly after the last existing track.</summary>
        public static int TrackInsertionPoint(TmbLayout layout)
        {
            int at = -1;
            foreach (var entry in layout.Entries)
                if (entry.Magic == TmbBinary.Tmtr) at = entry.End;

            if (at < 0)
                foreach (var entry in layout.Entries)
                    if (entry.Magic == TmbBinary.Tmac) at = entry.End;

            if (at < 0)
                throw new TmbFormatException(
                    "This timeline has no actor and no tracks, so tracks cannot be added to it.");

            foreach (var entry in layout.Entries)
            {
                if (entry.Offset >= at) continue;
                if (entry.Magic != TmbBinary.Tmtr && !TmbBinary.IsSkeleton(entry.Magic))
                    throw new TmbFormatException(
                        $"{entry} sits among this timeline's tracks, so new tracks cannot be grouped " +
                        "with them safely.");
            }

            return at;
        }

        /// <summary>One entry on its way into the rebuilt timeline.</summary>
        private sealed class Planned
        {
            public TmbEntry Source { get; init; } = null!;
            public byte[] Bytes { get; init; } = Array.Empty<byte>();
            public IReadOnlyDictionary<int, TmbPoolItem> Items { get; init; } =
                new Dictionary<int, TmbPoolItem>();
            public bool FromBundle { get; init; }
            public short NewId { get; set; }
            public int Offset { get; set; }
            public int Body => Offset + TmbBinary.EntryHeaderBytes;
        }

        // ---- adding tracks -------------------------------------------------------------------------

        /// <summary>
        /// Returns <paramref name="destTmb"/> rebuilt with the bundle's tracks added to the first
        /// actor. The input is not modified.
        /// </summary>
        public static byte[] AppendTracks(byte[] destTmb, TmbTrackBundle bundle) =>
            Build(destTmb, bundle);

        /// <summary>
        /// Rebuilds a timeline from its own parts, adding nothing.
        ///
        /// The writer's self-test. A faithful rebuild of a real file must reproduce that file
        /// byte-for-byte: same entries, same ids, same pool sections, same bytes. Any difference is a
        /// bug in the writer, findable offline against 35 known-correct expected outputs instead of
        /// by installing a mod and looking at it.
        /// </summary>
        public static byte[] Rebuild(byte[] tmb) => Build(tmb, null);

        /// <summary>
        /// Rebuilds a timeline without the given entries.
        ///
        /// Makes the add path testable against a known-correct answer: strip the DJ block out of a
        /// dance that works, add it back, and the result must be the original file byte for byte.
        /// </summary>
        public static byte[] Remove(byte[] tmb, IReadOnlyCollection<int> entryOffsets) =>
            Build(tmb, null, entryOffsets);

        private static byte[] Build(byte[] destTmb, TmbTrackBundle? bundle,
            IReadOnlyCollection<int>? exclude = null)
        {
            var dest = TmbBinary.Walk(destTmb);
            var actor = TmbTrackBundle.FirstActor(dest)
                ?? throw new TmbFormatException("This timeline has no actor to attach tracks to.");
            short[] existingTrackIds =
                TmbBinary.ResolveInt16List(dest, actor, actor.Fields[0]);

            int p1 = TrackInsertionPoint(dest);
            var plan = Plan(dest, destTmb, bundle, p1, exclude);

            // Entries deliberately dropped. A list naming one of these loses that reference;
            // a list naming anything else unaccounted for is still a bug and still throws.
            var removedIds = new HashSet<short>();
            if (exclude != null)
                foreach (var entry in dest.Entries)
                    if (exclude.Contains(entry.Offset) && TmbBinary.CarriesId(entry.Magic))
                        removedIds.Add(entry.Id);

            // --- ids are positions, so they fall out of the order alone ---
            var destIds = new Dictionary<short, short>();
            var bundleIds = new Dictionary<short, short>();
            short next = 1;
            foreach (var item in plan)
            {
                if (!TmbBinary.CarriesId(item.Source.Magic)) continue;
                item.NewId = next++;
                (item.FromBundle ? bundleIds : destIds)[item.Source.Id] = item.NewId;
            }

            // --- entry region ---
            int at = TmbBinary.HeaderBytes;
            foreach (var item in plan)
            {
                item.Offset = at;
                at += item.Bytes.Length;
            }
            int entriesEnd = at;

            // --- the pool, in the three sections the format uses, in entry order within each ---
            var floats = new List<(Planned Entry, int Field, byte[] Data)>();
            var lists = new List<(Planned Entry, int Field, short[] Ids)>();
            var strings = new List<(Planned Entry, int Field, string Value)>();

            foreach (var item in plan)
            {
                foreach (var field in item.Source.Fields)
                {
                    if (!item.Items.TryGetValue(field.BodyOffset, out var value)) continue;
                    switch (field.Kind)
                    {
                        case TmbFieldKind.Float32List:
                            floats.Add((item, field.BodyOffset, value.Bytes));
                            break;
                        case TmbFieldKind.Int16List:
                            // The actor's list is rebuilt rather than remapped: it is the one list
                            // whose CONTENTS change — tracks added, and any removed track dropped —
                            // so remapping it would throw on ids that are deliberately gone.
                            // Every other list must remap exactly, and still throws if it cannot.
                            bool isActorList = !item.FromBundle && item.Source.Offset == actor.Offset;
                            short[] ids = isActorList
                                ? existingTrackIds.Where(destIds.ContainsKey).Select(id => destIds[id])
                                    .Concat((bundle?.TrackIds ?? Array.Empty<short>())
                                        .Select(id => bundleIds[id]))
                                    .ToArray()
                                : ReferencedBy(item, value.Ids, destIds, bundleIds, removedIds);
                            lists.Add((item, field.BodyOffset, ids));
                            break;
                        case TmbFieldKind.String:
                            strings.Add((item, field.BodyOffset, value.Text));
                            break;
                    }
                }
            }

            var offsets = new Dictionary<(Planned, int), int>();
            int cursor = entriesEnd;
            foreach (var (entry, field, data) in floats)
            {
                offsets[(entry, field)] = cursor;
                cursor += data.Length;
            }
            foreach (var (entry, field, ids) in lists)
            {
                offsets[(entry, field)] = cursor;
                cursor += ids.Length * 2;
            }

            // Strings are shared: the same text written once, as a writer for this format would.
            var stringAt = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (entry, field, value) in strings)
            {
                if (!stringAt.TryGetValue(value, out int where))
                {
                    where = cursor;
                    stringAt[value] = where;
                    cursor += Encoding.ASCII.GetByteCount(value) + 1;
                }
                offsets[(entry, field)] = where;
            }

            // --- write ---
            var output = new byte[cursor];
            Encoding.ASCII.GetBytes(TmbBinary.TimelineMagic).CopyTo(output, 0);
            TmbBinary.WriteInt32(output, 4, output.Length);
            TmbBinary.WriteInt32(output, 8, plan.Count);

            foreach (var item in plan)
            {
                Buffer.BlockCopy(item.Bytes, 0, output, item.Offset, item.Bytes.Length);
                if (TmbBinary.CarriesId(item.Source.Magic))
                    TmbBinary.WriteInt16(output, item.Body, item.NewId);
            }

            foreach (var (entry, field, data) in floats)
            {
                int target = offsets[(entry, field)];
                Buffer.BlockCopy(data, 0, output, target, data.Length);
                TmbBinary.WriteInt32(output, entry.Body + field, target - entry.Body);
            }

            foreach (var (entry, field, ids) in lists)
            {
                int target = offsets[(entry, field)];
                for (int i = 0; i < ids.Length; i++)
                    TmbBinary.WriteInt16(output, target + i * 2, ids[i]);
                TmbBinary.WriteInt32(output, entry.Body + field, target - entry.Body);

                var declared = entry.Source.Fields.First(f => f.BodyOffset == field);
                TmbBinary.WriteInt32(output, entry.Body + declared.CountBodyOffset, ids.Length);
            }

            foreach (var (entry, field, value) in strings)
            {
                int target = offsets[(entry, field)];
                Encoding.ASCII.GetBytes(value).CopyTo(output, target);
                TmbBinary.WriteInt32(output, entry.Body + field, target - entry.Body);
            }

            var rebuilt = TmbBinary.Walk(output);
            AssertIdsSequential(rebuilt);
            AssertPoolIsCanonical(rebuilt);
            return output;
        }

        /// <summary>
        /// The final entry sequence: the destination's entries with the bundle's tracks slotted in
        /// beside the existing tracks and its items after the existing items.
        /// </summary>
        private static List<Planned> Plan(TmbLayout dest, byte[] destTmb, TmbTrackBundle? bundle,
            int p1, IReadOnlyCollection<int>? exclude)
        {
            var plan = new List<Planned>();

            void FromDest(bool beforeP1)
            {
                foreach (var entry in dest.Entries)
                {
                    if (entry.Offset < p1 != beforeP1) continue;
                    if (exclude != null && exclude.Contains(entry.Offset)) continue;
                    var bytes = new byte[entry.Size];
                    Buffer.BlockCopy(destTmb, entry.Offset, bytes, 0, entry.Size);
                    plan.Add(new Planned
                    {
                        Source = entry,
                        Bytes = bytes,
                        Items = TmbBinary.ResolveItems(dest, entry),
                    });
                }
            }

            void FromBundle(bool tracks)
            {
                if (bundle == null) return;
                for (int i = 0; i < bundle.DonorEntries.Count; i++)
                {
                    var entry = bundle.DonorEntries[i];
                    if (entry.Magic == TmbBinary.Tmtr != tracks) continue;
                    var bytes = new byte[entry.Size];
                    Buffer.BlockCopy(bundle.Entries, bundle.EntryOffsets[i], bytes, 0, entry.Size);
                    plan.Add(new Planned
                    {
                        Source = entry,
                        Bytes = bytes,
                        Items = bundle.Items[i],
                        FromBundle = true,
                    });
                }
            }

            FromDest(beforeP1: true);
            FromBundle(tracks: true);
            FromDest(beforeP1: false);
            FromBundle(tracks: false);
            return plan;
        }

        private static short[] ReferencedBy(Planned entry, short[] ids,
            Dictionary<short, short> destIds, Dictionary<short, short> bundleIds,
            HashSet<short> removedIds)
        {
            var map = entry.FromBundle ? bundleIds : destIds;
            var mapped = new List<short>(ids.Length);
            foreach (short id in ids)
            {
                if (!entry.FromBundle && removedIds.Contains(id)) continue;
                if (!map.TryGetValue(id, out short to))
                    throw new TmbFormatException(
                        $"{entry.Source} names entry id {id}, which this splice did not account for.");
                mapped.Add(to);
            }
            return mapped.ToArray();
        }

        // ---- the rules, asserted ---------------------------------------------------------------------

        /// <summary>
        /// Proves entry ids run 1, 2, 3, ... in file order, and that every track precedes every item.
        /// </summary>
        public static void AssertIdsSequential(TmbLayout layout)
        {
            short expected = 1;
            bool seenItem = false;

            foreach (var entry in layout.Entries)
            {
                if (entry.Magic == TmbBinary.Tmtr && seenItem)
                    throw new TmbFormatException(
                        $"{entry} follows an item entry; every track must precede every item. " +
                        "The timeline was not rewritten.");
                if (!TmbBinary.IsSkeleton(entry.Magic) && entry.Magic != TmbBinary.Tmtr)
                    seenItem = true;

                if (!TmbBinary.CarriesId(entry.Magic)) continue;
                if (entry.Id != expected)
                    throw new TmbFormatException(
                        $"Entry ids must run in file order: expected {expected} at {entry}, found " +
                        $"{entry.Id}. The timeline was not rewritten.");
                expected++;
            }
        }

        /// <summary>
        /// Proves the pool is the three contiguous sections the format uses — float blocks, then id
        /// lists, then strings — running from the last entry to the end of the file with no gaps.
        ///
        /// A timeline that breaks this is accepted by every parser and rejected by the game.
        /// </summary>
        public static void AssertPoolIsCanonical(TmbLayout layout)
        {
            // Sections are a property of WHERE things landed, not of the order fields happen to be
            // declared in — C012 declares its string before its float blocks, so walking declarations
            // says nothing about layout.
            var starts = new int[3] { int.MaxValue, int.MaxValue, int.MaxValue };
            var ends = new int[3];

            foreach (var entry in layout.Entries)
            {
                foreach (var field in entry.Fields)
                {
                    int section = field.Kind switch
                    {
                        TmbFieldKind.Float32List => 0,
                        TmbFieldKind.Int16List => 1,
                        _ => 2,
                    };

                    int target = TmbBinary.Target(layout, entry, field);
                    if (target < layout.EntriesEnd || target >= layout.TotalSize)
                        throw new TmbFormatException(
                            $"{entry}+{field.BodyOffset} points outside the pool. " +
                            "The timeline was not rewritten.");

                    int length = field.Kind switch
                    {
                        TmbFieldKind.Float32List => TmbBinary.ResolveFloat32List(layout, entry, field).Length,
                        TmbFieldKind.Int16List => TmbBinary.ResolveInt16List(layout, entry, field).Length * 2,
                        _ => TmbBinary.ResolveString(layout, entry, field).Length + 1,
                    };

                    starts[section] = Math.Min(starts[section], target);
                    ends[section] = Math.Max(ends[section], target + length);
                }
            }

            int cursor = layout.EntriesEnd;
            for (int section = 0; section < 3; section++)
            {
                if (starts[section] == int.MaxValue) continue;   // a timeline may use none of a kind
                if (starts[section] < cursor)
                    throw new TmbFormatException(
                        $"Pool section {section} starts at {starts[section]}, inside what came before " +
                        $"it ({cursor}). The timeline was not rewritten.");
                cursor = ends[section];
            }

            if (cursor != layout.TotalSize)
                throw new TmbFormatException(
                    $"The pool ends at {cursor} but the timeline is {layout.TotalSize} bytes. " +
                    "The timeline was not rewritten.");

            var (covered, total) = TmbBinary.PoolCoverage(layout);
            if (covered != total)
                throw new TmbFormatException(
                    $"The rebuilt pool has {total - covered} bytes belonging to nothing; a real " +
                    "timeline has none. The timeline was not rewritten.");
        }

        private static TmbEntry FindByOffset(TmbLayout layout, int offset)
        {
            foreach (var entry in layout.Entries)
                if (entry.Offset == offset) return entry;
            throw new TmbFormatException($"No entry at offset {offset} after splicing.");
        }

        // ---- repointing strings ---------------------------------------------------------------------

        /// <summary>
        /// Points string fields at new values, appended to the end of the timeline.
        ///
        /// Safe to leave as an append rather than a rebuild ONLY because it runs before
        /// <see cref="AppendTracks"/>, which rebuilds the pool from resolved values and so cleans up
        /// after it. Used on its own it would leave the string section non-canonical.
        /// </summary>
        public static byte[] SetStrings(byte[] tmb, IReadOnlyList<TmbStringEdit> edits)
        {
            if (edits == null || edits.Count == 0) return (byte[])tmb.Clone();

            var layout = TmbBinary.Walk(tmb);

            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            var appended = new List<byte>();
            foreach (var edit in edits)
            {
                if (positions.ContainsKey(edit.Value)) continue;
                positions[edit.Value] = layout.TotalSize + appended.Count;
                appended.AddRange(Encoding.ASCII.GetBytes(edit.Value));
                appended.Add(0x00);
            }

            var output = new byte[layout.TotalSize + appended.Count];
            Buffer.BlockCopy(tmb, 0, output, 0, layout.TotalSize);
            appended.CopyTo(output, layout.TotalSize);
            TmbBinary.WriteInt32(output, 4, output.Length);

            foreach (var edit in edits)
            {
                var entry = FindByOffset(layout, edit.EntryOffset);
                bool known = false;
                foreach (var field in entry.Fields)
                {
                    if (field.Kind != TmbFieldKind.String || field.BodyOffset != edit.FieldBodyOffset)
                        continue;
                    TmbBinary.WriteInt32(output, entry.Body + field.BodyOffset,
                        positions[edit.Value] - entry.Body);
                    known = true;
                }

                if (!known)
                    throw new TmbFormatException(
                        $"{entry} has no known string field at body offset {edit.FieldBodyOffset}.");
            }

            return output;
        }
    }
}
