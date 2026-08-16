namespace Pickles_Playlist_Editor.Tools;

public enum MediaService
{
    /// <summary>The string isn't a usable http(s) URL at all.</summary>
    Unknown,
    YouTube,
    SoundCloud,
    /// <summary>A well-formed URL on some other host. yt-dlp may still support it.</summary>
    Other
}

/// <summary>
/// Minimal URL classification for the download dialog. yt-dlp itself needs no help
/// picking an extractor — this exists so the UI can name the right service in
/// messages, pick a sensible default playlist name, and avoid offering a
/// YouTube-specific cookie hint when the failure came from somewhere else.
///
/// Anything that isn't YouTube or SoundCloud must still download normally, so
/// <see cref="MediaService.Other"/> is a valid, fully-supported outcome.
/// </summary>
public static class MediaUrlInfo
{
    public static MediaService Classify(string? url)
    {
        if (!TryParse(url, out var uri))
            return MediaService.Unknown;

        return ClassifyHost(uri!.Host);
    }

    /// <summary>
    /// Validates user input before it reaches a command line. The caller supplies the
    /// message, so the wording stays localized alongside the rest of the UI.
    /// </summary>
    public static bool TryValidate(string? url, out string cleaned)
    {
        cleaned = url?.Trim() ?? string.Empty;

        if (cleaned.Length == 0)
            return false;

        // Arguments are assembled by string interpolation and the URL is wrapped in double
        // quotes, so anything that can end that quoted run early lets the rest of the text
        // be parsed as yt-dlp options.
        //
        // Inside a quoted argument, CommandLineToArgvW gives exactly two characters that
        // power: '"' closes the run, and '\' escapes the character after it — so a URL
        // ending in a backslash turns the closing quote into a literal one and the
        // argument never terminates. Rejecting both closes the hole completely. Neither
        // is legal in a URL anyway; a genuine backslash arrives percent-encoded as %5C.
        if (cleaned.IndexOf('"') >= 0 || cleaned.IndexOf('\\') >= 0 || cleaned.Any(char.IsControl))
            return false;

        return TryParse(cleaned, out _);
    }

    private static bool TryParse(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        uri = parsed;
        return true;
    }

    private static MediaService ClassifyHost(string host)
    {
        // Strip a leading "www." so "www.soundcloud.com" and "soundcloud.com" match alike.
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host.Substring(4);

        if (Matches(host, "youtube.com") || Matches(host, "youtu.be") || Matches(host, "youtube-nocookie.com"))
            return MediaService.YouTube;

        if (Matches(host, "soundcloud.com") || Matches(host, "snd.sc"))
            return MediaService.SoundCloud;

        return MediaService.Other;
    }

    /// <summary>True when <paramref name="host"/> is <paramref name="domain"/> or a subdomain of it.</summary>
    private static bool Matches(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
