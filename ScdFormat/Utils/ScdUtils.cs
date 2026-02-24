using Pickles_Playlist_Editor;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using VfxEditor.Interop;
using VfxEditor.ScdFormat;
using System.Text.Json;
using System.Globalization;

namespace VfxEditor.Formats.ScdFormat.Utils {
    public static class ScdUtils {
        public static string VorbisHeader => Path.Combine( Plugin.RootLocation, "Files", "vorbis_header.bin" );

        public static void ConvertWavToOgg( string wavPath ) {
            Cleanup();
            
            InteropUtils.Run( "oggenc2.exe", "-s 0 -q 6 --scale 1.0 --resample 44100 -o \"" + Path.GetDirectoryName(wavPath) + "\" \""+wavPath+"\"", true, out var _ );
        }

        public static string Convertmp3toOgg(string mp3Path)
        {
            // Convert MP3 -> intermediate OGG that we'll run loudnorm on
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(mp3Path), "ogg"));
            string oggDir = Path.Combine(Path.GetDirectoryName(mp3Path), "ogg");
            string oggNameV = Path.Combine(oggDir, Path.GetFileNameWithoutExtension(mp3Path) + "_v.ogg");
            string oggName = Path.Combine(oggDir, Path.GetFileNameWithoutExtension(mp3Path) + ".ogg");

            if (File.Exists(oggNameV)) File.Delete(oggNameV);
            if (File.Exists(oggName)) File.Delete(oggName);

            // First conversion: encode to vorbis with a little padding to avoid truncation issues
            var convProc = new Process();
            convProc.StartInfo.FileName = "ffmpeg.exe";
            convProc.StartInfo.UseShellExecute = false;
            convProc.StartInfo.CreateNoWindow = true;
            convProc.StartInfo.RedirectStandardError = true;
            convProc.StartInfo.Arguments = "-i " + '"' + mp3Path + '"' + " -vn -acodec libvorbis -f ogg -q 7 -af \"apad=pad_dur=5\" " + '"' + oggNameV + '"';
            convProc.Start();
            convProc.WaitForExit();
            if (convProc.ExitCode != 0 || !File.Exists(oggNameV))
            {
                throw new InvalidDataException("Error converting mp3 to ogg.");
            }

            StripVideo(oggNameV, oggName);


            // Cleanup intermediate file
            if (File.Exists(oggNameV))
            {
                try { File.Delete(oggNameV); } catch { /* ignore */ }
            }

            oggNameV = oggName; // we'll analyze the stripped version to avoid cover art metadata interfering with loudnorm analysis

            // Run two-pass loudnorm:
            // 1) pass: analyze and capture JSON stats (ffmpeg writes analysis to stderr)
            string ffmpegAnalysisOutput = null;
            if (Settings.NormalizeVolume)
            {
                try
                {
                    var analyzeProc = new Process();
                    analyzeProc.StartInfo.FileName = "ffmpeg.exe";
                    analyzeProc.StartInfo.UseShellExecute = false;
                    analyzeProc.StartInfo.CreateNoWindow = true;
                    analyzeProc.StartInfo.RedirectStandardError = true;
                    // print_format=json makes ffmpeg/loudnorm emit a JSON block to stderr
                    analyzeProc.StartInfo.Arguments = "-i " + '"' + oggNameV + '"' + " -af loudnorm=I=-16:LRA=11:TP=-1.5:print_format=json -f null -";
                    analyzeProc.Start();
                    ffmpegAnalysisOutput = analyzeProc.StandardError.ReadToEnd();
                    analyzeProc.WaitForExit();
                    if (analyzeProc.ExitCode != 0)
                    {
                        // Analysis can still exit non-zero; we'll attempt to parse whatever was emitted
                    }
                }
                catch
                {
                    ffmpegAnalysisOutput = string.Empty;
                }
            }

