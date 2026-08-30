using System;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>Why a byte array is not a usable .pap.</summary>
    internal sealed class PapFormatException : Exception
    {
        public PapFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// The .pap container: a header, a fixed-width animation table, an opaque havok blob, and an
    /// embedded TMB timeline that runs to the end of the file.
    ///
    /// Nothing else in this repo parses .pap, and this deliberately does not parse all of it either —
    /// it is a container splitter, in the same spirit as <see cref="AvfxSoundPath"/>. The havok blob
    /// is never inspected, only carried. That is safe because of two facts measured across all 35
    /// dance .pap in a real DJ mod pack rather than derived from a spec:
    ///
    ///   LAYOUT. The three offsets in the header delimit everything:
    ///   <c>[0x1A header] [NumAnimations * 40 info] [havok] [TMB]</c>, with <c>InfoOffset</c> 0x1A,
    ///   <c>HavokOffset</c> == InfoOffset + NumAnimations*40, and <c>FooterOffset</c> the start of the
    ///   TMB. The havok blob carries its own packfile magic <c>CA B0 0D 1E D0 11 FA CE</c>.
    ///
    ///   THE NAME IS NOT IN THE HAVOK DATA. <c>cbem_dance_male</c> occurs exactly twice in every one
    ///   of the 35 files: once in the info table at offset 26, once in the TMB string pool. So
    ///   retargeting a dance animation is a fixed-width write into a 32-byte field plus a TMB string
    ///   repoint — it needs no havok tooling, and the file length does not change.
    ///
    /// Kept free of every other part of the app so the format logic can be exercised on its own.
    /// Orchestration lives in <see cref="DanceMod"/>.
    /// </summary>
    internal sealed class PapFile
    {
        /// <summary>"pap " — stored unreversed, unlike AVFX chunk names.</summary>
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("pap ");

        /// <summary>The only version seen in the wild, and the only one this will open.</summary>
        public const int ExpectedVersion = 0x00020001;

        /// <summary>Bytes before the animation table. Constant, and equal to InfoOffset in 35/35.</summary>
        public const int HeaderBytes = 0x1A;

        /// <summary>One entry in the animation table: a 32-byte name plus 8 bytes this does not model.</summary>
        public const int AnimationBytes = 40;

        /// <summary>The name field width. A name must fit in 31 characters plus its NUL.</summary>
        public const int NameBytes = 32;

        /// <summary>What every prepped DJ dance calls its looping animation.</summary>
        public const string DanceAnimationName = "cbem_dance_male_2lp";

        /// <summary>The non-looping intro name, where a dance ships one.</summary>
        public const string StartAnimationName = "cbem_dance_male_1";

        private readonly byte[] _bytes;

        public int Version { get; }
        public int AnimationCount { get; }
        public int ModelId { get; }
        public byte ModelType { get; }
        public byte Variant { get; }
        public int InfoOffset { get; }
        public int HavokOffset { get; }
        public int FooterOffset { get; }

        /// <summary>The race code this .pap targets, as Penumbra spells it in a game path.</summary>
        public string RaceCode => "c" + ModelId.ToString("D4");

        public int Length => _bytes.Length;

        private PapFile(byte[] bytes, int version, int animationCount, int modelId, byte modelType,
            byte variant, int infoOffset, int havokOffset, int footerOffset)
        {
            _bytes = bytes;
            Version = version;
            AnimationCount = animationCount;
            ModelId = modelId;
            ModelType = modelType;
            Variant = variant;
            InfoOffset = infoOffset;
            HavokOffset = havokOffset;
            FooterOffset = footerOffset;
        }

        /// <summary>
        /// Parses the container, or throws.
        ///
        /// Structural sanity only — it does NOT refuse a multi-animation file. Reading has to stay
        /// permissive so the round-trip check can be run over anything on disk; the surgery layer is
        /// where the narrower preconditions belong, because refusing there can say what the user
        /// should do about it.
        /// </summary>
        public static PapFile Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < HeaderBytes)
                throw new PapFormatException("Not a .pap file: shorter than its own header.");

            for (int i = 0; i < Magic.Length; i++)
            {
                if (bytes[i] != Magic[i])
                    throw new PapFormatException("Not a .pap file: the pap signature is missing.");
            }

            int version = BitConverter.ToInt32(bytes, 0x04);
            if (version != ExpectedVersion)
                throw new PapFormatException(
                    $"Unsupported .pap version 0x{version:X8} (expected 0x{ExpectedVersion:X8}).");

            int count = BitConverter.ToInt16(bytes, 0x08);
            int modelId = BitConverter.ToInt16(bytes, 0x0A);
            byte modelType = bytes[0x0C];
            byte variant = bytes[0x0D];
            int info = BitConverter.ToInt32(bytes, 0x0E);
            int havok = BitConverter.ToInt32(bytes, 0x12);
            int footer = BitConverter.ToInt32(bytes, 0x16);

            if (count < 1)
                throw new PapFormatException("This .pap declares no animations.");

            // Ordering and bounds, checked together: a single reversed pair here would have the
            // splitter hand a caller another section's bytes to rewrite.
            if (info < HeaderBytes || havok < info || footer < havok || footer > bytes.Length)
                throw new PapFormatException(
                    $"Section offsets are inconsistent (info {info}, havok {havok}, " +
                    $"timeline {footer}, length {bytes.Length}).");

            if (havok - info != count * AnimationBytes)
                throw new PapFormatException(
                    $"This .pap declares {count} animation(s) but reserves {havok - info} bytes for " +
                    $"them instead of {count * AnimationBytes}.");

            return new PapFile(bytes, version, count, modelId, modelType, variant, info, havok, footer);
        }

        /// <summary>The animation name, read out of its fixed-width field.</summary>
        public string GetAnimationName(int index)
        {
            int at = NameOffset(index);
            int length = 0;
            while (length < NameBytes && _bytes[at + length] != 0x00)
                length++;
            return Encoding.ASCII.GetString(_bytes, at, length);
        }

        /// <summary>
        /// Overwrites the animation name in place.
        ///
        /// The field is fixed width, so this changes no offset and no length — which is the whole
        /// reason the rename half of preparing a dance is free. The field is cleared first: a shorter
        /// name would otherwise leave the tail of the old one behind, and that reads back as a longer
        /// string on the next parse.
        /// </summary>
        public void SetAnimationName(int index, string name)
        {
            byte[] encoded = Encoding.ASCII.GetBytes(name ?? string.Empty);
            if (encoded.Length + 1 > NameBytes)
                throw new PapFormatException(
                    $"'{name}' is {encoded.Length} characters; a .pap animation name may be at most " +
                    $"{NameBytes - 1}.");

            int at = NameOffset(index);
            Array.Clear(_bytes, at, NameBytes);
            Buffer.BlockCopy(encoded, 0, _bytes, at, encoded.Length);
        }

        private int NameOffset(int index)
        {
            if (index < 0 || index >= AnimationCount)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"This .pap has {AnimationCount} animation(s).");
            return InfoOffset + index * AnimationBytes;
        }

        /// <summary>The embedded TMB timeline, copied out.</summary>
        public byte[] GetTimeline()
        {
            var tmb = new byte[_bytes.Length - FooterOffset];
            Buffer.BlockCopy(_bytes, FooterOffset, tmb, 0, tmb.Length);
            return tmb;
        }

        /// <summary>
        /// The file with a different timeline spliced onto the end.
        ///
        /// Every header offset is unchanged — the timeline is the last section, so only the total
        /// length moves. Nothing in the header records that length, which is why this needs no fixups
        /// at all and why the havok blob can be carried through untouched.
        /// </summary>
        public byte[] WithTimeline(byte[] timeline)
        {
            if (timeline == null || timeline.Length == 0)
                throw new PapFormatException("Refusing to write a .pap with an empty timeline.");

            var result = new byte[FooterOffset + timeline.Length];
            Buffer.BlockCopy(_bytes, 0, result, 0, FooterOffset);
            Buffer.BlockCopy(timeline, 0, result, FooterOffset, timeline.Length);
            return result;
        }

        /// <summary>The file as it currently stands, including any in-place name edits.</summary>
        public byte[] ToBytes() => (byte[])_bytes.Clone();
    }
}
