using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pickles_Playlist_Editor.Tools;

internal enum SoundCloudLinkKind { Unsupported, Track, Set }

internal sealed record ProbeEntry(string Id, string Url, int Index);
internal sealed record ScTrackInfo(string Id, string Title, string? Artist, double DurationSeconds);
internal sealed record YtCandidate(string Id, string Title, string? Channel, double? DurationSeconds, string? LiveStatus, int Rank);

/// <summary>The parts of a SoundCloud title the YouTube match is checked against (all normalized).</summary>
internal sealed record ScTitleParts(string Song, IReadOnlyList<string> VersionTokens, IReadOnlyList<string> Artists,
    string? RequiredSequence, string SongRaw, string? ArtistRaw, string VersionRaw);

/// <summary>
/// Pure parsing and matching for the YouTube match of SoundCloud tracks that yt-dlp reports as
/// DRM protected (not downloadable from SoundCloud). No process, network or UI code here.
/// </summary>
internal static class YouTubeFallbackMatcher
{
    private static readonly Regex DrmLine = new(@"^ERROR: \[soundcloud[^\]]*\] (\S+?): .*DRM protected",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex YouTubeId = new(@"\A[A-Za-z0-9_-]{11}\z", RegexOptions.CultureInvariant);
    private static readonly Regex BracketGroup = new(@"[\(\[]([^\)\]]*)[\)\]]", RegexOptions.CultureInvariant);
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] Dashes = { " - ", " – ", " — " };

