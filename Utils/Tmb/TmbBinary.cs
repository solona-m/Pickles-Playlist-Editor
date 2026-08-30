using System;
using System.Collections.Generic;
using System.Text;

namespace Pickles_Playlist_Editor.Utils.Tmb
{
    /// <summary>Why a byte array is not a usable TMB timeline.</summary>
    internal sealed class TmbFormatException : Exception
    {
        public TmbFormatException(string message) : base(message) { }
    }

    /// <summary>What the int32 at a known field position points at.</summary>
    internal enum TmbFieldKind
    {
        /// <summary>A NUL-terminated ASCII string in the string pool.</summary>
        String,

        /// <summary>An int16 array in the extra pool, whose length is a separate count field.</summary>
        Int16List,

        /// <summary>A float array in the extra pool, whose length is a separate count field.</summary>
        Float32List,
    }

    /// <summary>
    /// One int32 in an entry body that holds an offset rather than a value.
    ///
    /// <paramref name="BodyOffset"/> and <paramref name="CountBodyOffset"/> are both measured from the
    /// entry BODY — see <see cref="TmbBinary"/> for why that distinction is the whole design.
    /// </summary>
    internal readonly record struct TmbField(
        int BodyOffset,
        TmbFieldKind Kind,
        int CountBodyOffset = -1)
    {
        /// <summary>Bytes per element, for the list kinds.</summary>
        public int ElementBytes => Kind == TmbFieldKind.Float32List ? 4 : 2;
    }

    /// <summary>
    /// The pool data one field points at, lifted out so it can be written somewhere else.
    ///
    /// Holding the VALUE rather than an offset is what lets a timeline be rebuilt rather than patched:
    /// the pool can be laid out from scratch in the order the format actually uses, and every offset
    /// recomputed from where things landed.
    /// </summary>
    internal readonly record struct TmbPoolItem(TmbFieldKind Kind, string Text, byte[] Bytes, short[] Ids);

    /// <summary>One entry in a TMB, located but not fully decoded.</summary>
    internal sealed class TmbEntry
    {
        public string Magic { get; init; } = string.Empty;

        /// <summary>Offset of the entry, relative to the start of the TMB.</summary>
        public int Offset { get; init; }

        public int Size { get; init; }

        /// <summary>Where the body begins: past the 4-byte magic and the int32 size.</summary>
        public int Body => Offset + TmbBinary.EntryHeaderBytes;

        public int End => Offset + Size;

        /// <summary>The entry id, referenced by the int16 lists in TMAL / TMAC / TMTR.</summary>
        public short Id { get; init; }

        /// <summary>The int16 sharing the first body word with <see cref="Id"/>. 1 on C012, else 0.</summary>
        public short Flag { get; init; }

        /// <summary>
        /// The offset fields this entry is known to carry, or empty when it is not understood.
        ///
        /// Empty is not "has no offsets" — it is "we do not know", which is exactly why an entry with
        /// no fields must never be inspected or moved relative to the pools.
        /// </summary>
        public IReadOnlyList<TmbField> Fields { get; init; } = Array.Empty<TmbField>();

        /// <summary>True when this entry is carried verbatim and never interpreted.</summary>
        public bool IsOpaque => Fields.Count == 0;

        public override string ToString() => $"{Magic}@0x{Offset:X4}+{Size} id={Id}";
    }

    /// <summary>A walked TMB: its header, its entries, and where the pools begin.</summary>
    internal sealed class TmbLayout
    {
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
        public int TotalSize { get; init; }
        public IReadOnlyList<TmbEntry> Entries { get; init; } = Array.Empty<TmbEntry>();

        /// <summary>
        /// One past the last entry, and therefore the first byte of the pools. Every pool target in a
        /// healthy file resolves at or after this.
        /// </summary>
        public int EntriesEnd { get; init; }

        public int PoolBytes => TotalSize - EntriesEnd;

