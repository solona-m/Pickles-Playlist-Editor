using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

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
            string tmp = Path.Combine(Path.GetDirectoryName(oggName), "tmp_" + Path.GetFileName(oggName));
            var ci = CultureInfo.InvariantCulture;
            double targetLufs = Settings.NormalizationLoudnessLufs;
            double ceilingDb = Settings.NormalizationTruePeak;

            // Pass 1: measure the input's integrated loudness (LUFS). loudnorm with
            // print_format=json emits the measurements on stderr; we only need input_i.
            string measure = Run(
                $"-i \"{oggName}\" -af loudnorm=I=-5:LRA=11:TP=-0.3:print_format=json -f null -");

            string filter;
            if (TryParseMeasuredLoudness(measure, out double inputI))
            {
                // Apply the exact gain to reach the target loudness, then pin any
                // resulting peaks with a brickwall limiter at the ceiling. loudnorm
                // protects true-peak and so can't make an already-0dBFS master louder;
                // driving gain into a limiter raises the actual loudness (matching
                // Audacity's "normalize + amplify into limiting") while every track
                // still lands on the same target level for a consistent playlist.
                double gain = targetLufs - inputI;
                string g = gain.ToString("0.##", ci);
                string limit = Math.Pow(10, ceilingDb / 20.0).ToString("0.####", ci);
                filter = $"volume={g}dB,alimiter=limit={limit}:level=disabled";
            }
            else
            {
                // Measurement parsing failed (unexpected ffmpeg output) — fall back to
                // single-pass loudnorm so an export never crashes.
                string lufs = targetLufs.ToString("0.#", ci);
                string tp = ceilingDb.ToString("0.#", ci);
                filter = $"loudnorm=I={lufs}:LRA=11:TP={tp}";
            }

            // Optionally strip leading and trailing digital silence. silenceremove only
            // trims from the front, so trailing silence is removed by reversing the
            // stream, trimming its (now-leading) tail, then reversing back. Done before
            // the gain stage so the audible content starts on sample zero.
            if (Settings.TrimSilence)
            {
                const string trim = "silenceremove=start_periods=1:start_duration=0:start_threshold=-50dB:detection=peak";
                filter = $"{trim},areverse,{trim},areverse,{filter}";
            }

            Run($"-i \"{oggName}\" -af {filter} -acodec libvorbis -q:a 7 \"{tmp}\"");
            File.Move(tmp, oggName, true);
        }

        private static bool TryParseMeasuredLoudness(string ffmpegStderr, out double inputI)
        {
            inputI = 0;
            if (string.IsNullOrEmpty(ffmpegStderr)) return false;

            // The JSON block is printed at the end of stderr, after the normal log lines.
            int start = ffmpegStderr.LastIndexOf('{');
            int end = ffmpegStderr.LastIndexOf('}');
            if (start < 0 || end <= start) return false;

            try
            {
                using var doc = JsonDocument.Parse(ffmpegStderr.Substring(start, end - start + 1));
                string raw = doc.RootElement.GetProperty("input_i").GetString();
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out inputI)
                       && !double.IsInfinity(inputI);
            }
            catch
            {
                return false;
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

        private static string Run(string arguments)
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

            return message;
        }
    }
}
