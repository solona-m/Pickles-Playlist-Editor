using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

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
    private static readonly string ToolDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicklesPlaylistEditor", "current", "tools");
    private static readonly string LocalYtDlpPath = Path.Combine(ToolDirectory, YtDlpExeName);
    private static readonly string CookiesSavePath = Path.Combine(ToolDirectory, "cookies.txt");
    private static readonly string DenoExePath = Path.Combine(ToolDirectory, "deno.exe");
    private static readonly string DenoZipPath = Path.Combine(ToolDirectory, "deno.zip");

    private static string? _cookiesPath;
    private static TcpListener? _cookieListener;
    private static Thread? _cookieListenerThread;
    private static volatile bool _isListeningForCookies;

    public static bool HasCookies => !string.IsNullOrEmpty(_cookiesPath) && File.Exists(_cookiesPath);

    private static string? FindCookiesFile()
    {
        if (File.Exists(CookiesSavePath)) return CookiesSavePath;
        string vrcCookies = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCVideoCacher", "youtube_cookies.txt");
        if (File.Exists(vrcCookies)) return vrcCookies;
        return null;
    }

    public static void StartCookieListener()
    {
        _cookiesPath = FindCookiesFile();
        try
        {
            _cookieListener = new TcpListener(IPAddress.Loopback, 9696);
            _cookieListener.Start();
            _isListeningForCookies = true;
            _cookieListenerThread = new Thread(CookieListenerLoop)
            {
                IsBackground = true,
                Name = "VRCVideoCacherCookieListener"
            };
            _cookieListenerThread.Start();
        }
        catch { }
    }

    public static void StopCookieListener()
    {
        _isListeningForCookies = false;
        try { _cookieListener?.Stop(); } catch { }
    }

    public static bool ClearCookies()
    {
        try
        {
            if (File.Exists(CookiesSavePath))
                File.Delete(CookiesSavePath);
            _cookiesPath = FindCookiesFile();
            return true;
        }
        catch { return false; }
    }

    private static void CookieListenerLoop()
    {
        while (_isListeningForCookies && _cookieListener != null)
        {
            try
            {
                using var client = _cookieListener.AcceptTcpClient();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? line;
                int contentLength = 0;
                bool isPost = false;

                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    if (line.StartsWith("POST")) isPost = true;
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        if (int.TryParse(line.Substring(15).Trim(), out int len))
                            contentLength = len;
                }

                if (isPost && contentLength > 0)
                {
                    char[] bodyChars = new char[contentLength];
                    int read = reader.ReadBlock(bodyChars, 0, contentLength);
                    string body = new string(bodyChars, 0, read);

                    if (!string.IsNullOrEmpty(body) && body.Contains(".youtube.com"))
                    {
                        Directory.CreateDirectory(ToolDirectory);
                        File.WriteAllText(CookiesSavePath, body);
                        _cookiesPath = CookiesSavePath;
                    }
                }

                string response = "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\nConnection: close\r\n\r\nOK";
                byte[] buffer = Encoding.UTF8.GetBytes(response);
                stream.Write(buffer, 0, buffer.Length);
            }
            catch (SocketException) { break; }
            catch { }
        }
    }

    private static string CookiesArg => HasCookies ? $"--cookies \"{_cookiesPath}\"" : string.Empty;
    private static string DenoArg => File.Exists(DenoExePath)
        ? $"--js-runtimes \"deno:{DenoExePath}\""
        : string.Empty;
    private static readonly string AppDirectory = AppContext.BaseDirectory.TrimEnd('\\', '/');
    private static string FfmpegArg => File.Exists(Path.Combine(AppDirectory, "ffmpeg.exe"))
        ? $"--ffmpeg-location \"{AppDirectory}\""
        : string.Empty;

    public static async Task EnsureUpToDateAsync()
    {
        Directory.CreateDirectory(ToolDirectory);
        using var client = new HttpClient();

        if (!File.Exists(LocalYtDlpPath))
        {
            var bytes = await client.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe");
            await File.WriteAllBytesAsync(LocalYtDlpPath, bytes);
        }

        if (!File.Exists(DenoExePath))
        {
            var denoBytes = await client.GetByteArrayAsync("https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip");
            await File.WriteAllBytesAsync(DenoZipPath, denoBytes);
            ZipFile.ExtractToDirectory(DenoZipPath, ToolDirectory, overwriteFiles: true);
            try { File.Delete(DenoZipPath); } catch { }
        }

        await RunYtDlpAsync("-U");
    }

    public static async Task<YtDlpDownloadResult> DownloadAudioAsync(string url, string outputDirectory, YtDownloadMode mode, Action<YtDlpProgressInfo>? onProgress = null)
    {
        Directory.CreateDirectory(outputDirectory);
        string playlistFlag = mode == YtDownloadMode.Playlist ? "--yes-playlist" : "--no-playlist";
        string cookiesArg = CookiesArg;
        string denoArg = DenoArg;
        string ffmpegArg = FfmpegArg;

        var infoJson = await RunYtDlpAsync($"--dump-single-json --no-warnings --skip-download {denoArg} {playlistFlag} {cookiesArg} \"{url}\"");
        var parsed = JObject.Parse(infoJson);
        var title = parsed.Value<string>("title") ?? "YouTube Download";
        bool isPlaylist = string.Equals(parsed.Value<string>("_type"), "playlist", StringComparison.OrdinalIgnoreCase);
        int totalItems = Math.Max(1, parsed["entries"]?.Count() ?? (isPlaylist ? 0 : 1));

        string template = "%(title)s.%(ext)s";
        string outputArg = $"-o \"{Path.Combine(outputDirectory, template)}\"";
        await RunYtDlpWithProgressAsync($"-f bestaudio/best -x --audio-format vorbis --audio-quality 5 --newline --no-warnings {denoArg} {ffmpegArg} {playlistFlag} {cookiesArg} {outputArg} \"{url}\"", totalItems, onProgress);

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
