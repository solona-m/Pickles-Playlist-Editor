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
            PlaylistTreeView = new TreeView();
            progressBar1 = new CustomProgressBar();
            toolStrip1 = new ToolStrip();
            NewButton = new ToolStripButton();
            DeleteButton = new ToolStripButton();
            SettingsButton = new ToolStripButton();
            ShuffleButton = new ToolStripButton();
            panel1.SuspendLayout();
            tableLayoutPanel1.SuspendLayout();
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
            tableLayoutPanel1.Controls.Add(PlaylistTreeView, 0, 1);
            tableLayoutPanel1.Controls.Add(progressBar1, 0, 2);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Location = new Point(0, 25);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 3;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 5F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            tableLayoutPanel1.Size = new Size(1075, 619);
            tableLayoutPanel1.TabIndex = 2;
            // 
            // PlaylistTreeView
            // 
            PlaylistTreeView.AllowDrop = true;
            PlaylistTreeView.Dock = DockStyle.Fill;
            PlaylistTreeView.Location = new Point(3, 8);
            PlaylistTreeView.Name = "PlaylistTreeView";
            PlaylistTreeView.Size = new Size(1069, 580);
            PlaylistTreeView.TabIndex = 2;
            PlaylistTreeView.AfterCheck += PlaylistTreeView_AfterCheck;
            PlaylistTreeView.ItemDrag += PlaylistTreeView_ItemDrag;
            PlaylistTreeView.VisibleChanged += playlistTreeView_onload;
            PlaylistTreeView.DragDrop += PlaylistTreeView_DragDrop;
            PlaylistTreeView.DragEnter += PlaylistTreeView_DragEnter;
            // 
            // progressBar1
            // 
            progressBar1.Dock = DockStyle.Fill;
            progressBar1.Location = new Point(3, 594);
            progressBar1.Name = "progressBar1";
            progressBar1.Size = new Size(1069, 22);
            progressBar1.TabIndex = 3;
            // 
            // toolStrip1
            // 
            toolStrip1.Items.AddRange(new ToolStripItem[] { NewButton, DeleteButton, SettingsButton, ShuffleButton });
            toolStrip1.Location = new Point(0, 0);
            toolStrip1.Name = "toolStrip1";
            toolStrip1.Size = new Size(1075, 25);
            toolStrip1.TabIndex = 1;
            toolStrip1.Text = "toolStrip1";
            // 
            // NewButton
            // 
            NewButton.Image = (Image)resources.GetObject("NewButton.Image");
            NewButton.ImageTransparentColor = Color.Magenta;
            NewButton.Name = "NewButton";
            NewButton.Size = new Size(51, 22);
            NewButton.Text = "New";
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
    }
}
