using System;
using System.IO;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;

namespace Pickles_Playlist_Editor.Tools {
    public static class ScdOggExtractor {
        /// <summary>
        /// Extracts the specified audio entry from an SCD as an OGG file.
        /// </summary>
        /// <param name="scdPath">Path to the .scd file (or another audio file path accepted by ScdFile.Import).</param>
        /// <param name="outOggPath">Destination .ogg path to write.</param>
        /// <param name="audioIndex">Index of the audio entry to extract (default 0).</param>
        public static void ExtractOgg(string scdPath, string outOggPath, int audioIndex = 0) {
            if (string.IsNullOrWhiteSpace(scdPath)) throw new ArgumentNullException(nameof(scdPath));
            if (string.IsNullOrWhiteSpace(outOggPath)) throw new ArgumentNullException(nameof(outOggPath));
            if (!File.Exists(scdPath)) throw new FileNotFoundException("SCD file not found", scdPath);

            // Load SCD (uses existing import logic)
            var scd = ScdFile.Import(scdPath);
            if (scd.Audio == null || scd.Audio.Count == 0) throw new InvalidOperationException("No audio entries in SCD.");

            if (audioIndex < 0 || audioIndex >= scd.Audio.Count) throw new ArgumentOutOfRangeException(nameof(audioIndex));

            var entry = scd.Audio[audioIndex];

            // Ensure it's a Vorbis entry with data we can write
            if (!(entry.Data is ScdVorbis vorbis) || vorbis == null) {
                throw new InvalidOperationException("Selected audio entry is not Vorbis or has no data.");
            }

            if (vorbis.Data == null || vorbis.Data.Length == 0) {
                throw new InvalidOperationException("Vorbis data is empty.");
            }

            // Ensure destination directory exists
            var dir = Path.GetDirectoryName(outOggPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllBytes(outOggPath, vorbis.Data);
        }

        /// <summary>
        /// Returns the raw OGG bytes for the specified audio entry.
        /// </summary>
        public static byte[] GetOggBytes(string scdPath, int audioIndex = 0) {
            var scd = ScdFile.Import(scdPath);
            var entry = scd.Audio[audioIndex];
            if (!(entry.Data is ScdVorbis vorbis)) throw new InvalidOperationException("Selected audio entry is not Vorbis or has no data.");
            return vorbis.Data;
        }
    }
}