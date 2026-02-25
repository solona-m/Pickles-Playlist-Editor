using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Tools;

namespace Pickles_Playlist_Editor
{
    public partial class MainWindow : Form
    {
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

        private async void ExtractAudioMenuItem_Click(object? sender, EventArgs e)
        {
            var node = _contextMenuNode;
            if (node == null) return;

            var targetSongs = GetSongTargetsForNode(node);
            if (targetSongs.Count == 0)
            {
                MessageBox.Show("No songs were found for extraction.");
                return;
            }

            using var folderDialog = new FolderBrowserDialog { Description = "Choose output folder for extracted OGG files" };
            if (folderDialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(folderDialog.SelectedPath)) return;

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
            if (node == null) return;

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

            if (confirm != DialogResult.Yes) return;

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
            if (node == null) return;

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
            if (node == null) return;

            await OpenEqualizerWorkflowAsync(node);
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

        private async Task OpenEqualizerWorkflowAsync(TreeNode node)
        {
            var targets = GetSongTargetsForNode(node);
            if (targets.Count == 0)
            {
                MessageBox.Show("No songs were found for equalizer processing.");
                return;
            }

            using var form = new EqualizerForm();
            if (form.ShowDialog(this) != DialogResult.OK) return;

            await ApplyEqualizerSettingsToTargetsAsync(targets, form.SelectedSettings);
        }

        private async Task ApplyEqualizerSettingsToTargetsAsync(List<(Playlist playlist, Option option)> targets, EqualizerSettings settings)
        {
            var confirm = MessageBox.Show(
                $"Extract OGG from SCD, apply EQ settings, and repack {targets.Count} song(s)? This will overwrite existing SCD files.",
                "Apply EQ Settings",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

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
                FFMpeg.AdjustVolume(extractedOgg, db);

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

        private static void ApplyEqualizerSettingsToSongAudio(Option option, string filterChain)
        {
            string relativeScdPath = Playlist.GetScdPath(option);
            string fullScdPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, relativeScdPath);
            if (!File.Exists(fullScdPath))
                throw new FileNotFoundException("SCD file not found.", fullScdPath);

            string tempRoot = Path.Combine(Path.GetTempPath(), "pickles-equalizer", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            string extractedOgg = Path.Combine(tempRoot, "source.ogg");

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

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "audio";
            var invalidChars = Path.GetInvalidFileNameChars();
            var cleaned = new string(value.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(cleaned) ? "audio" : cleaned;
        }

        private static string GetUniquePath(string folder, string fileNameNoExt, string extension)
        {
            Directory.CreateDirectory(folder);
            string candidate = Path.Combine(folder, fileNameNoExt + extension);
            if (!File.Exists(candidate)) return candidate;
            int idx = 1;
            while (true)
            {
                candidate = Path.Combine(folder, $"{fileNameNoExt}_{idx}{extension}");
                if (!File.Exists(candidate)) return candidate;
                idx++;
            }
        }
    }
}