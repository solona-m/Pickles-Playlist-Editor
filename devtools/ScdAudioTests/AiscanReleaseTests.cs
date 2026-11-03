using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace ScdAudioTests;

public sealed class AiscanReleaseTests : IDisposable
{
    private static string Root() => RuntimeTargetTests.RepoRoot();
    private static string Workflow() => File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "upload-aiscan.yml"));
    private static string Script(string name) => Path.Combine(Root(), "devtools", "aiscan", name);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "aiscan-rel-" + Guid.NewGuid().ToString("N"));

    public AiscanReleaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static (int Code, string Out) Ps(string script, IDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(args)) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.Result);
    }

    // ---------- workflow ----------

    [Fact] // [AC-ai-assets-download-3] [EDGE-ai-assets-download-21]
    public void Workflow_TriggersOnlyOnAiscanTags()
    {
        string wf = Workflow();
        string on = Regex.Match(wf, @"(?ms)^on:\s*\n(.*?)^\S").Groups[1].Value;
        Assert.Matches(@"(?m)^\s+push:\s*\n\s+tags:\s*\n\s+- 'aiscan-\*'\s*$", on);
        foreach (var bad in new[] { "workflow_dispatch", "branches", "pull_request", "schedule" }) Assert.DoesNotContain(bad, wf);
        Assert.False(Glob("aiscan-v1", "v*"));
        Assert.False(Glob("aiscan-v1", "testing-*"));
        Assert.True(Glob("aiscan-v1", "aiscan-*"));
        Assert.Contains("- 'v*'", File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "release.yml")));
        Assert.Contains("- 'testing-*'", File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "testing-release.yml")));
    }

    private static bool Glob(string tag, string pattern) => Regex.IsMatch(tag, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");

    [Fact] // [AC-ai-assets-download-3]
    public void Workflow_UsesReleaseTokenAndReadOnlyPermissions()
    {
        string wf = Workflow();
        Assert.Matches(@"(?m)^permissions:\s*\n\s+contents: read\s*$", wf);
        Assert.DoesNotContain("secrets.GITHUB_TOKEN", wf);
        foreach (Match step in Regex.Matches(wf, @"(?ms)^      - name: .*?(?=^      - |\z)"))
        {
            string code = string.Join('\n', step.Value.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
            if (code.Contains("gh release")) Assert.Contains("GH_TOKEN: ${{ secrets.RELEASE_TOKEN }}", code);
        }
    }

    [Fact] // [AC-ai-assets-download-3] [EDGE-ai-assets-download-20]
    public void Workflow_RefusesExisting_ThenCreatesDraftNotLatest()
    {
        string wf = Workflow();
        int refuse = wf.IndexOf("gh release view", StringComparison.Ordinal);
        int create = wf.IndexOf("gh release create", StringComparison.Ordinal);
        Assert.True(refuse > 0 && create > refuse);
        Assert.Contains("$PSNativeCommandUseErrorActionPreference = $false", wf);
        string createLine = wf[create..wf.IndexOf("@((", create, StringComparison.Ordinal)];
        foreach (var flag in new[] { "--draft", "--latest=false", "--verify-tag", "--notes-file devtools/aiscan/release-notes.md" }) Assert.Contains(flag, createLine);
        Assert.Contains("devtools/aiscan/releases.json", wf);
        Assert.DoesNotContain("ffmpeg.exe", wf.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).Aggregate("", (a, b) => a + b));
        Assert.DoesNotContain("sonics", wf);
        Assert.Contains("REF_NAME: ${{ github.ref_name }}", wf);
        Assert.Contains(@"-cnotmatch '^aiscan-v\d+\z'", wf);
        Assert.DoesNotContain("-EmitPins", wf);
    }

    [Fact] // [AC-ai-assets-download-3]
    public void Workflow_ActionsPinnedBySha()
    {
        var uses = Regex.Matches(Workflow(), @"uses:\s*(\S+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(uses);
        Assert.All(uses, u => Assert.Matches("@[0-9a-f]{40}$", u));
    }

    // ---------- stage script ----------

    private string FakeManifest(long archiveBytes, string archiveSha, string exeSha = "0000000000000000000000000000000000000000000000000000000000000000", long exeBytes = 1)
    {
        var m = new JsonObject
        {
            ["aiscan-v1"] = new JsonArray(new JsonObject
            {
                ["name"] = "ffmpeg.exe", ["bytes"] = exeBytes, ["sha256"] = exeSha,
                ["source"] = new JsonObject
                {
                    ["kind"] = "gyan", ["url"] = "https://example.invalid/fake-archive.7z", ["archiveBytes"] = archiveBytes,
                    ["archiveSha256"] = archiveSha, ["path"] = "fake/bin/ffmpeg.exe", ["version"] = "x", ["sourceUrl"] = "x",
                },
            }),
        };
        string path = Path.Combine(_dir, "manifest-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, m.ToJsonString());
        return path;
    }

    private string OutDir() => Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N"));

    private static bool EmptyOrMissing(string dir) => !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();

    [Theory] // [AC-ai-assets-download-2] [EDGE-ai-assets-download-18]
    [InlineData("aiscan-x")]
    [InlineData("Aiscan-v1")]
    [InlineData("aiscan-v99")]
    [InlineData("aiscan-v1x")]
    public void Stage_RejectsMalformedOrUnlistedTag(string tag)
    {
        string outDir = OutDir();
        var (code, output) = Ps(Script("Stage-AiscanAssets.ps1"), null, "-Tag", tag, "-OutDir", outDir, "-CacheDir", Path.Combine(_dir, "cache"), "-Offline");
        Assert.NotEqual(0, code);
        Assert.Contains("FAIL", output);
        Assert.True(EmptyOrMissing(outDir));
    }

    [Fact] // [EDGE-ai-assets-download-18]
    public void Stage_UsesEndAnchoredTagRegex()
    {
        string s = File.ReadAllText(Script("Stage-AiscanAssets.ps1"));
        Assert.Contains(@"'^aiscan-v\d+\z'", s);
        Assert.DoesNotContain(@"'^aiscan-v\d+$'", s);
    }

    private string FakeArchive(int length)
    {
        string cache = Path.Combine(_dir, "cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "fake-archive.7z"), new byte[length]);
        return cache;
    }

    [Fact] // [AC-ai-assets-download-2] [EDGE-ai-assets-download-18]
    public void Stage_ArchiveLengthMismatch_FailsOffline()
    {
        string cache = FakeArchive(100), outDir = OutDir();
        var (code, output) = Ps(Script("Stage-AiscanAssets.ps1"), null, "-Tag", "aiscan-v1", "-Manifest", FakeManifest(101, new string('a', 64)),
            "-OutDir", outDir, "-CacheDir", cache, "-Offline");
        Assert.NotEqual(0, code);
        Assert.Contains("archive size mismatch", output);
        Assert.False(File.Exists(Path.Combine(cache, "fake-archive.7z")));
        Assert.True(EmptyOrMissing(outDir));
    }

    [Fact] // [AC-ai-assets-download-2] [EDGE-ai-assets-download-18]
    public void Stage_ArchiveHashMismatch_FailsOffline()
    {
        string cache = FakeArchive(100), outDir = OutDir();
        var (code, output) = Ps(Script("Stage-AiscanAssets.ps1"), null, "-Tag", "aiscan-v1", "-Manifest", FakeManifest(100, new string('a', 64)),
            "-OutDir", outDir, "-CacheDir", cache, "-Offline");
        Assert.NotEqual(0, code);
        Assert.Contains("archive SHA-256 mismatch", output);
        Assert.False(File.Exists(Path.Combine(cache, "fake-archive.7z")));
        Assert.True(EmptyOrMissing(outDir));
    }

    [Fact] // [AC-ai-assets-download-2] [EDGE-ai-assets-download-18]
    public void Stage_BlankPinsWithoutEmitPins_FailsBeforeArchive()
    {
        string cache = FakeArchive(100);
        string sha = Convert.ToHexString(SHA256.HashData(new byte[100])).ToLowerInvariant();
        var (code, output) = Ps(Script("Stage-AiscanAssets.ps1"), null, "-Tag", "aiscan-v1", "-Manifest", FakeManifest(100, sha, exeSha: "", exeBytes: 0),
            "-OutDir", OutDir(), "-CacheDir", cache, "-Offline");
        Assert.NotEqual(0, code);
        Assert.Contains("pins not filled", output);
        Assert.True(File.Exists(Path.Combine(cache, "fake-archive.7z"))); // stopped before touching the archive

        (code, output) = Ps(Script("Stage-AiscanAssets.ps1"), null, "-Tag", "aiscan-v1", "-Manifest", FakeManifest(100, ""),
            "-OutDir", OutDir(), "-CacheDir", cache, "-Offline", "-EmitPins");
        Assert.NotEqual(0, code);
        Assert.Contains("archive pin not filled", output);
    }

    [SkippableFact] // [AC-ai-assets-download-2]
    public void Stage_RealArchive_StagesPinnedFfmpegWithWorkingSoxr()
    {
        string? cache = Environment.GetEnvironmentVariable("AISCAN_ARCHIVE_DIR");
        Skip.If(string.IsNullOrEmpty(cache) || !File.Exists(Path.Combine(cache, "ffmpeg-9.0.2-full_build.7z")), "AISCAN_ARCHIVE_DIR does not hold the pinned archive");
        string outDir = OutDir();
        var (code, output) = Ps(Script("Stage-AiscanAssets.ps1"), null, "-Tag", "aiscan-v1", "-OutDir", outDir, "-CacheDir", cache!, "-Offline");
        Assert.True(code == 0, output);
        string exe = Path.Combine(outDir, "ffmpeg.exe");
        Assert.Equal(227823104, new FileInfo(exe).Length);
        Assert.Equal("589e50b766d251afdf181dd664d40bd94407e200b019989fd7468c7d118a28d0", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant());
        Assert.Contains("ffmpeg version 9.0.2-full_build-www.gyan.dev", output);
        Assert.Contains("--enable-libsoxr", output);
        Assert.Contains("soxr: 16000 samples", output);
        Assert.Contains("new(\"ffmpeg.exe\", \"aiscan-v1\", 227823104L,", output);
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "soxr.*", SearchOption.TopDirectoryOnly));
    }

    // ---------- publish script ----------

    private const string FakeGh = """
        $log = $env:FAKE_GH_LOG; $state = $env:FAKE_GH_STATE
        Add-Content $log ($args -join ' ')
        $s = Get-Content $state -Raw | ConvertFrom-Json
        if ($args[0] -eq 'api') { ConvertTo-Json -Depth 5 -InputObject @($s); exit 0 }
        if ($args[0] -eq 'release' -and $args[1] -eq 'upload') {
            if ($env:FAKE_GH_UPLOAD -eq 'noop') { exit 0 }
            $f = Get-Item $args[3]; $size = $f.Length; if ($env:FAKE_GH_UPLOAD -eq 'badsize') { $size = $size - 1 }
            $s.assets = @($s.assets) + @([pscustomobject]@{ name = $f.Name; size = $size })
            ConvertTo-Json -Depth 5 -InputObject $s | Set-Content $state; exit 0
        }
        if ($args[0] -eq 'release' -and $args[1] -eq 'edit') { exit 0 }
        exit 1
        """;

    private (string Dir, string Manifest, Dictionary<string, string> Env, string Log) PublishSetup(bool draft, bool preUploadOnnx = false, bool corrupt = false)
    {
        string files = Path.Combine(_dir, "models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(files);
        var onnx = new byte[300]; new Random(1).NextBytes(onnx);
        var data = new byte[500]; new Random(2).NextBytes(data);
        File.WriteAllBytes(Path.Combine(files, "m.onnx"), onnx);
        File.WriteAllBytes(Path.Combine(files, "m.onnx.data"), data);
        string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var manifest = new JsonObject
        {
            ["aiscan-v1"] = new JsonArray(
                new JsonObject { ["name"] = "ffmpeg.exe", ["bytes"] = 1000, ["sha256"] = new string('f', 64), ["source"] = new JsonObject { ["kind"] = "gyan" } },
                new JsonObject { ["name"] = "m.onnx", ["bytes"] = 300, ["sha256"] = corrupt ? new string('0', 64) : Hex(onnx), ["source"] = new JsonObject { ["kind"] = "manual" } },
                new JsonObject { ["name"] = "m.onnx.data", ["bytes"] = 500, ["sha256"] = Hex(data), ["source"] = new JsonObject { ["kind"] = "manual" } }),
        };
        string mpath = Path.Combine(files, "releases.json");
        File.WriteAllText(mpath, manifest.ToJsonString());

        var assets = new JsonArray(new JsonObject { ["name"] = "ffmpeg.exe", ["size"] = 1000 });
        if (preUploadOnnx) assets.Add(new JsonObject { ["name"] = "m.onnx", ["size"] = 300 });
        string state = Path.Combine(files, "state.json");
        File.WriteAllText(state, new JsonObject { ["tag_name"] = "aiscan-v1", ["draft"] = draft, ["assets"] = assets }.ToJsonString());
        string log = Path.Combine(files, "gh.log");
        File.WriteAllText(log, "");
        File.WriteAllText(Path.Combine(files, "gh.ps1"), FakeGh);
        return (files, mpath, new Dictionary<string, string> { ["FAKE_GH_LOG"] = log, ["FAKE_GH_STATE"] = state }, log);
    }

    private (int, string) Publish((string Dir, string Manifest, Dictionary<string, string> Env, string Log) s) =>
        Ps(Script("Publish-AiscanModels.ps1"), s.Env, "-Tag", "aiscan-v1", "-Dir", s.Dir, "-Manifest", s.Manifest, "-Gh", Path.Combine(s.Dir, "gh.ps1"));

    [Fact] // [AC-ai-assets-download-4] [EDGE-ai-assets-download-19]
    public void Publish_LocalMismatch_RefusesBeforeAnyGhCall()
    {
        var s = PublishSetup(draft: true, corrupt: true);
        var (code, output) = Publish(s);
        Assert.NotEqual(0, code);
        Assert.Contains("differs from the pin", output);
        Assert.Equal("", File.ReadAllText(s.Log).Trim());
    }

    [Fact] // [AC-ai-assets-download-4] [EDGE-ai-assets-download-19]
    public void Publish_RefusesPublishedRelease()
    {
        var s = PublishSetup(draft: false);
        var (code, output) = Publish(s);
        Assert.NotEqual(0, code);
        Assert.Contains("already published", output);
        Assert.DoesNotContain("release upload", File.ReadAllText(s.Log));
        Assert.DoesNotContain("release edit", File.ReadAllText(s.Log));
    }

    [Fact] // [AC-ai-assets-download-4]
    public void Publish_UploadsWithoutClobberThenPublishesNotLatest()
    {
        var s = PublishSetup(draft: true);
        var (code, output) = Publish(s);
        Assert.True(code == 0, output);
        var log = File.ReadAllLines(s.Log);
        Assert.Equal(2, log.Count(l => l.StartsWith("release upload aiscan-v1")));
        Assert.DoesNotContain(log, l => l.Contains("--clobber"));
        Assert.Contains(log, l => l.StartsWith("release edit aiscan-v1") && l.Contains("--draft=false") && l.Contains("--latest=false"));
        Assert.True(Array.FindLastIndex(log, l => l.StartsWith("release upload")) < Array.FindIndex(log, l => l.StartsWith("release edit")));
    }

    [Theory] // [AC-ai-assets-download-4] [EDGE-ai-assets-download-19]
    [InlineData("noop")]
    [InlineData("badsize")]
    public void Publish_DoesNotPublishWhenAssetMissingOrWrongSize(string mode)
    {
        var s = PublishSetup(draft: true);
        s.Env["FAKE_GH_UPLOAD"] = mode;
        var (code, _) = Publish(s);
        Assert.NotEqual(0, code);
        Assert.DoesNotContain("release edit", File.ReadAllText(s.Log));
    }

    [Fact] // [EDGE-ai-assets-download-19]
    public void Publish_RerunSkipsMatchingExistingAsset()
    {
        var s = PublishSetup(draft: true, preUploadOnnx: true);
        var (code, output) = Publish(s);
        Assert.True(code == 0, output);
        var log = File.ReadAllLines(s.Log);
        Assert.DoesNotContain(log, l => l.StartsWith("release upload") && l.EndsWith("m.onnx"));
        Assert.Single(log, l => l.StartsWith("release upload"));
    }

    [Fact] // [AC-ai-assets-download-4]
    public void ReleaseNotes_CreditFfmpegGplAndSonics()
    {
        string notes = File.ReadAllText(Script("release-notes.md"));
        foreach (var s in new[]
        {
            "GPL", "github.com/FFmpeg/FFmpeg", "licenses/FFmpeg-GPL-3.0.txt", "9.0.2-full_build-www.gyan.dev", "commit/946fcce07b",
            "SONICS", "awsaf49", "cc49422a98b5b92700876c608d0f9cdf27214a3e",
            "MIT", "Copyright (c)", "Permission is hereby granted, free of charge",
            "The above copyright notice and this permission notice shall be included", "THE SOFTWARE IS PROVIDED \"AS IS\"", "CC BY-NC",
        }) Assert.Contains(s, notes);
    }
}
