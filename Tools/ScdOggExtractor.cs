using System;
using System.IO;
using Pickles_Playlist_Editor.Utils;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;

namespace Pickles_Playlist_Editor.Tools {
    public static class ScdOggExtractor {
        /// <summary>
        /// Extracts the specified audio entry from an SCD as an OGG file (Vorbis as stored, ADPCM transcoded).
        /// </summary>
        /// <param name="scdPath">Path to the .scd file (or another audio file path accepted by ScdFile.Import).</param>
        /// <param name="outOggPath">Destination .ogg path to write.</param>
        /// <param name="audioIndex">Index of the audio entry to extract (default 0).</param>
        public static void ExtractOgg(string scdPath, string outOggPath, int audioIndex = 0) {
            if (string.IsNullOrWhiteSpace(scdPath)) throw new ArgumentNullException(nameof(scdPath));
            if (string.IsNullOrWhiteSpace(outOggPath)) throw new ArgumentNullException(nameof(outOggPath));
            if (!File.Exists(scdPath)) throw new FileNotFoundException("SCD file not found", scdPath);

            // Work from a copy so the SCD in the mod folder is never held open. The copy is
            // always deleted afterwards: the previous version used Path.GetTempFileName()
            // and leaked two files per playback, and GetTempFileName throws once 65535
            // tmp files have accumulated — a "crash on play" that only hits heavy users.
            string temppath = Path.Combine(Path.GetTempPath(),
                $"pickles-scd-{Guid.NewGuid():N}.scd");
            try {
                File.Copy(scdPath, temppath, true);

                var scd = ScdFile.Import(temppath);
                if (scd.Audio == null || scd.Audio.Count == 0) throw new InvalidOperationException("No audio entries in SCD.");

                if (audioIndex < 0 || audioIndex >= scd.Audio.Count) throw new ArgumentOutOfRangeException(nameof(audioIndex));

                WriteEntryAsOgg(scd.Audio[audioIndex], outOggPath);
            }
            finally {
                try { if (File.Exists(temppath)) File.Delete(temppath); } catch { }
            }
        }

        internal static void WriteEntryAsOgg(ScdAudioEntry entry, string outOggPath) {
            switch (entry.Data) {
                case ScdVorbis vorbis:
                    if (vorbis.Data == null || vorbis.Data.Length == 0) {
                        throw new InvalidOperationException("Vorbis data is empty.");
                    }
                    CreateParentDirectory(outOggPath);
                    File.WriteAllBytes(outOggPath, vorbis.Data);
                    break;
                case ScdAdpcm adpcm:
                    TranscodeAdpcm(adpcm, entry, outOggPath);
                    break;
                case null when entry.DataLength == 0:
                    throw new InvalidOperationException("This track has no audio data.");
                default:
                    throw new InvalidOperationException(
                        $"This track's audio format ({entry.Format}) isn't supported; only Vorbis and ADPCM can be played or extracted.");
            }
        }

        private static void TranscodeAdpcm(ScdAdpcm adpcm, ScdAudioEntry entry, string outOggPath) {
            if (adpcm.Data == null || adpcm.Data.Length == 0) throw new InvalidOperationException("ADPCM data is empty.");

            CreateParentDirectory(outOggPath);
            string wavPath = Path.Combine(Path.GetTempPath(), $"pickles-adpcm-{Guid.NewGuid():N}.wav");
            try {
                File.WriteAllBytes(wavPath, AdpcmWav.Build(adpcm.WaveHeader, adpcm.Data));

                long? loopStart = null, loopEnd = null;
                if (entry.LoopEnd > 0) {
                    loopStart = AdpcmWav.BytesToSamples(adpcm.WaveHeader, entry.LoopStart);
                    loopEnd = AdpcmWav.BytesToSamples(adpcm.WaveHeader, entry.LoopEnd);
                }

                try {
                    FFMpeg.TranscodeToOgg(wavPath, outOggPath, loopStart, loopEnd);
                }
                catch (InvalidOperationException ex) {
                    try { if (File.Exists(outOggPath)) File.Delete(outOggPath); } catch { }
                    throw new InvalidOperationException("Couldn't convert this ADPCM track: " + ex.Message, ex);
                }
            }
            finally {
                try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
            }
        }

        private static void CreateParentDirectory(string path) {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// Extracts to a temp .ogg unique to this call, hands its path to <paramref name="use"/>, then deletes it.
        /// Unique because an ADPCM transcode takes seconds, and a shared name lets concurrent callers collide.
        /// </summary>
        public static T WithExtractedOgg<T>(string scdPath, Func<string, T> use, int audioIndex = 0) {
            string oggPath = Path.Combine(Path.GetTempPath(), $"pickles-ogg-{Guid.NewGuid():N}.ogg");
            try {
                ExtractOgg(scdPath, oggPath, audioIndex);
                return use(oggPath);
            }
            finally {
                try { if (File.Exists(oggPath)) File.Delete(oggPath); } catch { }
            }
        }

        /// <summary>
        /// Returns the OGG bytes for the specified audio entry.
        /// </summary>
        public static byte[] GetOggBytes(string scdPath, int audioIndex = 0) =>
            WithExtractedOgg(scdPath, File.ReadAllBytes, audioIndex);
    }
}
