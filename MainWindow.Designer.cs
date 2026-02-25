using AutoUpdaterDotNET;

namespace Pickles_Playlist_Editor
{
    partial class MainWindow
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainWindow));
            panel1 = new Panel();
            tableLayoutPanel1 = new TableLayoutPanel();
            searchLayoutPanel = new TableLayoutPanel();
            searchTextBox = new TextBox();
            searchLabel = new Label();
            PlaylistTreeView = new TreeView();
            progressBar1 = new CustomProgressBar();
            toolStrip1 = new ToolStrip();
            AddSongsDropDownButton = new ToolStripDropDownButton();
            fromMyComputerToolStripMenuItem = new ToolStripMenuItem();
            fromYouTubeToolStripMenuItem = new ToolStripMenuItem();
            NewButton = new ToolStripButton();
            DeleteButton = new ToolStripButton();
            SettingsButton = new ToolStripButton();
            ShuffleButton = new ToolStripButton();
            SortByBPM = new ToolStripButton();
            PlayButton = new ToolStripButton();
            PauseButton = new ToolStripButton();
            StopIcon = new ToolStripButton();
            previousButton = new ToolStripButton();
            nextButton = new ToolStripButton();
            panel1.SuspendLayout();
            tableLayoutPanel1.SuspendLayout();
            searchLayoutPanel.SuspendLayout();
            toolStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // panel1
            // 
            panel1.Controls.Add(tableLayoutPanel1);
            panel1.Controls.Add(toolStrip1);
            panel1.Dock = DockStyle.Fill;
            panel1.Location = new Point(0, 0);
            panel1.Name = "panel1";
            panel1.Size = new Size(1075, 644);
            panel1.TabIndex = 0;
            panel1.Paint += panel1_Paint;
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.ColumnCount = 1;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.Controls.Add(searchLayoutPanel, 0, 1);
            tableLayoutPanel1.Controls.Add(PlaylistTreeView, 0, 2);
            tableLayoutPanel1.Controls.Add(progressBar1, 0, 3);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Location = new Point(0, 25);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 4;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 8F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            tableLayoutPanel1.Size = new Size(1075, 619);
            tableLayoutPanel1.TabIndex = 2;
            // 
            // searchLayoutPanel
            // 
            searchLayoutPanel.ColumnCount = 2;
            searchLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60F));
            searchLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            searchLayoutPanel.Controls.Add(searchTextBox, 1, 0);
            searchLayoutPanel.Controls.Add(searchLabel, 0, 0);
            searchLayoutPanel.Dock = DockStyle.Fill;
            searchLayoutPanel.Location = new Point(3, 11);
            searchLayoutPanel.Name = "searchLayoutPanel";
            searchLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            searchLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            searchLayoutPanel.Size = new Size(1069, 30);
            searchLayoutPanel.TabIndex = 4;
            // 
            // searchTextBox
            // 
            searchTextBox.Dock = DockStyle.Fill;
            searchTextBox.Location = new Point(63, 3);
            searchTextBox.Name = "searchTextBox";
            searchTextBox.Size = new Size(1003, 23);
            searchTextBox.TabIndex = 0;
            searchTextBox.TextChanged += SearchTextBox_TextChanged;
            // 
            // searchLabel
            // 
            searchLabel.AutoSize = true;
            searchLabel.Location = new Point(3, 0);
            searchLabel.Name = "searchLabel";
            searchLabel.Size = new Size(42, 15);
            searchLabel.TabIndex = 1;
            searchLabel.Text = "Search";
            // 
            // PlaylistTreeView
            // 
            PlaylistTreeView.AllowDrop = true;
            PlaylistTreeView.Dock = DockStyle.Fill;
            PlaylistTreeView.Location = new Point(3, 47);
            PlaylistTreeView.Name = "PlaylistTreeView";
            PlaylistTreeView.Size = new Size(1069, 549);
            PlaylistTreeView.TabIndex = 2;
            PlaylistTreeView.AfterCheck += PlaylistTreeView_AfterCheck;
            PlaylistTreeView.ItemDrag += PlaylistTreeView_ItemDrag;
            PlaylistTreeView.NodeMouseClick += PlaylistTreeView_NodeMouseClick;
            PlaylistTreeView.VisibleChanged += playlistTreeView_onload;
            PlaylistTreeView.DragDrop += PlaylistTreeView_DragDrop;
            PlaylistTreeView.DragEnter += PlaylistTreeView_DragEnter;
            // 
            // progressBar1
            // 
            progressBar1.Dock = DockStyle.Fill;
            progressBar1.Location = new Point(3, 602);
            progressBar1.Name = "progressBar1";
            progressBar1.Size = new Size(1069, 14);
            progressBar1.TabIndex = 3;
            // 
            // toolStrip1
            // 
            toolStrip1.Items.AddRange(new ToolStripItem[] { AddSongsDropDownButton, NewButton, DeleteButton, SettingsButton, ShuffleButton, SortByBPM, PlayButton, PauseButton, StopIcon, previousButton, nextButton });
            toolStrip1.Location = new Point(0, 0);
            toolStrip1.Name = "toolStrip1";
            toolStrip1.Size = new Size(1075, 25);
            toolStrip1.TabIndex = 1;
            toolStrip1.Text = "toolStrip1";
            // 
            // AddSongsDropDownButton
            // 
            AddSongsDropDownButton.DropDownItems.AddRange(new ToolStripItem[] { fromMyComputerToolStripMenuItem, fromYouTubeToolStripMenuItem });
            AddSongsDropDownButton.Image = (Image)resources.GetObject("AddSongsDropDownButton.Image");
            AddSongsDropDownButton.ImageTransparentColor = Color.Magenta;
            AddSongsDropDownButton.Name = "AddSongsDropDownButton";
            AddSongsDropDownButton.Size = new Size(93, 22);
            AddSongsDropDownButton.Text = "Add Songs";
            // 
            // fromMyComputerToolStripMenuItem
            // 
            fromMyComputerToolStripMenuItem.Name = "fromMyComputerToolStripMenuItem";
            fromMyComputerToolStripMenuItem.Size = new Size(179, 22);
            fromMyComputerToolStripMenuItem.Text = "From My Computer";
            fromMyComputerToolStripMenuItem.Click += fromMyComputerToolStripMenuItem_Click;
            // 
            // fromYouTubeToolStripMenuItem
            // 
            fromYouTubeToolStripMenuItem.Name = "fromYouTubeToolStripMenuItem";
            fromYouTubeToolStripMenuItem.Size = new Size(179, 22);
            fromYouTubeToolStripMenuItem.Text = "From YouTube";
            fromYouTubeToolStripMenuItem.Click += fromYouTubeToolStripMenuItem_Click;
            // 
            // NewButton
            // 
            NewButton.Image = (Image)resources.GetObject("NewButton.Image");
            NewButton.ImageTransparentColor = Color.Magenta;
            NewButton.Name = "NewButton";
            NewButton.Size = new Size(91, 22);
            NewButton.Text = "New Playlist";
            NewButton.Click += NewButton_Click;
            // 
            // DeleteButton
            // 
            DeleteButton.Enabled = false;
            DeleteButton.Image = (Image)resources.GetObject("DeleteButton.Image");
            DeleteButton.ImageTransparentColor = Color.Magenta;
            DeleteButton.Name = "DeleteButton";
            DeleteButton.Size = new Size(60, 22);
            DeleteButton.Text = "Delete";
            DeleteButton.Click += DeleteButton_Click;
            // 
            // SettingsButton
            // 
            SettingsButton.Alignment = ToolStripItemAlignment.Right;
            SettingsButton.Image = (Image)resources.GetObject("SettingsButton.Image");
            SettingsButton.ImageTransparentColor = Color.Magenta;
            SettingsButton.Name = "SettingsButton";
            SettingsButton.Size = new Size(69, 22);
            SettingsButton.Text = "Settings";
            SettingsButton.Click += SettingsButton_Click;
            // 
            // ShuffleButton
            // 
            ShuffleButton.Enabled = false;
            ShuffleButton.Image = (Image)resources.GetObject("ShuffleButton.Image");
            ShuffleButton.ImageTransparentColor = Color.Magenta;
            ShuffleButton.Name = "ShuffleButton";
            ShuffleButton.Size = new Size(64, 22);
            ShuffleButton.Text = "Shuffle";
            ShuffleButton.Click += ShuffleButton_Click;
            // 
            // SortByBPM
            // 
            SortByBPM.Enabled = false;
            SortByBPM.Image = (Image)resources.GetObject("SortByBPM.Image");
            SortByBPM.ImageAlign = ContentAlignment.MiddleLeft;
            SortByBPM.ImageTransparentColor = Color.Magenta;
            SortByBPM.Name = "SortByBPM";
            SortByBPM.Size = new Size(92, 22);
            SortByBPM.Text = "Sort by BPM";
            SortByBPM.TextImageRelation = TextImageRelation.TextBeforeImage;
            SortByBPM.Click += SortByBPM_Click;
            // 
            // PlayButton
            // 
            PlayButton.Image = (Image)resources.GetObject("PlayButton.Image");
            PlayButton.ImageTransparentColor = Color.Magenta;
            PlayButton.Name = "PlayButton";
            PlayButton.Size = new Size(49, 22);
            PlayButton.Text = "Play";
            PlayButton.Click += PlayButton_Click;
            // 
            // PauseButton
            // 
            PauseButton.Image = (Image)resources.GetObject("PauseButton.Image");
            PauseButton.ImageTransparentColor = Color.Magenta;
            PauseButton.Name = "PauseButton";
            PauseButton.Size = new Size(58, 22);
            PauseButton.Text = "Pause";
            PauseButton.Click += PauseButton_Click;
            // 
            // StopIcon
            // 
            StopIcon.Image = (Image)resources.GetObject("StopIcon.Image");
            StopIcon.ImageTransparentColor = Color.Magenta;
            StopIcon.Name = "StopIcon";
            StopIcon.Size = new Size(51, 22);
            StopIcon.Text = "Stop";
            StopIcon.Click += StopIcon_Click;
            // 
            // previousButton
            // 
            previousButton.Image = (Image)resources.GetObject("previousButton.Image");
            previousButton.ImageTransparentColor = Color.Magenta;
            previousButton.Name = "previousButton";
            previousButton.Size = new Size(72, 22);
            previousButton.Text = "Previous";
            previousButton.Click += toolStripButton1_Click;
            // 
            // nextButton
            // 
            nextButton.Image = (Image)resources.GetObject("nextButton.Image");
            nextButton.ImageTransparentColor = Color.Magenta;
            nextButton.Name = "nextButton";
            nextButton.Size = new Size(51, 22);
            nextButton.Text = "Next";
            nextButton.Click += nextButton_Click;
            // 
            // MainWindow
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1075, 644);
            Controls.Add(panel1);
            Icon = Properties.Resources.pickleIcon;
            Name = "MainWindow";
            Text = "Pickles Playlist Editor";
            Load += Form1_Load;
            panel1.ResumeLayout(false);
            panel1.PerformLayout();
            tableLayoutPanel1.ResumeLayout(false);
            searchLayoutPanel.ResumeLayout(false);
            searchLayoutPanel.PerformLayout();
            toolStrip1.ResumeLayout(false);
            toolStrip1.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private Panel panel1;
        private TreeView PlaylistTreeView;
        private ToolStrip toolStrip1;
        private ToolStripButton NewButton;
        private ToolStripButton DeleteButton;
        private ToolStripButton SettingsButton;
        private ToolStripButton ShuffleButton;
        private TableLayoutPanel tableLayoutPanel1;
        private TableLayoutPanel searchLayoutPanel;

        // Search box for filtering the tree view
        private TextBox searchTextBox;

        private CustomProgressBar progressBar1;

        class CustomProgressBar : ProgressBar
        {
            public CustomProgressBar()
            {
                this.SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                //base.OnPaint(e);

                Rectangle rect = this.ClientRectangle;
                Graphics g = e.Graphics;

                ProgressBarRenderer.DrawHorizontalBar(g, rect);
                rect.Inflate(-3, -3);
                if (this.Value > 0)
                {
                    Rectangle clip = new Rectangle(rect.X, rect.Y, (int)Math.Round(((float)this.Value / this.Maximum) * rect.Width), rect.Height);
                    ProgressBarRenderer.DrawHorizontalChunks(g, clip);
                }

                string text = $"{Text} - {Value}%";

                if (Value == 0)
                    text = $"{Text}";
                using (Font font = new Font("Arial", 10))
                {
                    SizeF textSize = e.Graphics.MeasureString(text, font);
                    PointF location = new PointF((Width - textSize.Width) / 2, (Height - textSize.Height) / 2);
                    e.Graphics.DrawString(text, font, Brushes.Black, location);
                }
            }
        }
        private ToolStripButton SortByBPM;
        private ToolStripButton PlayButton;
        private ToolStripButton PauseButton;
        private ToolStripButton StopIcon;
        private ToolStripButton previousButton;
        private ToolStripButton nextButton;
        private Label searchLabel;
        private ToolStripDropDownButton AddSongsDropDownButton;
        private ToolStripMenuItem fromMyComputerToolStripMenuItem;
        private ToolStripMenuItem fromYouTubeToolStripMenuItem;
    }
}
