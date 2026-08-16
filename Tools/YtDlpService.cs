using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
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

/// <summary>
/// A recognized download failure, so the UI can explain it in plain language instead of
/// echoing a raw yt-dlp error.
/// </summary>
public enum DownloadFailureKind
{
    /// <summary>Not recognized — show the underlying yt-dlp message.</summary>
    Unknown,
    YouTubeNeedsCookies,
    YouTubeCookiesExpired,
    SoundCloudGeoBlocked,
    SoundCloudPaidOrProtected,
    SoundCloudNotFound,
    SoundCloudRateLimited
}

public enum CookieStatus
{
    NotFound,
    Valid,
    Expired,
    Invalid
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
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Deliberately a sibling of Velopack's "current" install folder, not inside it.
    // Velopack replaces "current" wholesale on every app self-update, which used to
    // delete yt-dlp.exe, deno.exe and the saved cookie jar along with it. This matches
    // where Playlist.BackupDir and Logger already put their data.
    private static readonly string ToolDirectory = Path.Combine(LocalAppData, "PicklesPlaylistEditor", "tools");
    private static readonly string LegacyToolDirectory = Path.Combine(LocalAppData, "PicklesPlaylistEditor", "current", "tools");
    private static readonly string LocalYtDlpPath = Path.Combine(ToolDirectory, YtDlpExeName);
    private static readonly string CookiesSavePath = Path.Combine(ToolDirectory, "cookies.txt");
    private static readonly string DenoExePath = Path.Combine(ToolDirectory, "deno.exe");
    private static readonly string DenoZipPath = Path.Combine(ToolDirectory, "deno.zip");

    private static string? _cookiesPath;
    private static TcpListener? _cookieListener;
    private static Thread? _cookieListenerThread;
    private static volatile bool _isListeningForCookies;

    public static bool HasCookies => !string.IsNullOrEmpty(_cookiesPath) && File.Exists(_cookiesPath);