    private static readonly HashSet<string> NoiseGroupWords = new() { "free", "download", "dl", "premiere", "official" };
    private static readonly HashSet<string> VersionGroupWords = new() { "remix", "edit", "vip", "flip", "bootleg", "version", "mix", "rework" };
    private static readonly HashSet<string> VersionOnlyWords = new()
    {
        "original", "radio", "extended", "club", "mix", "edit", "remix", "vip", "live", "version", "rework", "dub",
        "instrumental", "acoustic", "remaster", "remastered", "flip", "bootleg", "demo", "cut",
    };
    private static readonly HashSet<string> CandidateNoise = new()
    {
        "official", "audio", "video", "music", "lyric", "lyrics", "visualizer", "hd", "hq", "mv", "topic",
    };
    private static readonly HashSet<string> FeatWords = new() { "feat", "ft", "featuring" };
    private static readonly Regex FeatTail = new(@"\s(?:feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // A bracketed version word on YouTube ("(VIP)", "(Acoustic Version)") must also be in the SoundCloud title.
    private static readonly HashSet<string> CandidateVersionWords = new()
    {
        "vip", "flip", "bootleg", "edit", "rework", "acoustic", "acapella", "demo", "dub", "version", "remix", "mix", "extended", "instrumental",
    };
    private static readonly HashSet<string> PlainVersionWords = new() { "original", "radio", "remaster", "remastered" };
    private static readonly string[] Markers =
    {
        "live", "cover", "remix", "nightcore", "sped up", "slowed", "reverb", "8d", "karaoke", "instrumental",
        "reaction", "loop", "extended", "hour", "hours", "tutorial", "mashup",
    };

    public static SoundCloudLinkKind ClassifyProbe(JObject probe) => probe.Value<string>("extractor_key") switch
    {
        "Soundcloud" => SoundCloudLinkKind.Track,
        "SoundcloudSet" => SoundCloudLinkKind.Set,
        _ => SoundCloudLinkKind.Unsupported,
    };

    public static IReadOnlyList<string> ParseDrmTrackIds(string? stderr) =>
        string.IsNullOrEmpty(stderr)
            ? Array.Empty<string>()
            : DrmLine.Matches(stderr.Replace("\r", "")).Select(m => m.Groups[1].Value).Distinct().ToList();

    public static IReadOnlyList<ProbeEntry> ParseProbeEntries(JObject probe, string userUrl)
    {
        if (probe["entries"] is not JArray entries)
        {
            string? id = probe.Value<string>("id");
            return string.IsNullOrEmpty(id) ? Array.Empty<ProbeEntry>() : new[] { new ProbeEntry(id, userUrl, 1) };
        }

        var result = new List<ProbeEntry>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not JObject e) continue;
            string? id = e.Value<string>("id");
            string? url = e.Value<string>("url") ?? e.Value<string>("webpage_url");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(url))
                result.Add(new ProbeEntry(id, url, i + 1));
        }
        return result;
    }

    /// <summary>Title and duration are required; null when the probe output is unusable.</summary>
    public static ScTrackInfo? ParseTrackInfo(string json)
    {
        JObject o;
        try { o = JObject.Parse(json); } catch (Exception) { return null; }

        string? title = o.Value<string>("title");
        double? duration = o["duration"]?.Type is JTokenType.Integer or JTokenType.Float ? o.Value<double>("duration") : null;
        if (string.IsNullOrWhiteSpace(title) || duration is not > 0)
            return null;

        string? artist = o.Value<string>("artist");
        if (string.IsNullOrWhiteSpace(artist) && o["artists"] is JArray artists && artists.Count > 0)
            artist = artists[0]?.ToString();
        if (string.IsNullOrWhiteSpace(artist))
            artist = o.Value<string>("uploader");

        return new ScTrackInfo(o.Value<string>("id") ?? string.Empty, title.Trim(),
            string.IsNullOrWhiteSpace(artist) ? null : artist.Trim(), duration.Value);
    }

    public static IReadOnlyList<YtCandidate> ParseSearchResults(string json) =>
        TryParseSearchResults(json, out var result) ? result : Array.Empty<YtCandidate>();

    /// <summary>False when yt-dlp's output isn't readable search JSON.</summary>
    public static bool TryParseSearchResults(string json, out IReadOnlyList<YtCandidate> candidates)
    {
        candidates = Array.Empty<YtCandidate>();
        JObject o;
        try { o = JObject.Parse(json); } catch (Exception) { return false; }
        if (o["entries"] is not JArray entries) return false;

        var result = new List<YtCandidate>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not JObject e) continue;
            double? duration = e["duration"]?.Type is JTokenType.Integer or JTokenType.Float ? e.Value<double>("duration") : null;
            result.Add(new YtCandidate(e.Value<string>("id") ?? string.Empty, e.Value<string>("title") ?? string.Empty,
                e.Value<string>("channel") ?? e.Value<string>("uploader"), duration, e.Value<string>("live_status"), i + 1));
        }
        candidates = result;
        return true;
    }

    public static double Tolerance(double scSeconds) => Math.Clamp(0.05 * scSeconds, 5, 15);

    public static bool IsDurationMatch(double scSeconds, double? ytSeconds) =>
        ytSeconds is > 0 && Math.Abs(scSeconds - ytSeconds.Value) <= Tolerance(scSeconds);

    public static bool IsValidYouTubeId(string? id) => id != null && YouTubeId.IsMatch(id);

    /// <summary>Lowercase, accents dropped, anything but letters and digits turned into single spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string[] Tokens(string normalized) => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // Whole-token containment: "run" is not in "running up".
    private static bool ContainsWhole(string haystack, string needle) =>
        needle.Length > 0 && $" {haystack} ".Contains($" {needle} ", StringComparison.Ordinal);

    private static string RemoveWhole(string haystack, string needle)
    {
        if (needle.Length == 0) return haystack;
        string padded = $" {haystack} ", find = $" {needle} ";
        while (padded.Contains(find, StringComparison.Ordinal))
            padded = padded.Replace(find, " ", StringComparison.Ordinal);
        return string.Join(' ', Tokens(padded));
    }

    public static ScTitleParts ParseScTitle(string title, string? artist)
    {
        var version = new List<string>();
        var versionRaw = new List<string>();

        string remainder = BracketGroup.Replace(title, m =>
        {
            var words = Tokens(Normalize(m.Groups[1].Value));
            if (words.Any(VersionGroupWords.Contains))
            {
                version.AddRange(words.Where(w => !NoiseGroupWords.Contains(w)));
                versionRaw.Add(m.Groups[1].Value.Trim());
            }
            return " ";
        });
        remainder = string.Join(' ', remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var artists = new List<string>();
        string? artistNorm = string.IsNullOrWhiteSpace(artist) ? null : Normalize(artist);
        if (!string.IsNullOrEmpty(artistNorm)) artists.Add(artistNorm);

        string songRaw = remainder;
        string? artistRaw = artist;
        string? required = null;

        int dash = -1, dashLen = 0;
        foreach (var d in Dashes)
        {
            int at = remainder.IndexOf(d, StringComparison.Ordinal);
            if (at > 0 && (dash < 0 || at < dash)) { dash = at; dashLen = d.Length; }
        }

        if (dash > 0)
        {
            string xRaw = remainder[..dash].Trim(), yRaw = remainder[(dash + dashLen)..].Trim();
            string x = Normalize(xRaw), y = Normalize(yRaw);
            var yTokens = Tokens(y);

            if (yTokens.Length > 0 && yTokens.All(t => VersionOnlyWords.Contains(t) || t.All(char.IsDigit)))
            {
                // "Song - Original Mix": the part after the dash is the version, not the song.
                songRaw = xRaw;
                version.AddRange(yTokens);
                versionRaw.Add(yRaw);
            }
            else if (y.Length > 0 && x.Length > 0)
            {
                songRaw = yRaw;
                bool xIsArtist = artistNorm != null && (ContainsWhole(x, artistNorm) || ContainsWhole(artistNorm, x));
                if (xIsArtist)
                {
                    if (!artists.Contains(x)) artists.Add(x);
                }
                else
                {
                    required = x;
                    artistRaw = xRaw;
                }
            }
        }

        songRaw = FeatTail.Replace(songRaw, string.Empty).Trim();
        return new ScTitleParts(Normalize(songRaw), version.Distinct().ToList(), artists, required,
            songRaw, artistRaw, string.Join(' ', versionRaw));
    }

    public static string BuildSearchQuery(ScTrackInfo track)
    {
        var parts = ParseScTitle(track.Title, track.Artist);
        var pieces = new List<string>();
        if (!string.IsNullOrWhiteSpace(parts.ArtistRaw) && !ContainsWhole(parts.Song, Normalize(parts.ArtistRaw)))
            pieces.Add(parts.ArtistRaw!);
        pieces.Add(parts.SongRaw);
        if (parts.VersionRaw.Length > 0) pieces.Add(parts.VersionRaw);

        var sb = new StringBuilder();
        foreach (char c in string.Join(' ', pieces))
            sb.Append(c == '"' || c == '\\' || char.IsControl(c) ? ' ' : c);
        string query = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimStart('-', ' ');
        return Truncate(query, 120);
    }

    /// <summary>The candidate title with brackets, a feat. tail, noise words, the artist and the version words removed.</summary>
    public static string CandidateCore(string candidateTitle, ScTitleParts parts, bool removeArtists = true)
    {
        var tokens = Tokens(Normalize(BracketGroup.Replace(candidateTitle, " "))).ToList();
        int feat = tokens.FindIndex(FeatWords.Contains);
        if (feat >= 0) tokens = tokens.Take(feat).ToList();

        string core = string.Join(' ', tokens.Where(t => !CandidateNoise.Contains(t)));
        if (removeArtists)
        {
            foreach (var a in parts.Artists) core = RemoveWhole(core, a);
            if (parts.RequiredSequence != null) core = RemoveWhole(core, parts.RequiredSequence);
        }
        return string.Join(' ', Tokens(core).Where(t => !parts.VersionTokens.Contains(t)));
    }

    public static YtCandidate? SelectCandidate(ScTrackInfo track, IReadOnlyList<YtCandidate> candidates)
    {
        var parts = ParseScTitle(track.Title, track.Artist);
        if (parts.Song.Length == 0) return null;
        string scTitle = Normalize(track.Title);

        var accepted = candidates.Where(c => Accepts(track, parts, scTitle, c)).ToList();
        if (accepted.Count == 0) return null;

        double best = accepted.Min(c => Delta(track, c));
        return accepted
            .Where(c => Delta(track, c) - best < 1.0)
            .OrderBy(c => IsOfficialChannel(c, parts) ? 0 : 1)
            .ThenBy(c => c.Rank)
            .First();
    }

    private static double Delta(ScTrackInfo track, YtCandidate c) => Math.Abs(track.DurationSeconds - (c.DurationSeconds ?? 0));

    private static bool IsOfficialChannel(YtCandidate c, ScTitleParts parts)
    {
        string channel = c.Channel ?? string.Empty;
        if (channel.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase)) return true;
        string n = Normalize(channel);
        return parts.Artists.Any(a => a == n) || (parts.RequiredSequence != null && parts.RequiredSequence == n);
    }

    private static bool Accepts(ScTrackInfo track, ScTitleParts parts, string scTitle, YtCandidate c)
    {
        if (!IsValidYouTubeId(c.Id)) return false;
        if (c.LiveStatus is not (null or "not_live")) return false;
        if (!IsDurationMatch(track.DurationSeconds, c.DurationSeconds)) return false;

        string title = Normalize(c.Title);
        if (Markers.Any(m => ContainsWhole(title, m) && !ContainsWhole(scTitle, m))) return false;
        foreach (Match g in BracketGroup.Matches(c.Title))
        {
            var words = Tokens(Normalize(g.Groups[1].Value));
            if (!words.Any(PlainVersionWords.Contains)
                && words.Any(w => CandidateVersionWords.Contains(w) && !ContainsWhole(scTitle, w)))
                return false;
        }
        if (parts.VersionTokens.Any(v => !ContainsWhole(title, v))) return false;

        // Also try with the artist left in, so "Tonight" by Tonight or "Killer Queen" by Queen still match.
        if (!SongMatches(CandidateCore(c.Title, parts), parts.Song) && !SongMatches(CandidateCore(c.Title, parts, false), parts.Song))
            return false;

        // An X before the dash that isn't the probed artist stands in for the artist.
        string where = Normalize($"{c.Channel} {c.Title}");
        if (parts.RequiredSequence != null) return ContainsWhole(where, parts.RequiredSequence);
        return parts.Artists.Count == 0 || parts.Artists.Any(a => ContainsWhole(where, a));
    }

    private static bool SongMatches(string core, string song)
    {
        var tokens = Tokens(song);
        if (tokens.Length <= 2) return core == song;
        var coreTokens = Tokens(core).ToHashSet();
        return tokens.Count(coreTokens.Contains) >= 0.6 * tokens.Length;
    }

    public static string SafeFileName(string title, string fallback)
    {
        static string Clean(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(s.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().Trim('.').Trim();
            return Truncate(cleaned, 120);
        }
        string name = Clean(title);
        if (name.Trim('_').Length == 0) name = Clean(fallback);
        return name.Trim('_').Length == 0 ? "YouTube track" : name;
    }

    // Never cuts an emoji (surrogate pair) in half.
    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        int cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut].TrimEnd();
    }

    public static string RedactUrls(string? text)
    {
        string redacted = Url.Replace(text ?? string.Empty, "<url>").Replace('\r', ' ').Replace('\n', ' ');
        return redacted.Length > 200 ? redacted[..200] : redacted;
    }

    public static string FormatOutcomeLog(string scId, FallbackTrackResult r) =>
        RedactUrls($"SoundCloud track {scId} '{r.SoundCloudTitle}': {r.Reason}" +
            (r.YouTubeTitle != null ? $" '{r.YouTubeTitle}'" : string.Empty)).Replace("{", "(").Replace("}", ")");
}
