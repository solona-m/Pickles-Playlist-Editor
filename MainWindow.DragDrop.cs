using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Pickles_Playlist_Editor
{
    public partial class MainWindow : Form
    {
        private void PlaylistTreeView_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.Move;
        }

        private void PlaylistTreeView_DragDrop(object sender, DragEventArgs e)
        {
            DoDragDrop(e);
        }

        private async Task<bool> DoDragDrop(DragEventArgs e)
        {
            try
            {
                TreeNode targetNode;
                Playlist targetPlaylist;
                var (flowControl, value) = GetTreeNode(e, out targetNode, out targetPlaylist);
                if (!flowControl)
                {
                    return value;
                }

                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    // use async helper for import
                    await AddOrInsertFilesToPlaylistAsync(targetNode, targetPlaylist, files);
                    return false;
                }

                // Retrieve the node that was dragged.
                TreeNode draggedNode = (TreeNode)e.Data.GetData(typeof(TreeNode));
                switch (targetNode.Level)
                {
                    case 0:
                        return false;
                    case 1:
                        if (draggedNode == null)
                            return false;
                        if (!draggedNode.Equals(targetNode) && targetNode != null)
                        {
                            Playlist playlist = Playlists[draggedNode.Parent.Name];
                            Option song = playlist.Options.Find(x => x.Name == draggedNode.Name);
                            draggedNode.Remove();
                            targetNode.Nodes.Insert(targetNode.Nodes.Count, draggedNode);
                            playlist.Options.Remove(song);
                            playlist.Save();

                            string oldPath = Playlist.GetScdPath(song);
                            string oldDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, oldPath.Substring(0, oldPath.LastIndexOf('\\')));
                            string oldSongName = oldPath.Substring(oldPath.LastIndexOf('\\') + 1, oldPath.Length - oldPath.LastIndexOf('\\') - 1);
                            var scdKey = Playlist.GetScdKey(song) ?? Settings.BaselineScdKey;
                            song.Files[scdKey] = Path.Combine(targetPlaylist.Name, song.Name, oldSongName);

                            targetPlaylist.Options.Add(song);
                            targetPlaylist.Save();

                            string newDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, targetPlaylist.Name, song.Name);
                            if (!Directory.Exists(newDir))
                                Directory.CreateDirectory(newDir);
                            File.Move(Path.Combine(oldDir, oldSongName), Path.Combine(newDir, oldSongName));

                            targetNode.Expand();
                            RecomputePlaylistDurations();
                        }
                        break;
                    case 2:
                        if (draggedNode == null)
                            return false;
                        if (!draggedNode.Equals(targetNode) && targetNode != null)
                        {
                            Playlist playlist = Playlists[draggedNode.Parent.Name];
                            Option song = playlist.Options.Find(x => x.Name == draggedNode.Name);
                            draggedNode.Remove();
                            int index = targetNode.Parent.Nodes.IndexOf(targetNode) + 1;
                            targetNode.Parent.Nodes.Insert(index, draggedNode);
                            playlist.Options.Remove(song);
                            playlist.Save();

                            string oldPath = Playlist.GetScdPath(song);
                            string oldDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, oldPath.Substring(0, oldPath.LastIndexOf('\\')));
                            string oldSongName = oldPath.Substring(oldPath.LastIndexOf('\\') + 1, oldPath.Length - oldPath.LastIndexOf('\\') - 1);
                            var scdKey = Playlist.GetScdKey(song) ?? Settings.BaselineScdKey;
                            song.Files[scdKey] = Path.Combine(targetPlaylist.Name, song.Name, oldSongName);

                            targetPlaylist.Options.Insert(index, song);
                            targetPlaylist.Save();

                            string newDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, targetPlaylist.Name, song.Name);
                            if (!Directory.Exists(newDir))
                                Directory.CreateDirectory(newDir);
                            File.Move(Path.Combine(oldDir, oldSongName), Path.Combine(newDir, oldSongName));

                            targetNode.Expand();
                            RecomputePlaylistDurations();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error during drag and drop: " + ex.ToString());
                return false;
            }

            return true;
        }

        private (bool flowControl, bool value) GetTreeNode(DragEventArgs e, out TreeNode targetNode, out Playlist targetPlaylist)
        {
            Point targetPoint = PlaylistTreeView.PointToClient(new Point(e.X, e.Y));
            targetNode = PlaylistTreeView.GetNodeAt(targetPoint);
            GetPlaylistFromTargetNode(targetNode, out targetPlaylist);
            return (flowControl: true, value: default);
        }

        private void GetPlaylistFromTargetNode(TreeNode targetNode, out Playlist targetPlaylist)
        {
            if (targetNode == null)
            {
                targetPlaylist = null;
                return;
            }
            switch (targetNode.Level)
            {
                case 0:
                    targetPlaylist = null;
                    break;
                case 1:
                    targetPlaylist = Playlists[targetNode.Name];
                    break;
                case 2:
                    targetPlaylist = Playlists[targetNode.Parent.Name];
                    break;
                default:
                    targetPlaylist = null;
                    break;
            }
        }

        private async Task<(bool flowControl, bool value)> AddOrInsertFilesToPlaylistAsync(TreeNode targetNode, Playlist targetPlaylist, string[] files)
        {
            if (files == null || files.Length == 0) return (flowControl: true, value: default);

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() =>
                {
                    SetProgressBarText("Importing songs...");
                    progressBar1.Value = 0;
                }));
            }
            else
            {
                SetProgressBarText("Importing songs...");
                progressBar1.Value = 0;
            }

            int insertIndex = -1;
            if (targetNode != null && targetNode.Level == 2)
            {
                insertIndex = targetNode.Parent.Nodes.IndexOf(targetNode) + 1;
            }

            await Task.Run(() =>
            {
                if (targetNode != null && targetNode.Level == 2)
                {
                    targetPlaylist.Insert(files, insertIndex, SetProgressBarPercent);
                }
                else
                {
                    targetPlaylist.Add(files, SetProgressBarPercent);
                }
            }).ConfigureAwait(false);

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() =>
                {
                    LoadPlaylists();
                    PlaylistTreeView.Nodes[0].Expand();
                    try
                    {
                        if (targetNode != null)
                        {
                            if (targetNode.Level == 2)
                                PlaylistTreeView.Nodes[0].Nodes[targetNode.Parent.Name].Expand();
                            else
                                PlaylistTreeView.Nodes[0].Nodes[targetNode.Name].Expand();
                        }
                    }
                    catch { }
                    SetProgressBarPercent(0);
                }));
            }
            else
            {
                LoadPlaylists();
                PlaylistTreeView.Nodes[0].Expand();
                try
                {
                    if (targetNode != null)
                    {
                        if (targetNode.Level == 2)
                            PlaylistTreeView.Nodes[0].Nodes[targetNode.Parent.Name].Expand();
                        else
                            PlaylistTreeView.Nodes[0].Nodes[targetNode.Name].Expand();
                    }
                }
                catch { }
                SetProgressBarPercent(0);
            }

            return (flowControl: false, value: false);
        }

        private (bool flowControl, bool value) AddOrInsertFilesToPlaylist(TreeNode targetNode, Playlist targetPlaylist, string[] files)
        {
            // kept for compatibility with code that might still call sync version
            if (files != null && files.Length > 0)
            {
                SetProgressBarText("Importing songs...");
                progressBar1.Value = 0;
                if (targetNode.Level == 2)
                {
                    int index = targetNode.Parent.Nodes.IndexOf(targetNode) + 1;
                    targetPlaylist.Insert(files, index, SetProgressBarPercent);
                }
                else
                {
                    targetPlaylist.Add(files, SetProgressBarPercent);
                }

                LoadPlaylists();
                PlaylistTreeView.Nodes[0].Expand();
                if (targetNode.Level == 2)
                {
                    PlaylistTreeView.Nodes[0].Nodes[targetNode.Parent.Name].Expand();
                }
                else
                {
                    PlaylistTreeView.Nodes[0].Nodes[targetNode.Name].Expand();
                }
                SetProgressBarPercent(0);
                return (flowControl: false, value: false);
            }

            return (flowControl: true, value: default);
        }

        private void fromMyComputerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AddSongsButton_Click(sender, e);
        }
    }
}