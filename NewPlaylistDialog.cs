using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

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
            try
            {
                Playlist.Create(PlaylistNameTextBox.Text, DirectoryPathTextBox.Text);
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error creating playlist: " + ex.Message);
            }
        }
    }
}
