using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>
    /// Artist, album, title and comment-type tags from an imported mp3/m4a/flac/wav itself (#20): the SCD made from it
    /// carries no tags on purpose, so these go to the tag cache only. Never throws.
    /// </summary>
    internal static class SourceTagReader
    {
        public static SongTags? Read(string sourcePath)
        {
            try { return FromFields(FFMpeg.ReadFfMetadata(sourcePath)); }
            catch (Exception ex)
            {
                Logger.LogWarn("Could not read tags from '{File}': {Error}", Path.GetFileName(sourcePath), ex.Message);
                return null;
            }
        }

        public static SongTags FromFields(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var list = fields.ToList();
            IEnumerable<string> Values(string key) => list
                .Where(f => f.Key.Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                .SelectMany(f => f.Value.Split(';')); // ffmpeg joins repeated tags with ";"
            string marker = AiGeneratorMarker.MarkerText(list);
            return SongTags.From(Values("artist"), Values("album"), Values("title"), AiGeneratorMarker.DetectText(marker), marker);
        }

        /// <summary>Tags worth storing for an import; null for .ogg/.scd (their SCD keeps its own tags), on failure, or when empty.</summary>
        public static SongTags? ForImport(string sourcePath, Func<string, SongTags?> read)
        {
            string ext = Path.GetExtension(sourcePath);
            if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".scd", StringComparison.OrdinalIgnoreCase)) return null;
            try { return read(sourcePath) is { IsEmpty: false } t ? t : null; }
            catch { return null; }
        }
    }
}