        /// <summary>
        /// The highest entry id in use. New entries are numbered from here.
        ///
        /// TMPP and TMAL are excluded because they DO NOT CARRY AN ID: the first word of their body
        /// is a pool offset, and reading it as an id yields values in the thousands. Numbering new
        /// entries from that produced ids like 184 in a file whose real ids ran 1..7, and the game
        /// silently ignored every one of them — the dance animated and fired nothing.
        /// </summary>
        public short MaxId
        {
            get
            {
                short max = 0;
                foreach (var e in Entries)
                {
                    if (!TmbBinary.CarriesId(e.Magic)) continue;
                    if (e.Id > max) max = e.Id;
                }
                return max;
            }
        }
    }

    /// <summary>
    /// The TMB timeline embedded in a .pap: locating its entries and resolving the pool targets of
    /// the handful of entry types this app needs to understand.
    ///
    /// This is NOT a TMB reader. VFXEditor models roughly a hundred entry types; porting them would
    /// put a hundred hand-transcribed binary layouts on the critical path of an operation that must
    /// not corrupt a user's mod, and it would buy nothing, because of one structural property:
    ///
    ///   POOL OFFSETS ARE RELATIVE TO THE ENTRY BODY, not to the file and not to the field. So if a
    ///   run of bytes is inserted BEFORE an entry, that entry and the pools move together and its
    ///   stored offsets stay byte-identical. Only entries that sit before the insertion point need
    ///   fixing up. <see cref="TmbSplice"/> exploits this by inserting immediately after the
    ///   TMDH/TMPP/TMAL/TMAC skeleton, which leaves the entire fixup set inside the four types below
    ///   and lets every undecoded C-entry be copied verbatim and never looked at.
    ///
    /// The layout of a TMB is
    ///   <c>["TMLB"] [int32 totalSize] [int32 entryCount] [entries] [extra pool] [string pool]</c>
    /// and each entry is <c>[4-char magic] [int32 size] [body]</c>, so the entry list walks on sizes
    /// alone without knowing any type. The table below was derived across all 35 dance .pap in a real
    /// DJ mod pack and then verified by hand against the bytes; every row resolves to a plausible,
    /// correctly-terminated target in every file.
    /// </summary>
    internal static class TmbBinary
    {
        public const string TimelineMagic = "TMLB";

        /// <summary>Magic, total size, entry count.</summary>
        public const int HeaderBytes = 12;

        /// <summary>Magic and size, ahead of every entry body.</summary>
        public const int EntryHeaderBytes = 8;

        public const string Tmdh = "TMDH";
        public const string Tmpp = "TMPP";
        public const string Tmal = "TMAL";
        public const string Tmac = "TMAC";
        public const string Tmtr = "TMTR";
        public const string AnimationEntry = "C009";
        public const string SoundEntry = "C063";

        /// <summary>
        /// The entry types the skeleton is made of: the timeline header, its name, the actor list and
        /// the actors. They always precede every track and item entry, which is what gives
        /// <see cref="TmbSplice"/> a safe insertion point.
        /// </summary>
        public static bool IsSkeleton(string magic) =>
            magic is Tmdh or Tmpp or Tmal or Tmac;

        /// <summary>
        /// Whether an entry's body begins with its id.
        ///
        /// Every entry type does EXCEPT <see cref="Tmpp"/> and <see cref="Tmal"/>, whose first body
        /// word is a pool offset instead. Measured across 35 real timelines: ids run 1, 2, 3, ... in
        /// file order over exactly the entries this returns true for, with no gaps — including the
        /// undecoded C042 and C118 types, which are numbered right along with the rest.
        ///
        /// That sequence is not decoration. Numbering appended entries outside it made the game
        /// ignore them completely.
        /// </summary>
        public static bool CarriesId(string magic) =>
            magic is not (Tmpp or Tmal);

