using Microsoft.UI.Xaml.Controls;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VfxEditor.ScdFormat;
using VfxEditor.ScdFormat.Music.Data;

namespace Pickles_Playlist_Editor
{
    public sealed partial class MainWindow
    {
        private MenuFlyout BuildContextMenu()
        {
            var flyout = new MenuFlyout();
            var extract = new MenuFlyoutItem { Text = AppStrings.Menu_ExtractAudio };
            extract.Click += ExtractAudioMenuItem_Click;
            var normalize = new MenuFlyoutItem { Text = AppStrings.Menu_NormalizeAudio };
            normalize.Click += NormalizeAudioMenuItem_Click;
            var rename = new MenuFlyoutItem { Text = AppStrings.Menu_Rename };
            rename.Click += RenameMenuItem_Click;
            flyout.Items.Add(rename);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(extract);
            flyout.Items.Add(normalize);
            flyout.Items.Add(new MenuFlyoutSeparator());
            var eq = new MenuFlyoutItem { Text = AppStrings.Menu_ManageEQ };
            eq.Click += ApplyEqSettingsMenuItem_Click;
            flyout.Items.Add(eq);
            flyout.Items.Add(new MenuFlyoutSeparator());
            var computeStats = new MenuFlyoutItem { Text = AppStrings.Menu_ComputeStats };
            computeStats.Click += ComputeStatsMenuItem_Click;
            flyout.Items.Add(computeStats);
            return flyout;
        }

        private MenuFlyout BuildRootContextMenu()
        {
            var flyout = new MenuFlyout();
            var normalize = new MenuFlyoutItem { Text = AppStrings.Menu_NormalizeAudio };
            normalize.Click += NormalizeAudioMenuItem_Click;
            flyout.Items.Add(normalize);
            var computeStats = new MenuFlyoutItem { Text = AppStrings.Menu_ComputeStats };
            computeStats.Click += ComputeStatsMenuItem_Click;
            flyout.Items.Add(computeStats);
            return flyout;
        }

        private async void RenameMenuItem_Click(object sender, object e)
        {
            var node = _contextMenuNode;
            if (node == null || (node.Level != 1 && node.Level != 2))
                return;

            await RenameNodeAsync(node);
        }

        private async void ExtractAudioMenuItem_Click(object sender, object e)
        {
            var node = _contextMenuNode;
            if (node == null) return;
            var targetSongs = GetSongTargetsForNode(node);
            if (targetSongs.Count == 0) { await ShowDialogAsync(AppStrings.Dlg_ExtractAudio_Title, AppStrings.Dlg_ExtractAudio_NoSongs); return; }
            string? outputFolder = await PickFolderAsync("Choose output folder for extracted OGG files");
            if (string.IsNullOrWhiteSpace(outputFolder)) return;
            SetProgressBarText(AppStrings.Prog_ExtractingAudio);
            SetProgressBarPercent(0);
            int extracted = 0;
            var errors = new List<string>();
            await Task.Run(() =>
            {
                foreach (var (playlist, option) in targetSongs)
                {
                    try
                    {
                        string outputDir = node.Level == 1
                            ? Path.Combine(outputFolder, SanitizeFileName(playlist.Name))
                            : outputFolder;
                        string outPath = GetUniquePath(outputDir, SanitizeFileName(option.Name), ".ogg");
                        string fullScdPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, Playlist.GetScdPath(option));
                        ScdOggExtractor.ExtractOgg(fullScdPath, outPath);
                        extracted++;
                    }
                    catch (Exception ex) { errors.Add($"{playlist.Name}/{option.Name}: {ex.Message}"); }
                    finally { SetProgressBarPercent((int)((extracted + errors.Count) / (double)targetSongs.Count * 100)); }
                }
            });
            SetProgressBarPercent(100);
            ShowOperationSummary(AppStrings.Summary_ExtractAudio, extracted, targetSongs.Count, errors);
        }

