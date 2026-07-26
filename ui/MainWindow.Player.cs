using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;

namespace Pickles_Playlist_Editor
{
    public sealed partial class MainWindow
    {
        private const string PlayGlyph = "";
        private const string PauseGlyph = "";
        private const string VolumeGlyph = "";
        private const string MuteGlyph = "";
        private const string RepeatAllGlyph = "";
        private const string RepeatOneGlyph = "";

        private enum RepeatMode { Off, All, One }

        private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private bool _isSeeking;
        private double _lastVolumeBeforeMute = 100;

        // Starts true so the Slider's XAML-declared default (Value="100"), which fires
        // ValueChanged during InitializeComponent(), doesn't clobber the saved volume
        // before InitializePlaybackTimer() gets a chance to restore it.
        private bool _suppressVolumePersist = true;

        private bool _shuffleEnabled;
        private RepeatMode _repeatMode = RepeatMode.Off;
        private readonly Random _shuffleRng = new();
        private List<int> _shuffleOrder = new();
        private int _shufflePos = -1;
        private string? _shufflePlaylistName;

        // The song actually loaded in the player. Deliberately separate from
        // _selectedNode (tree selection): a background playlist reload can reassign
        // _selectedNode via TreeView SelectionChanged, which must not derail playback.
        private string? _nowPlayingPlaylistName;
        private string? _nowPlayingSongName;

