using AutoUpdaterDotNET;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    public partial class MainWindow : Form
    {
        private Dictionary<string, Playlist> Playlists { get; set; }
        private readonly ContextMenuStrip _treeContextMenu = new ContextMenuStrip();
        private TreeNode? _contextMenuNode;

        public MainWindow()
        {
            AutoUpdater.Start("https://github.com/solona-m/Pickles-Playlist-Editor/releases/latest/download/update.xml");
            InitializeComponent();
            InitializeYouTubeDownloadControls();
            InitializeTreeContextMenu();
        }

        private void InitializeYouTubeDownloadControls()
        {
            ytDownloadModeComboBox.Items.Clear();
            ytDownloadModeComboBox.Items.AddRange(new object[] { "Single Track", "Playlist" });
            ytDownloadModeComboBox.SelectedIndex = 0;
        }

        private void InitializeTreeContextMenu()
        {
            var extractAudioMenuItem = new ToolStripMenuItem("Extract Audio", null, ExtractAudioMenuItem_Click);
            var normalizeAudioMenuItem = new ToolStripMenuItem("Normalize Audio", null, NormalizeAudioMenuItem_Click);
            var increaseVolumeMenuItem = new ToolStripMenuItem("Increase Volume", null, IncreaseVolumeMenuItem_Click);
            var applyEqMenuItem = new ToolStripMenuItem("Manage EQ Settings", null, ApplyEqSettingsMenuItem_Click);

            _treeContextMenu.Items.AddRange(new ToolStripItem[]
            {
                extractAudioMenuItem,
                normalizeAudioMenuItem,
                increaseVolumeMenuItem,
                new ToolStripSeparator(),
                applyEqMenuItem
            });
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

        private string GetBPMString(Option song)
        {
            string scdPath = Playlist.GetScdPath(song);
            if (string.IsNullOrEmpty(scdPath))
                return string.Empty;
            return " (" + BPMDetector.GetBPMFromSCD(scdPath) + " BPM)";
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

        private async void ExtractAudioMenuItem_Click(object? sender, EventArgs e)
        {
            var node = _contextMenuNode;
            if (node == null)
                return;

            var targetSongs = GetSongTargetsForNode(node);
            if (targetSongs.Count == 0)
            {
                MessageBox.Show("No songs were found for extraction.");
                return;
            }

            using var folderDialog = new FolderBrowserDialog
            {
                Description = "Choose output folder for extracted OGG files"
            };

            if (folderDialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(folderDialog.SelectedPath))
                return;

            SetProgressBarText("Extracting audio...");
            SetProgressBarPercent(0);

            int extracted = 0;
            var errors = new List<string>();
            string baseOutput = folderDialog.SelectedPath;

            await Task.Run(() =>
            {
                foreach (var (playlist, option) in targetSongs)
                {
                    try
                    {
                        string outputDir = node.Level == 1
                            ? Path.Combine(baseOutput, SanitizeFileName(playlist.Name))
                            : baseOutput;

                        string outPath = GetUniquePath(outputDir, SanitizeFileName(option.Name), ".ogg");
                        string fullScdPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, Playlist.GetScdPath(option));
                        ScdOggExtractor.ExtractOgg(fullScdPath, outPath);
                        extracted++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{playlist.Name}/{option.Name}: {ex.Message}");
                    }
                    finally
                    {
                        int percent = (int)((extracted + errors.Count) / (double)targetSongs.Count * 100);
                        SetProgressBarPercent(percent);
                    }
                }
            });

            SetProgressBarPercent(100);
            ShowOperationSummary("Audio extraction finished", extracted, targetSongs.Count, errors);
        }

        private async void NormalizeAudioMenuItem_Click(object? sender, EventArgs e)
        {
            var node = _contextMenuNode;
            if (node == null)
                return;

            var targetSongs = GetSongTargetsForNode(node);
            if (targetSongs.Count == 0)
            {
                MessageBox.Show("No songs were found for normalization.");
                return;
            }

            var confirm = MessageBox.Show(
                $"Normalize and repack {targetSongs.Count} song(s)? This will overwrite existing SCD files.",
                "Normalize Audio",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes)
                return;

            SetProgressBarText("Normalizing audio...");
            SetProgressBarPercent(0);

            int normalized = 0;
            var errors = new List<string>();

            await Task.Run(() =>
            {
                foreach (var (playlist, option) in targetSongs)
                {
                    try
                    {
                        NormalizeSongAudio(option);
                        normalized++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{playlist.Name}/{option.Name}: {ex.Message}");
                    }
                    finally
                    {
                        int percent = (int)((normalized + errors.Count) / (double)targetSongs.Count * 100);
                        SetProgressBarPercent(percent);
                    }
                }
            });

            SetProgressBarPercent(100);
            RecomputePlaylistDurations();
            ShowOperationSummary("Audio normalization finished", normalized, targetSongs.Count, errors);
        }

        private async void IncreaseVolumeMenuItem_Click(object? sender, EventArgs e)
        {
            var node = _contextMenuNode;
            if (node == null)
                return;

            var targetSongs = GetSongTargetsForNode(node);
            if (targetSongs.Count == 0)
            {
                MessageBox.Show("No songs were found for volume adjustment.");
                return;
            }

            SetProgressBarText("Increasing volume...");
            SetProgressBarPercent(0);

            int updated = 0;
            var errors = new List<string>();

            await Task.Run(() =>
            {
                foreach (var (playlist, option) in targetSongs)
                {
                    try
                    {
                        IncreaseSongVolume(option, 10);
                        updated++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{playlist.Name}/{option.Name}: {ex.Message}");
                    }
                    finally
                    {
                        int percent = (int)((updated + errors.Count) / (double)targetSongs.Count * 100);
                        SetProgressBarPercent(percent);
                    }
                }
            });

            SetProgressBarPercent(100);
            RecomputePlaylistDurations();
            ShowOperationSummary("Increase volume finished", updated, targetSongs.Count, errors);
        }

        private async void ApplyEqSettingsMenuItem_Click(object? sender, EventArgs e)
        {
            var node = _contextMenuNode;
            if (node == null)
                return;

            await OpenEqualizerWorkflowAsync(node);
        }

        private async Task OpenEqualizerWorkflowAsync(TreeNode node)
        {
            var targets = GetSongTargetsForNode(node);
            if (targets.Count == 0)
            {
                MessageBox.Show("No songs were found for equalizer processing.");
                return;
            }

            using var form = new EqualizerForm();
            if (form.ShowDialog(this) != DialogResult.OK)
                return;

            await ApplyEqualizerSettingsToTargetsAsync(targets, form.SelectedSettings);
        }

        private List<(Playlist playlist, Option option)> GetSongTargetsForNode(TreeNode node)
        {
            var results = new List<(Playlist playlist, Option option)>();

            if (node.Level == 2)
            {
                var playlist = Playlists[node.Parent.Name];
                var option = playlist.Options.FirstOrDefault(x => x.Name == node.Name);
                if (option != null && !string.IsNullOrEmpty(Playlist.GetScdPath(option)))
                {
                    results.Add((playlist, option));
                }
                return results;
            }

            if (node.Level == 1)
            {
                var playlist = Playlists[node.Name];
                foreach (var option in playlist.Options)
                {
                    if (!string.IsNullOrEmpty(Playlist.GetScdPath(option)))
                    {
                        results.Add((playlist, option));
                    }
                }
            }

            return results;
        }

        private async Task ApplyEqualizerSettingsToTargetsAsync(List<(Playlist playlist, Option option)> targets, EqualizerSettings settings)
        {
            var confirm = MessageBox.Show(
                $"Extract OGG from SCD, apply EQ settings, and repack {targets.Count} song(s)? This will overwrite existing SCD files.",
                "Apply EQ Settings",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes)
                return;

            SetProgressBarText("Applying EQ settings...");
            SetProgressBarPercent(0);

            int updated = 0;
            var errors = new List<string>();
            string filterChain = settings.ToFilterChain();

            await Task.Run(() =>
            {
                int total = targets.Count;
                int current = 0;

                foreach (var (playlist, option) in targets)
                {
                    current++;
                    try
                    {
                        SetProgressBarText($"Applying EQ settings ({current}/{total})");
                        ApplyEqualizerSettingsToSongAudio(option, filterChain);
                        updated++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{playlist.Name}/{option.Name}: {ex.Message}");
                    }
                    finally
                    {
                        int percent = (int)((updated + errors.Count) / (double)targets.Count * 100);
                        SetProgressBarPercent(percent);
                    }
                }
            });

            SetProgressBarPercent(100);
            RecomputePlaylistDurations();
            ShowOperationSummary("Apply EQ settings finished", updated, targets.Count, errors);
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "audio";

            var invalidChars = Path.GetInvalidFileNameChars();
            var cleaned = new string(value.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(cleaned) ? "audio" : cleaned;
        }

        private static string GetUniquePath(string folder, string fileNameNoExt, string extension)
        {
            Directory.CreateDirectory(folder);
            string candidate = Path.Combine(folder, fileNameNoExt + extension);
            if (!File.Exists(candidate))
                return candidate;

            int idx = 1;
            while (true)
            {
                candidate = Path.Combine(folder, $"{fileNameNoExt}_{idx}{extension}");
                if (!File.Exists(candidate))
                    return candidate;
                idx++;
            }
        }

        private static void NormalizeSongAudio(Option option)
        {
            string relativeScdPath = Playlist.GetScdPath(option);
            string fullScdPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, relativeScdPath);
            if (!File.Exists(fullScdPath))
                throw new FileNotFoundException("SCD file not found.", fullScdPath);

            string tempRoot = Path.Combine(Path.GetTempPath(), "pickles-normalize", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            string extractedOgg = Path.Combine(tempRoot, "source.ogg");

            try
            {
                ScdOggExtractor.ExtractOgg(fullScdPath, extractedOgg);
                FFMpeg.NormalizeVolume(extractedOgg);

                var scd = ScdFile.Import(fullScdPath);
                if (scd.Audio.Count == 0)
                    throw new InvalidOperationException("No audio entries were found in the SCD.");

                var oldEntry = scd.Audio[0];
                var newEntry = ScdVorbis.ImportOgg(extractedOgg, oldEntry);
                scd.Replace(oldEntry, newEntry);

                using var writer = new BinaryWriter(new FileStream(fullScdPath, FileMode.Create, FileAccess.Write, FileShare.None));
                scd.Write(writer);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
        }

        private static void IncreaseSongVolume(Option option, int db)
        {
            string relativeScdPath = Playlist.GetScdPath(option);
            string fullScdPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, relativeScdPath);
            if (!File.Exists(fullScdPath))
                throw new FileNotFoundException("SCD file not found.", fullScdPath);

            string tempRoot = Path.Combine(Path.GetTempPath(), "pickles-increase-volume", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            string extractedOgg = Path.Combine(tempRoot, "source.ogg");

            try
            {
                ScdOggExtractor.ExtractOgg(fullScdPath, extractedOgg);

                // Call FFMpeg.AdjustVolume with db change
                FFMpeg.AdjustVolume(extractedOgg, db);

                var scd = ScdFile.Import(fullScdPath);
                if (scd.Audio.Count == 0)
                    throw new InvalidOperationException("No audio entries were found in the SCD.");

                var oldEntry = scd.Audio[0];
                var newEntry = ScdVorbis.ImportOgg(extractedOgg, oldEntry, false);
                scd.Replace(oldEntry, newEntry);

                using var writer = new BinaryWriter(new FileStream(fullScdPath, FileMode.Create, FileAccess.Write, FileShare.None));
                scd.Write(writer);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
        }

        private static void ApplyEqualizerSettingsToSongAudio(Option option, string filterChain)
        {
            string relativeScdPath = Playlist.GetScdPath(option);
            string fullScdPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, relativeScdPath);
            if (!File.Exists(fullScdPath))
                throw new FileNotFoundException("SCD file not found.", fullScdPath);

            string tempRoot = Path.Combine(Path.GetTempPath(), "pickles-equalizer", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            string extractedOgg = Path.Combine(tempRoot, "source.ogg");
            string updatedOgg = Path.Combine(tempRoot, "equalized.ogg");

            try
            {
                ScdOggExtractor.ExtractOgg(fullScdPath, extractedOgg);
                FFMpeg.Equalize(extractedOgg, filterChain);
                bool oldNormalize = Settings.NormalizeVolume;
                Settings.NormalizeVolume = false;
                var scd = ScdFile.Import(fullScdPath);
                Settings.NormalizeVolume = oldNormalize;
                if (scd.Audio.Count == 0)
                    throw new InvalidOperationException("No audio entries were found in the SCD.");

                var oldEntry = scd.Audio[0];
                var newEntry = ScdVorbis.ImportOgg(updatedOgg, oldEntry);
                scd.Replace(oldEntry, newEntry);

                using var writer = new BinaryWriter(new FileStream(fullScdPath, FileMode.Create, FileAccess.Write, FileShare.None));
                scd.Write(writer);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
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



        private async void YtDownloadButton_Click(object? sender, EventArgs e)
        {
            string url = ytUrlTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("Please enter a YouTube URL.");
                return;
            }

            ytDownloadButton.Enabled = false;
            SetProgressBarText("Preparing download...");
            SetProgressBarPercent(5);

            string tempDir = Path.Combine(Path.GetTempPath(), "pickles-ytdlp", Guid.NewGuid().ToString("N"));
            try
            {
                YtDownloadMode selectedMode = GetSelectedYtDownloadMode();
                var result = await YtDlpService.DownloadAudioAsync(url, tempDir, selectedMode, UpdateYtDownloadProgress);
                SetProgressBarPercent(60);

                var filesToImport = result.DownloadedFiles;
                if (Settings.NormalizeVolume)
                {
                    SetProgressBarText("Post-processing audio...");
                    filesToImport = await PostProcessDownloadedFilesAsync(result.DownloadedFiles, true);
                }

                if (result.IsPlaylist)
                {
                    string playlistName = GetUniquePlaylistName(result.Title);
                    Playlist.Create(playlistName, string.Empty, null);
                    var playlists = Playlist.GetAll();
                    playlists[playlistName].Add(filesToImport.ToArray());
                }
                else
                {
                    string targetPlaylist = ResolveTargetPlaylistForSingle();
                    var playlists = Playlist.GetAll();
                    if (!playlists.ContainsKey(targetPlaylist))
                    {
                        Playlist.Create(targetPlaylist, string.Empty, null);
                        playlists = Playlist.GetAll();
                    }
                    bool oldNormalize = Settings.NormalizeVolume;
                    Settings.NormalizeVolume = false;
                    playlists[targetPlaylist].Add(filesToImport.ToArray());
                    Settings.NormalizeVolume = oldNormalize;
                }

                LoadPlaylists();
                SetProgressBarPercent(100);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"YouTube download failed: {ex.Message}");
            }
            finally
            {
                ytDownloadButton.Enabled = true;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        private async Task<List<string>> PostProcessDownloadedFilesAsync(List<string> inputFiles, bool normalize)
        {
            return await Task.Run(() =>
            {
                var outputFiles = new List<string>();
                int index = 0;
                foreach (var file in inputFiles)
                {
                    int current = index + 1;
                    if (normalize)
                        SetProgressBarText($"Normalizing {current}/{inputFiles.Count}");

                    FFMpeg.StripVideo(file);
                    FFMpeg.AdjustVolume(file, 10);
                    outputFiles.Add(file);
                    SetProgressBarPercent(60 + (int)Math.Round((current / (double)inputFiles.Count) * 35));
                }
                return outputFiles;
            });
        }

        private YtDownloadMode GetSelectedYtDownloadMode()
        {
            return ytDownloadModeComboBox.SelectedIndex == 1 ? YtDownloadMode.Playlist : YtDownloadMode.Single;
        }

        private void UpdateYtDownloadProgress(YtDlpProgressInfo info)
        {
            string text = $"{info.Stage} {info.Current}/{info.Total}";
            SetProgressBarText(text);

            int baseProgress = 10;
            int maxProgress = 60;
            double stageProgress = ((info.Current - 1) / (double)Math.Max(1, info.Total));
            if (info.Percent.HasValue)
                stageProgress += (info.Percent.Value / 100.0) / Math.Max(1, info.Total);

            int percent = baseProgress + (int)Math.Round(stageProgress * (maxProgress - baseProgress));
            SetProgressBarPercent(percent);
        }

        private string ResolveTargetPlaylistForSingle()
        {
            var selected = PlaylistTreeView.SelectedNode;
            if (selected != null)
            {
                if (selected.Level == 1) return selected.Name;
                if (selected.Level == 2 && selected.Parent != null) return selected.Parent.Name;
            }
            return "YouTube Singles";
        }

        private string GetUniquePlaylistName(string baseName)
        {
            string candidate = SanitizeFileName(baseName);
            if (string.IsNullOrWhiteSpace(candidate))
                candidate = "YouTube Playlist";

            var existing = Playlist.GetAll();
            if (!existing.ContainsKey(candidate))
                return candidate;

            int idx = 1;
            while (existing.ContainsKey($"{candidate} {idx}"))
                idx++;
            return $"{candidate} {idx}";
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
                            string oldPath = Playlist.GetScdPath(song);
                            string oldDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, oldPath.Substring(0, oldPath.LastIndexOf('\\')));
                            string oldSongName = oldPath.Substring(oldPath.LastIndexOf('\\') + 1, oldPath.Length - oldPath.LastIndexOf('\\')-1);
                            {
                                var scdKey = Playlist.GetScdKey(song) ?? Settings.BaselineScdKey;
                                song.Files[scdKey] = Path.Combine(targetPlaylist.Name, song.Name, oldSongName);
                            }

                            targetPlaylist.Options.Add(song);
                            targetPlaylist.Save();

                            string newDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, targetPlaylist.Name, song.Name);
                            if (!Directory.Exists(newDir))
                                Directory.CreateDirectory(newDir);
                            File.Move(Path.Combine(oldDir, oldSongName), Path.Combine(newDir, oldSongName));

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
                            string oldPath = Playlist.GetScdPath(song);
                            string oldDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, oldPath.Substring(0, oldPath.LastIndexOf('\\')));
                            string oldSongName = oldPath.Substring(oldPath.LastIndexOf('\\') + 1, oldPath.Length - oldPath.LastIndexOf('\\') - 1);
                            {
                                var scdKey = Playlist.GetScdKey(song) ?? Settings.BaselineScdKey;
                                song.Files[scdKey] = Path.Combine(targetPlaylist.Name, song.Name, oldSongName);
                            }

                            targetPlaylist.Options.Insert(index, song);
                            targetPlaylist.Save();

                            string newDir = Path.Combine(Settings.PenumbraLocation, Settings.ModName, targetPlaylist.Name, song.Name);
                            if (!Directory.Exists(newDir))
                                Directory.CreateDirectory(newDir);
                            File.Move(Path.Combine(oldDir, oldSongName), Path.Combine(newDir, oldSongName));

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
            string optPath = Playlist.GetScdPath(opt);
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
    }
}