        /// <summary>
        /// Known entry types: their exact size, and the offset fields in their bodies.
        ///
        /// The size is part of the key, not just documentation. An entry whose declared size differs
        /// from the one measured here is treated as opaque rather than decoded against this table —
        /// a layout that changed under us must degrade into "carry it verbatim", never into reading
        /// the wrong four bytes as an offset.
        /// </summary>
        private static readonly Dictionary<string, (int Size, TmbField[] Fields)> KnownTypes = new()
        {
            [Tmdh] = (16, Array.Empty<TmbField>()),
            [Tmpp] = (12, new[] { new TmbField(0, TmbFieldKind.String) }),
            [Tmal] = (16, new[] { new TmbField(0, TmbFieldKind.Int16List, CountBodyOffset: 4) }),
            [Tmac] = (28, new[] { new TmbField(12, TmbFieldKind.Int16List, CountBodyOffset: 16) }),
            [Tmtr] = (24, new[] { new TmbField(4, TmbFieldKind.Int16List, CountBodyOffset: 8) }),
            [AnimationEntry] = (24, new[] { new TmbField(12, TmbFieldKind.String) }),
            ["C010"] = (40, new[] { new TmbField(24, TmbFieldKind.String) }),

            // C012 carries FOUR pool pointers, each with its own count immediately after it, not one
            // record. Declaring only the first is what made a spliced dance animate correctly and
            // fire nothing: the other three kept their donor-relative distances and pointed into
            // whatever the new file happened to have there. Nothing detected it, because a table that
            // does not name a field cannot check it — which is why PoolCoverage exists now.
            ["C012"] = (72, new[]
            {
                new TmbField(12, TmbFieldKind.String),
                new TmbField(24, TmbFieldKind.Float32List, CountBodyOffset: 28),
                new TmbField(32, TmbFieldKind.Float32List, CountBodyOffset: 36),
                new TmbField(40, TmbFieldKind.Float32List, CountBodyOffset: 44),
                new TmbField(48, TmbFieldKind.Float32List, CountBodyOffset: 52),
            }),
            [SoundEntry] = (32, new[] { new TmbField(12, TmbFieldKind.String) }),
        };

        // ---- walking -------------------------------------------------------------------------------

        /// <summary>
        /// Locates every entry, or throws.
        ///
        /// Deliberately strict about the things a later splice relies on — the declared total size,
        /// the declared entry count, and every entry lying inside the file. A TMB that disagrees with
        /// itself is one this app must decline to rewrite, not one it should guess at.
        /// </summary>
        public static TmbLayout Walk(byte[] tmb)
        {
            if (tmb == null || tmb.Length < HeaderBytes)
                throw new TmbFormatException("Not a TMB timeline: shorter than its own header.");

            if (ReadMagic(tmb, 0) != TimelineMagic)
                throw new TmbFormatException("Not a TMB timeline: the TMLB signature is missing.");

            int totalSize = BitConverter.ToInt32(tmb, 4);
            int entryCount = BitConverter.ToInt32(tmb, 8);

            if (totalSize != tmb.Length)
                throw new TmbFormatException(
                    $"TMB declares {totalSize} bytes but occupies {tmb.Length}.");

            if (entryCount < 0 || entryCount > (totalSize - HeaderBytes) / EntryHeaderBytes)
                throw new TmbFormatException($"TMB declares an impossible entry count ({entryCount}).");

            var entries = new List<TmbEntry>(entryCount);
            int at = HeaderBytes;

            for (int i = 0; i < entryCount; i++)
            {
                if (at + EntryHeaderBytes > totalSize)
                    throw new TmbFormatException(
                        $"TMB entry {i} starts past the end of the file (offset {at}).");

                string magic = ReadMagic(tmb, at);
                int size = BitConverter.ToInt32(tmb, at + 4);

                if (size < EntryHeaderBytes || at + size > totalSize)
                    throw new TmbFormatException(
                        $"TMB entry {i} ({magic}) declares a size of {size} that does not fit at offset {at}.");

                // The body's first word is an int16 id and an int16 flag. Guarded because a
                // hypothetical 8-byte entry would have no body at all.
                short id = size >= EntryHeaderBytes + 4 ? BitConverter.ToInt16(tmb, at + EntryHeaderBytes) : (short)0;
                short flag = size >= EntryHeaderBytes + 4 ? BitConverter.ToInt16(tmb, at + EntryHeaderBytes + 2) : (short)0;

                entries.Add(new TmbEntry
                {
                    Magic = magic,
                    Offset = at,
                    Size = size,
                    Id = id,
                    Flag = flag,
                    Fields = FieldsFor(magic, size),
                });

                at += size;
            }

            if (at > totalSize)
                throw new TmbFormatException("TMB entry list runs past the end of the file.");

            return new TmbLayout
            {
                Bytes = tmb,
                TotalSize = totalSize,
                Entries = entries,
                EntriesEnd = at,
            };
        }

