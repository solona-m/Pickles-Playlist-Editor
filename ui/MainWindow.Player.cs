using System.IO;

namespace Pickles_Playlist_Editor
{
    public sealed partial class MainWindow
    {
        private void PlayButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_selectedNode == null || _selectedNode.Level != 2) return;
            string songName = _selectedNode.Name;
            string? playlistName = _selectedNode.Parent?.Name;
            if (playlistName == null || !Playlists.TryGetValue(playlistName, out var playlist)) return;
            var opt = playlist.Options.Find(x => x.Name == songName);
            if (opt != null) PlayOption(opt);
        }

        private void PlayOption(Option opt)
        {
            string songPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, Playlist.GetScdPath(opt));
            if (File.Exists(songPath))
            {
                Player.Play(songPath, onEnded: () =>
                {
                    DispatcherQueue.TryEnqueue(() => PlayNext());
                });
            }
            else
            {
                _ = ShowDialogAsync(AppStrings.Dlg_FileNotFound_Title, AppStrings.FileNotFoundContent(songPath));
            }
        }

        private void PauseButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Player.Pause();
        private void StopIcon_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Player.Stop();

        private void PreviousButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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

        private void NextButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            PlayNext();
        }

        private bool PlayNext()
        {
            if (_selectedNode == null || _selectedNode.Level != 2) return false;
            string songName = _selectedNode.Name;
            string? playlistName = _selectedNode.Parent?.Name;
            if (playlistName == null || !Playlists.TryGetValue(playlistName, out var playlist)) return false;
            int idx = playlist.Options.FindIndex(x => x.Name == songName);
            if (idx < 0 || idx + 1 >= playlist.Options.Count) return false;
            var nextOpt = playlist.Options[idx + 1];
            var playlistContent = FindPlaylistNode(playlistName);
            if (playlistContent != null && idx + 1 < playlistContent.Children.Count)
                _selectedNode = playlistContent.Children[idx + 1];
            PlayOption(nextOpt);
            return true;
        }
    }
}