        private void InitializePlaybackTimer()
        {
            _playbackTimer.Tick += (s, e) => UpdatePlaybackProgress();

            // The Slider's Thumb captures pointer input and marks it handled before it
            // bubbles up, so XAML-declared PointerPressed/PointerReleased on the Slider
            // itself never fire during a drag. handledEventsToo lets us see them anyway.
            SeekSlider.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SeekSlider_PointerPressed), true);
            SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SeekSlider_PointerReleased), true);

            int savedVolume = Settings.PlaybackVolume;
            Player.SetVolume(savedVolume);
            VolumeSlider.Value = savedVolume;
            _suppressVolumePersist = false;
        }

        // Resolves playlist/node/index context from whatever song is actually playing,
        // not from tree selection. Used by Prev/Next/shuffle so navigation can't be
        // hijacked by unrelated selection changes (e.g. a background playlist reload).
        private (Playlist playlist, PlaylistNodeContent playlistNode, string playlistName, int idx)? GetNowPlayingContext()
        {
            if (_nowPlayingPlaylistName == null || _nowPlayingSongName == null) return null;
            if (!Playlists.TryGetValue(_nowPlayingPlaylistName, out var playlist)) return null;
            int idx = playlist.Options.FindIndex(x => x.Name == _nowPlayingSongName);
            if (idx < 0) return null;
            var playlistNode = FindPlaylistNode(_nowPlayingPlaylistName);
            if (playlistNode == null) return null;
            return (playlist, playlistNode, _nowPlayingPlaylistName, idx);
        }

        // Builds a shuffled play order over the playlist's real songs (Options index 0 is
        // the "Off" placeholder). This never touches the playlist's own saved order.
        private void BuildShuffleOrder(Playlist playlist, string playlistName, int keepFirst)
        {
            var indices = Enumerable.Range(1, Math.Max(0, playlist.Options.Count - 1)).ToList();
            for (int i = indices.Count - 1; i > 0; i--)
            {
                int j = _shuffleRng.Next(i + 1);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }
            if (keepFirst >= 0)
            {
                indices.Remove(keepFirst);
                indices.Insert(0, keepFirst);
            }
            _shuffleOrder = indices;
            _shufflePlaylistName = playlistName;
            _shufflePos = keepFirst >= 0 ? 0 : -1;
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            var ctx = GetNowPlayingContext();
            if (ctx == null) return;
            var (playlist, playlistNode, playlistName, idx) = ctx.Value;

            int prevIdx;
            if (_shuffleEnabled && _shufflePlaylistName == playlistName && _shufflePos > 0)
            {
                _shufflePos--;
                prevIdx = _shuffleOrder[_shufflePos];
            }
            else
            {
                if (idx <= 1) return;
                prevIdx = idx - 1;
            }

            if (prevIdx < playlistNode.Children.Count)
                _selectedNode = playlistNode.Children[prevIdx];
            PlayOption(playlist.Options[prevIdx], playlistName, isShuffleNav: true);
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            PlayNext();
        }

        private bool PlayNext(bool isAutoAdvance = false)
        {
            var ctx = GetNowPlayingContext();
            if (ctx == null)
            {
                ResetPlaybackUI();
                return false;
            }
            var (playlist, playlistNode, playlistName, idx) = ctx.Value;

            if (isAutoAdvance && _repeatMode == RepeatMode.One)
            {
                PlayOption(playlist.Options[idx], playlistName, isShuffleNav: true);
                return true;
            }

            int nextIdx;
            if (_shuffleEnabled)
            {
                if (_shufflePlaylistName != playlistName || _shuffleOrder.Count == 0)
                    BuildShuffleOrder(playlist, playlistName, keepFirst: idx);

                if (_shufflePos + 1 < _shuffleOrder.Count)
                {
                    _shufflePos++;
                }
                else if (_repeatMode == RepeatMode.All)
                {
                    BuildShuffleOrder(playlist, playlistName, keepFirst: -1);
                    _shufflePos = 0;
                }
                else
                {
                    ResetPlaybackUI();
                    return false;
                }
                nextIdx = _shuffleOrder[_shufflePos];
            }
            else
            {
                nextIdx = idx + 1;
                if (nextIdx >= playlist.Options.Count)
                {
                    if (_repeatMode == RepeatMode.All)
                        nextIdx = 1;
                    else
                    {
                        ResetPlaybackUI();
                        return false;
                    }
                }
            }

            if (nextIdx < playlistNode.Children.Count)
                _selectedNode = playlistNode.Children[nextIdx];
            PlayOption(playlist.Options[nextIdx], playlistName, isShuffleNav: true);
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
                if (opt != null) PlayOption(opt, playlistName);
                return;
            }

            Player.Pause();
            UpdatePlayPauseIcon();
        }

        private void PlayOption(Option opt, string playlistName, bool isShuffleNav = false)
        {
            string songPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, Playlist.GetScdPath(opt));
            if (!File.Exists(songPath))
            {
                _ = ShowDialogAsync(AppStrings.Dlg_FileNotFound_Title, AppStrings.FileNotFoundContent(songPath));
                return;
            }

            // A track that can't be decoded (unsupported/corrupt SCD, unwritable temp
            // location, decoder refusing the stream) used to throw straight out of this
            // click handler and take the whole app down with it. Report it instead.
            try
            {
                _nowPlayingPlaylistName = playlistName;
                _nowPlayingSongName = opt.Name;

                NowPlayingLabel.Text = opt.Name;
                Player.Play(songPath, onEnded: () =>
                {
                    DispatcherQueue.TryEnqueue(() => PlayNext(isAutoAdvance: true));
                });
                UpdatePlayPauseIcon();
                _playbackTimer.Start();

                // A manually chosen song (double-click, Play button) anchors a fresh
                // shuffle cycle instead of continuing whatever order was already built.
                if (_shuffleEnabled && !isShuffleNav && Playlists.TryGetValue(playlistName, out var playlist))
                {
                    int idx = playlist.Options.FindIndex(x => x.Name == opt.Name);
                    if (idx >= 0)
                        BuildShuffleOrder(playlist, playlistName, keepFirst: idx);
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError("Playback of '{Song}' in '{Playlist}' failed ({Path}): {Error}",
                    opt.Name, playlistName, songPath, ex);

                TryStopPlayerQuietly();
                _nowPlayingPlaylistName = null;
                _nowPlayingSongName = null;
                NowPlayingLabel.Text = "";
                ResetPlaybackUI();

                _ = ShowDialogAsync(AppStrings.Dlg_Error,
                    AppStrings.ErrorLoadingSong(opt.Name, playlistName, FormatExceptionMessage(ex)));
            }
        }

        // Best-effort teardown after a failed start: the player may hold a half-open
        // stream, and a second failure here would defeat the point of the catch above.
        private static void TryStopPlayerQuietly()
        {
            try { Player.Stop(); }
            catch (Exception ex) { Utils.Logger.LogWarn("Stop after failed playback start failed: {Error}", ex.Message); }
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
            if (!_suppressVolumePersist)
                Settings.PlaybackVolume = (int)e.NewValue;
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            // Muting/unmuting shouldn't overwrite the remembered volume with 0.
            _suppressVolumePersist = true;
            if (VolumeSlider.Value > 0)
            {
                _lastVolumeBeforeMute = VolumeSlider.Value;
                VolumeSlider.Value = 0;
            }
            else
            {
                VolumeSlider.Value = _lastVolumeBeforeMute > 0 ? _lastVolumeBeforeMute : 100;
            }
            _suppressVolumePersist = false;
        }

        private void ShuffleToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _shuffleEnabled = !_shuffleEnabled;
            if (_shuffleEnabled)
            {
                var ctx = GetNowPlayingContext();
                if (ctx != null)
                {
                    var (playlist, _, playlistName, idx) = ctx.Value;
                    BuildShuffleOrder(playlist, playlistName, keepFirst: idx);
                }
            }
            else
            {
                _shuffleOrder.Clear();
                _shufflePos = -1;
                _shufflePlaylistName = null;
            }
            UpdateShuffleRepeatIcons();
        }

        private void RepeatToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _repeatMode = _repeatMode switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.Off
            };
            UpdateShuffleRepeatIcons();
        }

        private void UpdateShuffleRepeatIcons()
        {
            ShuffleIcon.Opacity = _shuffleEnabled ? 1.0 : 0.5;
            RepeatIcon.Opacity = _repeatMode == RepeatMode.Off ? 0.5 : 1.0;
            RepeatIcon.Glyph = _repeatMode == RepeatMode.One ? RepeatOneGlyph : RepeatAllGlyph;
        }
    }
}