    public static CookieStatus GetCookieStatus()
    {
        if (!HasCookies)
            return CookieStatus.NotFound;

        try
        {
            var lines = File.ReadAllLines(_cookiesPath!);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool sawAnyCookie = false;
            long maxExpiry = 0;

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                string[] fields = line.Split('\t');
                if (fields.Length < 7)
                    continue;

                sawAnyCookie = true;
                if (long.TryParse(fields[4], out long expiry) && expiry > maxExpiry)
                    maxExpiry = expiry;
            }

            if (!sawAnyCookie)
                return CookieStatus.Invalid;

            // maxExpiry == 0 means every cookie is a session cookie (no expiration recorded);
            // there's nothing to compare against time, so treat it as valid.
            if (maxExpiry > 0 && maxExpiry < now)
                return CookieStatus.Expired;

            return CookieStatus.Valid;
        }
        catch
        {
            return CookieStatus.Invalid;
        }
    }

    private static string? FindCookiesFile()
    {
        if (File.Exists(CookiesSavePath)) return CookiesSavePath;
        string vrcCookies = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCVideoCacher", "youtube_cookies.txt");
        if (File.Exists(vrcCookies)) return vrcCookies;
        return null;
    }

    public static void StartCookieListener()
    {
        // Runs at app launch, well before EnsureUpToDateAsync, so the migration has to
        // happen here too or the first cookie lookup of the session misses the old jar.
        MigrateLegacyToolDirectory();
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

    /// <summary>
    /// True when the user has signed in to SoundCloud from Settings.
    /// </summary>
    public static bool HasSoundCloudSignIn => Settings.HasSoundCloudToken;

    /// <summary>
    /// Private browser profile for the sign-in window. Lives beside the tools so it
    /// survives app updates for the same reason the cookie jar does.
    /// </summary>
    public static string WebViewDataDirectory
    {
        get
        {
            string dir = Path.Combine(ToolDirectory, "webview2");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Signs out: drops the stored token and deletes the browser profile, so a later
    /// sign-in starts from a real login page instead of silently resuming the old session.
    /// The profile is locked while the sign-in window is open, so failing to remove it is
    /// reported but not fatal — the token is gone either way, which is what authenticates.
    /// </summary>
    public static void ClearSoundCloudSignIn()
    {
        Settings.SoundCloudToken = "";

        try
        {
            string dir = Path.Combine(ToolDirectory, "webview2");
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.LogWarn("Signed out, but the cached browser profile could not be deleted: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// A cookie jar holding the saved SoundCloud session, or null when signed out.
    ///
    /// The token is stored DPAPI-encrypted, but yt-dlp can only read a plaintext jar, so
    /// it has to be written out for the duration of the download. The caller owns the
    /// returned path and must delete it — see <see cref="DeleteTempJar"/>.
    /// </summary>
    private static string? CreateSoundCloudCookieJar()
    {
        string token = Settings.SoundCloudToken;
        if (string.IsNullOrEmpty(token))
            return null;

        try
        {
            string path = Path.Combine(Path.GetTempPath(), $"pickles-sc-{Guid.NewGuid():N}.txt");

            // Netscape format: domain, includeSubdomains, path, secure, expiry, name, value
            // — seven tab-separated fields. A leading dot plus TRUE covers api-v2 and the
            // other soundcloud.com subdomains the extractor talks to. The expiry is a far
            // future placeholder because the cookie store doesn't hand one back reliably;
            // SoundCloud invalidating the token server-side is what actually ends it.
            long expiry = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            sb.AppendLine($".soundcloud.com\tTRUE\t/\tTRUE\t{expiry}\toauth_token\t{token}");
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception ex)
        {
            Logger.LogWarn("Could not prepare the SoundCloud sign-in for this download, continuing signed out: {Error}", ex.Message);
            return null;
        }
    }

    private static void DeleteTempJar(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try { File.Delete(path); }
        catch (Exception ex) { Logger.LogWarn("Could not delete the temporary SoundCloud cookie file '{Path}': {Error}", path, ex.Message); }
    }

    private static string DenoArg => File.Exists(DenoExePath)
        ? $"--js-runtimes \"deno:{DenoExePath}\""
        : string.Empty;
    private static readonly string AppDirectory = AppContext.BaseDirectory.TrimEnd('\\', '/');
    private static string FfmpegArg => File.Exists(Path.Combine(AppDirectory, "ffmpeg.exe"))
        ? $"--ffmpeg-location \"{AppDirectory}\""
        : string.Empty;

    /// <summary>
    /// Carries a cookie jar left in the pre-move location over to the durable one.
    /// The binaries aren't worth migrating — they re-download in seconds — but the
    /// cookies came from a manual browser-extension export the user would have to redo.
    /// </summary>
    private static void MigrateLegacyToolDirectory()
    {
        try
        {
            string legacyCookies = Path.Combine(LegacyToolDirectory, "cookies.txt");
            if (File.Exists(legacyCookies) && !File.Exists(CookiesSavePath))
            {
                Directory.CreateDirectory(ToolDirectory);
                File.Copy(legacyCookies, CookiesSavePath);
                Logger.LogInfo("Migrated saved cookies out of the Velopack 'current' folder so app updates stop wiping them.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarn("Could not migrate the old cookie file (harmless): {Error}", ex.Message);
        }
    }

    public static async Task EnsureUpToDateAsync()
    {
        Directory.CreateDirectory(ToolDirectory);
        MigrateLegacyToolDirectory();
        using var client = new HttpClient();

        if (!File.Exists(LocalYtDlpPath))
        {
            var bytes = await client.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp.exe").ConfigureAwait(false);
            await File.WriteAllBytesAsync(LocalYtDlpPath, bytes).ConfigureAwait(false);
        }

        if (!File.Exists(DenoExePath))
        {
            var denoBytes = await client.GetByteArrayAsync("https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip").ConfigureAwait(false);
            await File.WriteAllBytesAsync(DenoZipPath, denoBytes).ConfigureAwait(false);
            // Unpacking deno writes ~93 MB and has no async overload, so it has to be
            // pushed off the caller's thread explicitly or it stalls the window.
            await Task.Run(() => ZipFile.ExtractToDirectory(DenoZipPath, ToolDirectory, overwriteFiles: true)).ConfigureAwait(false);
            try { File.Delete(DenoZipPath); } catch { }
        }

        // --update-to (not -U) so installs that already have a stable-channel binary
        // switch over to nightly instead of staying pinned to their original channel.
        //
        // A failed update must not abort the download: the binary already on disk still
        // works, and nightly checks run before every single download, so a transient
        // network error would otherwise make the feature unusable while offline.
        try
        {
            await RunYtDlpAsync("--update-to nightly").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarn("yt-dlp update check failed (harmless, continuing on the installed version): {Error}", ex.Message);
        }
    }

    public static async Task<YtDlpDownloadResult> DownloadAudioAsync(string url, string outputDirectory, YtDownloadMode mode, Action<YtDlpProgressInfo>? onProgress = null)
    {
        Directory.CreateDirectory(outputDirectory);
        string denoArg = DenoArg;
        string ffmpegArg = FfmpegArg;
        var service = MediaUrlInfo.Classify(url);

        // The two services treat sign-in differently on purpose. A YouTube jar is a
        // fallback, attached only after an auth-shaped failure, because most videos don't
        // need it. SoundCloud sign-in is opt-in — the user deliberately signed in — so it
        // goes on the first attempt; withholding it would just buy a guaranteed-failing
        // pass before the retry.
        string? scJar = service == MediaService.SoundCloud ? CreateSoundCloudCookieJar() : null;
        if (scJar != null)
            Logger.LogInfo("Using the saved SoundCloud sign-in for this download.");

        try
        {
            return await DownloadAudioAttemptAsync(url, outputDirectory, mode, denoArg, ffmpegArg, cookiesArg: CookiesArgFor(scJar), onProgress).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRetryWithCookies(url, ex))
        {
            Logger.LogInfo("Retrying the download with saved YouTube cookies after a sign-in error.");
            return await DownloadAudioAttemptAsync(url, outputDirectory, mode, denoArg, ffmpegArg, cookiesArg: CookiesArg, onProgress).ConfigureAwait(false);
        }
        finally
        {
            // Covers the success path, the retry path and any hard failure, so the
            // decrypted token never outlives the download that needed it.
            DeleteTempJar(scJar);
        }
    }

    private static string CookiesArgFor(string? jarPath) =>
        string.IsNullOrEmpty(jarPath) ? string.Empty : $"--cookies \"{jarPath}\"";

    /// <summary>
    /// The saved jar is a YouTube jar, so replaying a download with it only ever helps a
    /// YouTube sign-in failure. This used to catch every exception, which meant an
    /// unrelated failure (a SoundCloud 404, a full disk, bad JSON) silently ran the whole
    /// download a second time with irrelevant cookies attached.
    /// </summary>
    private static bool ShouldRetryWithCookies(string url, Exception ex) =>
        MediaUrlInfo.Classify(url) == MediaService.YouTube
        && GetCookieStatus() == CookieStatus.Valid
        && LooksLikeAuthFailure(ex.Message);

    /// <summary>
    /// Recognizes the yt-dlp errors that a signed-in session would actually fix. Feeds
    /// both the cookie retry and <see cref="ClassifyFailure"/>, so the decision to retry
    /// and the hint the user is shown can't drift apart.
    /// </summary>
    private static bool LooksLikeAuthFailure(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return false;

        return message.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase)
            || message.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Sign in to confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
            || message.Contains("members-only", StringComparison.OrdinalIgnoreCase)
            || message.Contains("private video", StringComparison.OrdinalIgnoreCase)
            || (message.Contains("cookies", StringComparison.OrdinalIgnoreCase)
                && message.Contains("authentication", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sorts a raw yt-dlp error into something worth showing a user. Anything unrecognized
    /// stays <see cref="DownloadFailureKind.Unknown"/> so the original text is surfaced
    /// rather than replaced by a guess.
    /// </summary>
    public static DownloadFailureKind ClassifyFailure(string? message, MediaService service)
    {
        if (string.IsNullOrEmpty(message))
            return DownloadFailureKind.Unknown;

        if (service == MediaService.YouTube && LooksLikeAuthFailure(message))
        {
            return GetCookieStatus() == CookieStatus.Expired
                ? DownloadFailureKind.YouTubeCookiesExpired
                : DownloadFailureKind.YouTubeNeedsCookies;
        }

        if (service == MediaService.SoundCloud)
            return ClassifySoundCloudFailure(message);

        return DownloadFailureKind.Unknown;
    }

    /// <summary>
    /// Picks the cause that actually dominates a SoundCloud failure.
    ///
    /// A set download reports one error line per failed track, and these used to be tested
    /// against the whole concatenated buffer with the first match winning. That meant one
    /// DRM-protected track in a set of twenty geo-blocked ones made the app announce the
    /// whole set was copy-protected, hiding the cause that a VPN would have fixed. Counting
    /// the lines instead means the reported reason is the one most tracks actually hit.
    /// </summary>
    private static DownloadFailureKind ClassifySoundCloudFailure(string message)
    {
        var counts = new Dictionary<DownloadFailureKind, int>();

        foreach (var line in message.Split('\n'))
        {
            var kind = ClassifySoundCloudLine(line);
            if (kind != DownloadFailureKind.Unknown)
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }

        if (counts.Count == 0)
            return DownloadFailureKind.Unknown;

        // Ties go to whatever the user can act on — a transient rate limit is worth
        // retrying, whereas a DRM track never becomes downloadable however long they wait.
        // Ordering explicitly also keeps the result deterministic; dictionary order is not.
        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => ActionabilityRank(pair.Key))
            .First().Key;
    }

    private static DownloadFailureKind ClassifySoundCloudLine(string line)
    {
        if (Has("rate limit") || Has("HTTP Error 429") || Has("Unable to extract client id"))
            return DownloadFailureKind.SoundCloudRateLimited;

        if (Has("not available from your location") || Has("geo restricted") || Has("geo-restricted"))
            return DownloadFailureKind.SoundCloudGeoBlocked;

        // Match yt-dlp's actual wording ("This video is DRM protected") rather than a bare
        // "DRM": the error line carries the track title, and a three-letter substring test
        // would misfire on any title that happened to contain those letters.
        if (Has("DRM protected") || Has("only available for registered users") || Has("not available for this client"))
            return DownloadFailureKind.SoundCloudPaidOrProtected;

        if (Has("HTTP Error 404") || Has("HTTP Error 403"))
            return DownloadFailureKind.SoundCloudNotFound;

        return DownloadFailureKind.Unknown;

        bool Has(string needle) => line.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static int ActionabilityRank(DownloadFailureKind kind) => kind switch
    {
        DownloadFailureKind.SoundCloudRateLimited => 0,
        DownloadFailureKind.SoundCloudGeoBlocked => 1,
        DownloadFailureKind.SoundCloudNotFound => 2,
        DownloadFailureKind.SoundCloudPaidOrProtected => 3,
        _ => 4,
    };

    private static async Task<YtDlpDownloadResult> DownloadAudioAttemptAsync(string url, string outputDirectory, YtDownloadMode mode, string denoArg, string ffmpegArg, string cookiesArg, Action<YtDlpProgressInfo>? onProgress)
    {
        string playlistFlag = mode == YtDownloadMode.Playlist ? "--yes-playlist" : "--no-playlist";

        // Two things going on in the arguments below:
        //  - "--" terminates option parsing, so a URL beginning with "-" can't be read as
        //    a flag. The URL itself is quoted, and MediaUrlInfo.TryValidate rejects both
        //    '"' and '\' — the only two characters that can end a quoted argument early
        //    under CommandLineToArgvW — so it can't break out of that quoting either.
        //  - --flat-playlist keeps the probe cheap. Without it yt-dlp fully resolves every
        //    entry before the download even starts; on a SoundCloud user page or a large
        //    YouTube playlist that's hundreds of API calls against a rate-limited endpoint.
        //    _type, title and a countable entries array are all still present under it.
        var infoJson = await RunYtDlpAsync($"--dump-single-json --flat-playlist --no-warnings --skip-download {denoArg} {playlistFlag} {cookiesArg} -- \"{url}\"").ConfigureAwait(false);
        var parsed = JObject.Parse(infoJson);
        var title = parsed.Value<string>("title") ?? "Download";
        bool isPlaylist = string.Equals(parsed.Value<string>("_type"), "playlist", StringComparison.OrdinalIgnoreCase);
        int totalItems = Math.Max(1, parsed["entries"]?.Count() ?? (isPlaylist ? 0 : 1));

        // --no-playlist only works on URLs that are simultaneously one item and a list
        // (YouTube's watch?v=..&list=..). SoundCloud's /sets/, user and /likes URLs carry
        // no single-track id, so their extractors ignore it entirely and hand back the
        // whole set — meaning "Single Track" on a SoundCloud set used to dump every track
        // into the target playlist. Cap explicitly when the probe contradicts the mode.
        bool capToFirstItem = isPlaylist && mode == YtDownloadMode.Single;
        string itemsArg = capToFirstItem ? "--playlist-items 1" : string.Empty;
        if (capToFirstItem)
        {
            Logger.LogInfo("URL resolved to a playlist but Single Track was requested; limiting to the first item.");
            totalItems = 1;
        }

        // Numbering the files does two jobs on a multi-track download: it keeps the
        // playlist's own order (the glob below is alphabetical, which otherwise scrambles
        // an album), and it stops two tracks with the same title from resolving to one
        // filename, where yt-dlp would skip the second as "already downloaded".
        //
        // Pad to 5 digits, not 3: the sort is a plain ordinal string compare, so as soon
        // as the index gains a digit the padding stops equalising the width and ordering
        // breaks ("1000 - " sorts before "999 - "). A SoundCloud user page or /likes feed
        // can easily run past a thousand tracks; 5 digits covers anything realistic.
        bool numbered = isPlaylist && !capToFirstItem;
        string template = numbered ? "%(playlist_index)05d - %(title)s.%(ext)s" : "%(title)s.%(ext)s";
        string outputArg = $"-o \"{Path.Combine(outputDirectory, template)}\"";
        var run = await RunYtDlpWithProgressAsync($"-f bestaudio/best -x --audio-format vorbis --audio-quality 5 --newline --no-warnings {denoArg} {ffmpegArg} {playlistFlag} {itemsArg} {cookiesArg} {outputArg} -- \"{url}\"", totalItems, onProgress).ConfigureAwait(false);

        // Enumerating and renaming a whole set's worth of files is blocking disk work, so
        // keep it off the caller's thread along with everything else here.
        var files = await Task.Run(() =>
        {
            var found = Directory.GetFiles(outputDirectory, "*.ogg", SearchOption.TopDirectoryOnly)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Song names come from the file name (Playlist.AddFiles uses
            // Path.GetFileNameWithoutExtension), so drop the ordering prefix now that the
            // sort has served its purpose — otherwise every track shows up as "00001 - Name".
            return numbered ? StripOrderingPrefixes(found) : found;
        }).ConfigureAwait(false);

        // Only treat a non-zero exit as fatal when nothing survived it. Individual tracks
        // failing mid-set is normal on SoundCloud — DRM-protected tracks are common, and
        // yt-dlp still exits non-zero after downloading every other track in the set.
        // Throwing there would have thrown away a whole album over one bad track.
        if (files.Count == 0)
        {
            throw new InvalidOperationException(run.ExitCode != 0
                ? $"yt-dlp failed ({run.ExitCode}): {run.Error}"
                : "yt-dlp completed but no OGG files were created.");
        }

        if (run.ExitCode != 0)
            Logger.LogWarn("Some tracks in this download could not be fetched, keeping the {Count} that succeeded: {Error}", files.Count, run.Error);

        return new YtDlpDownloadResult
        {
            IsPlaylist = isPlaylist,
            Title = title,
            DownloadedFiles = files
        };
    }

    /// <summary>
    /// Renames "00001 - Track.ogg" back to "Track.ogg", preserving the order the files were
    /// passed in. Where two tracks in the same set really do share a title, the later one
    /// keeps a " (2)" suffix so it can't overwrite the first. Any single rename that fails
    /// leaves that file under its prefixed name rather than losing it.
    /// </summary>
    private static List<string> StripOrderingPrefixes(List<string> files)
    {
        var result = new List<string>(files.Count);

        foreach (var file in files)
        {
            string dir = Path.GetDirectoryName(file) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(file);
            string ext = Path.GetExtension(file);

            int sep = name.IndexOf(" - ", StringComparison.Ordinal);
            if (sep <= 0 || !name.Substring(0, sep).All(char.IsDigit))
            {
                result.Add(file);
                continue;
            }

            string stripped = name.Substring(sep + 3).Trim();
            if (stripped.Length == 0)
            {
                result.Add(file);
                continue;
            }

            string target = Path.Combine(dir, stripped + ext);
            for (int i = 2; File.Exists(target); i++)
                target = Path.Combine(dir, $"{stripped} ({i}){ext}");

            try
            {
                File.Move(file, target);
                result.Add(target);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Could not rename '{File}' (keeping the numbered name): {Error}", Path.GetFileName(file), ex.Message);
                result.Add(file);
            }
        }

        return result;
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
        await process.WaitForExitAsync().ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed ({process.ExitCode}): {stderr}");

        return stdout;
    }

    /// <summary>
    /// Outcome of a download run. Reported rather than thrown, so the caller can decide
    /// whether a non-zero exit actually cost anything — a part-failed set still leaves
    /// usable files behind.
    /// </summary>
    private sealed record YtDlpRunResult(int ExitCode, string Error);

    private static async Task<YtDlpRunResult> RunYtDlpWithProgressAsync(string arguments, int totalItems, Action<YtDlpProgressInfo>? onProgress)
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
            string? errLine;
            while ((errLine = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (!string.IsNullOrWhiteSpace(errLine))
                    stderrBuffer.Add(errLine.Trim());
            }
        });

        // Loop on ReadLineAsync returning null rather than testing EndOfStream: that
        // property does a *synchronous* Peek on the pipe, so it blocks whichever thread
        // asks — and this runs from an async void click handler, i.e. the UI thread.
        // Combined with the missing ConfigureAwait below it froze the whole window for
        // the length of the download.
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
        {
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

        await process.WaitForExitAsync().ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);

        string err = string.Join(Environment.NewLine, stderrBuffer.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new YtDlpRunResult(process.ExitCode, err);
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
