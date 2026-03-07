using Microsoft.UI.Xaml.Controls;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    public sealed class YouTubeDownloadResult
    {
        public List<string> DownloadedFiles { get; set; } = new();
        public bool IsPlaylist { get; init; }
        public string Title { get; init; } = string.Empty;
    }

    public sealed partial class YouTubeDownloadDialog : ContentDialog
    {
        public YouTubeDownloadResult? DownloadResult { get; private set; }

        public YouTubeDownloadDialog()
        {
            this.InitializeComponent();
        }

        private async void DownloadButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var url = UrlTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
            {
                args.Cancel = true;
                StatusLabel.Text = AppStrings.Dlg_EnterYouTubeUrl;
                return;
            }

            var deferral = args.GetDeferral();
            IsPrimaryButtonEnabled = false;
            StatusLabel.Text = AppStrings.Prog_PreparingDownload;
            ProgressBar1.Value = 5;

            var tempDir = Path.Combine(Path.GetTempPath(), "pickles-ytdlp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var mode = ModeComboBox.SelectedIndex == 1 ? YtDownloadMode.Playlist : YtDownloadMode.Single;

                var progress = new Progress<YtDlpProgressInfo>(info =>
                {
                    StatusLabel.Text = $"{info.Stage} {info.Current}/{info.Total}";
                    int baseProgress = 10, maxProgress = 60;
                    double stageProgress = (info.Current - 1) / (double)Math.Max(1, info.Total);
                    if (info.Percent.HasValue)
                        stageProgress += (info.Percent.Value / 100.0) / Math.Max(1, info.Total);
                    var percent = baseProgress + (int)Math.Round(stageProgress * (maxProgress - baseProgress));
                    ProgressBar1.Value = Math.Clamp(percent, 0, 100);
                });

                var dlResult = await YtDlpService.DownloadAudioAsync(url, tempDir, mode,
                    p => ((IProgress<YtDlpProgressInfo>)progress).Report(p));

                DownloadResult = new YouTubeDownloadResult
                {
                    DownloadedFiles = dlResult.DownloadedFiles,
                    IsPlaylist = dlResult.IsPlaylist,
                    Title = dlResult.Title ?? string.Empty,
                };

                ProgressBar1.Value = 60;
                StatusLabel.Text = AppStrings.Prog_PostProcessingAudio;

                if (Settings.NormalizeVolume)
                {
                    var processed = new List<string>();
                    foreach (var file in DownloadResult.DownloadedFiles)
                    {
                        StatusLabel.Text = AppStrings.PostProcessingFile(Path.GetFileName(file));
                        await Task.Run(() => FFMpeg.StripVideo(file));
                        processed.Add(file);
                        ProgressBar1.Value = Math.Clamp(ProgressBar1.Value + 5, 60, 95);
                    }
                    DownloadResult.DownloadedFiles = processed;
                }

                ProgressBar1.Value = 100;
                StatusLabel.Text = AppStrings.Prog_Done;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = AppStrings.YTDownloadFailed(ex.Message);
                IsPrimaryButtonEnabled = true;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
