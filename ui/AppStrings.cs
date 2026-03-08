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
        public static string Dlg_BPMDetection_Title => _r.GetString("Dlg_BPMDetection_Title");
        public static string Dlg_BPMDetection_Content => _r.GetString("Dlg_BPMDetection_Content");
        public static string Dlg_ConfirmDelete_Title => _r.GetString("Dlg_ConfirmDelete_Title");
        public static string Dlg_ConfirmDelete_Content => _r.GetString("Dlg_ConfirmDelete_Content");
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
        public static string Prog_ComputingDurations => _r.GetString("Prog_ComputingDurations");
        public static string Dlg_FileNotFound_Title => _r.GetString("Dlg_FileNotFound_Title");
        public static string Dlg_EnterYouTubeUrl => _r.GetString("Dlg_EnterYouTubeUrl");
        public static string Prog_PreparingDownload => _r.GetString("Prog_PreparingDownload");
        public static string Prog_PostProcessingAudio => _r.GetString("Prog_PostProcessingAudio");
        public static string Prog_Done => _r.GetString("Prog_Done");
        public static string YT_DefaultPlaylist => _r.GetString("YT_DefaultPlaylist");
        public static string Prog_ExtractingAudio => _r.GetString("Prog_ExtractingAudio");
        public static string Prog_NormalizingAudio => _r.GetString("Prog_NormalizingAudio");
        public static string Prog_IncreasingVolume => _r.GetString("Prog_IncreasingVolume");
        public static string Prog_ApplyingEQSettings => _r.GetString("Prog_ApplyingEQSettings");
        public static string Menu_ExtractAudio => _r.GetString("Menu_ExtractAudio");
        public static string Menu_NormalizeAudio => _r.GetString("Menu_NormalizeAudio");
        public static string Menu_IncreaseVolume => _r.GetString("Menu_IncreaseVolume");
        public static string Menu_Rename => _r.GetString("Menu_Rename");
        public static string Menu_ManageEQ => _r.GetString("Menu_ManageEQ");

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
