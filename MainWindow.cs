using AutoUpdaterDotNET;
using Pickles_Playlist_Editor.Utils;
using System.Windows.Forms;

namespace Pickles_Playlist_Editor
{
    public partial class MainWindow : Form
    {
        private Dictionary<string, Playlist> Playlists { get; set; }

        public MainWindow()
        {
            AutoUpdater.Start("https://github.com/solona-m/Pickles-Playlist-Editor/releases/latest/download/update.xml");
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }

        private void panel1_Paint(object sender, PaintEventArgs e)
        {

        }

        private void playlistTreeView_onload(object sender, EventArgs e)
        {
            LoadPlaylists();
        }

        public void LoadPlaylists()
        {
            LoadPlaylists(string.Empty);
        }

        public void LoadPlaylists(string filter)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(LoadPlaylists));
                return;
            }

            //PlaylistTreeView.BeginUpdate();
            Dictionary<string, bool> expandedPlaylists = new Dictionary<string, bool>();
            string selectedSong = PlaylistTreeView.SelectedNode?.Name;
            if (PlaylistTreeView.Nodes.Count > 0)
            {
                foreach (TreeNode childNode in PlaylistTreeView.Nodes[0].Nodes)
                {
                    expandedPlaylists[childNode.Name] = childNode.IsExpanded;
                }
            }

            try
            {
                Playlists = Playlist.GetAll();

                bool skipDurationComputation = false;
                if (BPMDetector.ShowFirstTimeMessage())
                {
                    skipDurationComputation = true;
                    // User has been shown the first time message
                    RecomputePlaylistDurationsAsync();
                }

                PlaylistTreeView.Nodes.Clear();
                PlaylistTreeView.ImageList = new ImageList();
                PlaylistTreeView.ImageList.ImageSize = new Size(16, 16);
                PlaylistTreeView.ImageList.Images.Add("playlist", Properties.Resources.playlistIcon);
                PlaylistTreeView.ImageList.Images.Add("song", Properties.Resources.noteIcon);
                PlaylistTreeView.ShowNodeToolTips = true;
                TreeNode rootNode = new TreeNode("Playlists");
                
                PlaylistTreeView.Nodes.Add(rootNode);
                foreach (Playlist playlist in Playlists.Values)
                {
                    TreeNode playlistNode = new TreeNode(playlist.Name);
                    TimeSpan playlistTime = TimeSpan.Zero;
                    playlistNode.ImageKey = "playlist";
                    bool matchFound = false;
                    if (playlist.Options == null) continue;
                    foreach (Option song in playlist.Options)
                    {
                        try
                        {
                            TimeSpan time = TimeSpan.Zero;
                            if (!skipDurationComputation && song.Files.ContainsKey("sound/bpmloop.scd"))
                            {
                                time = BPMDetector.GetDuration(song.Files["sound/bpmloop.scd"]);
                                playlistTime = playlistTime.Add(time);
                            }
                            TreeNode songNode = new TreeNode(song.Name + (skipDurationComputation ? string.Empty : GetBPMString(song)) + GetTimeString(time));
                            songNode.ImageKey = "song";
                            songNode.Name = song.Name;
                            if (!string.IsNullOrEmpty(filter))
                            {
                                var comparison = StringComparison.OrdinalIgnoreCase;
                                if (playlist.Name?.IndexOf(filter, comparison) >= 0 ||
                                    song.Name?.IndexOf(filter, comparison) >= 0)
                                {
                                    matchFound = true;
                                    playlistNode.Nodes.Add(songNode);
                                }
                            }
                            else
                            {
                                playlistNode.Nodes.Add(songNode);
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("Error loading song " + song.Name + " in playlist " + playlist.Name + ": " + ex.ToString());
                        }
                    }
                    if (filter.Length > 0 && playlistNode.Nodes.Count == 0)
                        continue;
                    rootNode.Nodes.Add(playlistNode);

                    playlistNode.Name = playlist.Name;
                    playlistNode.Text = playlist.Name + GetTimeString(playlistTime);
                    if (matchFound)
                    {
                        expandedPlaylists[playlist.Name] = true;
                    }
                }
                PlaylistTreeView.CheckBoxes = true;
                rootNode.Expand();
                foreach (var kvp in expandedPlaylists)
                {
                    if (PlaylistTreeView.Nodes[0].Nodes.ContainsKey(kvp.Key) && kvp.Value)
                    {
                        PlaylistTreeView.Nodes[0].Nodes[kvp.Key].Expand();
                    }
                }
                if (!string.IsNullOrWhiteSpace(selectedSong) && PlaylistTreeView.Nodes[0].Nodes.Find(selectedSong, true).Length > 0)
                {
                    PlaylistTreeView.SelectedNode = PlaylistTreeView.Nodes[0].Nodes.Find(selectedSong, true)[0];
                }
                //PlaylistTreeView.EndUpdate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading playlists: " + ex.ToString());
                return;
            }
        }

        /// <summary>
        /// Filter handler for the search box.
        /// Rebuilds the tree to only include playlists and songs that contain the filter text.
        /// Matching is case-insensitive and checks playlist name and song name.
        /// </summary>
        private void SearchTextBox_TextChanged(object? sender, EventArgs e)
        {
            var filter = searchTextBox.Text?.Trim();
            LoadPlaylists(filter != null ? filter : string.Empty);
        }

        private void RecomputePlaylistDurations(bool checkUI = true)
        {
            foreach (Playlist playlist in Playlists.Values)
            {
                if (checkUI && PlaylistTreeView.Nodes[0].Nodes.Find(playlist.Name, false).Length == 0)
                    continue;
                TreeNode playlistNode = PlaylistTreeView.Nodes[0].Nodes.Find(playlist.Name, false)[0];
                TimeSpan playlistTime = TimeSpan.Zero;
                
                if (playlist.Options == null) continue;
                foreach (Option song in playlist.Options)
                {
                    try
                    {
                        TimeSpan time = TimeSpan.Zero;
                        if (song.Files.ContainsKey("sound/bpmloop.scd"))
                        {
                            time = BPMDetector.GetDuration(song.Files["sound/bpmloop.scd"]);
                            playlistTime = playlistTime.Add(time);
                        }
                
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Error loading song " + song.Name + " in playlist " + playlist.Name + ": " + ex.ToString());
                    }
                }

                playlistNode.Text = playlist.Name + GetTimeString(playlistTime);
            }

            if (checkUI)
                return;
            LoadPlaylists();
            SetProgressBarPercent(100);
        }

        private string GetBPMString(Option song)
        {
            if (!song.Files.ContainsKey("sound/bpmloop.scd"))
                return string.Empty;
            return " (" + BPMDetector.GetBPMFromSCD(song.Files["sound/bpmloop.scd"]) + " BPM)";
        }

        private string GetTimeString(TimeSpan time)
        {
            // Use total minutes so durations > 60 minutes are shown correctly
            var hours = (int)time.TotalHours;
            var minutes = (int)time.Minutes;
            var seconds = time.Seconds;
            return $" ({hours:D2}:{minutes:D2}:{seconds:D2})";
        }

        private void PlaylistTreeView_ItemDrag(object sender, ItemDragEventArgs e)
        {
            DoDragDrop(e.Item, DragDropEffects.Move);
        }

        private void PlaylistTreeView_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.Move;
        }

        private void PlaylistTreeView_DragDrop(object sender, DragEventArgs e)
        {
            DoDragDrop(e);
        }

        public void SetProgressBarPercent(int percent)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<int>(SetProgressBarPercent), percent);
                return;
            }

            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            progressBar1.Value = percent;
            if (percent == 100)
            {
                progressBar1.ResetText();
                progressBar1.Value = 0;
            }
        }

        public void SetProgressBarText(string text)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(SetProgressBarText), text);
                return;
            }
            progressBar1.Text = text;
        }

        private async Task<bool> DoDragDrop(DragEventArgs e)
        {
            try
            {
                // Retrieve the client coordinates of the drop location.
                Point targetPoint = PlaylistTreeView.PointToClient(new Point(e.X, e.Y));

                // Retrieve the node at the drop location.
                TreeNode targetNode = PlaylistTreeView.GetNodeAt(targetPoint);
                if (targetNode == null)
                    return false;

                string parentName = targetNode.Level == 0 ? null : targetNode.Level == 1 ? targetNode.Name : targetNode.Parent.Name;

                Playlist targetPlaylist;

                switch (targetNode.Level)
                {
                    case 0:
                        return false;
                    case 1:
                        targetPlaylist = Playlists[targetNode.Name];
                        break;
                    case 2:
                        targetPlaylist = Playlists[targetNode.Parent.Name];
                        break;
                    default:
                        return false;

                }

                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
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
                    PlaylistTreeView.Nodes[0].Nodes[parentName].Expand();
                    SetProgressBarPercent(0);
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
                        // Confirm that the node at the drop location is not 
                        // the dragged node and that target node isn't null
                        // (for example if you drag outside the control)
                        if (!draggedNode.Equals(targetNode) && targetNode != null)
                        {
                            // Remove the node from its current 
                            // location and add it to the node at the drop location.
                            Playlist playlist = Playlists[draggedNode.Parent.Name];
                            Option song = playlist.Options.Find(x => x.Name == draggedNode.Name);
                            draggedNode.Remove();
                            targetNode.Nodes.Insert(targetNode.Nodes.Count, draggedNode);
                            playlist.Options.Remove(song);
                            playlist.Save();
                            song.Files["sound/bpmloop.scd"] = Path.Combine(targetPlaylist.Name, song.Name, "bpmloop.scd");
                            targetPlaylist.Options.Add(song);
                            targetPlaylist.Save();
                            Directory.Move(Path.Combine(Settings.PenumbraLocation, Settings.ModName, playlist.Name, song.Name),
                                Path.Combine(Settings.PenumbraLocation, Settings.ModName, targetPlaylist.Name, song.Name));

                            // Expand the node at the location 
                            // to show the dropped node.
                            targetNode.Expand();
                            RecomputePlaylistDurations();
                        }
                        break;
                    case 2:
                        if (draggedNode == null)
                            return false;
                        // Confirm that the node at the drop location is not 
                        // the dragged node and that target node isn't null
                        // (for example if you drag outside the control)
                        if (!draggedNode.Equals(targetNode) && targetNode != null)
                        {
                            // Remove the node from its current 
                            // location and add it to the node at the drop location.

                            Playlist playlist = Playlists[draggedNode.Parent.Name];
                            Option song = playlist.Options.Find(x => x.Name == draggedNode.Name);
                            draggedNode.Remove();
                            int index = targetNode.Parent.Nodes.IndexOf(targetNode) + 1;
                            targetNode.Parent.Nodes.Insert(index, draggedNode);
                            playlist.Options.Remove(song);
                            playlist.Save();
                            song.Files["sound/bpmloop.scd"] = Path.Combine(targetPlaylist.Name, song.Name, "bpmloop.scd");
                            targetPlaylist.Options.Insert(index, song);
                            targetPlaylist.Save();

                            string sourceDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, playlist.Name, song.Name);
                            string destDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, targetPlaylist.Name, song.Name);
                            if (sourceDir != destDir)
                                Directory.Move(sourceDir, destDir);

                            // Expand the node at the location 
                            // to show the dropped node.
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

        private void PlaylistTreeView_AfterCheck(object sender, TreeViewEventArgs e)
        {
            int checkedCount = 0;
            foreach (TreeNode childNode in PlaylistTreeView.Nodes[0].Nodes)
            {
                if (childNode.Checked)
                {
                    DeleteButton.Enabled = true;
                    ShuffleButton.Enabled = true;
                    SortByBPM.Enabled = true;
                    return;
                }

                foreach (TreeNode subChild in childNode.Nodes)
                {
                    if (subChild.Checked)
                    {
                        DeleteButton.Enabled = true;
                        checkedCount++;
                    }
                }
            }
            SortByBPM.Enabled = false;
            ShuffleButton.Enabled = false;
            if (checkedCount == 0)
            {
                DeleteButton.Enabled = false;
            }
        }

        private void DeleteButton_Click(object sender, EventArgs e)
        {
            DoDelete();
        }

        private async Task<bool> DoDelete()
        {
            try
            {
                var result = MessageBox.Show("Are you sure you want to delete the selected playlists/songs? This action cannot be undone.", "Confirm Deletion", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result == DialogResult.No)
                {
                    return false;
                }
                foreach (TreeNode childNode in PlaylistTreeView.Nodes[0].Nodes)
                {
                    if (childNode.Checked)
                    {
                        Playlists[childNode.Name].Delete();
                    }
                    else
                    {
                        Playlist playlist = Playlists[childNode.Name];
                        foreach (TreeNode subChild in childNode.Nodes)
                        {
                            if (subChild.Checked)
                            {
                                Option song = playlist.Options.Find(x => x.Name == subChild.Name);
                                playlist.Options.Remove(song);
                                playlist.Save();
                                string songDirectory = Path.Combine(Settings.PenumbraLocation, Settings.ModName, playlist.Name, song.Name);
                                if (Directory.Exists(songDirectory))
                                    Directory.Delete(songDirectory, true);
                            }
                        }
                    }
                }
                ShuffleButton.Enabled = false;
                DeleteButton.Enabled = false;
                LoadPlaylists();

            }
            catch (Exception ex)
            {
                MessageBox.Show("Error confirming deletion: " + ex.Message);
                return false;
            }

            return true;
        }

        private void NewButton_Click(object sender, EventArgs e)
        {
            NewPlaylistForm newPlaylistForm = new NewPlaylistForm();
            newPlaylistForm.ShowDialog();
            LoadPlaylists();
        }

        private void SettingsButton_Click(object sender, EventArgs e)
        {
            SettingsForm settingsForm = new SettingsForm();
            settingsForm.ShowDialog();
            LoadPlaylists();
        }

        private void ShuffleButton_Click(object sender, EventArgs e)
        {
            foreach (TreeNode childNode in PlaylistTreeView.Nodes[0].Nodes)
            {
                if (childNode.Checked)
                {
                    Playlist playlist = Playlists[childNode.Name];
                    playlist.Shuffle();
                }
            }
            LoadPlaylists();
        }

        private SortDirection CurrentDirection = SortDirection.Ascending;

        private void SortByBPM_Click(object sender, EventArgs e)
        {
            if (!SortByBPM.Enabled)
                return;

            foreach (TreeNode childNode in PlaylistTreeView.Nodes[0].Nodes)
            {
                if (childNode.Checked)
                {
                    Playlist playlist = Playlists[childNode.Name];
                    if (CurrentDirection == SortDirection.Ascending)
                    {
                        playlist.Sort(SortDirection.Descending);
                        CurrentDirection = SortDirection.Descending;
                    }
                    else
                    {
                        playlist.Sort(SortDirection.Ascending);
                        CurrentDirection = SortDirection.Ascending;
                    }
                }
            }
            LoadPlaylists();
        }

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
            string optPath = opt.Files["sound/bpmloop.scd"];
            string songPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, optPath);
            if (File.Exists(songPath))
            {
                // pass a callback to run when playback ends
                Player.Play(songPath, onEnded: () =>
                {
                    // marshal back to UI thread if needed
                    if (InvokeRequired)
                    {
                        Invoke(new Action(() =>
                        {
                            PlayNext();
                        }));
                    }
                    else
                    {
                    }
                });
            }
            else
            {
                MessageBox.Show($"Song file not found: {songPath}");
            }
        }

        private void PauseButton_Click(object sender, EventArgs e)
        {
            Player.Pause();
        }

        private void StopIcon_Click(object sender, EventArgs e)
        {
            Player.Stop();
        }

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
            if (!flowControl)
            {
                return;
            }
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

        private Task RecomputePlaylistDurationsAsync()
        {
            // Snapshot playlist file paths on the UI thread to avoid cross-thread access
            var playlistScdPaths = new List<List<string>>();
            var playlists = Playlist.GetAll();
            foreach (var playlist in playlists.Values)
            {
                var files = new List<string>();
                if (playlist.Options != null)
                {
                    foreach (var opt in playlist.Options)
                    {
                        if (opt?.Files != null && opt.Files.TryGetValue("sound/bpmloop.scd", out var scdPath))
                        {
                            // store full path as used elsewhere in the app
                            files.Add(Path.Combine(Settings.PenumbraLocation, Settings.ModName, scdPath));
                        }
                    }
                }
                playlistScdPaths.Add(files);
            }
            

            // Run expensive duration work on a background thread, then update UI
            return Task.Run(() =>
            {
                try
                {
                    SetProgressBarText("Computing song durations and bpm...");
                    int filesProcessed = 0;
                    int totalFiles = 0;
                    foreach (var fileList in playlistScdPaths)
                    {
                        foreach (var scd in fileList)
                        {
                            totalFiles++;
                        }
                    }
                    
                    foreach (var fileList in playlistScdPaths)
                    {
                        foreach (var scd in fileList)
                        {
                            try
                            {
                                // warm/cache duration (BPMDetector caches results internally)
                                BPMDetector.GetDuration(scd);
                                filesProcessed++;
                                SetProgressBarPercent((int)((filesProcessed / (double)totalFiles) * 100));
                            }
                            catch
                            {
                                // ignore per-file failures
                            }
                        }
                    }
                }
                finally
                {
                    // Marshal back to UI thread to update nodes safely
                    if (IsHandleCreated)
                    {
                        BeginInvoke(new Action(() => RecomputePlaylistDurations(false)));
                    }
                    else
                    {
                        // fallback: call directly (rare)
                        RecomputePlaylistDurations(false);
                    }
                }
            });
        }
    }
}
