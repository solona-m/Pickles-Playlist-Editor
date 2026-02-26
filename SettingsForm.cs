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
        public SettingsForm()
        {
            InitializeComponent();

            DirecotryPathTextBox.Text = Path.Combine(Settings.PenumbraLocation ?? string.Empty, Settings.ModName ?? string.Empty);
            BaselineScdTextBox.Text = Settings.BaselineScdKey;

            NormalizeVolumeCheckBox.Checked = Settings.NormalizeVolume;
            autoreloadCheckBox.Checked = Settings.AutoReloadMod;
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

        private void BrowseBaselineScdButton_Click(object sender, EventArgs e)
        {
            using OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "SCD files (*.scd)|*.scd|All files (*.*)|*.*";

            if (Directory.Exists(DirecotryPathTextBox.Text))
                openFileDialog.InitialDirectory = DirecotryPathTextBox.Text;

            if (openFileDialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(openFileDialog.FileName))
                return;

            string selectedPath = openFileDialog.FileName;
            string baselineKey = Path.GetFileName(selectedPath);

            if (Directory.Exists(DirecotryPathTextBox.Text))
            {
                try
                {
                    string relativePath = Path.GetRelativePath(DirecotryPathTextBox.Text, selectedPath);
                    if (!relativePath.StartsWith(".."))
                        baselineKey = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                }
                catch
                {
                    baselineKey = Path.GetFileName(selectedPath);
                }
            }

            BaselineScdTextBox.Text = baselineKey;
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
                Settings.AutoReloadMod = autoreloadCheckBox.Checked;

                this.Close();
            }
        }

        private void OrganizeLibraryButton_Click(object sender, EventArgs e)
        {
            foreach (Playlist playlist in MainWindow.Playlists.Values)
            {
                playlist.Cleanup();
            }
        }
    }
}
