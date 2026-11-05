using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>
    /// The SONICS classifier on ONNX Runtime (the Windows ML build the Windows App SDK ships): one 120 s window of
    /// 16 kHz mono in, one logit out. The only file that touches ONNX Runtime, so telemetry is off before any use.
    /// </summary>
    internal sealed class SonicsModel : IDisposable
    {
        internal const string InputName = "audio";
        internal const string OutputName = "logit";
        internal const int WindowSamples = 1_920_000;

        private static readonly object s_envLock = new();
        private static bool s_telemetryOff;

        private readonly InferenceSession _session;
        private readonly RunOptions _runOptions = new();
        private readonly object _runLock = new();

        private SonicsModel(InferenceSession session) => _session = session;

        /// <summary>Loads the model; throws if ONNX Runtime can't load it or its signature isn't the expected one.</summary>
        public static SonicsModel Load(string onnxPath, bool cpuMemArena = true)
        {
            lock (s_envLock)
            {
                if (!s_telemetryOff)
                {
                    OrtEnv.Instance().DisableTelemetryEvents(); // before any session exists
                    s_telemetryOff = true;
                }
            }
            using var options = new SessionOptions
            {
                IntraOpNumThreads = 1, // more would start normal-priority ORT worker threads
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableCpuMemArena = cpuMemArena,
            };
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            var session = new InferenceSession(onnxPath, options);
            try
            {
                string? error = ValidateSignature(
                    session.InputMetadata.ToDictionary(kv => kv.Key, kv => (kv.Value.ElementDataType, kv.Value.Dimensions)),
                    session.OutputMetadata.ToDictionary(kv => kv.Key, kv => (kv.Value.ElementDataType, kv.Value.Dimensions)));
                if (error != null) throw new InvalidOperationException("Unexpected AI model: " + error);
                return new SonicsModel(session);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        /// <summary>Null when the model takes audio float32 [1, 1920000] and returns a one-element logit.</summary>
        internal static string? ValidateSignature(IReadOnlyDictionary<string, (TensorElementType Type, int[] Dims)> inputs,
            IReadOnlyDictionary<string, (TensorElementType Type, int[] Dims)> outputs)
        {
            if (!inputs.TryGetValue(InputName, out var input)) return $"no '{InputName}' input";
            if (input.Type != TensorElementType.Float) return $"'{InputName}' is {input.Type}, not float";
            if (input.Dims.Length != 2 || input.Dims[0] is not (1 or -1) || input.Dims[1] != WindowSamples)
                return $"'{InputName}' is [{string.Join(", ", input.Dims)}], not [1, {WindowSamples}]";
            if (!outputs.TryGetValue(OutputName, out var output)) return $"no '{OutputName}' output";
            if (output.Type != TensorElementType.Float) return $"'{OutputName}' is {output.Type}, not float";
            if (output.Dims.Any(d => d > 1)) return $"'{OutputName}' is [{string.Join(", ", output.Dims)}], not one value";
            return null;
        }

        /// <summary>The raw logit for one window of exactly <see cref="WindowSamples"/> samples.</summary>
        public float Logit(float[] window)
        {
            if (window.Length != WindowSamples) throw new ArgumentException($"window must hold {WindowSamples} samples", nameof(window));
            lock (_runLock)
            {
                using var input = OrtValue.CreateTensorValueFromMemory(window, new long[] { 1, WindowSamples });
                using var results = _session.Run(_runOptions, new[] { InputName }, new[] { input }, new[] { OutputName });
                return results[0].GetTensorDataAsSpan<float>()[0];
            }
        }

        public void Dispose()
        {
            _session.Dispose();
            _runOptions.Dispose();
        }

        // One session shared by every run that is in progress; the last one out unloads ~87 MB of weights.
        private static readonly object s_leaseLock = new();
        private static SonicsModel? s_shared;
        private static int s_leases;

        internal sealed class Lease : IDisposable
        {
            private int _disposed;
            public SonicsModel Model { get; }
            internal Lease(SonicsModel model) => Model = model;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                lock (s_leaseLock)
                {
                    if (--s_leases == 0)
                    {
                        s_shared?.Dispose();
                        s_shared = null;
                    }
                }
            }
        }

        public static Lease Acquire(Func<SonicsModel> factory)
        {
            lock (s_leaseLock)
            {
                s_shared ??= factory();
                s_leases++;
                return new Lease(s_shared);
            }
        }

        internal static int ActiveLeases { get { lock (s_leaseLock) return s_leases; } }
    }
}
