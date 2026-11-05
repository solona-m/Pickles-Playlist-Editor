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
        private readonly WaveOutEvent _out;
        public event Action<Exception?>? Stopped;

        public WaveOutAudioOutput()
        {
            // WaveOutEvent posts PlaybackStopped to the context it was built on; with none it raises
            // it on its own playback thread, which is what PlaybackEngine's locking expects.
            var saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try { _out = new WaveOutEvent { DesiredLatency = 200 }; }
            finally { SynchronizationContext.SetSynchronizationContext(saved); }
            _out.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
        }

        public void Init(ISampleProvider source) => _out.Init(source, convertTo16Bit: true);
        public void Play() => _out.Play();
        public void Pause() => _out.Pause();
        public void Stop() => _out.Stop();
        public void Dispose() => _out.Dispose();
    }
}