        private async void NormalizeAudioMenuItem_Click(object sender, object e)
        {
            var node = _contextMenuNode;
            if (node == null) return;
            var targetSongs = GetSongTargetsForNode(node);
            if (targetSongs.Count == 0) { await ShowDialogAsync(AppStrings.Dlg_NormalizeAudio_Title, AppStrings.Dlg_NoSongs); return; }
            var confirm = await ShowDialogAsync(AppStrings.Dlg_NormalizeAudio_Title,
                AppStrings.NormalizeConfirm(targetSongs.Count),
                AppStrings.Btn_Yes, null, AppStrings.Btn_No);
            if (confirm != ContentDialogResult.Primary) return;
            SetProgressBarText(AppStrings.Prog_NormalizingAudio);
            SetProgressBarPercent(0);
            int normalized = 0;
            var errors = new List<string>();
            await Task.Run(() =>
            {
                foreach (var (playlist, option) in targetSongs)
                {
                    try { NormalizeSongAudio(option); normalized++; }
                    catch (Exception ex) { errors.Add($"{playlist.Name}/{option.Name}: {ex.Message}"); }
                    finally { SetProgressBarPercent((int)((normalized + errors.Count) / (double)targetSongs.Count * 100)); }
                }
            });
            SetProgressBarPercent(100);
            RecomputePlaylistDurations();
            ShowOperationSummary(AppStrings.Summary_NormalizeAudio, normalized, targetSongs.Count, errors);
        }

        private async void ApplyEqSettingsMenuItem_Click(object sender, object e)
        {
            var node = _contextMenuNode;
            if (node == null) return;
            await OpenEqualizerWorkflowAsync(node);
        }

        private async void ComputeStatsMenuItem_Click(object sender, object e)
        {
            var node = _contextMenuNode;
            if (node == null) return;
            var targetSongs = GetSongTargetsForNode(node);
            if (targetSongs.Count == 0) { await ShowDialogAsync(AppStrings.Menu_ComputeStats, AppStrings.Dlg_NoSongs); return; }
            SetProgressBarText(AppStrings.Prog_ComputingStats);
            SetProgressBarPercent(0);
            int processed = 0;
            var errors = new List<string>();
            var touchedPlaylists = new HashSet<Playlist>();
            await Task.Run(() =>
            {
                foreach (var (playlist, option) in targetSongs)
                {
                    try
                    {
                        string scdPath = Playlist.GetScdPath(option);
                        int bpm = BPMDetector.GetBPMFromSCD(scdPath);
                        string key = KeyDetector.GetKeyFromSCD(scdPath);
                        TimeSpan duration = BPMDetector.GetDuration(scdPath);
                        OptionStatsNaming.UpdateName(option, bpm, key, duration);
                        touchedPlaylists.Add(playlist);
                        processed++;
                    }
                    catch (Exception ex) { errors.Add($"{playlist.Name}/{option.Name}: {ex.Message}"); }
                    finally { SetProgressBarPercent((int)((processed + errors.Count) / (double)targetSongs.Count * 100)); }
                }
                foreach (var playlist in touchedPlaylists)
                {
                    // One unsaveable playlist must not abort the whole batch (or escape this
                    // Task.Run into an async void handler and kill the app) — report it and
                    // carry on with the rest.
                    try { playlist.Save(); }
                    catch (Exception ex) { errors.Add($"{playlist.Name}: {ex.Message}"); }
                }
            });
            SetProgressBarPercent(100);
            // Song names changed (stats baked in) — refresh only the touched playlists' songs.
            foreach (var playlist in touchedPlaylists)
                SyncPlaylistNode(playlist);
            ShowOperationSummary(AppStrings.Summary_ComputeStats, processed, targetSongs.Count, errors);
        }

        private List<(Playlist playlist, Option option)> GetSongTargetsForNode(PlaylistNodeContent node)
        {
            var results = new List<(Playlist, Option)>();
            if (node.Level == 2 && node.Parent != null)
            {
                if (Playlists.TryGetValue(node.Parent.Name, out var pl))
                {
                    var opt = pl.Options.FirstOrDefault(x => x.Name == node.Name);
                    if (opt != null && !string.IsNullOrEmpty(Playlist.GetScdPath(opt)))
                        results.Add((pl, opt));
                }
                return results;
            }
            if (node.Level == 1 && Playlists.TryGetValue(node.Name, out var playlist))
            {
                foreach (var opt in playlist.Options)
                    if (!string.IsNullOrEmpty(Playlist.GetScdPath(opt)))
                        results.Add((playlist, opt));
                return results;
            }
            if (node.Level == 0)
                foreach (var pl in Playlists.Values)
                    foreach (var opt in pl.Options)
                        if (!string.IsNullOrEmpty(Playlist.GetScdPath(opt)))
                            results.Add((pl, opt));
            return results;
        }

