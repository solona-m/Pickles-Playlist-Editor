using System.Diagnostics;

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
            Run($"-i \"{oggName}\" -af loudnorm=I=-14:LRA=7:TP=-1 -acodec libvorbis -q:a 7 \"{tmp}\"");
            File.Move(tmp, oggName, true);
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
