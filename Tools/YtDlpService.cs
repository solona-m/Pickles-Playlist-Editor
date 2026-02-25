using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace Pickles_Playlist_Editor.Tools;

public class YtDlpDownloadResult
{
    public required bool IsPlaylist { get; init; }
    public required string Title { get; init; }
    public required List<string> DownloadedFiles { get; init; }
}

public enum YtDownloadMode
{
    Single,
    Playlist
}

public sealed class YtDlpProgressInfo
{
    public required string Stage { get; init; }
    public required int Current { get; init; }
    public required int Total { get; init; }
    public double? Percent { get; init; }
}

public static class YtDlpService
{
    private const string YtDlpExeName = "yt-dlp.exe";
    private static readonly string ToolDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicklesPlaylistEditor", "tools");
    private static readonly string LocalYtDlpPath = Path.Combine(ToolDirectory, YtDlpExeName);

    public static async Task EnsureUpToDateAsync()
    {
        Directory.CreateDirectory(ToolDirectory);
        if (!File.Exists(LocalYtDlpPath))
        {
            using var client = new HttpClient();
            var bytes = await client.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe");
            await File.WriteAllBytesAsync(LocalYtDlpPath, bytes);
        }

        await RunYtDlpAsync("-U");
    }

    public static async Task<YtDlpDownloadResult> DownloadAudioAsync(string url, string outputDirectory, YtDownloadMode mode, Action<YtDlpProgressInfo>? onProgress = null)
    {
        Directory.CreateDirectory(outputDirectory);
        string playlistFlag = mode == YtDownloadMode.Playlist ? "--yes-playlist" : "--no-playlist";

        var infoJson = await RunYtDlpAsync($"--dump-single-json --no-warnings --skip-download {playlistFlag} \"{url}\"");
        var parsed = JObject.Parse(infoJson);
        var title = parsed.Value<string>("title") ?? "YouTube Download";
        bool isPlaylist = string.Equals(parsed.Value<string>("_type"), "playlist", StringComparison.OrdinalIgnoreCase);
        int totalItems = Math.Max(1, parsed["entries"]?.Count() ?? (isPlaylist ? 0 : 1));

        string template = "%(title)s.%(ext)s";
        await RunYtDlpWithProgressAsync($"-x --audio-format vorbis --audio-quality 5 --newline --no-warnings {playlistFlag} -o \"{Path.Combine(outputDirectory, template)}\" \"{url}\"", totalItems, onProgress);

        var files = Directory.GetFiles(outputDirectory, "*.ogg", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
            throw new InvalidOperationException("yt-dlp completed but no OGG files were created.");

        return new YtDlpDownloadResult
        {
            IsPlaylist = isPlaylist,
            Title = title,
            DownloadedFiles = files
        };
    }

    private static async Task<string> RunYtDlpAsync(string arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = LocalYtDlpPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed ({process.ExitCode}): {stderr}");

        return stdout;
    }

    private static async Task RunYtDlpWithProgressAsync(string arguments, int totalItems, Action<YtDlpProgressInfo>? onProgress)
    {
        using var process = new Process();
        process.StartInfo.FileName = LocalYtDlpPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        var stderrBuffer = new List<string>();
        int currentItem = 0;
        int total = Math.Max(1, totalItems);

        process.Start();
        Task stderrTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                string? errLine = await process.StandardError.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(errLine))
                    stderrBuffer.Add(errLine.Trim());
            }
        });

        while (!process.StandardOutput.EndOfStream)
        {
            string? line = await process.StandardOutput.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase))
            {
                currentItem = Math.Min(total, currentItem + 1);
                onProgress?.Invoke(new YtDlpProgressInfo { Stage = "Downloading", Current = currentItem, Total = total });
                continue;
            }

            if (line.StartsWith("[download]", StringComparison.OrdinalIgnoreCase) && line.Contains('%'))
            {
                double? percent = TryParsePercent(line);
                onProgress?.Invoke(new YtDlpProgressInfo { Stage = "Downloading", Current = Math.Max(1, currentItem), Total = total, Percent = percent });
                continue;
            }

            if (line.StartsWith("[ExtractAudio]", StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke(new YtDlpProgressInfo { Stage = "Converting", Current = Math.Max(1, currentItem), Total = total });
            }
        }

        await process.WaitForExitAsync();
        await stderrTask;
        if (process.ExitCode != 0)
        {
            string err = string.Join(Environment.NewLine, stderrBuffer.Where(x => !string.IsNullOrWhiteSpace(x)));
            throw new InvalidOperationException($"yt-dlp failed ({process.ExitCode}): {err}");
        }
    }

    private static double? TryParsePercent(string line)
    {
        int percentIdx = line.IndexOf('%');
        if (percentIdx <= 0)
            return null;

        int start = percentIdx - 1;
        while (start >= 0 && (char.IsDigit(line[start]) || line[start] == '.'))
            start--;

        string token = line.Substring(start + 1, percentIdx - start - 1);
        if (double.TryParse(token, out double value))
            return value;

        return null;
    }
}
