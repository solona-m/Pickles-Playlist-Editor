using NAudio.Wave;
using System;
using System.Threading;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>The sound device behind <see cref="PlaybackEngine"/>, swappable for a fake in tests.</summary>
    internal interface IAudioOutput : IDisposable
    {
        void Init(ISampleProvider source);
        void Play();
        void Pause();
        void Stop();
        /// <summary>Raised once per playback: the source drained, Stop was called, or the device failed (non-null).</summary>
        event Action<Exception?> Stopped;
    }

    internal sealed class WaveOutAudioOutput : IAudioOutput
    {
        private readonly WaveOut _out; // event-callback device (NAudio 2's WaveOutEvent)
        public event Action<Exception?>? Stopped;

        public WaveOutAudioOutput()
        {
            // WaveOut posts PlaybackStopped to the context it was built on; with none it raises
            // it on its own playback thread, which is what PlaybackEngine's locking expects.
            var saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            // 2 x 100 ms = the 200 ms DesiredLatency NAudio 2 used.
            try { _out = new WaveOut { NumberOfBuffers = 2, BufferMilliseconds = 100 }; }
            finally { SynchronizationContext.SetSynchronizationContext(saved); }
            _out.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
        }

        public void Init(ISampleProvider source) => _out.Init(source.ToWaveProvider16());
        public void Play() => _out.Play();
        public void Pause() => _out.Pause();
        public void Stop() => _out.Stop();
        public void Dispose() => _out.Dispose();
    }
}
