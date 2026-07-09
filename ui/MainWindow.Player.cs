using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace Pickles_Playlist_Editor
{
    public sealed partial class MainWindow
    {
        private const string PlayGlyph = "";
        private const string PauseGlyph = "";
        private const string VolumeGlyph = "";
        private const string MuteGlyph = "";

        private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private bool _isSeeking;
        private double _lastVolumeBeforeMute = 100;

        private void InitializePlaybackTimer()
        {
            _playbackTimer.Tick += (s, e) => UpdatePlaybackProgress();

            // The Slider's Thumb captures pointer input and marks it handled before it
            // bubbles up, so XAML-declared PointerPressed/PointerReleased on the Slider
            // itself never fire during a drag. handledEventsToo lets us see them anyway.
            SeekSlider.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SeekSlider_PointerPressed), true);
            SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SeekSlider_PointerReleased), true);
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedNode == null || _selectedNode.Level != 2) return;
            string songName = _selectedNode.Name;
            string? playlistName = _selectedNode.Parent?.Name;
            if (playlistName == null || !Playlists.TryGetValue(playlistName, out var playlist)) return;
            int idx = playlist.Options.FindIndex(x => x.Name == songName);
            if (idx <= 1) return;
            var prevOpt = playlist.Options[idx - 1];
            var playlistContent = FindPlaylistNode(playlistName);
            if (playlistContent != null && idx - 1 < playlistContent.Children.Count)
                _selectedNode = playlistContent.Children[idx - 1];
            PlayOption(prevOpt);
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            PlayNext();
        }

        private bool PlayNext()
        {
            if (_selectedNode == null || _selectedNode.Level != 2)
            {
                ResetPlaybackUI();
                return false;
            }
            string songName = _selectedNode.Name;
            string? playlistName = _selectedNode.Parent?.Name;
            if (playlistName == null || !Playlists.TryGetValue(playlistName, out var playlist))
            {
                ResetPlaybackUI();
                return false;
            }
            int idx = playlist.Options.FindIndex(x => x.Name == songName);
            if (idx < 0 || idx + 1 >= playlist.Options.Count)
            {
                ResetPlaybackUI();
                return false;
            }
            var nextOpt = playlist.Options[idx + 1];
            var playlistContent = FindPlaylistNode(playlistName);
            if (playlistContent != null && idx + 1 < playlistContent.Children.Count)
                _selectedNode = playlistContent.Children[idx + 1];
            PlayOption(nextOpt);
            return true;
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!Player.IsPlaying)
            {
                if (_selectedNode == null || _selectedNode.Level != 2) return;
                string songName = _selectedNode.Name;
                string? playlistName = _selectedNode.Parent?.Name;
                if (playlistName == null || !Playlists.TryGetValue(playlistName, out var playlist)) return;
                var opt = playlist.Options.Find(x => x.Name == songName);
                if (opt != null) PlayOption(opt);
                return;
            }

            Player.Pause();
            UpdatePlayPauseIcon();
        }

        private void PlayOption(Option opt)
        {
            string songPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, Playlist.GetScdPath(opt));
            if (File.Exists(songPath))
            {
                NowPlayingLabel.Text = opt.Name;
                Player.Play(songPath, onEnded: () =>
                {
                    DispatcherQueue.TryEnqueue(() => PlayNext());
                });
                UpdatePlayPauseIcon();
                _playbackTimer.Start();
            }
            else
            {
                _ = ShowDialogAsync(AppStrings.Dlg_FileNotFound_Title, AppStrings.FileNotFoundContent(songPath));
            }
        }

        private void UpdatePlaybackProgress()
        {
            if (!Player.IsPlaying)
            {
                ResetPlaybackUI();
                return;
            }

            var duration = Player.GetDuration();
            var position = Player.GetPosition();

            DurationLabel.Text = FormatTime(duration);
            ElapsedLabel.Text = FormatTime(position);

            if (!_isSeeking)
            {
                SeekSlider.Maximum = Math.Max(duration.TotalMilliseconds, 1);
                SeekSlider.Value = position.TotalMilliseconds;
            }
        }

        private void ResetPlaybackUI()
        {
            _playbackTimer.Stop();
            SeekSlider.Value = 0;
            ElapsedLabel.Text = "0:00";
            DurationLabel.Text = "0:00";
            UpdatePlayPauseIcon();
        }

        private static string FormatTime(TimeSpan time) => $"{(int)time.TotalMinutes}:{time.Seconds:D2}";

        private void UpdatePlayPauseIcon()
        {
            PlayPauseIcon.Glyph = Player.IsPlaying && !Player.IsPaused ? PauseGlyph : PlayGlyph;
        }

        private void SeekSlider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isSeeking = true;
        }

        private void SeekSlider_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isSeeking = false;
            Player.Seek(TimeSpan.FromMilliseconds(SeekSlider.Value));
        }

        private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            Player.SetVolume((int)e.NewValue);
            MuteIcon.Glyph = e.NewValue <= 0 ? MuteGlyph : VolumeGlyph;
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (VolumeSlider.Value > 0)
            {
                _lastVolumeBeforeMute = VolumeSlider.Value;
                VolumeSlider.Value = 0;
            }
            else
            {
                VolumeSlider.Value = _lastVolumeBeforeMute > 0 ? _lastVolumeBeforeMute : 100;
            }
        }
    }
}
