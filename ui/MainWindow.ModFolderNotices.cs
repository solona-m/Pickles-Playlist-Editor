using Microsoft.UI.Xaml;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    /// <summary>
    /// The two notices about the mod folder: Penumbra has stopped accepting reloads, and the folder
    /// lost content that this app did not remove.
    ///
    /// Both are non-modal and dismissible on purpose. A revert and a change the user made themselves
    /// in Penumbra's own UI are indistinguishable on disk, so the wording says what was observed
    /// rather than accusing, and "that was me" has to be one click away. Nothing here restores
    /// anything without being asked — see <see cref="ModFolderGuard"/> for why.
    /// </summary>
    public sealed partial class MainWindow
    {
        private ModFolderGuard.ExternalChange? _pendingRevert;

        // How long after a restore to keep detecting and logging but stop offering the undo. Covers
        // the restore's own settling — including the reload it arms — so putting the playlists back
        // cannot immediately re-raise the bar that asked for it.
        private static readonly TimeSpan RevertQuietWindow = TimeSpan.FromSeconds(90);

        /// <summary>
        /// Subscribes to the guard. Called once from the constructor; the events fire from a timer
        /// thread and a background confirmation, so every handler hops to the UI queue.
        /// </summary>
        private void WireModFolderNotices()
        {
            ModFolderGuard.ExternalChangeDetected += OnExternalChangeDetected;
            Playlist.ReloadPausedChanged += OnReloadPausedChanged;
        }

        private void OnExternalChangeDetected(ModFolderGuard.ExternalChange change)
        {
            if (!_uiDispatcherQueue.HasThreadAccess)
            {
                _uiDispatcherQueue.TryEnqueue(() => OnExternalChangeDetected(change));
                return;
            }

            try
            {
                _pendingRevert = change;
                RevertInfoBar.Title = AppStrings.Revert_Title;
                RevertInfoBar.Message = AppStrings.RevertMessage(
                    change.Before.Playlists, change.Before.Songs,
                    change.Before.TakenAt.ToString("HH:mm"),
                    change.Playlists, change.Songs);
                RevertUndoButton.Content = AppStrings.Revert_UndoAction;
                RevertUndoButton.Visibility = Visibility.Visible;
                RevertInfoBar.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
                RevertInfoBar.IsOpen = true;
            }
            catch (Exception ex)
            {
                Utils.Logger.LogWarn("Could not show the mod folder notice: {Error}", ex.Message);
            }
        }

        private void OnReloadPausedChanged(bool paused)
        {
            if (!_uiDispatcherQueue.HasThreadAccess)
            {
                _uiDispatcherQueue.TryEnqueue(() => OnReloadPausedChanged(paused));
                return;
            }

            try
            {
                if (!paused)
                {
                    ReloadPausedInfoBar.IsOpen = false;
                    return;
                }

                ReloadPausedInfoBar.Title = AppStrings.Reload_PausedTitle;
                ReloadPausedInfoBar.Message = AppStrings.ReloadPausedMessage(
                    ModFolderGuard.FailuresBeforePause,
                    (int)ModFolderGuard.RetryAfterPause.TotalMinutes);
                ReloadPausedInfoBar.IsOpen = true;
            }
            catch (Exception ex)
            {
                Utils.Logger.LogWarn("Could not show the reload notice: {Error}", ex.Message);
            }
        }

        // Dismissing is the user saying "that change was mine". Drop the offer rather than leave a
        // stale one armed; only a fresh detection brings it back.
        private void RevertInfoBar_Closed(Microsoft.UI.Xaml.Controls.InfoBar sender,
            Microsoft.UI.Xaml.Controls.InfoBarClosedEventArgs args)
        {
            _pendingRevert = null;
        }

        private async void RevertUndoButton_Click(object sender, RoutedEventArgs e)
        {
            var change = _pendingRevert;
            if (change == null) return;

            // One-shot. Consumed before any awaiting, so a second click cannot start a second
            // restore against a folder the first one is still writing.
            _pendingRevert = null;
            RevertUndoButton.IsEnabled = false;

            // Detection and logging continue through this; only the offer is suppressed. Set before
            // the restore, because the restore itself changes the folder and arms a reload.
            ModFolderGuard.QuietFor(RevertQuietWindow);

            try
            {
                var entry = await Task.Run(() => PickRestoreTarget(change));
                if (entry == null)
                {
                    RevertInfoBar.Message = AppStrings.Revert_NoBackup;
                    RevertUndoButton.Visibility = Visibility.Collapsed;
                    return;
                }

                List<string>? log = null;
                try
                {
                    await Task.Run(() => { log = Utils.BackupCatalog.Restore(entry); });
                }
                finally
                {
                    // The tree is showing whichever library lost the argument.
                    LoadPlaylists();
                }

                Utils.Logger.LogInfo("Restore after an external change: {Lines}",
                    string.Join(" | ", log ?? new List<string>()));

                RevertInfoBar.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
                RevertInfoBar.Message = AppStrings.RevertRestored(entry.PlaylistCount, entry.SongCount);
                RevertUndoButton.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError("Undoing an external mod folder change failed: {Error}", ex);
                RevertInfoBar.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
                RevertInfoBar.Message = AppStrings.RevertRestoreFailed(ex.Message);
                RevertUndoButton.Visibility = Visibility.Collapsed;
            }
            finally
            {
                RevertUndoButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// The backup to put back: the newest restorable one that holds at least what was lost.
        ///
        /// "At least" rather than "exactly" because the snapshot taken when the reload failed is the
        /// folder as it stood then, which is the state we want — but a restore that put back FEWER
        /// playlists than the user just lost would be its own small disaster, so a smaller set is
        /// never chosen. Runs on a background thread: List parses every group file of every set.
        /// </summary>
        private static Utils.BackupCatalog.BackupEntry? PickRestoreTarget(
            ModFolderGuard.ExternalChange change) =>
            Utils.BackupCatalog.List()
                .FirstOrDefault(e => e.Compatible
                    && e.PlaylistCount >= change.Before.Playlists
                    && e.SongCount >= change.Before.Songs);
    }
}