        /// <summary>
        /// The offset fields for an entry, or empty when the type is unknown OR its size does not
        /// match what this table was measured against.
        /// </summary>
        private static IReadOnlyList<TmbField> FieldsFor(string magic, int size) =>
            KnownTypes.TryGetValue(magic, out var known) && known.Size == size
                ? known.Fields
                : Array.Empty<TmbField>();

        public static string ReadMagic(byte[] bytes, int at) =>
            Encoding.ASCII.GetString(bytes, at, 4);

        // ---- resolving -----------------------------------------------------------------------------

        /// <summary>
        /// Where a field points, as an offset from the start of the TMB.
        ///
        /// This single line is the property the whole design rests on: the stored int32 is added to
        /// the entry's BODY, so it encodes a distance rather than a position.
        /// </summary>
        public static int Target(TmbLayout layout, TmbEntry entry, TmbField field) =>
            entry.Body + BitConverter.ToInt32(layout.Bytes, entry.Body + field.BodyOffset);

        /// <summary>The NUL-terminated string a field points at.</summary>
        public static string ResolveString(TmbLayout layout, TmbEntry entry, TmbField field)
        {
            if (field.Kind != TmbFieldKind.String)
                throw new ArgumentException($"{entry.Magic}+{field.BodyOffset} is not a string field.");

            int at = Target(layout, entry, field);
            if (at < 0 || at >= layout.TotalSize)
                throw new TmbFormatException(
                    $"{entry} points its string outside the file (offset {at} of {layout.TotalSize}).");

            int length = 0;
            while (at + length < layout.TotalSize && layout.Bytes[at + length] != 0x00)
                length++;

            if (at + length >= layout.TotalSize)
                throw new TmbFormatException($"{entry} points at an unterminated string.");

            return Encoding.ASCII.GetString(layout.Bytes, at, length);
        }

        /// <summary>The int16 id list a field points at.</summary>
        public static short[] ResolveInt16List(TmbLayout layout, TmbEntry entry, TmbField field)
        {
            if (field.Kind != TmbFieldKind.Int16List)
                throw new ArgumentException($"{entry.Magic}+{field.BodyOffset} is not a list field.");

            int at = Target(layout, entry, field);
            int count = BitConverter.ToInt32(layout.Bytes, entry.Body + field.CountBodyOffset);

            if (count < 0 || at < 0 || at + count * 2 > layout.TotalSize)
                throw new TmbFormatException(
                    $"{entry} points at a {count}-item list that does not fit at offset {at}.");

            var ids = new short[count];
            for (int i = 0; i < count; i++)
                ids[i] = BitConverter.ToInt16(layout.Bytes, at + i * 2);
            return ids;
        }

        /// <summary>The float array a field points at, copied out as raw bytes.</summary>
        public static byte[] ResolveFloat32List(TmbLayout layout, TmbEntry entry, TmbField field)
        {
            if (field.Kind != TmbFieldKind.Float32List)
                throw new ArgumentException($"{entry.Magic}+{field.BodyOffset} is not a float list field.");

            int at = Target(layout, entry, field);
            int bytes = ListCount(layout, entry, field) * field.ElementBytes;

            if (bytes < 0 || at < 0 || at + bytes > layout.TotalSize)
                throw new TmbFormatException(
                    $"{entry} points at a {bytes}-byte list that does not fit at offset {at}.");

            var record = new byte[bytes];
            Buffer.BlockCopy(layout.Bytes, at, record, 0, bytes);
            return record;
        }

        private static int ListCount(TmbLayout layout, TmbEntry entry, TmbField field) =>
            BitConverter.ToInt32(layout.Bytes, entry.Body + field.CountBodyOffset);

