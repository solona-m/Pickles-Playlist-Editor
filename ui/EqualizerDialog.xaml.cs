using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Globalization;

namespace Pickles_Playlist_Editor
{
    public sealed partial class EqualizerDialog : ContentDialog
    {
        public EqualizerSettings SelectedSettings { get; } = new EqualizerSettings();

        public EqualizerDialog()
        {
            this.InitializeComponent();
        }

        private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            UpdateSettingsFromSliders();
        }

        private void UpdateSettingsFromSliders()
        {
            SelectedSettings.BassGain = (float)(BassSlider.Value / 10.0);
            SelectedSettings.LowMidGain = (float)(LowMidSlider.Value / 10.0);
            SelectedSettings.MidGain = (float)(MidSlider.Value / 10.0);
            SelectedSettings.HighMidGain = (float)(HighMidSlider.Value / 10.0);
            SelectedSettings.TrebleGain = (float)(TrebleSlider.Value / 10.0);

            BassLabel.Text = FormatGain(BassSlider.Value);
            LowMidLabel.Text = FormatGain(LowMidSlider.Value);
            MidLabel.Text = FormatGain(MidSlider.Value);
            HighMidLabel.Text = FormatGain(HighMidSlider.Value);
            TrebleLabel.Text = FormatGain(TrebleSlider.Value);
        }

        private static string FormatGain(double value) =>
            $"{(value / 10.0).ToString("0.0", CultureInfo.InvariantCulture)} dB";
    }
}
