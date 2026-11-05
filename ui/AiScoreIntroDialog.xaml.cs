using Microsoft.UI.Xaml.Controls;

namespace Pickles_Playlist_Editor
{
    /// <summary>The startup AI intro: the Settings hint and checkbox, ticked by default, with OK.</summary>
    public sealed partial class AiScoreIntroDialog : ContentDialog
    {
        public AiScoreIntroDialog()
        {
            this.InitializeComponent();
            Title = AppStrings.Summary_AiScore;
            HintText.Text = AppStrings.AiScoreHint(AiScoreText.DownloadMb);
        }

        internal bool EnableChecked => EnableCheckBox.IsChecked == true;
    }
}
