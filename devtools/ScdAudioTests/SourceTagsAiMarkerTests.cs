using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pickles_Playlist_Editor;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using VfxEditor.ScdFormat;
using Xunit;

namespace ScdAudioTests;

internal static class SourceFiles
{
    public const string HolyCrit = "made with suno; created=2026-01-24T21:53:34Z; id=20165c61-3d9e-4daf-9810-09d929100000";

    /// <summary>A short noise file in the given container, tagged from ffmetadata text (so multi-line values work).</summary>
    public static string Make(AdpcmScdFixture fx, string name, string ext, string metadata = "")
    {
        string codec = ext.ToLowerInvariant() switch
        {
            ".mp3" => "libmp3lame",
            ".m4a" => "aac",
            ".flac" => "flac",
            ".wav" => "pcm_s16le",
            _ => "libvorbis",
        };
        string outPath = fx.PathFor(name + ext);
        if (metadata.Length == 0)
        {
            AdpcmScdFixture.RunFfmpeg($"-y -v error -f lavfi -i anoisesrc=d=2:a=0.3 -c:a {codec} \"{outPath}\"");
            return outPath;
        }
        string meta = fx.PathFor(name + ".ffmeta.txt");
        File.WriteAllText(meta, ";FFMETADATA1\n" + metadata + "\n");
        AdpcmScdFixture.RunFfmpeg($"-y -v error -f lavfi -i anoisesrc=d=2:a=0.3 -i \"{meta}\" -map_metadata 1 -c:a {codec} \"{outPath}\"");
        return outPath;
    }

    /// <summary>An ffmetadata value: escapes, and newlines as backslash continuations.</summary>
    public static string Esc(string v) =>
        v.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", "\\\n");

    public static int TempMetaFiles() => Directory.GetFiles(Path.GetTempPath(), "pickles-meta-*.txt").Length;
}

/// <summary>Source-tag reads, import and the SCD marker read, against the bundled ffmpeg.</summary>
[Collection("ffmpeg")]
public sealed class SourceTagsAiMarkerFfmpegTests : IDisposable
{
    private readonly AdpcmScdFixture _fx = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "srctags-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { _fx.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private const string Abc = "artist=Emotional Oranges\nalbum=West Coast Love\ntitle=Song Title";

    [Theory] // [AC-gh-20-source-tags-ai-marker-1] [EDGE-gh-20-source-tags-ai-marker-6]
    [InlineData(".mp3")]
    [InlineData(".m4a")]
    [InlineData(".flac")]
    [InlineData(".wav")]
    [InlineData(".MP3")]
    public void SourceRead_EachFormat_ReturnsArtistAlbumTitle(string ext)
    {
        var t = SourceTagReader.Read(SourceFiles.Make(_fx, "abc", ext, Abc));
        Assert.NotNull(t);
        Assert.Equal("Emotional Oranges", t!.Value.Artist);
        Assert.Equal("West Coast Love", t.Value.Album);
        Assert.Equal("Song Title", t.Value.Title);
        Assert.Equal("", t.Value.AiTool);
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-1] [AC-gh-20-source-tags-ai-marker-2]
    public void Import_ConvertedScdIsTagFree_ButSourceTagsCaptured()
    {
        string src = SourceFiles.Make(_fx, "hc", ".m4a", Abc + "\ncomment=" + SourceFiles.Esc(SourceFiles.HolyCrit));
        string ogg = _fx.PathFor("hc-out.ogg");
        FFMpeg.ConvertToOgg(src, ogg, false);
        string scd = TaggedScd.Wrap(_fx, ogg, "hc-out");
        byte[] bytes = File.ReadAllBytes(scd);
        var fromScd = SongTagReader.FromScdBytes(bytes);
        Assert.Equal(SongTags.None, fromScd);
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var s in new[] { "Emotional Oranges", "made with suno", "handler_name", "major_brand" })
            Assert.DoesNotContain(s, text, StringComparison.OrdinalIgnoreCase);

