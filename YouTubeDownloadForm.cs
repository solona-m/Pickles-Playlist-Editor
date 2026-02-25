using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;

namespace Pickles_Playlist_Editor
{
    public sealed class YouTubeDownloadResult
    {
        public List<string> DownloadedFiles { get; set; }
        public bool IsPlaylist { get; init; }
        public string Title { get; init; } = string.Empty;
    }

    public partial class YouTubeDownloadForm : Form
    {
        private readonly TextBox _urlText;
        private readonly ComboBox _mode;
        private readonly Button _downloadButton;
        private readonly ProgressBar _progress;
        private readonly Label _status;
        private readonly Button _cancel;

        public event Action<YouTubeDownloadResult>? DownloadFinished;

        public YouTubeDownloadForm()
        {
            Text = "Add Songs From YouTube";
            Width = 520;
            Height = 180;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var urlLabel = new Label { Text = "YouTube URL:", Left = 10, Top = 12, Width = 80 };
            _urlText = new TextBox { Left = 100, Top = 8, Width = 390 };

            var modeLabel = new Label { Text = "Mode:", Left = 10, Top = 44, Width = 80 };
            _mode = new ComboBox { Left = 100, Top = 40, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            _mode.Items.AddRange(new object[] { "Single Track", "Playlist" });
            _mode.SelectedIndex = 0;

            _downloadButton = new Button { Text = "Download", Left = 320, Top = 38, Width = 85 };
            _downloadButton.Click += DownloadButton_Click;

            _progress = new ProgressBar { Left = 10, Top = 76, Width = 480, Height = 18 };
            _status = new Label { Left = 10, Top = 98, Width = 480, Height = 24, Text = "" };

            _cancel = new Button { Text = "Close", Left = 410, Top = 116, Width = 80 };
            _cancel.Click += (s, e) => Close();

            Controls.AddRange(new Control[] { urlLabel, _urlText, modeLabel, _mode, _downloadButton, _progress, _status, _cancel });
        }

        private async void DownloadButton_Click(object? sender, EventArgs e)
        {
            var url = _urlText.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show(this, "Please enter a YouTube URL.", "Input required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _downloadButton.Enabled = false;
            _status.Text = "Preparing download...";
            _progress.Value = 5;

            var tempDir = Path.Combine(Path.GetTempPath(), "pickles-ytdlp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var mode = _mode.SelectedIndex == 1 ? YtDownloadMode.Playlist : YtDownloadMode.Single;

                var progress = new Progress<YtDlpProgressInfo>(info =>
                {
                    _status.Text = $"{info.Stage} {info.Current}/{info.Total}";
                    int baseProgress = 10, maxProgress = 60;
                    double stageProgress = ((info.Current - 1) / (double)Math.Max(1, info.Total));
                    if (info.Percent.HasValue) stageProgress += (info.Percent.Value / 100.0) / Math.Max(1, info.Total);
                    var percent = baseProgress + (int)Math.Round(stageProgress * (maxProgress - baseProgress));
                    _progress.Value = Math.Clamp(percent, 0, 100);
                });

                var dlResult = await YtDlpService.DownloadAudioAsync(url, tempDir, mode, p => ((IProgress<YtDlpProgressInfo>)progress).Report(p));

                YouTubeDownloadResult result = new YouTubeDownloadResult
                {
                    DownloadedFiles = dlResult.DownloadedFiles,
                    IsPlaylist = dlResult.IsPlaylist,
                    Title = dlResult.Title ?? string.Empty,
                };

                _progress.Value = 60;
                _status.Text = "Post-processing audio...";

                if (Settings.NormalizeVolume)
                {
                    // Post-process (strip video + adjust volume) - inline copy of main logic
                    var processed = new List<string>();
                    foreach (var file in result.DownloadedFiles)
                    {
                        _status.Text = $"Post-processing {Path.GetFileName(file)}";
                        FFMpeg.StripVideo(file);
                        FFMpeg.AdjustVolume(file, 10);
                        processed.Add(file);
                        // update progress simply
                        _progress.Value = Math.Clamp(_progress.Value + 5, 60, 95);
                        await Task.Delay(50);
                    }
                    result.DownloadedFiles = processed;
                }

                _progress.Value = 100;
                _status.Text = "Done";

                DownloadFinished?.Invoke(result);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"YouTube download failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _downloadButton.Enabled = true;
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}