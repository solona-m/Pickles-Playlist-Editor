using Microsoft.Windows.ApplicationModel.Resources;

namespace Pickles_Playlist_Editor
{
    internal static class AppStrings
    {
        private static readonly ResourceLoader _r = new ResourceLoader();

        public static string Btn_Yes => _r.GetString("Btn_Yes");
        public static string Btn_No => _r.GetString("Btn_No");
        public static string Btn_Install => _r.GetString("Btn_Install");
        public static string Btn_Later => _r.GetString("Btn_Later");
        public static string Dlg_Error => _r.GetString("Dlg_Error");
        public static string Dlg_NoPlaylist_Title => _r.GetString("Dlg_NoPlaylist_Title");
        public static string Dlg_NoPlaylist_Content => _r.GetString("Dlg_NoPlaylist_Content");
        public static string Dlg_ConfirmDelete_Title => _r.GetString("Dlg_ConfirmDelete_Title");
        public static string Dlg_ConfirmDelete_Content => _r.GetString("Dlg_ConfirmDelete_Content");
        public static string Dlg_NonEmptyPlaylist_Title => _r.GetString("Dlg_NonEmptyPlaylist_Title");
        public static string Dlg_NonEmptyPlaylist_Content => _r.GetString("Dlg_NonEmptyPlaylist_Content");
        public static string Dlg_UpdateAvailable_Title => _r.GetString("Dlg_UpdateAvailable_Title");
        public static string Dlg_ExtractAudio_Title => _r.GetString("Dlg_ExtractAudio_Title");
        public static string Dlg_ExtractAudio_NoSongs => _r.GetString("Dlg_ExtractAudio_NoSongs");
        public static string Summary_ExtractAudio => _r.GetString("Summary_ExtractAudio");
        public static string Dlg_NormalizeAudio_Title => _r.GetString("Dlg_NormalizeAudio_Title");
        public static string Dlg_NoSongs => _r.GetString("Dlg_NoSongs");
        public static string Summary_NormalizeAudio => _r.GetString("Summary_NormalizeAudio");
        public static string Dlg_IncreaseVolume_Title => _r.GetString("Dlg_IncreaseVolume_Title");
        public static string Summary_IncreaseVolume => _r.GetString("Summary_IncreaseVolume");
        public static string Dlg_Equalizer_Title => _r.GetString("Dlg_Equalizer_Title");
        public static string Dlg_ApplyEQ_Title => _r.GetString("Dlg_ApplyEQ_Title");
        public static string Summary_ApplyEQ => _r.GetString("Summary_ApplyEQ");
        public static string Prog_ImportingSongs => _r.GetString("Prog_ImportingSongs");
        public static string Dlg_FileNotFound_Title => _r.GetString("Dlg_FileNotFound_Title");
        public static string Dlg_EnterYouTubeUrl => _r.GetString("Dlg_EnterYouTubeUrl");
        public static string Dlg_UnsupportedUrl => _r.GetString("Dlg_UnsupportedUrl");
        public static string Prog_PreparingDownload => _r.GetString("Prog_PreparingDownload");
        public static string Prog_PostProcessingAudio => _r.GetString("Prog_PostProcessingAudio");
        public static string Prog_Done => _r.GetString("Prog_Done");
        public static string YT_DefaultPlaylist => _r.GetString("YT_DefaultPlaylist");
        public static string SC_DefaultPlaylist => _r.GetString("SC_DefaultPlaylist");
        public static string URL_DefaultPlaylist => _r.GetString("URL_DefaultPlaylist");
        public static string Dlg_YTNoCookies => _r.GetString("Dlg_YTNoCookies");
        public static string Dlg_SCGeoBlocked => _r.GetString("Dlg_SCGeoBlocked");
        public static string Dlg_SCPaidTrack => _r.GetString("Dlg_SCPaidTrack");
        public static string Dlg_SCNotFound => _r.GetString("Dlg_SCNotFound");
        public static string Dlg_SCRateLimited => _r.GetString("Dlg_SCRateLimited");
        public static string Prog_ExtractingAudio => _r.GetString("Prog_ExtractingAudio");
        public static string Prog_NormalizingAudio => _r.GetString("Prog_NormalizingAudio");
        public static string Prog_IncreasingVolume => _r.GetString("Prog_IncreasingVolume");
        public static string Prog_ApplyingEQSettings => _r.GetString("Prog_ApplyingEQSettings");
        public static string Menu_ExtractAudio => _r.GetString("Menu_ExtractAudio");
        public static string Menu_NormalizeAudio => _r.GetString("Menu_NormalizeAudio");
        public static string Menu_IncreaseVolume => _r.GetString("Menu_IncreaseVolume");
        public static string Menu_Rename => _r.GetString("Menu_Rename");
        public static string Dlg_OrganizeLibrary_Title => _r.GetString("Dlg_OrganizeLibrary_Title");
        public static string Dlg_OrganizeLibrary_Content => _r.GetString("Dlg_OrganizeLibrary_Content");
        public static string Dlg_RepairLibrary_Title => _r.GetString("Dlg_RepairLibrary_Title");
        public static string Dlg_RepairLibrary_Content => _r.GetString("Dlg_RepairLibrary_Content");

