using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    internal enum AiOptInOutcome { Enabled, AlreadyReady, Declined, Cancelled, Failed, NotFound, LowDisk }

    internal sealed record AiConsent(long Bytes, string Folder, IReadOnlyList<string> Sources, bool Redownload);

    internal sealed record AiOptInReport(AiOptInOutcome Outcome, AiAssetFailure Failure, long BytesNeeded, string Folder);

    /// <summary>What the opt-in needs from the window. Gets data, never text, so the flow is testable without WinUI.</summary>
    internal interface IAiOptInUi
    {
        Task<bool> ConfirmAsync(AiConsent consent);
        Task<AiAssetResult> RunWithProgressAsync(Func<IProgress<AiAssetProgress>, CancellationToken, Task<AiAssetResult>> work);
        Task ShowOutcomeAsync(AiOptInReport report);
    }

    /// <summary>Turning the AI score on: check the files, ask, download with progress and cancel, report.</summary>
    internal static class AiScoreOptIn
    {
        public static async Task<AiOptInOutcome> RunAsync(IAiOptInUi ui, AiAssetDownloadService service, Func<long, string, bool> hasFreeSpace,
            Action<bool> setEnabled, bool redownload)
        {
            string folder = Path.GetDirectoryName(service.PathFor(AiAssets.Model))!;
            bool ready = await Task.Run(() => { using (ThreadPriorityScope.BelowNormal()) return service.IsReady(); });
            if (ready)
            {
                setEnabled(true);
                if (!redownload) await ui.ShowOutcomeAsync(new(AiOptInOutcome.AlreadyReady, AiAssetFailure.None, 0, folder));
                return AiOptInOutcome.AlreadyReady;
            }

            if (!await ui.ConfirmAsync(new AiConsent(AiAssets.TotalBytes, folder, AiAssets.SourceHosts, redownload)))
                return AiOptInOutcome.Declined;

            long needed = AiAssets.TotalBytes + AiAssets.TotalBytes / 10;
            if (!hasFreeSpace(needed, folder))
            {
                await ui.ShowOutcomeAsync(new(AiOptInOutcome.LowDisk, AiAssetFailure.Disk, needed, folder));
                return AiOptInOutcome.LowDisk;
            }

            AiAssetResult result;
            try { result = await ui.RunWithProgressAsync((progress, ct) => service.EnsureAsync(progress, ct)); }
            catch (OperationCanceledException)
            {
                // Only reached after the download task has finished: the progress UI awaits it before returning.
                await ui.ShowOutcomeAsync(new(AiOptInOutcome.Cancelled, AiAssetFailure.None, 0, folder));
                return AiOptInOutcome.Cancelled;
            }

            if (result.Ok)
            {
                setEnabled(true);
                if (!redownload) await ui.ShowOutcomeAsync(new(AiOptInOutcome.Enabled, AiAssetFailure.None, 0, folder));
                return AiOptInOutcome.Enabled;
            }
            Logger.LogWarn("AI score download failed: {Detail}", result.Detail);
            var outcome = result.Failure == AiAssetFailure.NotFound ? AiOptInOutcome.NotFound : AiOptInOutcome.Failed;
            await ui.ShowOutcomeAsync(new(outcome, result.Failure, 0, folder));
            return outcome;
        }

        public static bool HasFreeSpace(long bytes, string folder)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                return string.IsNullOrEmpty(root) || new DriveInfo(root).AvailableFreeSpace >= bytes;
            }
            catch { return true; } // unknown: let the download find out
        }
    }

    /// <summary>Passes on at most one report per interval, and always the final one.</summary>
    internal sealed class ThrottledProgress<T>(IProgress<T> inner, Func<T, bool> isFinal, TimeSpan interval, Func<long>? clockMs = null) : IProgress<T>
    {
        private readonly Func<long> _clock = clockMs ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        private long _last = long.MinValue;

        public void Report(T value)
        {
            long now = _clock();
            if (!isFinal(value) && _last != long.MinValue && now - Interlocked.Read(ref _last) < (long)interval.TotalMilliseconds) return;
            Interlocked.Exchange(ref _last, now);
            inner.Report(value);
        }
    }

    /// <summary>The opt-in's dialogs. Each show goes through the window's dialog gate, one dialog at a time.</summary>
    internal sealed class ContentDialogOptInUi(Func<XamlRoot> xamlRoot, Func<ContentDialog, Task<ContentDialogResult>> showGated) : IAiOptInUi
    {
        private static long Mb(long bytes) => (bytes + 999_999) / 1_000_000;

        public async Task<bool> ConfirmAsync(AiConsent c)
        {
            var dialog = new ContentDialog
            {
                Title = AppStrings.AiScore_Consent_Title,
                Content = new TextBlock
                {
                    Text = c.Redownload ? AppStrings.AiScoreRedownload(Mb(c.Bytes)) : AppStrings.AiScoreConsent(Mb(c.Bytes), c.Folder, string.Join(", ", c.Sources)),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = AppStrings.AiScore_Btn_Download,
                CloseButtonText = AppStrings.Btn_Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot(),
            };
            return await showGated(dialog) == ContentDialogResult.Primary;
        }

        public async Task<AiAssetResult> RunWithProgressAsync(Func<IProgress<AiAssetProgress>, CancellationToken, Task<AiAssetResult>> work)
        {
            var text = new TextBlock { Text = AppStrings.AiScore_Checking, TextWrapping = TextWrapping.Wrap };
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 12, 0, 0) };
            var panel = new StackPanel();
            panel.Children.Add(text);
            panel.Children.Add(bar);
            var dialog = new ContentDialog
            {
                Title = AppStrings.AiScore_Downloading_Title,
                Content = panel,
                CloseButtonText = AppStrings.Btn_Cancel,
                XamlRoot = xamlRoot(),
            };
            using var cts = new CancellationTokenSource();
            Task<AiAssetResult>? running = null;
            bool closing = false;
            dialog.CloseButtonClick += (_, args) =>
            {
                if (closing) return;
                args.Cancel = true; // stay open until the download has actually stopped
                cts.Cancel();
                dialog.IsSecondaryButtonEnabled = false;
                dialog.CloseButtonText = "";
                text.Text = AppStrings.AiScore_Cancelling;
            };
            var progress = new ThrottledProgress<AiAssetProgress>(new Progress<AiAssetProgress>(p =>
            {
                if (cts.IsCancellationRequested) return;
                text.Text = AppStrings.AiScoreProgress(p.FileName, p.FileIndex + 1, p.FileCount, Mb(p.BytesDone), Mb(p.BytesTotal));
                bar.Value = p.BytesTotal == 0 ? 0 : 100.0 * p.BytesDone / p.BytesTotal;
            }), p => p.BytesDone >= p.BytesTotal, TimeSpan.FromMilliseconds(100));

            dialog.Opened += (_, _) => running ??= work(progress, cts.Token);
            var shown = showGated(dialog);
            while (running == null && !shown.IsCompleted) await Task.Delay(20);
            if (running == null) return new(false, AiAssetFailure.None, null, "the progress dialog could not open"); // collided with another dialog
            try { return await running; }
            finally
            {
                closing = true;
                dialog.Hide();
                await shown;
            }
        }

        public async Task ShowOutcomeAsync(AiOptInReport r)
        {
            string content = r.Outcome switch
            {
                AiOptInOutcome.Enabled or AiOptInOutcome.AlreadyReady => AppStrings.AiScore_Ready,
                AiOptInOutcome.LowDisk => AppStrings.AiScoreLowDisk(Mb(r.BytesNeeded), r.Folder),
                AiOptInOutcome.NotFound => AppStrings.Get("AiScore_Fail_NotFound"),
                _ => AppStrings.AiScoreDownloadFailed(AppStrings.Get(AiScoreText.FailureKey(r.Outcome, r.Failure) ?? "AiScore_Fail_Http")),
            };
            var dialog = new ContentDialog
            {
                Title = AppStrings.Summary_AiScore,
                Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
                XamlRoot = xamlRoot(),
            };
            await showGated(dialog);
        }
    }
}