        private async Task OpenEqualizerWorkflowAsync(PlaylistNodeContent node)
        {
            var targets = GetSongTargetsForNode(node);
            if (targets.Count == 0) { await ShowDialogAsync(AppStrings.Dlg_Equalizer_Title, AppStrings.Dlg_NoSongs); return; }
            var dialog = new EqualizerDialog { XamlRoot = this.Content.XamlRoot };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
            await ApplyEqualizerSettingsToTargetsAsync(targets, dialog.SelectedSettings);
        }

        private async Task ApplyEqualizerSettingsToTargetsAsync(
            List<(Playlist playlist, Option option)> targets, EqualizerSettings settings)
        {
            var confirm = await ShowDialogAsync(AppStrings.Dlg_ApplyEQ_Title,
                AppStrings.ApplyEQConfirm(targets.Count),
                AppStrings.Btn_Yes, null, AppStrings.Btn_No);
            if (confirm != ContentDialogResult.Primary) return;
            SetProgressBarText(AppStrings.Prog_ApplyingEQSettings);
            SetProgressBarPercent(0);
            int updated = 0;
            var errors = new List<string>();
            string filterChain = settings.ToFilterChain();
            await Task.Run(() =>
            {
                int total = targets.Count, current = 0;
                foreach (var (playlist, option) in targets)
                {
                    current++;
                    try
                    {
                        SetProgressBarText(AppStrings.ApplyingEQ(current, total));
                        ApplyEqualizerSettingsToSongAudio(option, filterChain);
                        updated++;
                    }
                    catch (Exception ex) { errors.Add($"{playlist.Name}/{option.Name}: {ex.Message}"); }
                    finally { SetProgressBarPercent((int)((updated + errors.Count) / (double)total * 100)); }
                }
            });
            SetProgressBarPercent(100);
            RecomputePlaylistDurations();
            ShowOperationSummary(AppStrings.Summary_ApplyEQ, updated, targets.Count, errors);
        }

        private static void NormalizeSongAudio(Option option)
        {
            string fullScdPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, Playlist.GetScdPath(option));
            if (!File.Exists(fullScdPath)) throw new FileNotFoundException("SCD not found.", fullScdPath);
            string tempRoot = Path.Combine(Path.GetTempPath(), "pickles-normalize", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            string ogg = Path.Combine(tempRoot, "source.ogg");
            try
            {
                ScdOggExtractor.ExtractOgg(fullScdPath, ogg);
                // Normalize explicitly so the right-click action works regardless of the
                // "Normalize volume" setting, then suppress ImportOgg's implicit pass so
                // we don't normalize twice when that setting happens to be enabled.
                FFMpeg.NormalizeVolume(ogg);
                bool oldNorm = Settings.NormalizeVolume;
                Settings.NormalizeVolume = false;
                try
                {
                    var scd = ScdFile.Import(fullScdPath);
                    if (scd.Audio.Count == 0) throw new InvalidOperationException("No audio entries in SCD.");
                    var old = scd.Audio[0];
                    scd.Replace(old, ScdVorbis.ImportOgg(ogg, old));
                    using var w = new BinaryWriter(new FileStream(fullScdPath, FileMode.Create, FileAccess.Write, FileShare.None));
                    scd.Write(w);
                }
                finally { Settings.NormalizeVolume = oldNorm; }
            }
            finally { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
        }

        private static void ApplyEqualizerSettingsToSongAudio(Option option, string filterChain)
        {
            string fullScdPath = Path.Combine(Settings.PenumbraLocation, Settings.ModName, Playlist.GetScdPath(option));
            if (!File.Exists(fullScdPath)) throw new FileNotFoundException("SCD not found.", fullScdPath);
            string tempRoot = Path.Combine(Path.GetTempPath(), "pickles-equalizer", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            string ogg = Path.Combine(tempRoot, "source.ogg");
            try
            {
                ScdOggExtractor.ExtractOgg(fullScdPath, ogg);
                FFMpeg.Equalize(ogg, filterChain);
                bool oldNorm = Settings.NormalizeVolume;
                Settings.NormalizeVolume = false;
                var scd = ScdFile.Import(fullScdPath);
                Settings.NormalizeVolume = oldNorm;
                if (scd.Audio.Count == 0) throw new InvalidOperationException("No audio entries in SCD.");
                var old = scd.Audio[0];
                scd.Replace(old, ScdVorbis.ImportOgg(ogg, old));
                using var w = new BinaryWriter(new FileStream(fullScdPath, FileMode.Create, FileAccess.Write, FileShare.None));
                scd.Write(w);
            }
            finally { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "audio";
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
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
