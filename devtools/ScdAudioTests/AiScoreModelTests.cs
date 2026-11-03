using System.Diagnostics;
using System.Text.Json;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using Xunit;
using Xunit.Abstractions;

namespace ScdAudioTests;

/// <summary>
/// The real SONICS model on the Windows ML ONNX Runtime. Skipped unless PICKLES_AISCAN_DIR points at a folder holding
/// the pinned aiscan files (e.g. %LOCALAPPDATA%\PicklesPlaylistEditor\aiscan\aiscan-v1).
/// </summary>
[Collection("ffmpeg")]
public sealed class AiScoreModelTests(ITestOutputHelper log)
{
    private static string? Dir()
    {
        string? d = Environment.GetEnvironmentVariable("PICKLES_AISCAN_DIR");
        return d != null && File.Exists(Path.Combine(d, AiAssets.Model.Name)) ? d : null;
    }

    private static string RequireDir()
    {
        string? d = Dir();
        Skip.If(d == null, "PICKLES_AISCAN_DIR does not hold the AI model");
        return d!;
    }

    /// <summary>The same signals as devtools/aiscan/make_reference.py: computed in double, stored as float32.</summary>
    internal static float[] Synthetic(string variant)
    {
        var x = new float[AiScoreMath.WindowSamples];
        for (int i = 0; i < x.Length; i++)
        {
            double v;
            if (variant == "tones")
                v = 0.3 * Math.Sin(2 * Math.PI * 440.0 * i / 16000) + 0.1 * Math.Sin(2 * Math.PI * 1234.5 * i / 16000);
            else
            {
                double t = i / 16000.0;
                v = 0.25 * Math.Sin(2 * Math.PI * (100.0 * t + 15.0 * t * t)) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 0.5 * t));
            }
            x[i] = (float)v;
        }
        return x;
    }

    [SkippableFact] // [AC-ai-score-9]
    public void RealModel_SyntheticSignal_MatchesPyTorchLogit()
    {
        string dir = RequireDir();
        var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "aiscan", "reference.json")))
            .RootElement.GetProperty("logits");
        using var model = SonicsModel.Load(Path.Combine(dir, AiAssets.Model.Name));
        foreach (var variant in new[] { "tones", "chirp" })
        {
            double expected = reference.GetProperty(variant).GetDouble();
            double got = model.Logit(Synthetic(variant));
            log.WriteLine($"{variant}: ORT {got:F6}  PyTorch {expected:F6}  gap {Math.Abs(got - expected):E2}  p {AiScoreMath.Sigmoid(got):F4} vs {AiScoreMath.Sigmoid(expected):F4}");
            // ORT and PyTorch differ in float rounding (measured logit gaps 4e-4 and 3.5e-3); compared as probabilities.
            Assert.InRange(Math.Abs(AiScoreMath.Sigmoid(got) - AiScoreMath.Sigmoid(expected)), 0, 1e-3);
        }
    }

    // Probabilities from the prototype (PyTorch + librosa soxr_hq) for the reference tracks it was checked on.
    private static readonly (string File, double Expected)[] s_refTracks =
    [
        ("ogg/mkkuroi - i want that rush_1.ogg", 0.994),
        ("ogg/Kuroi - Holy Crit_1.ogg", 0.012),
        ("human/dua lipa - don't start now [jinxspr0 remix]_747787633 - jinxspr0 💎_1.ogg", 0.014),
        ("human/elton john, dua lipa - cold heart (balzanelli, vitale, michelle bootleg remix) cucky re - edit_1171614697 - housechart1_1.ogg", 0.036),
        ("ref/demo_fake_song.mp3", 0.997),
        ("ref/demo_real_song.mp3", 0.007),
    ];

    [SkippableFact] // [AC-ai-score-9] the whole pipeline (full ffmpeg soxr + model) on real songs; local files only
    public void RealModel_ReferenceTracks_MatchPrototypeScores()
    {
        string dir = RequireDir();
        string? refs = Environment.GetEnvironmentVariable("PICKLES_AISCAN_REFS");
        Skip.If(string.IsNullOrEmpty(refs) || !Directory.Exists(refs), "PICKLES_AISCAN_REFS not set");
        using var model = SonicsModel.Load(Path.Combine(dir, AiAssets.Model.Name));
        string ffmpeg = Path.Combine(dir, AiAssets.Ffmpeg.Name);
        foreach (var (file, expected) in s_refTracks)
        {
            string path = Path.Combine(refs!, file);
            Assert.True(File.Exists(path), "missing reference track " + file);
            var r = AiAudioDecoder.DecodeAndScore(ffmpeg, path, model.Logit, CancellationToken.None);
            log.WriteLine($"{file}: {r.Outcome} {r.Score:F4} (prototype {expected:F3}), {r.Windows} window(s)");
            Assert.Equal(AiScoreOutcome.Scored, r.Outcome);
            Assert.InRange(Math.Abs(r.Score!.Value - expected), 0, 0.005);
        }
    }

    [SkippableFact] // [EDGE-ai-score-27] one shared session; the last lease out unloads it
    public void Lease_SharedAndDisposedWithLast()
    {
        string dir = RequireDir();
        int creates = 0;
        SonicsModel Factory() { creates++; return SonicsModel.Load(Path.Combine(dir, AiAssets.Model.Name)); }
        var a = SonicsModel.Acquire(Factory);
        var b = SonicsModel.Acquire(Factory);
        Assert.Equal(1, creates);
        Assert.Same(a.Model, b.Model);
        a.Dispose();
        a.Dispose(); // a second dispose does nothing
        Assert.Equal(1, SonicsModel.ActiveLeases);
        b.Dispose();
        Assert.Equal(0, SonicsModel.ActiveLeases);
        using var c = SonicsModel.Acquire(Factory);
        Assert.Equal(2, creates);
    }

    [SkippableFact] // [EDGE-ai-score-44]
    public void RealModel_PeakWorkingSetIncrease_Under600MB()
    {
        string dir = RequireDir();
        using var proc = Process.GetCurrentProcess();
        GC.Collect();
        proc.Refresh();
        long baseline = proc.WorkingSet64, peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                proc.Refresh();
                peak = Math.Max(peak, proc.WorkingSet64);
                await Task.Delay(50);
            }
        });
        using (var model = SonicsModel.Load(Path.Combine(dir, AiAssets.Model.Name)))
        {
            var w = Synthetic("chirp");
            for (int i = 0; i < 3; i++) model.Logit(w);
        }
        stop.Cancel();
        sampler.Wait();
        long mb = (peak - baseline) / (1024 * 1024);
        log.WriteLine($"peak working-set increase {mb} MB (baseline {baseline / (1024 * 1024)} MB)");
        Assert.InRange(mb, 0, 599);
    }
}
