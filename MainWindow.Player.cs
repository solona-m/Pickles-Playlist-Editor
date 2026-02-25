using System;
using System.Windows.Forms;

namespace Pickles_Playlist_Editor
{
    public partial class MainWindow : Form
    {
        private void PlayButton_Click(object sender, EventArgs e)
        {
            foreach (TreeNode childNode in PlaylistTreeView.Nodes[0].Nodes)
            {
                foreach (TreeNode song in childNode.Nodes)
                {
                    if (song.IsSelected)
                    {
                        Playlist targetPlaylist = Playlists[childNode.Name];
                        Option opt = targetPlaylist.Options[song.Index];
                        PlayOption(opt);
                        break;
                    }
                }
            }
        }

        private void PlayOption(Option opt)
        {
            string optPath = Playlist.GetScdPath(opt);
            string songPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, optPath);
            if (File.Exists(songPath))
            {
                Player.Play(songPath, onEnded: () =>
                {
                    if (InvokeRequired)
                    {
                        Invoke(new Action(() => PlayNext()));
                    }
                    else
                    {
                        PlayNext();
                    }
                });
            }
            else
            {
                MessageBox.Show($"Song file not found: {songPath}");
            }
        }

        private void PauseButton_Click(object sender, EventArgs e) => Player.Pause();
        private void StopIcon_Click(object sender, EventArgs e) => Player.Stop();

        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            foreach (TreeNode childNode in PlaylistTreeView.Nodes[0].Nodes)
            {
                foreach (TreeNode song in childNode.Nodes)
                {
                    if (song.IsSelected)
                    {
                        if (song.Index - 1 < 1)
                            return;
                        Playlist targetPlaylist = Playlists[childNode.Name];
                        Option opt = targetPlaylist.Options[song.Index - 1];
                        PlaylistTreeView.SelectedNode = childNode.Nodes[song.Index - 1];
                        PlayOption(opt);
                        break;
                    }
                }
            }
        }

        private void nextButton_Click(object sender, EventArgs e)
        {
            bool flowControl = PlayNext();
            if (!flowControl) return;
        }

        private bool PlayNext()
        {
            foreach (TreeNode childNode in PlaylistTreeView.Nodes[0].Nodes)
            {
                foreach (TreeNode song in childNode.Nodes)
                {
                    if (song.IsSelected)
                    {
                        if (song.Index + 1 >= childNode.Nodes.Count)
                            return false;
                        Playlist targetPlaylist = Playlists[childNode.Name];
                        Option opt = targetPlaylist.Options[song.Index + 1];
                        PlaylistTreeView.SelectedNode = childNode.Nodes[song.Index + 1];
                        PlayOption(opt);
                        break;
                    }
                }
            }
            return true;
        }
    }
}