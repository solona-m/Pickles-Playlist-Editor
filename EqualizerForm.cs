using System.Globalization;

namespace Pickles_Playlist_Editor
{
    internal sealed class EqualizerForm : Form
    {
        private readonly TrackBar _bassTrackBar;
        private readonly TrackBar _lowMidTrackBar;
        private readonly TrackBar _midTrackBar;
        private readonly TrackBar _highMidTrackBar;
        private readonly TrackBar _trebleTrackBar;
        private readonly Label _bassValueLabel;
        private readonly Label _lowMidValueLabel;
        private readonly Label _midValueLabel;
        private readonly Label _highMidValueLabel;
        private readonly Label _trebleValueLabel;

        public EqualizerSettings SelectedSettings { get; } = new EqualizerSettings();

        public EqualizerForm()
        {
            Text = "Equalizer";
            Width = 700;
            Height = 520;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 8,
                Padding = new Padding(10)
            };

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 0; i < 5; i++)
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var helpLabel = new Label
            {
                Text = "Adjust EQ settings, then test playback in the app.",
                Dock = DockStyle.Fill,
                AutoSize = true
            };
            root.Controls.Add(helpLabel, 0, 0);

            (_bassTrackBar, _bassValueLabel) = AddSliderRow(root, 1, "Bass (64 Hz)");
            (_lowMidTrackBar, _lowMidValueLabel) = AddSliderRow(root, 2, "Low Mid (250 Hz)");
            (_midTrackBar, _midValueLabel) = AddSliderRow(root, 3, "Mid (1 kHz)");
            (_highMidTrackBar, _highMidValueLabel) = AddSliderRow(root, 4, "High Mid (4 kHz)");
            (_trebleTrackBar, _trebleValueLabel) = AddSliderRow(root, 5, "Treble (12 kHz)");

            var actionButtonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Anchor = AnchorStyles.None
            };

            var applyButton = new Button { Text = "Apply", AutoSize = true };
            applyButton.Click += (_, _) =>
            {
                UpdateSettingsFromSliders();
                DialogResult = DialogResult.OK;
                Close();
            };

            var discardButton = new Button { Text = "Discard", AutoSize = true };
            discardButton.Click += (_, _) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            _bassTrackBar.ValueChanged += RealtimeEq_ValueChanged;
            _lowMidTrackBar.ValueChanged += RealtimeEq_ValueChanged;
            _midTrackBar.ValueChanged += RealtimeEq_ValueChanged;
            _highMidTrackBar.ValueChanged += RealtimeEq_ValueChanged;
            _trebleTrackBar.ValueChanged += RealtimeEq_ValueChanged;

            actionButtonPanel.Controls.Add(applyButton);
            actionButtonPanel.Controls.Add(discardButton);

            root.Controls.Add(actionButtonPanel, 0, 7);
            Controls.Add(root);

            SyncLabels();
        }

        private void RealtimeEq_ValueChanged(object? sender, EventArgs e)
        {
            UpdateSettingsFromSliders();
        }

        private static (TrackBar trackBar, Label valueLabel) AddSliderRow(TableLayoutPanel root, int row, string labelText)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 3,
                AutoSize = true
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));

            var label = new Label { Text = labelText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            var trackBar = new TrackBar
            {
                Minimum = -120,
                Maximum = 120,
                TickFrequency = 10,
                LargeChange = 10,
                SmallChange = 1,
                Value = 0,
                Dock = DockStyle.Fill
            };
            var valueLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };

            trackBar.ValueChanged += (_, _) =>
            {
                float gain = trackBar.Value / 10f;
                valueLabel.Text = $"{gain.ToString("0.0", CultureInfo.InvariantCulture)} dB";
            };

            panel.Controls.Add(label, 0, 0);
            panel.Controls.Add(trackBar, 1, 0);
            panel.Controls.Add(valueLabel, 2, 0);

            root.Controls.Add(panel, 0, row);
            return (trackBar, valueLabel);
        }

        private void SyncLabels()
        {
            _bassValueLabel.Text = FormatGain(_bassTrackBar.Value);
            _lowMidValueLabel.Text = FormatGain(_lowMidTrackBar.Value);
            _midValueLabel.Text = FormatGain(_midTrackBar.Value);
            _highMidValueLabel.Text = FormatGain(_highMidTrackBar.Value);
            _trebleValueLabel.Text = FormatGain(_trebleTrackBar.Value);
        }

        private static string FormatGain(int value)
        {
            return $"{(value / 10f).ToString("0.0", CultureInfo.InvariantCulture)} dB";
        }

        private void UpdateSettingsFromSliders()
        {
            SelectedSettings.BassGain = _bassTrackBar.Value / 10f;
            SelectedSettings.LowMidGain = _lowMidTrackBar.Value / 10f;
            SelectedSettings.MidGain = _midTrackBar.Value / 10f;
            SelectedSettings.HighMidGain = _highMidTrackBar.Value / 10f;
            SelectedSettings.TrebleGain = _trebleTrackBar.Value / 10f;

            _bassValueLabel.Text = FormatGain(_bassTrackBar.Value);
            _lowMidValueLabel.Text = FormatGain(_lowMidTrackBar.Value);
            _midValueLabel.Text = FormatGain(_midTrackBar.Value);
            _highMidValueLabel.Text = FormatGain(_highMidTrackBar.Value);
            _trebleValueLabel.Text = FormatGain(_trebleTrackBar.Value);
        }
    }
}
