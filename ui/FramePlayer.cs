using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;

namespace Pickles_Playlist_Editor
{
    /// <summary>Plays animation frames on a card's preview at the timing the game will use.</summary>
    internal sealed class FramePlayer
    {
        private readonly Image _target;
        private readonly DispatcherTimer _timer = new();
        private IReadOnlyList<WriteableBitmap> _frames = Array.Empty<WriteableBitmap>();
        private FrameTimeline? _timeline;
        private int _index;

        public FramePlayer(Image target)
        {
            _target = target;
            _timer.Tick += (_, _) => Next();
        }

        public bool IsPlaying => _timer.IsEnabled;

        public void Play(IReadOnlyList<WriteableBitmap> frames, FrameTimeline timeline)
        {
            Stop();
            if (frames.Count == 0) return;
            _frames = frames;
            _timeline = timeline;
            _index = -1;
            Next();
            if (frames.Count > 1) _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _frames = Array.Empty<WriteableBitmap>();
            _timeline = null;
        }

        private void Next()
        {
            if (_timeline == null || _frames.Count == 0) return;
            _index = (_index + 1) % _frames.Count;
            _target.Source = _frames[_index];

            var starts = _timeline.StartTicks;
            int end = _index + 1 < starts.Count ? starts[_index + 1] : _timeline.LoopTicks;
            int ticks = Math.Max(1, end - starts[Math.Min(_index, starts.Count - 1)]);
            _timer.Interval = TimeSpan.FromSeconds(ticks / (double)FramePlan.TicksPerSecond);
        }
    }
}
