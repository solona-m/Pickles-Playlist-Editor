using System;
using System.Collections.Generic;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// One <c>Sound</c> field of an <c>AvfxEmitter</c>, located inside a .avfx file's bytes.
    /// </summary>
    internal readonly record struct SoundRef(int TagOffset, int SizeOffset, int PathOffset, int PathLength)
    {
        /// <summary>Bytes the payload occupies: size + pad, i.e. the 4-byte bucket the size sits in.</summary>
        public int PayloadBytes => AvfxSoundPath.RoundUpToFour(PathLength + 1);
    }

    /// <summary>
    /// The AVFX side of the sound-path rename: finding the emitter field that REQUESTS a sound path,
    /// and rewriting it in place.
    ///
    /// Nothing else in this repo parses AVFX, and this deliberately does not either — it is a byte
    /// scanner with structural checks, not a format reader. Two facts make that safe, and both were
    /// measured from real emitter files rather than derived:
    ///
    ///   LAYOUT. VFXEditor serialises the field as
    ///   <c>[4 bytes "mNdS"] [int32 size] [ASCII path] [NUL] [pad]</c>, where <c>mNdS</c> is
    ///   <c>SdNm</c> byte-reversed (AVFX stores chunk names reversed), <c>size</c> is path length + 1
    ///   — it counts the NUL and excludes the pad — and <c>pad</c> rounds the payload up to a multiple
    ///   of 4. Verified byte for byte:
    ///   <c>74 02 00 00 | 6D 4E 64 53 | 12 00 00 00 | "sound/bpmloop.scd" | 00 00 00 | 6F 4E 64 53</c>
    ///   — 17 characters, size 18, two bytes of pad, 28 bytes of footprint.
    ///
    ///   POSITION. It is nested inside an <c>Emit</c> chunk rather than being a top-level node, so a
    ///   chunk walk never reaches it. Scanning bytes is not a shortcut here; it is the only way in
    ///   short of writing a full AVFX reader.
    ///
    /// This file is kept free of every other part of the app so the format logic can be exercised on
    /// its own. Orchestration — discovery across a mod folder, backups, the JSON half — lives in
    /// <see cref="SoundPathRename"/>.
    /// </summary>
    internal static class AvfxSoundPath
    {
        /// <summary>"SdNm" byte-reversed, as AVFX stores chunk names.</summary>
        private static readonly byte[] SoundChunkTag = Encoding.ASCII.GetBytes("mNdS");

        private static readonly byte[] ScdExtension = Encoding.ASCII.GetBytes(".scd");

        /// <summary>The tag and the size field that must precede a path for it to be a Sound field.</summary>
        private const int HeaderBytes = 8;

        internal static int RoundUpToFour(int value) => (value + 3) & ~3;

        /// <summary>
        /// A game path as Penumbra spells it: trimmed, forward slashes, no leading slash. Matches
        /// <see cref="PenumbraMeta.NormalizeScdKey"/>, duplicated here only to keep this file
        /// standalone.
        /// </summary>
        public static string Normalize(string? path) =>
            (path ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');

        // ---- the length bucket -------------------------------------------------------------------

        /// <summary>
        /// How long a replacement path may be, in characters.
        ///
        /// The size field and the pad share a 4-byte bucket, so ANY new size in the same bucket leaves
        /// the chunk's footprint identical — and therefore every enclosing chunk size correct without
        /// touching one. That is what makes this a few dozen lines of patching rather than a full
        /// AVFX rewriter.
        ///
        /// The rule: <c>newPath.Length + 1</c> must round up to the same multiple of 4 as
        /// <c>oldPath.Length + 1</c>. So <c>sound/bpmloop.scd</c> (17, size 18, bucket 20) admits
        /// 16-19 characters, and <c>sound/dam.scd</c> (13, size 14, bucket 16) admits 12-15. Nothing
        /// may assume the 17-character case.
        ///
        /// Anything outside the bucket is refused rather than resized: rewriting ancestor chunk sizes
        /// is real surgery on a format this app has no parser for, and it buys a few characters.
        /// </summary>
        public static (int Min, int Max) AllowedLengthRange(string oldPath)
        {
            int bucket = RoundUpToFour((oldPath ?? string.Empty).Length + 1);
            return (Math.Max(1, bucket - 4), bucket - 1);
        }

        public static bool FitsBucket(string oldPath, string newPath) =>
            RoundUpToFour((oldPath ?? string.Empty).Length + 1)
            == RoundUpToFour((newPath ?? string.Empty).Length + 1);

        // ---- scanning ----------------------------------------------------------------------------

        /// <summary>
        /// Every Sound field in <paramref name="bytes"/> that names <paramref name="path"/>.
        /// </summary>
        public static List<SoundRef> FindReferences(byte[] bytes, string path)
        {
            var found = new List<SoundRef>();
            string wanted = Normalize(path);
            if (bytes == null || bytes.Length < HeaderBytes || wanted.Length == 0)
                return found;

            for (int i = 0; i + ScdExtension.Length <= bytes.Length; i++)
            {
                if (!MatchesExtension(bytes, i))
                    continue;

                int end = i + ScdExtension.Length;   // one past the final 'd'
                if (end >= bytes.Length || bytes[end] != 0x00)
                    continue;                        // a path is always NUL-terminated

                // Walk back over everything that could be part of a path, then test the candidate
                // starts LONGEST FIRST rather than trusting the walk to have stopped in the right
                // place. The byte immediately before the path is the size field's low byte, and that
                // can itself be a legal path character — 0x2E is '.', which is exactly what a
                // 45-character path's size field holds — so a greedy walk steps over the boundary and
                // loses the reference silently. Testing every candidate start costs nothing and
                // cannot miss one.
                int start = i;
                while (start > HeaderBytes && IsPathByte(bytes[start - 1]))
                    start--;

                for (int candidate = start; candidate < i; candidate++)
                {
                    int length = end - candidate;
                    if (length != wanted.Length)
                        continue;
                    if (BitConverter.ToInt32(bytes, candidate - 4) != length + 1)
                        continue;
                    if (!MatchesTag(bytes, candidate - HeaderBytes))
                        continue;
                    if (!string.Equals(Encoding.ASCII.GetString(bytes, candidate, length), wanted,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    found.Add(new SoundRef(candidate - HeaderBytes, candidate - 4, candidate, length));
                    break;
                }
            }

            return found;
        }

        /// <summary>
        /// Every offset at which <paramref name="path"/> appears as plain ASCII, whether or not it is
        /// a Sound field. Used to prove that every textual occurrence was accounted for: one that no
        /// validated reference covers may be a field this scanner failed to recognise, and patching
        /// the rest while leaving it behind is the half-renamed mod the whole operation avoids.
        /// </summary>
        public static List<int> FindLiteralOffsets(byte[] bytes, string path)
        {
            var offsets = new List<int>();
            byte[] literal = Encoding.ASCII.GetBytes(Normalize(path));
            if (bytes == null || literal.Length == 0)
                return offsets;

            for (int i = 0; i + literal.Length <= bytes.Length; i++)
            {
                int j = 0;
                while (j < literal.Length && bytes[i + j] == literal[j]) j++;
                if (j == literal.Length) offsets.Add(i);
            }
            return offsets;
        }

        private static bool MatchesExtension(byte[] bytes, int offset)
        {
            for (int j = 0; j < ScdExtension.Length; j++)
            {
                byte b = bytes[offset + j];
                if (b >= 'A' && b <= 'Z') b += 32;
                if (b != ScdExtension[j]) return false;
            }
            return true;
        }

        private static bool MatchesTag(byte[] bytes, int offset)
        {
            if (offset < 0 || offset + SoundChunkTag.Length > bytes.Length)
                return false;
            for (int j = 0; j < SoundChunkTag.Length; j++)
            {
                if (bytes[offset + j] != SoundChunkTag[j]) return false;
            }
            return true;
        }

        private static bool IsPathByte(byte b) =>
            b is (>= (byte)'a' and <= (byte)'z')
              or (>= (byte)'A' and <= (byte)'Z')
              or (>= (byte)'0' and <= (byte)'9')
              or (byte)'/' or (byte)'_' or (byte)'-' or (byte)'.' or (byte)' ';

        // ---- patching ----------------------------------------------------------------------------

        /// <summary>
        /// Rewrites every reference in place: the leaf size field, the string, the NUL and the pad.
        /// The file's total length never changes and no ancestor chunk is touched.
        ///
        /// The pad bytes are ZEROED rather than left alone. When the new path is shorter than the old
        /// one the tail of the old string would otherwise survive inside the padding, which reads back
        /// as garbage and defeats the verification pass that runs afterwards.
        /// </summary>
        public static void Patch(byte[] bytes, IReadOnlyList<SoundRef> refs, string newPath)
        {
            byte[] encoded = Encoding.ASCII.GetBytes(Normalize(newPath));

            foreach (var reference in refs)
            {
                int payload = reference.PayloadBytes;
                if (encoded.Length + 1 > payload)
                    throw new InvalidOperationException(
                        $"'{newPath}' does not fit the {payload}-byte slot the old path occupied.");

                Array.Clear(bytes, reference.PathOffset, payload);
                Buffer.BlockCopy(encoded, 0, bytes, reference.PathOffset, encoded.Length);
                BitConverter.GetBytes(encoded.Length + 1).CopyTo(bytes, reference.SizeOffset);
            }
        }
    }
}
