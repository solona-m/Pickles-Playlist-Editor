using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Tools;

namespace Pickles_Playlist_Editor
{
    public sealed partial class MainWindow
    {
        private async void YtDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new YouTubeDownloadDialog { XamlRoot = this.Content.XamlRoot };
            await dlg.ShowAsync();

            var result = dlg.DownloadResult;
            if (result == null || result.DownloadedFiles == null || result.DownloadedFiles.Count == 0)
                return;

            try
            {
                if (result.IsPlaylist)
                {
                    string playlistName = GetUniquePlaylistName(result.Title);
                    await Task.Run(() => Playlist.Create(playlistName, string.Empty, null));
                    var playlists = Playlist.GetAll();
                    if (playlists.TryGetValue(playlistName, out var pl))
                        await Task.Run(() => pl.Add(result.DownloadedFiles.ToArray()));
                }
                else
                {
                    string targetPlaylist = ResolveTargetPlaylistForSingle();
                    var playlists = Playlist.GetAll();
                    if (!playlists.ContainsKey(targetPlaylist))
                    {
                        await Task.Run(() => Playlist.Create(targetPlaylist, string.Empty, null));
                        playlists = Playlist.GetAll();
                    }
                    if (playlists.TryGetValue(targetPlaylist, out var pl))
                        await Task.Run(() => pl.Add(result.DownloadedFiles.ToArray()));
                }

                LoadPlaylists();
                SetProgressBarPercent(100);
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(AppStrings.Dlg_Error, AppStrings.YTAddFailed(ex.Message));
            }
        }

        private string GetUniquePlaylistName(string baseName)
        {
            string candidate = SanitizeFileName(baseName);
            if (string.IsNullOrWhiteSpace(candidate))
                candidate = AppStrings.YT_DefaultPlaylist;

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
