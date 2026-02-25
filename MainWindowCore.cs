using AutoUpdaterDotNET;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;
using Pickles_Playlist_Editor.Tools;

namespace Pickles_Playlist_Editor
{
    public partial class MainWindow : Form
    {
        public static Dictionary<string, Playlist> Playlists { get; set; }
        private readonly ContextMenuStrip _treeContextMenu = new ContextMenuStrip();
        private TreeNode? _contextMenuNode;

        public MainWindow()
        {
            AutoUpdater.Start("https://github.com/solona-m/Pickles-Playlist-Editor/releases/latest/download/update.xml");
            InitializeComponent();
            InitializeTreeContextMenu();
        }

        private async void Form1_Load(object sender, EventArgs e)
        {
            try
            {
                await YtDlpService.EnsureUpToDateAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to initialize yt-dlp: {ex.Message}", "yt-dlp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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
                        if (song == null)
                            continue;
                        try
                        {
                            TimeSpan time = TimeSpan.Zero;
                            if (!skipDurationComputation && !string.IsNullOrEmpty(Playlist.GetScdPath(song)))
                            {
                                time = BPMDetector.GetDuration(Playlist.GetScdPath(song));
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
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading playlists: " + ex.ToString());
                return;
            }
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
                        if (!string.IsNullOrEmpty(Playlist.GetScdPath(song)))
                        {
                            time = BPMDetector.GetDuration(Playlist.GetScdPath(song));
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
                        if (opt?.Files != null)
                        {
                            var scdPath = Playlist.GetScdPath(opt);
                            if (!string.IsNullOrEmpty(scdPath))
                            {
                                // store full path as used elsewhere in the app
                                files.Add(Path.Combine(Settings.PenumbraLocation, Settings.ModName, scdPath));
                            }
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

        private void SearchTextBox_TextChanged(object? sender, EventArgs e)
        {
            var filter = searchTextBox.Text?.Trim();
            LoadPlaylists(filter != null ? filter : string.Empty);
        }

        // Updated AddSongsButton_Click to await the async helper
        private async void AddSongsButton_Click(object? sender, EventArgs e)
        {
            try
            {
                var playlists = Playlist.GetAll();

                // Determine target playlist and node
                var selectedNode = PlaylistTreeView.SelectedNode;
                Playlist targetPlaylist = null;
                TreeNode? targetNode = selectedNode;

                if (selectedNode == null)
                {
                    // Fallback to ResolveTargetPlaylistForSingle (returns default when nothing selected)
                    var targetName = ResolveTargetPlaylistForSingle();
                    if (!playlists.ContainsKey(targetName))
                    {
                        MessageBox.Show($"Target playlist '{targetName}' not found.");
                        return;
                    }
                    targetPlaylist = playlists[targetName];

                    // Try to find a corresponding TreeNode for UI updates (may be null)
                    if (PlaylistTreeView.Nodes.Count > 0)
                    {
                        var found = PlaylistTreeView.Nodes[0].Nodes.Find(targetName, false);
                        if (found.Length > 0) targetNode = found[0];
                    }
                }
                else
                {
                    GetPlaylistFromTargetNode(selectedNode, out targetPlaylist);
                    targetNode = selectedNode;
                }

                if (targetPlaylist == null)
                {
                    MessageBox.Show("Please select a playlist or a song inside a playlist to add files to.");
                    return;
                }

                using var dlg = new OpenFileDialog
                {
                    Multiselect = true,
                    Filter = "Audio Files|*.ogg;*.wav;*.mp3;*.m4a",
                    Title = "Select audio files to add"
                };

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                var files = dlg.FileNames;
                if (files == null || files.Length == 0) return;

                // Run the add/insert on a background thread and update the UI afterwards
                await AddOrInsertFilesToPlaylistAsync(targetNode ?? PlaylistTreeView.Nodes[0], targetPlaylist, files);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error adding songs: " + ex.Message);
            }
        }

        private string GetBPMString(Option song)
        {
            string scdPath = Playlist.GetScdPath(song);
            if (string.IsNullOrEmpty(scdPath))
                return string.Empty;
            return " (" + BPMDetector.GetBPMFromSCD(scdPath) + " BPM)";
        }

        private string GetTimeString(TimeSpan time)
        {
            var hours = (int)time.TotalHours;
            var minutes = (int)time.Minutes;
            var seconds = time.Seconds;
            return $" ({hours:D2}:{minutes:D2}:{seconds:D2})";
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

        private void PlaylistTreeView_ItemDrag(object sender, ItemDragEventArgs e)
        {
            DoDragDrop(e.Item, DragDropEffects.Move);
        }

        private void PlaylistTreeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
                return;

            PlaylistTreeView.SelectedNode = e.Node;
            _contextMenuNode = e.Node;

            bool validTarget = e.Node.Level == 1 || e.Node.Level == 2;
            foreach (ToolStripItem item in _treeContextMenu.Items)
            {
                item.Enabled = validTarget;
            }

            if (validTarget)
            {
                _treeContextMenu.Show(PlaylistTreeView, e.Location);
            }
        }

        private void ShowOperationSummary(string title, int successCount, int totalCount, List<string> errors)
        {
            if (errors.Count == 0)
            {
                MessageBox.Show($"{title}. Processed {successCount}/{totalCount} song(s).");
                return;
            }

            string details = string.Join(Environment.NewLine, errors.Take(10));
            if (errors.Count > 10)
                details += Environment.NewLine + $"...and {errors.Count - 10} more.";

            MessageBox.Show($"{title}. Processed {successCount}/{totalCount} song(s).{Environment.NewLine}{Environment.NewLine}Errors:{Environment.NewLine}{details}");
        }
    }
}