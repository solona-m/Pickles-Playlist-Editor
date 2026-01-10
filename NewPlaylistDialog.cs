using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Pickles_Playlist_Editor.Utils;

namespace Pickles_Playlist_Editor
{
    public partial class NewPlaylistForm : Form
    {
        public NewPlaylistForm()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
            folderBrowserDialog.ShowDialog();
            DirectoryPathTextBox.Text = folderBrowserDialog.SelectedPath;
        }

        private void DirectoryPathTextBox_TextChanged(object sender, EventArgs e)
        {
            ValidateFields();
        }   

        private void PlaylistNameTextBox_TextChanged(object sender, EventArgs e)
        {
            ValidateFields();
        }   

        private void ValidateFields()
        {
            GoButton.Enabled = !string.IsNullOrEmpty(PlaylistNameTextBox.Text) && (string.IsNullOrEmpty(DirectoryPathTextBox.Text) || Directory.Exists(DirectoryPathTextBox.Text));
        }

        private void GoButton_Click(object sender, EventArgs e)
        {
            if (!GoButton.Enabled)
                return;
            string playlistName = PlaylistNameTextBox.Text;
            string directory = DirectoryPathTextBox.Text;
            Task.Run(() => DoGo(playlistName, directory));

            this.Close();
        }

        private void SetProgressBarPercent(int value)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<int>(SetProgressBarPercent), value);
                return;
            }
            MainWindow main = (MainWindow)Application.OpenForms["MainWindow"];
            main.SetProgressBarPercent(value);
        }

        private async Task<bool> DoGo(string playlistName, string directory)
        {
            try
            {
                MainWindow main = (MainWindow)Application.OpenForms["MainWindow"];
                main.SetProgressBarText("Importing songs...");
                Playlist.Create(playlistName, directory, SetProgressBarPercent);
                SetProgressBarPercent(0);
                main.LoadPlaylists();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error creating playlist: " + ex.Message);
                Logger.LogError("Error creating playlist: " + ex.ToString());
            }
            return true;
        }
    }
}