        // Shown when the configured folder isn't a readable Penumbra mod at all — not for a mod that
        // simply has no playlists yet, and not for one in Penumbra's older layout, which is supported.
        public static string Dlg_ModFolderUnreadable_Title => _r.GetString("Dlg_ModFolderUnreadable_Title");

        public static string ModFolderUnreadableMessage(string modName) =>
            string.Format(_r.GetString("Dlg_ModFolderUnreadable_Content"), modName);
        public static string Menu_ManageEQ => _r.GetString("Menu_ManageEQ");
        public static string Menu_ComputeStats => _r.GetString("Menu_ComputeStats");
        public static string Prog_ComputingStats => _r.GetString("Prog_ComputingStats");
        public static string Summary_ComputeStats => _r.GetString("Summary_ComputeStats");
        public static string Prog_ConvertingToStereo => _r.GetString("Prog_ConvertingToStereo");
        public static string Tree_PlaylistsRoot => _r.GetString("Tree_PlaylistsRoot");
        public static string Dlg_RestartRequired_Title => _r.GetString("Dlg_RestartRequired_Title");
        public static string Dlg_RestartRequired_Content => _r.GetString("Dlg_RestartRequired_Content");

        // ---- sound path rename ----
        // ---- dances -------------------------------------------------------------------------
        public static string Dlg_Dances_Title => _r.GetString("Dlg_Dances_Title");
        public static string Prog_AddingDance => _r.GetString("Prog_AddingDance");
        public static string DanceScanRunning => _r.GetString("Dances_ScanRunning");
        public static string DanceModNoneFound => _r.GetString("Dances_NoModsFound");
        public static string DanceModLegacyLayout => _r.GetString("Dances_LegacyLayout");
        public static string DanceOrderPending => _r.GetString("Dances_OrderPending");
        public static string DanceOrderDone => _r.GetString("Dances_OrderDone");

        public static string DanceCount(int count) =>
            string.Format(_r.GetString("Dances_Count"), count);
        public static string DanceModFound(int count) =>
            string.Format(_r.GetString("Dances_ModsFound"), count);
        public static string DanceModNoGroup(string mod) =>
            string.Format(_r.GetString("Dances_NoGroup"), mod, DanceGroupName);
        public static string DanceModCandidate(string mod, int dances, bool prepped) =>
            string.Format(_r.GetString(prepped ? "Dances_CandidatePrepped" : "Dances_Candidate"), mod, dances);
        public static string DanceSourcesFound(int mods, int dances) =>
            string.Format(_r.GetString("Dances_SourcesFound"), mods, dances);
        public static string DanceBundle(int tracks, int effects) =>
            string.Format(_r.GetString("Dances_Bundle"), tracks, effects);
        public static string AddDancePreview(string oldName, string newName, int sounds, int tracks, int effects) =>
            string.Format(_r.GetString("Dances_AddPreview"), oldName, newName, sounds, tracks, effects);
        public static string AddDanceDone(string dance) =>
            string.Format(_r.GetString("Dances_AddDone"), dance);
        public static string RenameDanceHint(string dance) =>
            string.Format(_r.GetString("Dances_RenameHint"), dance);
        public static string RenameDanceDone(string from, string to) =>
            string.Format(_r.GetString("Dances_RenameDone"), from, to);
        public static string RemoveDanceConfirm(string dance) =>
            string.Format(_r.GetString("Dances_RemoveConfirm"), dance);
        public static string RemoveDanceDone(string dance) =>
            string.Format(_r.GetString("Dances_RemoveDone"), dance);

        /// <summary>
        /// The group name is Penumbra data, not UI text: it must match what is in the mod, so it is
        /// deliberately NOT translated.
        /// </summary>
        private const string DanceGroupName = Utils.DanceMod.DancesGroupName;

        public static string Dlg_SoundPath_Title => _r.GetString("Dlg_SoundPath_Title");
        public static string Prog_RenamingSoundPath => _r.GetString("Prog_RenamingSoundPath");
        public static string SoundPathConfirmFooter => _r.GetString("SoundPath_ConfirmFooter");
        public static string SoundPathNoMod => _r.GetString("SoundPath_NoMod");

