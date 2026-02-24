using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Pickles_Playlist_Editor
{
    public partial class SettingsForm : Form
    {
        // Checkbox for normalize volume
        private CheckBox NormalizeVolumeCheckBox;

        public SettingsForm()
        {
            InitializeComponent();

            DirecotryPathTextBox.Text = Path.Combine(Settings.PenumbraLocation ?? string.Empty, Settings.ModName ?? string.Empty);
            BaselineScdTextBox.Text = Settings.BaselineScdKey;

            // Create and place the checkbox (below the BaselineScdTextBox)
            NormalizeVolumeCheckBox = new CheckBox
            {
                Text = "Normalize Volume",
                AutoSize = true,
                Checked = Settings.NormalizeVolume, // connect to stored setting
            };
            // Position it under the BaselineScdTextBox (adjust spacing if needed)
            NormalizeVolumeCheckBox.Location = new Point(BaselineScdTextBox.Left, BaselineScdTextBox.Bottom + 8);
            Controls.Add(NormalizeVolumeCheckBox);
            NormalizeVolumeCheckBox.BringToFront();

            ValidateFields();
        }

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
            folderBrowserDialog.ShowDialog();
            this.DirecotryPathTextBox.Text = folderBrowserDialog.SelectedPath;
            ValidateFields();
        }

        private void ValidateFields()
        {
            bool validDirectory = !string.IsNullOrEmpty(DirecotryPathTextBox.Text) && Directory.Exists(DirecotryPathTextBox.Text) && File.Exists(Path.Combine(DirecotryPathTextBox.Text, "meta.json"));
            bool validScd = !string.IsNullOrWhiteSpace(BaselineScdTextBox.Text) && BaselineScdTextBox.Text.Trim().EndsWith(".scd", StringComparison.OrdinalIgnoreCase);
            OkButton.Enabled = validDirectory && validScd;
        }

        private void DirecotryPathTextBox_TextChanged(object sender, EventArgs e)
        {
            ValidateFields();
        }

        private void BaselineScdTextBox_TextChanged(object sender, EventArgs e)
        {
            ValidateFields();
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            if (OkButton.Enabled)
            {
                string modName = Path.GetFileName(DirecotryPathTextBox.Text);
                string penLocation = DirecotryPathTextBox.Text.Substring(0, DirecotryPathTextBox.Text.Length - modName.Length);
                Settings.ModName = modName;
                Settings.PenumbraLocation = penLocation;
                Settings.BaselineScdKey = BaselineScdTextBox.Text;

                // Save normalize volume setting
                if (NormalizeVolumeCheckBox != null)
                {
                    Settings.NormalizeVolume = NormalizeVolumeCheckBox.Checked;
                }

                this.Close();
            }
        }
    }
}