            // Try to extract the JSON block from stderr and parse measured values
            bool parsed = false;
            double measured_I = 0, measured_LRA = 0, measured_TP = 0, measured_thresh = 0, offset = 0;
            if (!string.IsNullOrEmpty(ffmpegAnalysisOutput))
            {
                // attempt to find the JSON object in the stderr text
                var firstBrace = ffmpegAnalysisOutput.IndexOf('{');
                var lastBrace = ffmpegAnalysisOutput.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                {
                    var json = ffmpegAnalysisOutput.Substring(firstBrace, lastBrace - firstBrace + 1);
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        // loudnorm print_format=json typically contains keys like "input_i","input_lra","input_tp","input_thresh","target_offset"
                        string[] tryKeysInputI = new[] { "input_i", "measured_I", "measured_i", "input_I" };
                        string[] tryKeysInputLRA = new[] { "input_lra", "measured_LRA", "measured_lra" };
                        string[] tryKeysInputTP = new[] { "input_tp", "measured_TP", "measured_tp" };
                        string[] tryKeysInputThresh = new[] { "input_thresh", "measured_thresh", "measured_Thresh" };
                        string[] tryKeysOffset = new[] { "target_offset", "offset", "targetOffset" };

                        bool TryGetDouble(JsonElement el, string[] keys, out double result)
                        {
                            foreach (var k in keys)
                            {
                                if (el.TryGetProperty(k, out var prop))
                                {
                                    var s = prop.GetString();
                                    if (!string.IsNullOrEmpty(s) && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
                                        return true;
                                    // sometimes values are numeric already
                                    if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out result)) return true;
                                }
                            }
                            result = 0;
                            return false;
                        }

                        if (TryGetDouble(root, tryKeysInputI, out measured_I)
                            && TryGetDouble(root, tryKeysInputLRA, out measured_LRA)
                            && TryGetDouble(root, tryKeysInputTP, out measured_TP)
                            && TryGetDouble(root, tryKeysInputThresh, out measured_thresh)
                            && TryGetDouble(root, tryKeysOffset, out offset))
                        {
                            parsed = true;
                        }
                    }
                    catch
                    {
                        parsed = false;
                    }
                }
            }

            // If parsing succeeded, run second pass using measured_* parameters, otherwise just re-mux (or re-encode) without loudnorm
            if (parsed)
            {
                // use invariant culture to ensure '.' decimal separator
                var measuredI = measured_I.ToString(CultureInfo.InvariantCulture);
                var measuredLRA = measured_LRA.ToString(CultureInfo.InvariantCulture);
                var measuredTP = measured_TP.ToString(CultureInfo.InvariantCulture);
                var measuredThresh = measured_thresh.ToString(CultureInfo.InvariantCulture);
                var offsetStr = offset.ToString(CultureInfo.InvariantCulture);

                var secondProc = new Process();
                secondProc.StartInfo.FileName = "ffmpeg.exe";
                secondProc.StartInfo.UseShellExecute = false;
                secondProc.StartInfo.CreateNoWindow = false;
                secondProc.StartInfo.RedirectStandardError = true;
                // apply loudnorm with measured values in the second pass and encode to final ogg
                secondProc.StartInfo.Arguments =
                    "-i " + '"' + oggNameV + '"' +
                    " -af \"loudnorm=I=-16:LRA=11:TP=-1.5:measured_I=" + measuredI +
                    ":measured_LRA=" + measuredLRA +
                    ":measured_TP=" + measuredTP +
                    ":measured_thresh=" + measuredThresh +
                    ":offset=" + offsetStr + "\"" + '"' + oggName + '"';
                secondProc.Start();
                secondProc.WaitForExit();
            }

            return oggName;
        }

        private static void StripVideo(string oggNameV, string oggName)
        {
            // No analysis results — produce final ogg by copying audio stream (strip cover art)
            var stripProc = new Process();
            stripProc.StartInfo.FileName = "ffmpeg.exe";
            stripProc.StartInfo.UseShellExecute = false;
            stripProc.StartInfo.CreateNoWindow = true;
            stripProc.StartInfo.Arguments = "-i " + '"' + oggNameV + '"' + " -vn -codec:a libvorbis -q 7  " + '"' + oggName + '"';
            stripProc.Start();
            stripProc.WaitForExit();
            if (stripProc.ExitCode != 0 || !File.Exists(oggName))
            {
                throw new InvalidDataException("Error creating final ogg.");
            }
        }

        public static void ConvertToAdpcm( string wavPath ) {
            Cleanup();
            InteropUtils.Run( "adpcmencode3.exe", $"-b 256 \"{wavPath}\" \"{ScdManager.ConvertWav}\"", false, out var _ );
        }

        public static void Cleanup() {
            if( File.Exists( ScdManager.ConvertWav ) ) File.Delete( ScdManager.ConvertWav );
            if( File.Exists( ScdManager.ConvertOgg ) ) File.Delete( ScdManager.ConvertOgg );
        }

        public static void XorDecode( byte[] vorbisHeader, byte encodeByte ) {
            for( var i = 0; i < vorbisHeader.Length; i++ ) {
                vorbisHeader[i] ^= encodeByte;
            }
        }

        public static void XorDecodeFromTable( byte[] dataFile, int dataLength ) {
            var byte1 = dataLength & 0xFF & 0x7F;
            var byte2 = byte1 & 0x3F;
            for( var i = 0; i < dataFile.Length; i++ ) {
                var xorByte = XORTABLE[byte2 + i & 0xFF];
                xorByte &= 0xFF;
                xorByte ^= dataFile[i] & 0xFF;
                xorByte ^= byte1;
                dataFile[i] = ( byte )xorByte;
            }
        }

        public static readonly int[] XORTABLE = [
            0x003A,
            0x0032,
            0x0032,
            0x0032,
            0x0003,
            0x007E,
            0x0012,
            0x00F7,
            0x00B2,
            0x00E2,
            0x00A2,
            0x0067,
            0x0032,
            0x0032,
            0x0022,
            0x0032,
            0x0032,
            0x0052,
            0x0016,
            0x001B,
            0x003C,
            0x00A1,
            0x0054,
            0x007B,
            0x001B,
            0x0097,
            0x00A6,
            0x0093,
            0x001A,
            0x004B,
            0x00AA,
            0x00A6,
            0x007A,
            0x007B,
            0x001B,
            0x0097,
            0x00A6,
            0x00F7,
            0x0002,
            0x00BB,
            0x00AA,
            0x00A6,
            0x00BB,
            0x00F7,
            0x002A,
            0x0051,
            0x00BE,
            0x0003,
            0x00F4,
            0x002A,
            0x0051,
            0x00BE,
            0x0003,
            0x00F4,
            0x002A,
            0x0051,
            0x00BE,
            0x0012,
            0x0006,
            0x0056,
            0x0027,
            0x0032,
            0x0032,
            0x0036,
            0x0032,
            0x00B2,
            0x001A,
            0x003B,
            0x00BC,
            0x0091,
            0x00D4,
            0x007B,
            0x0058,
            0x00FC,
            0x000B,
            0x0055,
            0x002A,
            0x0015,
            0x00BC,
            0x0040,
            0x0092,
            0x000B,
            0x005B,
            0x007C,
            0x000A,
            0x0095,
            0x0012,
            0x0035,
            0x00B8,
            0x0063,
            0x00D2,
            0x000B,
            0x003B,
            0x00F0,
            0x00C7,
            0x0014,
            0x0051,
            0x005C,
            0x0094,
            0x0086,
            0x0094,
            0x0059,
            0x005C,
            0x00FC,
            0x001B,
            0x0017,
            0x003A,
            0x003F,
            0x006B,
            0x0037,
            0x0032,
            0x0032,
            0x0030,
            0x0032,
            0x0072,
            0x007A,
            0x0013,
            0x00B7,
            0x0026,
            0x0060,
            0x007A,
            0x0013,
            0x00B7,
            0x0026,
            0x0050,
            0x00BA,
            0x0013,
            0x00B4,
            0x002A,
            0x0050,
            0x00BA,
            0x0013,
            0x00B5,
            0x002E,
            0x0040,
            0x00FA,
            0x0013,
            0x0095,
            0x00AE,
            0x0040,
            0x0038,
            0x0018,
            0x009A,
            0x0092,
            0x00B0,
            0x0038,
            0x0000,
            0x00FA,
            0x0012,
            0x00B1,
            0x007E,
            0x0000,
            0x00DB,
            0x0096,
            0x00A1,
            0x007C,
            0x0008,
            0x00DB,
            0x009A,
            0x0091,
            0x00BC,
            0x0008,
            0x00D8,
            0x001A,
            0x0086,
            0x00E2,
            0x0070,
            0x0039,
            0x001F,
            0x0086,
            0x00E0,
            0x0078,
            0x007E,
            0x0003,
            0x00E7,
            0x0064,
            0x0051,
            0x009C,
            0x008F,
            0x0034,
            0x006F,
            0x004E,
            0x0041,
            0x00FC,
            0x000B,
            0x00D5,
            0x00AE,
            0x0041,
            0x00FC,
            0x000B,
            0x00D5,
            0x00AE,
            0x0041,
            0x00FC,
            0x003B,
            0x0070,
            0x0071,
            0x0064,
            0x0033,
            0x0032,
            0x0012,
            0x0032,
            0x0032,
            0x0036,
            0x0070,
            0x0034,
            0x002B,
            0x0056,
            0x0022,
            0x0070,
            0x003A,
            0x0013,
            0x00B7,
            0x0026,
            0x0060,
            0x00BA,
            0x001B,
            0x0094,
            0x00AA,
            0x0040,
            0x0038,
            0x0000,
            0x00FA,
            0x00B2,
            0x00E2,
            0x00A2,
            0x0067,
            0x0032,
            0x0032,
            0x0012,
            0x0032,
            0x00B2,
            0x0032,
            0x0032,
            0x0032,
            0x0032,
            0x0075,
            0x00A3,
            0x0026,
            0x007B,
            0x0083,
            0x0026,
            0x00F9,
            0x0083,
            0x002E,
            0x00FF,
            0x00E3,
            0x0016,
            0x007D,
            0x00C0,
            0x001E,
            0x0063,
            0x0021,
            0x0007,
            0x00E3,
            0x0001
        ];
    }
}