        /// <summary>
        /// How many bytes of the pools are actually claimed by a declared field, and how many are not.
        ///
        /// This exists because of a bug that every other check here was structurally incapable of
        /// catching. C012 was modelled with one pool pointer when it has four; the three that were
        /// not declared kept their donor-relative offsets through a splice and pointed at nothing,
        /// and the resulting dance animated perfectly while firing no effects and playing no music.
        /// The resolve-before-and-after invariant passed throughout, because a field the table does
        /// not name is a field it cannot compare.
        ///
        /// Coverage is the check that does not depend on knowing what you are looking for: if the
        /// declared fields do not account for the whole pool, something in there is reachable only
        /// through a pointer this code has not modelled, and splicing will corrupt it. It is allowed
        /// to be imperfect — alignment padding and genuinely dead bytes exist — but a large gap is a
        /// standing invitation to look again.
        /// </summary>
        public static (int Covered, int Total) PoolCoverage(TmbLayout layout)
        {
            var claimed = new bool[layout.TotalSize];

            void Claim(int at, int length)
            {
                for (int i = at; i < at + length && i < layout.TotalSize; i++)
                    if (i >= 0) claimed[i] = true;
            }

            foreach (var entry in layout.Entries)
            {
                foreach (var field in entry.Fields)
                {
                    int at = Target(layout, entry, field);
                    switch (field.Kind)
                    {
                        case TmbFieldKind.String:
                            Claim(at, ResolveString(layout, entry, field).Length + 1);
                            break;
                        case TmbFieldKind.Int16List:
                        case TmbFieldKind.Float32List:
                            Claim(at, ListCount(layout, entry, field) * field.ElementBytes);
                            break;
                    }
                }
            }

            int covered = 0;
            for (int i = layout.EntriesEnd; i < layout.TotalSize; i++)
                if (claimed[i]) covered++;

            return (covered, layout.PoolBytes);
        }

        /// <summary>
        /// Every resolvable target of every known entry, as a comparable string.
        ///
        /// This is the verification hook: resolving the whole file before and after a rewrite and
        /// comparing the two lists proves that no understood entry lost track of its data. Paired
        /// with the byte-identity assertion on the untouched region, it covers both halves.
        /// </summary>
        public static List<string> ResolveAll(TmbLayout layout)
        {
            var resolved = new List<string>();
            foreach (var entry in layout.Entries)
            {
                foreach (var field in entry.Fields)
                {
                    string value = field.Kind switch
                    {
                        TmbFieldKind.String => ResolveString(layout, entry, field),
                        TmbFieldKind.Int16List => string.Join(",", ResolveInt16List(layout, entry, field)),
                        TmbFieldKind.Float32List => Convert.ToHexString(ResolveFloat32List(layout, entry, field)),
                        _ => string.Empty,
                    };
                    resolved.Add($"{entry.Magic}+{field.BodyOffset}={value}");
                }
            }
            return resolved;
        }

        /// <summary>
        /// Everything an entry points at, keyed by the field's body offset.
        ///
        /// The unit a rebuild works in: resolve an entry's pool data once, then write it wherever the
        /// new layout puts it and set the offset to match.
        /// </summary>
        public static Dictionary<int, TmbPoolItem> ResolveItems(TmbLayout layout, TmbEntry entry)
        {
            var items = new Dictionary<int, TmbPoolItem>();
            foreach (var field in entry.Fields)
            {
                items[field.BodyOffset] = field.Kind switch
                {
                    TmbFieldKind.String => new TmbPoolItem(field.Kind,
                        ResolveString(layout, entry, field), Array.Empty<byte>(), Array.Empty<short>()),
                    TmbFieldKind.Float32List => new TmbPoolItem(field.Kind,
                        string.Empty, ResolveFloat32List(layout, entry, field), Array.Empty<short>()),
                    TmbFieldKind.Int16List => new TmbPoolItem(field.Kind,
                        string.Empty, Array.Empty<byte>(), ResolveInt16List(layout, entry, field)),
                    _ => default,
                };
            }
            return items;
        }

        // ---- primitives used by the splice ----------------------------------------------------------

        public static void WriteInt32(byte[] bytes, int at, int value) =>
            BitConverter.GetBytes(value).CopyTo(bytes, at);

        public static void WriteInt16(byte[] bytes, int at, short value) =>
            BitConverter.GetBytes(value).CopyTo(bytes, at);

        public static int ReadInt32(byte[] bytes, int at) => BitConverter.ToInt32(bytes, at);

        /// <summary>Rounds up to the next multiple of four, as the pools are aligned.</summary>
        public static int RoundUpToFour(int value) => (value + 3) & ~3;
    }
}
