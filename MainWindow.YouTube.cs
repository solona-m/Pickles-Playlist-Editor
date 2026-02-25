using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Tools;

namespace Pickles_Playlist_Editor
{
    public partial class MainWindow : Form
    {
        private void YtDownloadButton_Click(object? sender, EventArgs e)
        {
            using var dlg = new YouTubeDownloadForm();
            dlg.DownloadFinished += result =>
            {
                try
                {
                    var filesToImport = result.DownloadedFiles;
                    if (filesToImport == null || filesToImport.Count == 0) return;

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
                        playlists[targetPlaylist].Add(filesToImport.ToArray());
                    }

                    LoadPlaylists();
                    SetProgressBarPercent(100);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to add downloaded files: {ex.Message}");
                }
            };

            dlg.ShowDialog(this);
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
                    outputFiles.Add(file);
                    SetProgressBarPercent(60 + (int)Math.Round((current / (double)inputFiles.Count) * 35));
                }
                return outputFiles;
            });
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
    }
}