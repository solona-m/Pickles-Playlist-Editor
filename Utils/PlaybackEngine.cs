using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Plays one OGG at a time. Every change to the generation or state happens under one lock, and each
    /// output's Stopped handler carries the generation of the Open that created it, so a late event from
    /// an old song can never stop or skip the one playing now.
    /// </summary>
    internal sealed class PlaybackEngine
    {
        private enum State { Stopped, Playing, Paused }

        private readonly Func<IAudioOutput> _outputFactory;
        private readonly Func<string, WaveStream> _readerFactory;
        private readonly Action<string> _warn;
        private readonly object _stateLock = new();

        private long _generation;
        private State _state = State.Stopped;
        private IAudioOutput? _output;
        private Session? _session;
        private volatile int _volume = 100;

        /// <summary>Test-only interleaving points: "open-after-gen", "stopped-after-check".</summary>
        internal Action<string>? TestHook;

        public PlaybackEngine(Func<IAudioOutput> outputFactory, Action<string>? warn = null, Func<string, WaveStream>? readerFactory = null)
        {
            _outputFactory = outputFactory;
            _readerFactory = readerFactory ?? (path => new VorbisWaveReader(path));
            _warn = warn ?? (m => Logger.LogWarn("{Message}", m));
        }

        public bool IsPlaying { get { lock (_stateLock) return _state != State.Stopped; } }
        public bool IsPaused { get { lock (_stateLock) return _state == State.Paused; } }

        // Lock-free so a decoder stuck inside Read can't freeze the UI timer.
        public TimeSpan Position => _session?.Position ?? TimeSpan.Zero;
        public TimeSpan Duration => _session?.Duration ?? TimeSpan.Zero;

        public int Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 100);
                var s = _session;
                if (s != null) s.VolumeProvider.Volume = _volume / 100f;
            }
        }

        public bool IsCurrent(long generation) => Interlocked.Read(ref _generation) == generation;

        /// <summary>Starts <paramref name="oggPath"/> and returns its generation. Throws InvalidOperationException if it can't.</summary>
        public long Open(string oggPath, Action<long>? onEnded)
        {
            long myGen;
            lock (_stateLock)
            {
                TearDownLocked();
                myGen = Interlocked.Increment(ref _generation);
            }
            TestHook?.Invoke("open-after-gen");

            Session session;
            try { session = new Session(_readerFactory(oggPath), _warn, _volume); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"The audio decoder could not open this track: {ex.Message}", ex);
            }

            IAudioOutput? output = null;
            try
            {
                output = _outputFactory();
                output.Stopped += ex => OnOutputStopped(myGen, ex, onEnded);
                output.Init(session.VolumeProvider);
            }
            catch (Exception ex)
            {
                DisposeQuietly(output);
                session.Close();
                throw new InvalidOperationException($"Playback could not be started: {ex.Message}", ex);
            }

            lock (_stateLock)
            {
                // A Stop or another Open won while this one was opening files: it is no longer current.
                if (Interlocked.Read(ref _generation) != myGen)
                {
                    DisposeQuietly(output);
                    session.Close();
                    return myGen;
                }
                _session = session;
                _output = output;
                try { output.Play(); }
                catch (Exception ex)
                {
                    TearDownLocked();
                    throw new InvalidOperationException($"Playback could not be started: {ex.Message}", ex);
                }
                _state = State.Playing;
            }
            return myGen;
        }

        public void TogglePause()
        {
            lock (_stateLock)
            {
                if (_output == null) return;
                try
                {
                    if (_state == State.Playing) { _output.Pause(); _state = State.Paused; }
                    else if (_state == State.Paused) { _output.Play(); _state = State.Playing; }
                }
                catch (Exception ex)
                {
                    // The state flips only if the device honoured it.
                    _warn($"Pause/resume refused: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                Interlocked.Increment(ref _generation);
                TearDownLocked();
            }
        }

        public void Seek(TimeSpan position)
        {
            var s = _session;
            if (s == null) return;
            s.Seek(position);
        }

        private void OnOutputStopped(long capturedGen, Exception? ex, Action<long>? onEnded)
        {
            lock (_stateLock)
            {
                if (capturedGen != Interlocked.Read(ref _generation) || _state != State.Playing) return;
                TestHook?.Invoke("stopped-after-check");
                _state = State.Stopped;
            }
            if (ex != null)
            {
                _warn($"Audio output stopped with an error: {ex.Message}");
                return;
            }
            Task.Run(() =>
            {
                try { onEnded?.Invoke(capturedGen); }
                catch (Exception cbEx) { Logger.LogCrash("Playback onEnded callback", cbEx); }
            });
        }

        // Caller holds _stateLock.
        private void TearDownLocked()
        {
            _state = State.Stopped;
            var output = _output;
            var session = _session;
            _output = null;
            _session = null;
            // Reader first, so a playback thread still inside Read gets 0 and queues no more of the old song.
            session?.Close();
            if (output != null)
            {
                try { output.Stop(); } catch (Exception ex) { _warn($"Audio output stop failed: {ex.Message}"); }
                DisposeQuietly(output);
            }
        }

        private void DisposeQuietly(IAudioOutput? output)
        {
            if (output == null) return;
            try { output.Dispose(); } catch (Exception ex) { _warn($"Audio output dispose failed: {ex.Message}"); }
        }

        /// <summary>One opened file: the reader, the guarded sample source and the volume stage.</summary>
        private sealed class Session : ISampleProvider
        {
            private readonly WaveStream _reader;
            private readonly ISampleProvider _samples;
            private readonly object _readerLock = new();
            private readonly Action<string> _warn;
            private volatile bool _closed;
            private bool _warned, _disposed;
            private long _positionTicks;

            public TimeSpan Duration { get; }
            public VolumeSampleProvider VolumeProvider { get; }
            public WaveFormat WaveFormat => _samples.WaveFormat;
            public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));

            public Session(WaveStream reader, Action<string> warn, int volume)
            {
                _warn = warn;
                _reader = reader;
                _samples = reader as ISampleProvider ?? reader.ToSampleProvider();
                Duration = _reader.TotalTime;
                VolumeProvider = new VolumeSampleProvider(this) { Volume = volume / 100f };
            }

            public int Read(float[] buffer, int offset, int count)
            {
                lock (_readerLock)
                {
                    if (_closed) { DisposeReaderOnce(); return 0; }
                    try
                    {
                        int n = _samples.Read(buffer, offset, count);
                        if (_closed) { DisposeReaderOnce(); return 0; } // Close timed out while this Read ran
                        Interlocked.Exchange(ref _positionTicks, _reader.CurrentTime.Ticks);
                        return n;
                    }
                    catch (Exception ex)
                    {
                        // A broken stream ends the track, as libZPlay did; one warning per song.
                        if (!_warned) { _warned = true; _warn($"Playback decoder failure: {ex.Message}"); }
                        return 0;
                    }
                }
            }

            public void Seek(TimeSpan position)
            {
                // NVorbis can throw seeking into the final page, so stay half a second short of the end.
                double max = Duration.TotalSeconds < 1 ? 0 : Duration.TotalSeconds - 0.5;
                var target = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, max));
                if (!Monitor.TryEnter(_readerLock, 200))
                {
                    _warn("Seek dropped: the decoder is busy.");
                    return;
                }
                try
                {
                    if (_closed) return;
                    _reader.CurrentTime = target;
                    Interlocked.Exchange(ref _positionTicks, target.Ticks);
                }
                catch (Exception ex) { _warn($"Seek failed: {ex.Message}"); }
                finally { Monitor.Exit(_readerLock); }
            }

            public void Close()
            {
                _closed = true;
                if (!Monitor.TryEnter(_readerLock, 1000))
                {
                    // The busy Read disposes the reader when it returns.
                    _warn("Closing the decoder timed out; the file stays open until the decoder returns.");
                    return;
                }
                try { DisposeReaderOnce(); }
                finally { Monitor.Exit(_readerLock); }
            }

            // Caller holds _readerLock.
            private void DisposeReaderOnce()
            {
                if (_disposed) return;
                _disposed = true;
                try { _reader.Dispose(); }
                catch (Exception ex) { _warn($"Closing the decoder failed: {ex.Message}"); }
            }
        }
    }
}
