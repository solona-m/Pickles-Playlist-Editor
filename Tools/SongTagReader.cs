using System.Linq;
using System.Collections.Generic;
using Pickles_Playlist_Editor.Utils;
using System;
using System.IO;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>Reads artist, album and title from an SCD's Vorbis comments. Never throws.</summary>
    internal static class SongTagReader
    {
        // The comment header sits within the first ~12 KB of almost every SCD; the rest is audio.
        public const int PrefixBytes = 64 * 1024;

        /// <summary>Prefix first, the whole file only if that fails. Null: unreadable or unparseable, so not cached.</summary>
        public static SongTags? Read(string scdFullPath)
        {
            try
            {
                long length = new FileInfo(scdFullPath).Length;
                var tags = FromScdBytes(ReadPrefix(scdFullPath, PrefixBytes));
                if (tags == null && length > PrefixBytes)
                    tags = FromScdBytes(ReadPrefix(scdFullPath, int.MaxValue));
                return tags;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>Up to <paramref name="maxBytes"/> from the start; shared and read-only, closed before returning.</summary>
        internal static byte[] ReadPrefix(string path, int maxBytes)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int n = (int)Math.Min(fs.Length, maxBytes);
            var buf = new byte[n];
            int read = fs.ReadAtLeast(buf, n, throwOnEndOfStream: false);
            return read == n ? buf : buf[..read];
        }

        /// <summary>None for a non-Vorbis SCD; null when the bytes don't parse (e.g. a prefix too short).</summary>
        public static SongTags? FromScdBytes(byte[] scd)
        {
            try
            {
                using var br = new BinaryReader(new MemoryStream(scd));
                var file = new ScdFile(br, false);
                if (file.Audio.Count == 0) return SongTags.None;
                return file.Audio[0].Data is ScdVorbis v ? FromOgg(v.Data) : SongTags.None;
            }
            catch { return null; }
        }

        // The Vorbis comment fields a generator's note can be in.
        private static readonly string[] MarkerKeys =
            ["COMMENT", "DESCRIPTION", "ENCODED_BY", "ENCODED-BY", "SOFTWARE", "PUBLISHER", "ORGANIZATION", "COPYRIGHT", "LICENSE"];

        public static SongTags? FromOgg(byte[] ogg)
        {
            try
            {
                using var r = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
                // by name: NVorbis 0.10's Tags.All throws (a failing dictionary cast)
                var comments = MarkerKeys.SelectMany(k => r.Tags.GetTagMulti(k).Select(v => new KeyValuePair<string, string>(k, v)));
                return SongTags.From(r.Tags.GetTagMulti("ARTIST"), r.Tags.GetTagMulti("ALBUM"), r.Tags.GetTagMulti("TITLE"),
                    AiGeneratorMarker.Detect(comments));
            }
            catch { return null; }
        }
    }
}
