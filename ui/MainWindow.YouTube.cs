using System;
using System.Collections.Generic;
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
            var dlg = new YouTubeDownloadDialog(ResolveTargetPlaylistForSingle()) { XamlRoot = this.Content.XamlRoot };
            await dlg.ShowAsync();

            var result = dlg.DownloadResult;
            if (result == null || result.DownloadedFiles == null || result.DownloadedFiles.Count == 0)
                return;

            // Held until after the overlay comes down: the error dialog is useless to a
            // user who is still staring at a "Please Wait" spinner behind it.
            string? errorMessage = null;

            try
            {
                string fallbackName = DefaultPlaylistNameFor(result.Service);

                // Importing a downloaded set is minutes of work — encoding each track to
                // SCD plus BPM detection. Report it the same way the drag-and-drop import
                // does, or the window just sits there looking dead.
                SetProgressBarText(AppStrings.Prog_ImportingSongs);
                SetProgressBarPercent(0);

                var dispatcherQueue = _uiDispatcherQueue;
                void ReportProgress(int percent) => dispatcherQueue.TryEnqueue(() => SetProgressBarPercent(percent));

                var toImport = result.DownloadedFiles.ToArray();

                string affectedName;
                if (result.IsPlaylist)
                {
                    string playlistName = GetUniquePlaylistName(result.Title, fallbackName);
                    await Task.Run(() => Playlist.Create(playlistName, string.Empty, null));
                    var playlists = Playlist.GetAll();
                    if (playlists.TryGetValue(playlistName, out var pl))
                        await Task.Run(() => pl.Add(toImport, ReportProgress));
                    affectedName = playlistName;
                }
                else
                {
                    string targetPlaylist = result.TargetPlaylistName ?? ResolveTargetPlaylistForSingle();
                    if (string.IsNullOrWhiteSpace(targetPlaylist))
                        targetPlaylist = fallbackName;

                    var playlists = Playlist.GetAll();
                    if (!playlists.ContainsKey(targetPlaylist))
                    {
                        await Task.Run(() => Playlist.Create(targetPlaylist, string.Empty, null));
                        playlists = Playlist.GetAll();
                    }
                    if (playlists.TryGetValue(targetPlaylist, out var pl))
                        await Task.Run(() => pl.Add(toImport, ReportProgress));
                    affectedName = targetPlaylist;
                }

                // Reflect just the affected playlist instead of rebuilding the whole tree.
                Playlists = Playlist.GetAll();
                if (Playlists.TryGetValue(affectedName, out var affected) && !affected.IsVFXGroup())
                {
                    if (FindPlaylistNode(affectedName) == null) AddNewPlaylistNode(affected);
                    else SyncPlaylistNode(affected);
                }
                else
                {
                    LoadPlaylists();
                }
                SetProgressBarPercent(100);
            }
            catch (Exception ex)
            {
                Logger.LogError("Importing downloaded songs failed: {Error}", ex.Message);
                errorMessage = AppStrings.YTAddFailed(ex.Message);
            }
            finally
            {
                // The busy overlay sets MainContentGrid.IsHitTestVisible = false, so it has
                // to come down on every path. Only the success path used to clear it (via
                // SetProgressBarPercent(100)), which left a failed import with the whole
                // window permanently unclickable behind a spinner.
                ClearProgressDisplay();
                CleanupYtTempFiles(result.DownloadedFiles);
            }

            if (errorMessage != null)
                await ShowDialogAsync(AppStrings.Dlg_Error, errorMessage);
        }

        private static void CleanupYtTempFiles(List<string> files)
        {
            if (files == null || files.Count == 0) return;
            try
            {
                string dir = Path.GetDirectoryName(files[0]);
                if (dir != null && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { }
        }

        /// <summary>
        /// Name for an auto-created playlist when the download itself didn't supply a usable
        /// title. Keeps a YouTube import reading "YouTube Playlist" as it always has.
        /// </summary>
        private static string DefaultPlaylistNameFor(MediaService service) => service switch
        {
            MediaService.YouTube => AppStrings.YT_DefaultPlaylist,
            MediaService.SoundCloud => AppStrings.SC_DefaultPlaylist,
            _ => AppStrings.URL_DefaultPlaylist,
        };

        private string GetUniquePlaylistName(string baseName, string fallbackName)
        {
            string candidate = SanitizeFileName(baseName);
            if (string.IsNullOrWhiteSpace(candidate))
                candidate = fallbackName;

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