        var captured = SourceTagReader.ForImport(src, SourceTagReader.Read);
        Assert.NotNull(captured);
        Assert.Equal("Emotional Oranges", captured!.Value.Artist);
        Assert.Equal("West Coast Love", captured.Value.Album);
        Assert.Equal("Suno", captured.Value.AiTool);
    }

    // by name: NVorbis 0.10's Tags.All throws
    private static Dictionary<string, string> OggTags(string ogg)
    {
        using var r = new NVorbis.VorbisReader(ogg);
        return new[] { "LOOPSTART", "LOOPEND", "ARTIST" }
            .Where(k => !string.IsNullOrEmpty(r.Tags.GetTagSingle(k)))
            .ToDictionary(k => k, k => r.Tags.GetTagSingle(k));
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-2] [EDGE-gh-20-source-tags-ai-marker-4]
    public void ConvertToOgg_KeepsLoopTagsOnly()
    {
        string src = SourceFiles.Make(_fx, "loop", ".flac", "artist=Someone\nLoopStart=1000\nLoopEnd=50000");
        string ogg = _fx.PathFor("loop-out.ogg");
        FFMpeg.ConvertToOgg(src, ogg, false);
        var tags = OggTags(ogg);
        Assert.Equal("1000", tags["LOOPSTART"]);
        Assert.Equal("50000", tags["LOOPEND"]);
        Assert.False(tags.ContainsKey("ARTIST"));
    }

    private const string Lyrics = "[Verse]\nfirst line\n;semi line\n#hash line\n[Chorus]\nsecond line";

    [Fact] // [AC-gh-20-source-tags-ai-marker-2] [EDGE-gh-20-source-tags-ai-marker-4]
    public void ConvertToOgg_LoopStartAfterBracketLyrics_StillCarried()
    {
        string src = SourceFiles.Make(_fx, "lyr", ".flac", "artist=Someone\ncomment=" + SourceFiles.Esc(Lyrics) + "\nLoopStart=1234\nLoopEnd=56789");
        string ogg = _fx.PathFor("lyr-out.ogg");
        FFMpeg.ConvertToOgg(src, ogg, false);
        var tags = OggTags(ogg);
        Assert.Equal("1234", tags["LOOPSTART"]);
        Assert.Equal("56789", tags["LOOPEND"]);
        Assert.False(tags.ContainsKey("ARTIST"));
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-4]
    public void SourceRead_LyricsWithBracketLines_KeepsLaterKeys()
    {
        string src = SourceFiles.Make(_fx, "lyr3", ".mp3",
            "lyrics_text=" + SourceFiles.Esc(Lyrics) + "\ncomment=" + SourceFiles.Esc(SourceFiles.HolyCrit) + "\nLoopStart=777");
        var fields = FFMpeg.ReadFfMetadata(src);
        Assert.Contains(fields, f => f.Value == Lyrics);
        Assert.Contains(fields, f => f.Value == SourceFiles.HolyCrit);
        Assert.Contains(fields, f => f.Key.Equals("LoopStart", StringComparison.OrdinalIgnoreCase) && f.Value == "777");
        Assert.Equal("Suno", SourceTagReader.Read(src)!.Value.AiTool);
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-22]
    public void SourceRead_CrLfComment_KeepsLaterKeys()
    {
        string src = SourceFiles.Make(_fx, "crlf", ".flac", "comment=" + SourceFiles.Esc("line1\r\nline2") + "\nartist=After");
        var fields = FFMpeg.ReadFfMetadata(src);
        // ffmpeg's own reader may drop the CR; the parser keeps one (FfMetadata_ParseGlobal_Rules). Later keys survive either way.
        Assert.Contains(fields, f => f.Value.StartsWith("line1", StringComparison.Ordinal));
        Assert.Equal("After", SourceTagReader.Read(src)!.Value.Artist);
    }

    [Theory] // [AC-gh-20-source-tags-ai-marker-3] [EDGE-gh-20-source-tags-ai-marker-6] [EDGE-gh-20-source-tags-ai-marker-17]
    [InlineData("a.ogg")]
    [InlineData("a.OGG")]
    [InlineData("a.scd")]
    [InlineData("a.Scd")]
    public void ForImport_OggAndScd_AnyCase_NeverCallReader(string name)
    {
        Assert.Null(SourceTagReader.ForImport(Path.Combine(@"C:\x", name), _ => throw new InvalidOperationException("called")));
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-3] [EDGE-gh-20-source-tags-ai-marker-6]
    public void ScdFileImport_UppercaseOgg_KeepsTags()
    {
        string ogg = TaggedScd.Ogg(_fx, "upper", "-metadata artist=\"Upper Artist\"", seconds: 3);
        string lower = _fx.PathFor("copy-lower.ogg"), upper = _fx.PathFor("COPY-UPPER.OGG");
        File.Copy(ogg, lower);
        File.Copy(ogg, upper);
        string Imported(string path, string outName)
        {
            var scd = ScdFile.Import(path);
            string outPath = _fx.PathFor(outName);
            using (var bw = new BinaryWriter(File.Create(outPath))) scd.Write(bw);
            return SongTagReader.FromScdBytes(File.ReadAllBytes(outPath))?.Artist ?? "";
        }
        string fromLower = Imported(lower, "lower.scd");
        Assert.Equal(fromLower, Imported(upper, "upper.scd")); // the same path as ".ogg" now
        Assert.Equal("Upper Artist", fromLower);
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-9] [EDGE-gh-20-source-tags-ai-marker-2]
    public void ForImport_ReaderFailsOrThrows_NullNoThrow()
    {
        Assert.Null(SourceTagReader.ForImport(@"C:\x\a.mp3", _ => null));
        Assert.Null(SourceTagReader.ForImport(@"C:\x\a.mp3", _ => throw new IOException("boom")));
        string junk = _fx.PathFor("junk.mp3");
        File.WriteAllBytes(junk, Enumerable.Range(0, 4096).Select(i => (byte)(i * 37)).ToArray());
        Assert.Null(SourceTagReader.Read(junk));
        Assert.Null(SourceTagReader.Read(_fx.PathFor("missing.mp3")));
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-9] [EDGE-gh-20-source-tags-ai-marker-23]
    public void ReadFfMetadata_Timeout_KillsAndThrows_ReadReturnsNull()
    {
        string src = SourceFiles.Make(_fx, "slow", ".flac", Abc);
        int before = SourceFiles.TempMetaFiles();
        Assert.Throws<TimeoutException>(() => FFMpeg.ReadFfMetadata(src, TimeSpan.FromMilliseconds(1)));
        Assert.Equal(before, SourceFiles.TempMetaFiles()); // killed, waited for, then deleted
        Assert.Null(SourceTagReader.ForImport(src, p =>
        {
            try { return SourceTagReader.FromFields(FFMpeg.ReadFfMetadata(p, TimeSpan.FromMilliseconds(1))); }
            catch (TimeoutException) { return null; }
        }));
    }

    [Theory] // [EDGE-gh-20-source-tags-ai-marker-1] [EDGE-gh-20-source-tags-ai-marker-9]
    [InlineData(".mp3")]
    [InlineData(".m4a")]
    [InlineData(".wav")]
    [InlineData(".flac")]
    public void SourceRead_Untagged_AllFormats_NoSourceEntry(string ext)
    {
        string src = SourceFiles.Make(_fx, "plain", ext);
        Assert.Contains(FFMpeg.ReadFfMetadata(src), f => f.Value.StartsWith("Lavf", StringComparison.Ordinal));
        Assert.Equal(SongTags.None, SourceTagReader.Read(src));
        Assert.Null(SourceTagReader.ForImport(src, SourceTagReader.Read));

        string cachePath = Path.Combine(_dir, "song_tags_cache_v2.json");
        var c = new SongTagCache(cachePath);
        if (SourceTagReader.ForImport(src, SourceTagReader.Read) is { } t) c.StoreSource(@"C:\M\plain.scd", t);
        c.Flush();
        Assert.Null(new SongTagCache(cachePath).TryGet(@"C:\M\plain.scd"));
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-3] [EDGE-gh-20-source-tags-ai-marker-24]
    public void SourceRead_EscapedAndMultiValue()
    {
        const string comment = "two\nlines = with; specials # and \\ backslash";
        string src = SourceFiles.Make(_fx, "multi", ".flac", "artist=" + SourceFiles.Esc("A;B") + "\ncomment=" + SourceFiles.Esc(comment));
        var t = SourceTagReader.Read(src)!.Value;
        Assert.Equal("A, B", t.Artist);
        Assert.Contains(FFMpeg.ReadFfMetadata(src), f => f.Value == comment);
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-7]
    public void ForImport_PassesOriginalPath()
    {
        string? seen = null;
        SourceTagReader.ForImport(@"D:\Music\My Song.m4a", p => { seen = p; return null; });
        Assert.Equal(@"D:\Music\My Song.m4a", seen);
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-4] [AC-gh-20-source-tags-ai-marker-7] [EDGE-gh-20-source-tags-ai-marker-17]
    public void ScdReader_HolyCritDescription_Flags()
    {
        string ogg = TaggedScd.Ogg(_fx, "hcscd", $"-metadata comment=\"{SourceFiles.HolyCrit}\" -metadata artist=\"Kuroi\"");
        string scd = TaggedScd.Wrap(_fx, ogg, "hcscd");
        Assert.Equal("Suno", SongTagReader.FromScdBytes(SongTagReader.ReadPrefix(scd, SongTagReader.PrefixBytes))!.Value.AiTool);
        Assert.Equal("Suno", SongTagReader.FromScdBytes(File.ReadAllBytes(scd))!.Value.AiTool);
        Assert.Equal("Suno", SongTagReader.Read(scd)!.Value.AiTool);
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-7] [EDGE-gh-20-source-tags-ai-marker-14]
    public void Scan_V2Cache_RereadsSong_StoresMarker_V1Untouched()
    {
        Directory.CreateDirectory(_dir);
        string v1 = Path.Combine(_dir, "song_tags_cache_v1.json");
        File.WriteAllText(v1, "{\"version\":1,\"songs\":{\"c:\\\\m\\\\hc.scd\":[\"Kuroi\",\"\",\"\"]}}");
        byte[] v1Before = File.ReadAllBytes(v1);
        string ogg = TaggedScd.Ogg(_fx, "scan", $"-metadata comment=\"{SourceFiles.HolyCrit}\"");
        string scd = TaggedScd.Wrap(_fx, ogg, "scan");
        var c = new SongTagCache(Path.Combine(_dir, "song_tags_cache_v2.json"));
        var r = SongTagScan.Run([scd], c, SongTagReader.Read, CancellationToken.None);
        Assert.Equal(1, r.Marked);
        Assert.Equal("Suno", new SongTagCache(Path.Combine(_dir, "song_tags_cache_v2.json")).TryGet(scd)!.Value.AiTool);
        Assert.Equal(v1Before, File.ReadAllBytes(v1));
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-29]
    public void ScdReader_Adpcm_NoMarker()
    {
        Assert.Equal(SongTags.None, SongTagReader.Read(_fx.MakeAdpcmScd()) ?? SongTags.None);
    }
}

