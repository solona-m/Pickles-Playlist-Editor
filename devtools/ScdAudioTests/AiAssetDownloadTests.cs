using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Pickles_Playlist_Editor.Tools;
using Xunit;

namespace ScdAudioTests;

public sealed class AiAssetDownloadTests : IDisposable
{
    private const string Tag = "aiscan-v1";
    private const string Mirror = "https://mirror.test/pickles/";
    private const string GitHub = "https://github.test/releases/download/";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "scd-audio-tests", "aiscan-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _bodies = new();
    private readonly List<AiAsset> _pins = new();
    private readonly ConcurrentQueue<string> _requests = new();

    public AiAssetDownloadTests()
    {
        foreach (var (name, seed) in new[] { ("fake-a.exe", 1), ("fake-b.onnx", 2), ("fake-b.onnx.data", 3) })
        {
            var body = new byte[3 * 1024 * 1024 + seed]; // over the 1 MB buffer
            new Random(seed).NextBytes(body);
            _bodies[name] = body;
            _pins.Add(new AiAsset(name, Tag, body.Length, Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static string[] Bases(string tag) => [Mirror + tag + "/", GitHub + tag + "/"];

    private string Dest(int i) => Path.Combine(_root, Tag, _pins[i].Name);

    private AiAssetDownloadService Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        List<TimeSpan>? waits = null, TimeSpan? readTimeout = null, string? root = null) =>
        new(root ?? _root, _pins, Bases, new FakeHandler((req, ct) => { _requests.Enqueue(req.RequestUri!.ToString()); return respond(req, ct); }),
            (attempt, retryAfter) => { waits?.Add(retryAfter ?? TimeSpan.Zero); return TimeSpan.Zero; }, readTimeout);

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    private static HttpResponseMessage Chunked(byte[] body) => new(HttpStatusCode.OK) { Content = new StreamContent(new CountingStream(body, _ => { })) };
    private static HttpResponseMessage Status(int code) => new((HttpStatusCode)code) { Content = new ByteArrayContent([]) };
    private string NameOf(HttpRequestMessage req) => req.RequestUri!.Segments[^1];
    private bool IsMirror(HttpRequestMessage req) => req.RequestUri!.ToString().StartsWith(Mirror, StringComparison.Ordinal);

    private Task<HttpResponseMessage> Serve(HttpRequestMessage req) => Task.FromResult(Ok(_bodies[NameOf(req)]));

    private void AssertAllPresent()
    {
        for (int i = 0; i < _pins.Count; i++) Assert.Equal(_bodies[_pins[i].Name], File.ReadAllBytes(Dest(i)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, Tag), "*.part"));
    }

    [Fact] // [AC-ai-assets-download-5]
    public async Task Ensure_DownloadsEveryFileFromMirror_VerifiedAtFinalPath()
    {
        using var svc = Service((req, _) => Serve(req));
        var r = await svc.EnsureAsync(null, CancellationToken.None);
        Assert.True(r.Ok, r.Detail);
        AssertAllPresent();
        Assert.Equal(_pins.Select(p => Mirror + Tag + "/" + p.Name), _requests);
        Assert.Equal(Dest(0), svc.PathFor(_pins[0]));
    }

    [Fact] // [AC-ai-assets-download-5] [EDGE-ai-assets-download-1]
    public async Task MirrorNotDeployed404_FallsBackToGitHubOnce()
    {
        using var svc = Service((req, _) => IsMirror(req) ? Task.FromResult(Status(404)) : Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        AssertAllPresent();
        foreach (var p in _pins) Assert.Single(_requests, u => u == Mirror + Tag + "/" + p.Name);
    }

    [Fact] // [EDGE-ai-assets-download-2]
    public async Task Mirror429_HonoursCappedRetryAfter_ThenGitHub()
    {
        var waits = new List<TimeSpan>();
        using var svc = Service((req, _) =>
        {
            if (!IsMirror(req)) return Serve(req);
            var r = Status(429);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(r);
        }, waits);
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        Assert.Equal(3, _requests.Count(u => u == Mirror + Tag + "/" + _pins[0].Name));
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromSeconds(7), w));
        Assert.Equal(2 * _pins.Count, waits.Count); // a wait between attempts, none after the last
    }

    [Fact] // [EDGE-ai-assets-download-2] the default backoff caps a long Retry-After at 30 s
    public void DefaultBackoff_CapsRetryAfter()
    {
        var m = typeof(PinnedFileDownloader).GetMethod("DefaultBackoff", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal(TimeSpan.FromSeconds(30), (TimeSpan)m.Invoke(null, [1, (TimeSpan?)TimeSpan.FromMinutes(5)])!);
        Assert.Equal(TimeSpan.FromSeconds(4), (TimeSpan)m.Invoke(null, [1, (TimeSpan?)TimeSpan.FromSeconds(4)])!);
    }

    [Fact] // [EDGE-ai-assets-download-3]
    public async Task MirrorTransportError_FallsBackToGitHub()
    {
        using var svc = Service((req, _) => IsMirror(req) ? throw new HttpRequestException("no such host") : Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        AssertAllPresent();
    }

    [Fact] // [EDGE-ai-assets-download-4]
    public async Task TruncatedBody200_NeverPromoted_RetriedThenFallback()
    {
        int mirrorHits = 0;
        using var svc = Service((req, _) =>
        {
            Assert.False(File.Exists(Path.Combine(_root, Tag, NameOf(req)))); // nothing unverified at the final path
            if (!IsMirror(req)) return Serve(req);
            Interlocked.Increment(ref mirrorHits);
            var b = _bodies[NameOf(req)];
            return Task.FromResult(Chunked(b[..(b.Length / 2)])); // no Content-Length: the transfer just stops early
        });
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        Assert.Equal(3 * _pins.Count, mirrorHits);
        AssertAllPresent();
    }

    [Fact] // [EDGE-ai-assets-download-5]
    public async Task RightSizeWrongBytes_OneMirrorAttemptThenGitHub()
    {
        using var svc = Service((req, _) => IsMirror(req) ? Task.FromResult(Ok(new byte[_bodies[NameOf(req)].Length])) : Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        foreach (var p in _pins) Assert.Single(_requests, u => u == Mirror + Tag + "/" + p.Name);
        AssertAllPresent();
    }

    [Fact] // [AC-ai-assets-download-7] [EDGE-ai-assets-download-6]
    public async Task BothSourcesFail_NamesFileAndReason_KeepsEarlierFiles()
    {
        using (var svc = Service((req, _) => NameOf(req) == _pins[1].Name ? Task.FromResult(Status(404)) : Serve(req)))
        {
            var r = await svc.EnsureAsync(null, CancellationToken.None);
            Assert.False(r.Ok);
            // Every source answered 404, so the failure is NotFound, not Http.
            Assert.Equal(AiAssetFailure.NotFound, r.Failure);
            Assert.Equal(_pins[1].Name, r.FileName);
            Assert.Contains("404", r.Detail);
            Assert.True(File.Exists(Dest(0)));
            Assert.False(File.Exists(Dest(1)));
        }
        while (_requests.TryDequeue(out _)) { }
        using (var svc = Service((req, _) => Serve(req)))
        {
            Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
            Assert.DoesNotContain(_requests, u => u.EndsWith("/" + _pins[0].Name));
        }
    }

    [Fact] // [AC-ai-score-3] [EDGE-ai-score-36]
    public async Task Every404_NotFound()
    {
        using var svc = Service((_, _) => Task.FromResult(Status(404)));
        var r = await svc.EnsureAsync(null, CancellationToken.None);
        Assert.Equal(AiAssetFailure.NotFound, r.Failure);
    }

    [Fact] // [AC-ai-score-3] [EDGE-ai-score-36]
    public async Task Mirror410GitHub404_NotFound()
    {
        using var svc = Service((req, _) => Task.FromResult(Status(IsMirror(req) ? 410 : 404)));
        Assert.Equal(AiAssetFailure.NotFound, (await svc.EnsureAsync(null, CancellationToken.None)).Failure);
    }

    [Fact] // [EDGE-ai-score-37] a real server fault somewhere is not "not published yet"
    public async Task Mirror404GitHub500_Http()
    {
        using var svc = Service((req, _) => Task.FromResult(Status(IsMirror(req) ? 404 : 500)));
        Assert.Equal(AiAssetFailure.Http, (await svc.EnsureAsync(null, CancellationToken.None)).Failure);
    }

    [Fact] // [AC-ai-assets-download-6] [EDGE-ai-assets-download-7]
    public async Task PresentIntactFiles_MakeNoRequests()
    {
        using (var svc = Service((req, _) => Serve(req))) Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        while (_requests.TryDequeue(out _)) { }
        using (var svc = Service((req, _) => Serve(req)))
        {
            Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
            Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
            Assert.Empty(_requests);
            Assert.Equal(0, svc.HashCount); // remembered from the download, nothing re-hashed
        }
    }

    [Fact] // [AC-ai-assets-download-6] [EDGE-ai-assets-download-8]
    public async Task CorruptOrTruncatedPresentFile_IsRefetched()
    {
        Directory.CreateDirectory(Path.Combine(_root, Tag));
        File.WriteAllBytes(Dest(0), _bodies[_pins[0].Name][..1000]);
        File.WriteAllBytes(Dest(1), new byte[_bodies[_pins[1].Name].Length]);
        using var svc = Service((req, _) => Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        AssertAllPresent();
    }

    [Fact] // [AC-ai-assets-download-6] [EDGE-ai-assets-download-29]
    public async Task SameSession_TruncateDeleteOrRewrite_Refetches()
    {
        using var svc = Service((req, _) => Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        while (_requests.TryDequeue(out _)) { }

        using (var fs = new FileStream(Dest(0), FileMode.Open)) fs.SetLength(10);
        File.Delete(Dest(1));
        File.WriteAllBytes(Dest(2), new byte[_bodies[_pins[2].Name].Length]);
        File.SetLastWriteTimeUtc(Dest(2), DateTime.UtcNow.AddMinutes(1));

        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        Assert.Equal(_pins.Select(p => Mirror + Tag + "/" + p.Name), _requests);
        AssertAllPresent();
    }

    [Fact] // [EDGE-ai-assets-download-29]
    public async Task SameSession_TouchOnly_RehashesWithoutRequest()
    {
        using var svc = Service((req, _) => Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        while (_requests.TryDequeue(out _)) { }
        File.SetLastWriteTimeUtc(Dest(0), DateTime.UtcNow.AddMinutes(1));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        Assert.Empty(_requests);
        Assert.Equal(1, svc.HashCount);
    }

    [Fact] // [EDGE-ai-assets-download-9]
    public async Task StalePartFile_IgnoredAndCleaned()
    {
        string dir = Path.Combine(_root, Tag);
        Directory.CreateDirectory(dir);
        string stale = Path.Combine(dir, _pins[0].Name + ".0123.part");
        File.WriteAllBytes(stale, _bodies[_pins[0].Name]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
        using var svc = Service((req, _) => Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        Assert.False(File.Exists(stale));
        Assert.Contains(_requests, u => u.EndsWith("/" + _pins[0].Name)); // the .part was not taken as the asset
        AssertAllPresent();
    }

    [Fact] // [EDGE-ai-assets-download-9]
    public async Task LockedStalePart_SkippedWithoutError()
    {
        string dir = Path.Combine(_root, Tag);
        Directory.CreateDirectory(dir);
        string stale = Path.Combine(dir, "other.4567.part");
        File.WriteAllBytes(stale, [1, 2, 3]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
        using var held = new FileStream(stale, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var svc = Service((req, _) => Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        Assert.True(File.Exists(stale));
    }

    [Fact] // [AC-ai-assets-download-7] [EDGE-ai-assets-download-10]
    public async Task CancelMidBody_ThrowsAndLeavesNothing()
    {
        using var cts = new CancellationTokenSource();
        using var svc = Service((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CallbackStream(_bodies[NameOf(req)], 1024 * 1024, () => cts.Cancel())),
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.EnsureAsync(null, cts.Token));
        Assert.False(File.Exists(Dest(0)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, Tag), "*.part"));
    }

    [Fact] // [EDGE-ai-assets-download-11]
    public async Task StalledBody_TimesOut_ThenFallsBack()
    {
        using var svc = Service((req, _) => IsMirror(req)
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallStream()) })
            : Serve(req), readTimeout: TimeSpan.FromMilliseconds(200));
        var run = svc.EnsureAsync(null, CancellationToken.None);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.True((await run).Ok);
        AssertAllPresent();
    }

    [Fact] // [EDGE-ai-assets-download-12]
    public async Task ConcurrentEnsure_DownloadsOnce()
    {
        using var svc = Service((req, _) => Serve(req));
        var a = svc.EnsureAsync(null, CancellationToken.None);
        var b = svc.EnsureAsync(null, CancellationToken.None);
        Assert.True((await a).Ok);
        Assert.True((await b).Ok);
        Assert.Equal(_pins.Count, _requests.Count);
    }

    [Fact] // [EDGE-ai-assets-download-13]
    public async Task LockedCorruptDestination_FailsDiskAndCleansUp()
    {
        Directory.CreateDirectory(Path.Combine(_root, Tag));
        var junk = new byte[_bodies[_pins[0].Name].Length];
        File.WriteAllBytes(Dest(0), junk);
        AiAssetResult r;
        using (var held = new FileStream(Dest(0), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var svc = Service((req, _) => Serve(req));
            r = await svc.EnsureAsync(null, CancellationToken.None);
        }
        Assert.False(r.Ok);
        Assert.Equal(AiAssetFailure.Disk, r.Failure);
        Assert.Equal(junk, File.ReadAllBytes(Dest(0)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, Tag), "*.part"));
    }

    [Fact] // [EDGE-ai-assets-download-13]
    public async Task LockedValidDestination_Accepted()
    {
        Directory.CreateDirectory(Path.Combine(_root, Tag));
        for (int i = 0; i < _pins.Count; i++) File.WriteAllBytes(Dest(i), _bodies[_pins[i].Name]);
        using var held = new FileStream(Dest(0), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var svc = Service((req, _) => Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        Assert.Empty(_requests);
    }

    [Fact] // [EDGE-ai-assets-download-13]
    public async Task TransientDestinationLock_RetriedMoveSucceeds()
    {
        Directory.CreateDirectory(Path.Combine(_root, Tag));
        File.WriteAllBytes(Dest(0), new byte[10]);
        var held = new FileStream(Dest(0), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var svc = Service((req, _) => Serve(req));
        var release = Task.Delay(300).ContinueWith(_ => held.Dispose());
        var r = await svc.EnsureAsync(null, CancellationToken.None);
        await release;
        Assert.True(r.Ok, r.Detail);
        AssertAllPresent();
    }

    [Fact] // [EDGE-ai-assets-download-14]
    public async Task UnwritableRoot_ReturnsDiskFailure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        string fileRoot = _root + ".file";
        File.WriteAllText(fileRoot, "not a folder");
        try
        {
            using var svc = Service((req, _) => Serve(req), root: fileRoot);
            var r = await svc.EnsureAsync(null, CancellationToken.None);
            Assert.False(r.Ok);
            Assert.Equal(AiAssetFailure.Disk, r.Failure);
        }
        finally { File.Delete(fileRoot); }
    }

    [Fact] // [AC-ai-assets-download-7] [EDGE-ai-assets-download-16]
    public async Task Progress_IsAbsoluteAndReachesTotal()
    {
        Directory.CreateDirectory(Path.Combine(_root, Tag));
        File.WriteAllBytes(Dest(0), _bodies[_pins[0].Name]); // already present: done at once
        var reports = new ConcurrentQueue<AiAssetProgress>();
        using var svc = Service((req, _) => Serve(req));
        Assert.True((await svc.EnsureAsync(new SyncProgress(p => reports.Enqueue(p)), CancellationToken.None)).Ok);
        long total = _pins.Sum(p => p.Bytes);
        var all = reports.ToList();
        Assert.All(all, p => Assert.Equal(total, p.BytesTotal));
        Assert.Equal(total, all[^1].BytesDone);
        Assert.Equal(_pins[0].Bytes, all[0].BytesDone);
        for (int i = 1; i < all.Count; i++) Assert.True(all[i].BytesDone >= all[i - 1].BytesDone);
        Assert.True(all.Count(p => p.FileName == _pins[1].Name) > 2);
        Assert.DoesNotContain(_requests, u => u.EndsWith("/" + _pins[0].Name));
    }

    [Fact] // [AC-ai-assets-download-7]
    public async Task Ensure_RunsOffTheCallingThread_AndReturnsBeforeTheWork()
    {
        var gate = new TaskCompletionSource();
        using var entered = new ManualResetEventSlim();
        int workThread = 0;
        using var svc = Service(async (req, _) =>
        {
            if (Interlocked.CompareExchange(ref workThread, Environment.CurrentManagedThreadId, 0) == 0) entered.Set();
            await gate.Task;
            return Ok(_bodies[NameOf(req)]);
        });
        int caller = Environment.CurrentManagedThreadId;
        var run = svc.EnsureAsync(null, CancellationToken.None);
        Assert.False(run.IsCompleted);
        Assert.True(entered.Wait(10000)); // this thread stays busy, so the work must be on another one
        Assert.NotEqual(caller, workThread);
        gate.SetResult();
        Assert.True((await run).Ok);
    }

    [Fact] // [EDGE-ai-assets-download-11] a server that never sends headers
    public async Task HeaderStall_TimesOut_ThenFallsBack()
    {
        using var svc = Service(async (req, ct) =>
        {
            if (IsMirror(req)) await Task.Delay(Timeout.Infinite, ct);
            return Ok(_bodies[NameOf(req)]);
        }, readTimeout: TimeSpan.FromMilliseconds(200));
        var run = svc.EnsureAsync(null, CancellationToken.None);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.True((await run).Ok);
        AssertAllPresent();
    }

    [Fact] // [EDGE-ai-assets-download-5] a source serving a bigger file is a wrong source, not a slow one
    public async Task OversizeBody_StopsEarly_ThenGitHub()
    {
        int maxRead = 0;
        using var svc = Service((req, _) =>
        {
            if (!IsMirror(req)) return Serve(req);
            var big = new byte[_bodies[NameOf(req)].Length * 4];
            var stream = new CountingStream(big, n => maxRead = Math.Max(maxRead, n));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        });
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        foreach (var p in _pins) Assert.Single(_requests, u => u == Mirror + Tag + "/" + p.Name);
        Assert.True(maxRead < _bodies[_pins[0].Name].Length * 2, $"read {maxRead}");
        AssertAllPresent();
    }

    [Fact] // [EDGE-ai-assets-download-5]
    public async Task ContentLengthMismatch_SkipsSourceWithoutDownloading()
    {
        using var svc = Service((req, _) => IsMirror(req) ? Task.FromResult(Ok(new byte[100])) : Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        foreach (var p in _pins) Assert.Single(_requests, u => u == Mirror + Tag + "/" + p.Name);
        AssertAllPresent();
    }

    [Fact] // [AC-ai-assets-download-7] [EDGE-ai-assets-download-14] an existing folder we may not write into
    public async Task UnwritableTagFolder_ReturnsDiskFailure_WithoutRetrying()
    {
        string dir = Path.Combine(_root, Tag);
        Directory.CreateDirectory(dir);
        var info = new DirectoryInfo(dir);
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
            System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData,
            System.Security.AccessControl.AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            using var svc = Service((req, _) => Serve(req));
            var r = await svc.EnsureAsync(null, CancellationToken.None);
            Assert.False(r.Ok);
            Assert.Equal(AiAssetFailure.Disk, r.Failure);
            Assert.Equal(_pins[0].Name, r.FileName);
            Assert.Single(_requests); // no retry, no second source
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
    }

    [Fact] // [EDGE-ai-assets-download-27]
    public async Task Http403_IsRetried()
    {
        var seen = new ConcurrentDictionary<string, int>();
        using var svc = Service((req, _) =>
            IsMirror(req) && seen.AddOrUpdate(NameOf(req), 1, (_, n) => n + 1) == 1 ? Task.FromResult(Status(403)) : Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        Assert.DoesNotContain(_requests, u => u.StartsWith(GitHub, StringComparison.Ordinal));
    }

    [Fact] // [EDGE-ai-assets-download-17]
    public async Task LoopbackServer_FollowsRedirectOverRealSockets()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => ServeLoopback(listener, stop.Token));
        try
        {
            using var svc = new AiAssetDownloadService(_root, _pins, tag => [$"http://127.0.0.1:{port}/gh/{tag}/"], null, (_, _) => TimeSpan.Zero);
            var r = await svc.EnsureAsync(null, CancellationToken.None);
            Assert.True(r.Ok, r.Detail);
            AssertAllPresent();
        }
        finally { stop.Cancel(); listener.Stop(); }
    }

    private async Task ServeLoopback(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); } catch { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    while (!ct.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (string.IsNullOrEmpty(line)) return;
                        string path = line.Split(' ')[1];
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                        string name = path[(path.LastIndexOf('/') + 1)..];
                        if (path.StartsWith("/gh/"))
                        {
                            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: /blob/{name}\r\nContent-Length: 0\r\n\r\n");
                            await stream.WriteAsync(head, ct);
                        }
                        else
                        {
                            var body = _bodies[name];
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n"), ct);
                            await stream.WriteAsync(body, ct);
                        }
                    }
                }
            }, ct);
        }
    }

    [Fact] // [AC-ai-assets-download-8] [EDGE-ai-assets-download-23]
    public async Task FullRun_NeverTouchesAppFolder()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ffmpeg)));
        using var svc = Service((req, _) => Serve(req));
        Assert.True((await svc.EnsureAsync(null, CancellationToken.None)).Ok);
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ffmpeg))));
        foreach (var p in _pins)
            Assert.Empty(Directory.GetFiles(AppContext.BaseDirectory, p.Name, SearchOption.AllDirectories));
        Assert.False(Path.GetFullPath(AiAssets.DefaultRoot).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    private sealed class SyncProgress(Action<AiAssetProgress> onReport) : IProgress<AiAssetProgress>
    {
        public void Report(AiAssetProgress value) => onReport(value);
    }

    /// <summary>Serves a body and calls back once a byte count has gone out.</summary>
    private sealed class CallbackStream(byte[] body, int after, Action callback) : MemoryStream(body)
    {
        private bool _fired;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_fired && Position >= after) { _fired = true; callback(); }
            ct.ThrowIfCancellationRequested();
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 256 * 1024)], ct);
        }
    }

    /// <summary>A non-seekable body (so no Content-Length, like a chunked transfer) that reports how far the reader got.</summary>
    private sealed class CountingStream(byte[] body, Action<int> onPosition) : Stream
    {
        private readonly MemoryStream _inner = new(body);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { int n = _inner.Read(buffer, offset, count); onPosition((int)_inner.Position); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await _inner.ReadAsync(buffer, ct);
            onPosition((int)_inner.Position);
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Sends nothing until cancelled.</summary>
    private sealed class StallStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }
}