        public static string SoundPathNameUnusable => _r.GetString("SoundPath_NameUnusable");

        // Phrased in DJ-NAME characters, not path characters. The bucket is a whole-path limit, and
        // quoting it next to a box labelled "your DJ name" reads as demanding a 16-letter name.
        public static string SoundPathEnterName(int maxName) =>
            string.Format(_r.GetString("SoundPath_EnterName"), maxName);
        public static string SoundPathPreview(string path) =>
            string.Format(_r.GetString("SoundPath_Preview"), path);
        public static string SoundPathPreviewPadded(string path, int minName) =>
            string.Format(_r.GetString("SoundPath_PreviewPadded"), path, minName);
        public static string SoundPathCompanionHeader(string oldPath) =>
            string.Format(_r.GetString("SoundPath_CompanionHeader"), oldPath);
        public static string SoundPathCompanion(string name, int count) =>
            string.Format(_r.GetString("SoundPath_Companion"), name, count);
        public static string SoundPathCompanionHasSongs(string name) =>
            string.Format(_r.GetString("SoundPath_CompanionHasSongs"), name);
        public static string SoundPathConfirmHeader(string oldPath, string newPath) =>
            string.Format(_r.GetString("SoundPath_ConfirmHeader"), oldPath, newPath);
        public static string SoundPathConfirmCounts(string mod, int effects, int songs) =>
            string.Format(_r.GetString("SoundPath_ConfirmCounts"), mod, effects, songs);
        public static string SoundPathConfirmCompanion(string name, int effects) =>
            string.Format(_r.GetString("SoundPath_ConfirmCompanion"), name, effects);
        public static string SoundPathConfirmSuspects(int count) =>
            string.Format(_r.GetString("SoundPath_ConfirmSuspects"), count);
        public static string SoundPathDone(string newPath, int effects, int songs) =>
            string.Format(_r.GetString("SoundPath_Done"), newPath, effects, songs);
        public static string SoundPathDoneCompanions(string names) =>
            string.Format(_r.GetString("SoundPath_DoneCompanions"), names);
        public static string SoundPathBackup(string folder) =>
            string.Format(_r.GetString("SoundPath_Backup"), folder);
        public static string SoundPathKeyOnlyWarning(int count, string oldPath, string newPath) =>
            string.Format(_r.GetString("SoundPath_KeyOnlyWarning"), count, oldPath, newPath);

        public static string ErrorAddingSongs(string msg) => string.Format(_r.GetString("Dlg_ErrorAddingSongs"), msg);
        public static string ErrorDeletion(string msg) => string.Format(_r.GetString("Dlg_ErrorDeletion"), msg);
        public static string UpdateAvailableContent(string version) => string.Format(_r.GetString("Dlg_UpdateAvailable_Content"), version);
        public static string NormalizeConfirm(int count) => string.Format(_r.GetString("Dlg_NormalizeConfirm"), count);
        public static string ApplyEQConfirm(int count) => string.Format(_r.GetString("Dlg_ApplyEQConfirm"), count);
        public static string ApplyingEQ(int current, int total) => string.Format(_r.GetString("Prog_ApplyingEQ"), current, total);
        public static string FileNotFoundContent(string path) => string.Format(_r.GetString("Dlg_FileNotFound_Content"), path);
        public static string ErrorFileDrop(string msg) => string.Format(_r.GetString("Dlg_ErrorFileDrop"), msg);
        public static string ErrorDragDrop(string msg) => string.Format(_r.GetString("Dlg_ErrorDragDrop"), msg);
        public static string PostProcessingFile(string file) => string.Format(_r.GetString("Prog_PostProcessingFile"), file);
        public static string YTDownloadFailed(string msg) => string.Format(_r.GetString("Dlg_YTDownloadFailed"), msg);
        public static string YTAddFailed(string msg) => string.Format(_r.GetString("Dlg_YTAddFailed"), msg);
        public static string ErrorLoadingSong(string song, string playlist, string msg) => string.Format(_r.GetString("Dlg_ErrorLoadingSong"), song, playlist, msg);
        public static string ErrorLoadingPlaylists(string msg) => string.Format(_r.GetString("Dlg_ErrorLoadingPlaylists"), msg);
        public static string Processed(int success, int total) => string.Format(_r.GetString("Dlg_Processed"), success, total);
        public static string ProcessedErrors(string errors) => string.Format(_r.GetString("Dlg_ProcessedErrors"), errors);
        public static string AndMore(int count) => string.Format(_r.GetString("Dlg_AndMore"), count);
    }
}
