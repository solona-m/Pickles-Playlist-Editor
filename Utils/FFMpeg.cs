using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Utils
{
    internal static class FFMpeg
    {
        public static void ConvertMp3ToOgg(string mp3Path, string oggName)
        {
            Run("-i " + '"' + mp3Path + '"' + " -vn -acodec libvorbis -f ogg -q 7 -af \"apad=pad_dur=5\" " + '"' + oggName + '"');
        }

        public static void StripVideo(string oggName)
        {
            string tmp = Path.Combine(Path.GetDirectoryName(oggName), "tmp_" + Path.GetFileName(oggName));
            Run("-i " + '"' + oggName + '"' + " -vn -codec:a libvorbis -q 7  " + '"' + tmp + '"');
            File.Move(tmp, oggName,true);
        }

        public static void NormalizeVolume(string oggName)
        {
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
                    analyzeProc.StartInfo.Arguments = "-i " + '"' + oggName + '"' + " -af loudnorm=I=-16:LRA=11:TP=-1.5:print_format=json -f null -";
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

                string tmp = Path.Combine(Path.GetDirectoryName(oggName), "tmp_" + Path.GetFileName(oggName));
                Run(
                    "-i " + '"' + oggName + '"' +
                    " -af \"loudnorm=I=-16:LRA=11:TP=-1.5:measured_I=" + measuredI +
                    ":measured_LRA=" + measuredLRA +
                    ":measured_TP=" + measuredTP +
                    ":measured_thresh=" + measuredThresh +
                    ":offset=" + offsetStr + "\" -acodec libvorbis -q:a 7 " + '"' + tmp + '"');

                File.Move(tmp, oggName, true);
            }
        }

        public static void AdjustVolume(string oggName, int dbChange)
        {
            string tmp = Path.Combine(Path.GetDirectoryName(oggName), "tmp_" + Path.GetFileName(oggName));
            Run($"-i \"{oggName}\" -filter:a \"volume ={dbChange}dB\" \"{tmp}\"");
            File.Move(tmp, oggName, true);
        }

        public static void Equalize(string oggName, string filterChain)
        {
            string tmp = Path.Combine(Path.GetDirectoryName(oggName), "tmp_" + Path.GetFileName(oggName));
            Run($"-y -i \"{oggName}\" -af \"{filterChain}\" -vn -acodec libvorbis -q:a 7 \"{tmp}\"");
            File.Move(tmp, oggName,true);
        }

        private static void Run(string arguments)
        {
            using var process = new Process();
            process.StartInfo.FileName = "ffmpeg.exe";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = false;
            process.StartInfo.Arguments = arguments;

            process.Start();
            string message = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{message}");
            }
        }
    }
}