/// <summary>Parsing, the marker vocabulary, the two-file cache and the display model: no ffmpeg.</summary>
public sealed class SourceTagsAiMarkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "srcmark-" + Guid.NewGuid().ToString("N"));
    private string ScdPath => Path.Combine(_dir, "song_tags_cache_v2.json");
    private string SourcePath => Path.Combine(_dir, "song_source_tags_v1.json");
    private SongTagCache Cache() => new(ScdPath);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static KeyValuePair<string, string> F(string k, string v) => new(k, v);

    // ── ffmetadata parsing ──

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-3] [EDGE-gh-20-source-tags-ai-marker-4] [EDGE-gh-20-source-tags-ai-marker-22]
    public void FfMetadata_ParseGlobal_Rules()
    {
        // the shape the bundled ffmpeg writes: escapes, backslash-newline continuations, then stream sections
        const string text =
            ";FFMETADATA1\n" +
            "major_brand=M4A \n" +
            "comment=[Verse]\\\n[Chorus]\\\n\\;semi\\\n\\#hash\\\nlast \\= done\n" +
            "crlf=one\r\\\ntwo\r\n" +
            "# a comment line\n" +
            "; another\n" +
            "no equals sign here\n" +
            "k\\=ey=v\\\\al\n" +
            "encoder=Lavf62.6.101\n" +
            "[STREAM]\n" +
            "handler_name=SoundHandler\n";
        var p = FfMetadata.ParseGlobal(text);
        Assert.Equal(new[] { "major_brand", "comment", "crlf", "k=ey", "encoder" }, p.Select(x => x.Key));
        Assert.Equal("M4A ", p[0].Value);
        Assert.Equal("[Verse]\n[Chorus]\n;semi\n#hash\nlast = done", p[1].Value);
        Assert.Equal("one\r\ntwo", p[2].Value);
        Assert.Equal("v\\al", p[3].Value);
        Assert.DoesNotContain(p, x => x.Key == "handler_name");
        Assert.Empty(FfMetadata.ParseGlobal(""));
    }

    // ── marker vocabulary ──

    [Theory] // [AC-gh-20-source-tags-ai-marker-8] [EDGE-gh-20-source-tags-ai-marker-9]
    [InlineData("artist", "Suno")]
    [InlineData("title", "Udio")]
    [InlineData("album", "made with suno")]
    [InlineData("album_artist", "Suno")]
    [InlineData("genre", "udio.com")]
    [InlineData("comment", "Recorded at studio.com")]
    [InlineData("comment", "studio.com")]
    [InlineData("comment", "audio.com")]
    [InlineData("comment", "Sunoco station")]
    [InlineData("comment", "remade with sunotron")]
    [InlineData("comment", "Converted by SpotiDownloader.com")]
    [InlineData("encoder", "made with suno")]
    [InlineData("encoded_by", "Lavf62.6.101")]
    [InlineData("comment", "Lavc62.19.100 libvorbis")]
    public void Marker_FalsePositives_NotFlagged(string key, string value)
    {
        Assert.Equal("", AiGeneratorMarker.Detect([F(key, value)]));
        if (key is "encoder" or "encoded_by" || value.StartsWith("Lav")) Assert.Equal("", AiGeneratorMarker.MarkerText([F(key, value)]));
    }

    [Theory] // [AC-gh-20-source-tags-ai-marker-4] [EDGE-gh-20-source-tags-ai-marker-10]
    [InlineData("comment", SourceFiles.HolyCrit, "Suno")]
    [InlineData("comment-eng", "Made With SUNO", "Suno")]
    [InlineData("ID3v1 Comment", "created with Udio", "Udio")]
    [InlineData("DESCRIPTION", "generated by udio", "Udio")]
    [InlineData("encoded_by", "suno.ai", "Suno")]
    [InlineData("software", "udio.com", "Udio")]
    [InlineData("publisher", "made with suno", "Suno")]
    [InlineData("ORGANIZATION", "made using udio", "Udio")]
    [InlineData("comment", "made with udio, then suno.com", "Suno")]
    public void Marker_Variants_Detected(string key, string value, string tool)
    {
        Assert.Equal(tool, AiGeneratorMarker.Detect([F(key, value)]));
        Assert.Equal(tool, AiGeneratorMarker.DetectText(AiGeneratorMarker.MarkerText([F(key, value)])));
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-5] [EDGE-gh-20-source-tags-ai-marker-21]
    public void Marker_BoundsAndLinearTime()
    {
        string late = new string('x', 600) + " made with suno " + new string('y', 100_000);
        Assert.Equal("", AiGeneratorMarker.Detect([F("comment", late)]));
        string early = "0123456789made with suno " + new string('y', 100_000);
        Assert.Equal("", AiGeneratorMarker.Detect([F("comment", early)])); // "0123456789made" is one word
        Assert.Equal("Suno", AiGeneratorMarker.Detect([F("comment", "012345678 made with suno " + new string('y', 100_000))]));

        var many = Enumerable.Range(0, 20).Select(i => F("comment" + i, new string((char)('a' + i), 2000))).ToList();
        string mt = AiGeneratorMarker.MarkerText(many);
        Assert.True(mt.Length <= AiGeneratorMarker.MaxText);
        Assert.All(mt.Split('\n'), l => Assert.True(l.Length <= AiGeneratorMarker.MaxValue + 20));
        Assert.Equal(new string('a', 200) + "…", SourceTagReader.FromFields([F("artist", new string('a', 300))]).Artist);

        string nearMiss = string.Concat(Enumerable.Repeat("made with sun ", 4000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("", AiGeneratorMarker.Detect([F("comment", nearMiss)]));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-20]
    public void FromFields_EmojiCjkControl_SanitizedAndSaveable()
    {
        string artist = new string('a', 199) + "😀" + "x";
        var t = SourceTagReader.FromFields([F("artist", artist + "\t日本\n"), F("comment", new string('c', 511) + "😀made with suno")]);
        Assert.Equal(new string('a', 199) + "…", t.Artist);
        string commentLine = t.MarkerText.Split('\n').Single();
        Assert.Equal("comment=" + new string('c', 511), commentLine); // the emoji at 511/512 is dropped whole
        Assert.Equal("", t.AiTool); // the marker was past the cut

        // the total cap lands inside an emoji: cut before it, whole lines only
        var big = Enumerable.Range(0, 8).Select(i => F("comment" + i, new string('z', 509) + "😀")).ToList();
        string mt = AiGeneratorMarker.MarkerText(big);
        Assert.False(mt.Length > 0 && char.IsHighSurrogate(mt[^1]));

        var c = Cache();
        var withEmoji = SourceTagReader.FromFields([F("artist", "日本 😀"), F("comment", "😀 made with suno 😀")]);
        c.StoreSource(@"C:\M\e.scd", withEmoji);
        c.Flush();
        System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SourcePath));
        Assert.Equal(withEmoji, Cache().TryGet(@"C:\M\e.scd"));
        Assert.Equal("Suno", withEmoji.AiTool);
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-30]
    public void MarkerText_MultiLineAndEscapes_Lossless()
    {
        var t = SourceTagReader.FromFields([F("comment", "first line\n" + SourceFiles.HolyCrit)]);
        Assert.Equal("Suno", t.AiTool);
        Cache().StoreSource(@"C:\M\ml.scd", t);
        Assert.Equal("Suno", Cache().TryGet(@"C:\M\ml.scd")!.Value.AiTool);

        // escapes round-trip: a value that only matches if "\" and "=" come back exactly
        var tricky = new[] { F("comment=x","a\\b=c\r\nd\\nmade with\\ suno"), F("description", "x\\\nmade with udio") };
        string mt = AiGeneratorMarker.MarkerText(tricky);
        Assert.Equal(AiGeneratorMarker.Detect(tricky), AiGeneratorMarker.DetectText(mt));
        Assert.Equal("Udio", AiGeneratorMarker.DetectText(mt));
        Assert.Single(mt.Split('\n'), l => l.StartsWith("comment\\=x=",StringComparison.Ordinal));
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-25]
    public void SourceEntry_MarkerRederivedFromStoredText()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SourcePath,
            "{\"version\":1,\"songs\":{\"c:\\\\m\\\\a.scd\":[\"A\",\"\",\"\",\"comment=made with suno\"],\"c:\\\\m\\\\b.scd\":[\"B\",\"\",\"\",\"comment=hello\"]}}");
        Assert.Equal("Suno", Cache().TryGet(@"C:\M\a.scd")!.Value.AiTool);
        Assert.Equal("", Cache().TryGet(@"C:\M\b.scd")!.Value.AiTool);
    }

    // ── caches ──

    private static readonly SongTags Tagged = new("Kuroi", "Album", "Holy Crit", "Suno");
    // the source file keeps the marker text and derives AiTool from it on load
    private static readonly SongTags SrcTagged = new("Kuroi", "Album", "Holy Crit", "Suno", "comment=made with suno");

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-14] [EDGE-gh-20-source-tags-ai-marker-15]
    public void Cache_V2_RoundTrip_PerEntryValidation()
    {
        Cache().Store(@"C:\M\t.scd", Tagged);
        Assert.Equal(Tagged, Cache().TryGet(@"C:\M\t.scd"));
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ScdPath))!;
        Assert.Equal(2, root["version"]!.GetValue<int>());
        Assert.Equal(4, root["songs"]!.AsObject().Single().Value!.AsArray().Count);

        File.WriteAllText(ScdPath, "{\"version\":2,\"songs\":{" +
            "\"c:\\\\m\\\\good.scd\":[\"a\",\"b\",\"c\",\"Suno\"]," +
            "\"c:\\\\m\\\\three.scd\":[\"a\",\"b\",\"c\"]," +
            "\"c:\\\\m\\\\five.scd\":[\"a\",\"b\",\"c\",\"\",\"x\"]," +
            "\"c:\\\\m\\\\num.scd\":[\"a\",\"b\",3,\"\"]," +
            "\"c:\\\\m\\\\nul.scd\":null}}");
        var c = Cache();
        Assert.Equal(new SongTags("a", "b", "c", "Suno"), c.TryGet(@"C:\M\good.scd"));
        foreach (var bad in new[] { "three", "five", "num", "nul" }) Assert.Null(c.TryGet($@"C:\M\{bad}.scd"));
    }

    [Theory] // [EDGE-gh-20-source-tags-ai-marker-16]
    [InlineData(false)]
    [InlineData(true)]
    public void Cache_BadOrOtherVersion_RenamedAside_NeverOverwritten(bool sourceFile)
    {
        Directory.CreateDirectory(_dir);
        string path = sourceFile ? SourcePath : ScdPath;
        int expected = sourceFile ? 1 : 2;
        var value = sourceFile ? SrcTagged : Tagged;
        void Write(SongTagCache c, string p) { if (sourceFile) c.StoreSource(p, value); else c.Store(p, value); }

        File.WriteAllText(path, "{ not json");
        Write(Cache(), @"C:\M\a.scd");
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
        Assert.Equal(expected, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["version"]!.GetValue<int>());

        File.WriteAllText(path, "{\"version\":99,\"songs\":{}}");
        Write(Cache(), @"C:\M\b.scd");
        Assert.True(File.Exists(path + ".v99.bak"));

        File.WriteAllText(path, "{ again not json");
        Write(Cache(), @"C:\M\c.scd");
        Assert.Single(Directory.GetFiles(_dir, Path.GetFileName(path) + ".*.bad")); // the taken name gets a timestamp
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));

        // locked: can't be read or set aside, so nothing is written and a later access retries
        File.WriteAllText(path, "{ locked not json");
        var locked = Cache();
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Write(locked, @"C:\M\d.scd");
        }
        Assert.Equal("{ locked not json", File.ReadAllText(path)); // never overwritten while it couldn't be read
        locked.Flush(); // retried now that the lock is gone: the bad file is set aside, the entry saved
        Assert.Equal(value, Cache().TryGet(@"C:\M\d.scd"));
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-14]
    public void Cache_DefaultPaths()
    {
        string src = File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), "Utils", "SongTagCache.cs"));
        Assert.Contains("\"song_tags_cache_v2.json\"", src);
        Assert.Contains("\"song_source_tags_v1.json\"", src);
        Assert.DoesNotContain("song_tags_cache_v1.json", src);
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-1] [EDGE-gh-20-source-tags-ai-marker-26]
    public void Cache_SourceWinsLookup_SurvivesScdCacheWipe()
    {
        var a = new SongTags("Src", "Alb", "T", "", "comment=x");
        var c = Cache();
        c.StoreSource(@"C:\M\p.scd", a);
        c.Store(@"C:\M\p.scd", SongTags.None);
        Assert.Equal(a, c.TryGet(@"C:\M\p.scd"));
        File.Delete(ScdPath);
        Assert.Equal(a, Cache().TryGet(@"C:\M\p.scd"));
        File.WriteAllText(ScdPath, "{\"version\":3,\"songs\":{}}");
        Assert.Equal(a, Cache().TryGet(@"C:\M\p.scd"));
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-18] [EDGE-gh-20-source-tags-ai-marker-8] [EDGE-gh-20-source-tags-ai-marker-31]
    public void Cache_SourceEntry_FollowsMoveAndForget()
    {
        var src = new SongTags("Src", "", "", "", "comment=s");
        var scd = new SongTags("Scd", "", "", "");
        var c = Cache();
        c.StoreSource(@"C:\M\both.scd", src);
        c.Store(@"C:\M\both.scd", scd);
        c.UpdateCacheForSCD(@"C:\M\both.scd", @"C:\M\both2.scd");
        Assert.Equal(src, Cache().TryGet(@"C:\M\both2.scd"));
        Assert.Null(Cache().TryGet(@"C:\M\both.scd"));
        c.Forget(@"C:\M\both2.scd"); // removes both: no SCD entry shows through
        Assert.Null(Cache().TryGet(@"C:\M\both2.scd"));

        c.StoreSource(@"C:\M\srconly.scd", src);
        c.UpdateCacheForSCD(@"C:\M\srconly.scd", @"C:\M\srconly2.scd");
        Assert.Equal(src, Cache().TryGet(@"C:\M\srconly2.scd"));
        Assert.Null(Cache().TryGet(@"C:\M\srconly.scd"));

        c.Store(@"C:\M\scdonly.scd", scd);
        c.UpdateCacheForSCD(@"C:\M\scdonly.scd", @"C:\M\scdonly2.scd");
        Assert.Equal(scd, Cache().TryGet(@"C:\M\scdonly2.scd"));
        Assert.Null(Cache().TryGet(@"C:\M\scdonly.scd"));

        long g = c.Generation(@"C:\M\gen.scd");
        c.UpdateCacheForSCD(@"C:\M\gen.scd", @"C:\M\gen2.scd");
        Assert.False(c.StoreIfUnchanged(@"C:\M\gen.scd", scd, g));

        c.StoreSource(@"C:\M\srconly2.scd", SrcTagged);
        Assert.Equal(SrcTagged, Cache().TryGet(@"C:\M\srconly2.scd"));

        // while the source file is locked (unloaded), a forget and a move still win over the later load
        var fresh = Cache();
        using (new FileStream(SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fresh.Forget(@"C:\M\srconly2.scd");
            fresh.UpdateCacheForSCD(@"C:\M\scdonly2.scd", @"C:\M\elsewhere.scd");
        }
        fresh.Flush();
        Assert.Null(Cache().TryGet(@"C:\M\srconly2.scd"));
        Assert.Null(fresh.TryGet(@"C:\M\srconly2.scd"));
    }

    [Fact] // [EDGE-gh-20-source-tags-ai-marker-27]
    public void Cache_AbsoluteKeys_OtherRootMisses()
    {
        var c = Cache();
        c.StoreSource(SongTagCache.FullPathOf("pl/x.scd", @"C:\A\Mod"), SrcTagged);
        Assert.Equal(SrcTagged, c.TryGetForRel("pl/x.scd", @"C:\A\Mod"));
        Assert.Null(c.TryGetForRel("pl/x.scd", @"D:\B\Mod"));
    }

    // ── display ──

    public static IEnumerable<object?[]> DisplayCases() =>
        from stored in new int?[] { null, -1, 10, 455, 600, 900, 1000 }
        from tool in new[] { "", "Suno" }
        from enabled in new[] { true, false }
        select new object?[] { stored, tool, enabled };

    [Theory] // [AC-gh-20-source-tags-ai-marker-4] [EDGE-gh-20-source-tags-ai-marker-11] [EDGE-gh-20-source-tags-ai-marker-12] [EDGE-gh-20-source-tags-ai-marker-13] [EDGE-gh-20-source-tags-ai-marker-28]
    [MemberData(nameof(DisplayCases))]
    public void Display_ForSong(int? stored, string tool, bool enabled)
    {
        var d = AiScoreDisplay.ForSong(stored, tool, enabled);
        if (tool.Length > 0 && enabled)
        {
            Assert.Equal((true, AiBand.CertainAi, 100, true), d);
            Assert.Equal(100, AiScoreDisplay.TooltipPercentForSong(stored, tool, enabled));
        }
        else
        {
            var (v, b, p) = AiScoreDisplay.For(stored, enabled);
            Assert.Equal((v, b, p, false), d);
            Assert.Equal(AiScoreDisplay.TooltipPercent(stored, enabled), AiScoreDisplay.TooltipPercentForSong(stored, tool, enabled));
        }
    }

    private static readonly Dictionary<string, string> s_en = Resw("en-US");
    private static string Get(string key) => s_en[key];

    private static Dictionary<string, string> Resw(string locale) =>
        XDocument.Load(Path.Combine(RuntimeTargetTests.RepoRoot(), "Strings", locale, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");

    [Fact] // [AC-gh-20-source-tags-ai-marker-4] [EDGE-gh-20-source-tags-ai-marker-28]
    public void Chip_RedCertain_AmberUnchanged()
    {
        Assert.Equal(0xFFFF6B6Bu, AiScoreText.ChipArgb(AiBand.CertainAi));
        Assert.Equal(0xFFF2A33Au, AiScoreText.ChipArgb(AiBand.LikelyAi));
        Assert.Equal(0xFFF5D08Au, AiScoreText.ChipArgb(AiBand.PossiblyAi));
        Assert.Equal("AiScore_Band_Certain", AiScoreText.BandKey(AiBand.CertainAi));
        Assert.Equal("Certainly AI", Get("AiScore_Band_Certain"));
        Assert.Equal("AiScore_Band_Ai", AiScoreText.BandKey(AiBand.LikelyAi));
        Assert.Equal("AiScore_Band_PossiblyAi", AiScoreText.BandKey(AiBand.PossiblyAi));
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-4] [AC-gh-20-source-tags-ai-marker-5] [EDGE-gh-20-source-tags-ai-marker-11] [EDGE-gh-20-source-tags-ai-marker-12] [EDGE-gh-20-source-tags-ai-marker-28]
    public void ChipTooltip_AndAutomationName()
    {
        string tip = AiScoreText.ChipTooltip("Suno", 100, Get);
        Assert.Equal("AI likelihood: 100%.\nTagged as made with Suno.", tip);
        Assert.Equal("Certainly AI; AI likelihood: 100%; Tagged as made with Suno", AiScoreText.AutomationName("Certainly AI", tip, true));
        string untagged = AiScoreText.ChipTooltip("", 72, Get);
        Assert.Equal("AI likelihood: 72%. An estimate, not proof.", untagged);
        Assert.Equal("Likely AI, AI likelihood: 72%. An estimate, not proof.", AiScoreText.AutomationName("Likely AI", untagged, false));
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-5] [EDGE-gh-20-source-tags-ai-marker-13]
    public void Tooltip_Tagged_100Percent_OrderAndVisibility()
    {
        var t = new SongTags("A", "B", "", "Suno");
        const string full = "Artist: A\nAlbum: B\nAI likelihood: 100%.\nTagged as made with Suno.";
        Assert.Equal(full, SongTooltip.Build(t, 10, true, Get));
        Assert.Equal(full, SongTooltip.Build(t, null, true, Get));
        Assert.Equal("Artist: A\nAlbum: B", SongTooltip.Build(t, 10, false, Get));
        Assert.Null(SongTooltip.Build(new SongTags("", "", "", "Suno"), 10, false, Get));
        Assert.Equal("Artist: A\nAI likelihood: 10%. An estimate, not proof.", SongTooltip.Build(new SongTags("A", "", ""), 100, true, Get));
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-6] [EDGE-gh-20-source-tags-ai-marker-11] [EDGE-gh-20-source-tags-ai-marker-12]
    public void Sort_Tagged100_AboveAllScores_StableAmongTagged()
    {
        Option S(string n) => new() { Name = n };
        var t1 = S("t1"); var tu = S("tu"); var m1000 = S("1000"); var m900 = S("900"); var m300 = S("300"); var un = S("un"); var neg = S("neg");
        var data = new Dictionary<Option, (int? Stored, string Tool)>
        {
            [t1] = (10, "Suno"), [tu] = (null, "Udio"), [m1000] = (1000, ""), [m900] = (900, ""), [m300] = (300, ""), [un] = (null, ""), [neg] = (-1, ""),
        };
        var all = new[] { t1, tu, m1000, m900, m300, un, neg };
        int? Key(Option o) => AiScoreDisplay.SortKey(data[o].Stored, data[o].Tool);
        Assert.Equal(new[] { t1, tu, m1000, m900, m300, un, neg }, Playlist.OrderByAiScore(all, Key, SortDirection.Descending));
        Assert.Equal(new[] { m300, m900, m1000, t1, tu, un, neg }, Playlist.OrderByAiScore(all, Key, SortDirection.Ascending));
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-10]
    public void NewAiKeys_InAllSevenLocales()
    {
        foreach (var loc in new[] { "en-US", "de-DE", "es-ES", "fr-FR", "nl-NL", "sv-SE", "zh-Hans" })
        {
            var r = Resw(loc);
            Assert.Single(Regex.Matches(r["AiScore_Tagged"], @"\{0\}"));
            Assert.Single(Regex.Matches(r["AiScore_TooltipTagged"], @"\{0\}"));
            Assert.DoesNotMatch(@"\{\d+\}", r["AiScore_Band_Certain"]);
            Assert.NotEmpty(r["AiScore_Band_Certain"].Trim());
        }
        Assert.Equal("Tagged as made with {0}.", s_en["AiScore_Tagged"]);
        Assert.Equal("AI likelihood: {0}%.", s_en["AiScore_TooltipTagged"]);
    }
}

/// <summary>Source-level checks of the wiring the user tests in the app.</summary>
public sealed class SourceTagsAiMarkerWiringTests
{
    private static string Src(string rel) => File.ReadAllText(Path.Combine(RuntimeTargetTests.RepoRoot(), rel));

    private static string Method(string src, string signature)
    {
        int start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = src.IndexOf('{', start), depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[start..(i + 1)];
        }
        return src[start..];
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-2] [AC-gh-20-source-tags-ai-marker-9]
    public void FFMpeg_StripsMetadata_MetadataRunsBounded()
    {
        string f = Src("Utils/FFMpeg.cs");
        Assert.Contains("-map_metadata -1", Method(f, "public static void ConvertToOgg("));
        Assert.Contains("-map_metadata -1", Method(f, "public static void TranscodeToOgg("));
        Assert.Contains("ReadFfMetadata(", Method(f, "private static string LoopTagArgs("));
        Assert.Contains("TimeSpan.FromSeconds(15)", Method(f, "internal static IReadOnlyList<KeyValuePair<string, string>> ReadFfMetadata("));
        string run = Method(f, "private static string Run(");
        Assert.Contains("Kill(entireProcessTree: true)", run);
        Assert.True(run.IndexOf("Kill(", StringComparison.Ordinal) < run.IndexOf("process.WaitForExit();", run.IndexOf("Kill(", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-3]
    public void ScdFileImport_SwitchCaseInsensitive_YtDlpVorbis()
    {
        Assert.Contains("switch (Path.GetExtension(path).ToLowerInvariant())", Src("ScdFormat/ScdFile.cs"));
        Assert.Contains("--audio-format vorbis", Src("Tools/YtDlpService.cs"));
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-4]
    public void ApplyAiChip_MarkerFromCache_ColourFromChipArgb()
    {
        string chip = Method(Src("ui/MainWindow.AiScore.cs"), "private static void ApplyAiChip(");
        foreach (var s in new[] { "AiScoreDisplay.ForSong(", "SongTagCache.Default.TryGetForRel(node.ScdRel, modRoot)", "AiScoreText.ChipArgb(",
                     "AiScoreText.ChipTooltip(", "AiScoreText.AutomationName(" })
            Assert.Contains(s, chip);
        foreach (var s in new[] { "SongTagReader", "SourceTagReader", "0xF2", "0xF5" }) Assert.DoesNotContain(s, chip);
        Assert.Contains("ApplyAiChip(node, modRoot);", Src("ui/MainWindowCore.cs"));
        Assert.DoesNotContain("never red", Src("ui/MainWindow.xaml"));
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-6]
    public void SortByAiLikelihood_UsesSortKeyAndTagCache()
    {
        string sort = Method(Src("Playlist.cs"), "internal void SortByAiLikelihood(");
        Assert.Contains("AiScoreDisplay.SortKey(", sort);
        Assert.Contains("SongTagCache.Default.TryGetForRel(", sort);
    }

    [Fact] // [AC-gh-20-source-tags-ai-marker-7] [EDGE-gh-20-source-tags-ai-marker-19]
    public void ScanEnd_RefreshesChipsToo()
    {
        Assert.Contains("ApplyAiChip(n, modRoot); ApplySongTooltip(n, modRoot);", Src("ui/MainWindow.SongTags.cs"));
    }
}
