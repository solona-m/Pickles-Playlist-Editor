namespace Pickles_Playlist_Editor
{
    partial class SettingsForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            tableLayoutPanel1 = new TableLayoutPanel();
            label1 = new Label();
            tableLayoutPanel2 = new TableLayoutPanel();
            BrowseButton = new Button();
            DirecotryPathTextBox = new TextBox();
            label2 = new Label();
            BaselineScdTextBox = new TextBox();
            checksTableLayoutPanel = new TableLayoutPanel();
            NormalizeVolumeLabel = new Label();
            NormalizeVolumeCheckBox = new CheckBox();
            OrganizeLibraryButton = new Button();
            OkButton = new Button();
            tableLayoutPanel1.SuspendLayout();
            tableLayoutPanel2.SuspendLayout();
            checksTableLayoutPanel.SuspendLayout();
            SuspendLayout();
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.ColumnCount = 1;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.Controls.Add(label1, 0, 0);
            tableLayoutPanel1.Controls.Add(tableLayoutPanel2, 0, 1);
            tableLayoutPanel1.Controls.Add(label2, 0, 2);
            tableLayoutPanel1.Controls.Add(BaselineScdTextBox, 0, 3);
            tableLayoutPanel1.Controls.Add(checksTableLayoutPanel, 0, 4);
            tableLayoutPanel1.Controls.Add(OkButton, 0, 5);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Location = new Point(0, 0);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 61F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 8F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.Size = new Size(631, 206);
            tableLayoutPanel1.TabIndex = 0;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Dock = DockStyle.Fill;
            label1.Location = new Point(3, 0);
            label1.Name = "label1";
            label1.Size = new Size(625, 20);
            label1.TabIndex = 0;
            label1.Text = "Mod Directory in Penumbra";
            // 
            // tableLayoutPanel2
            // 
            tableLayoutPanel2.ColumnCount = 2;
            tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            tableLayoutPanel2.Controls.Add(BrowseButton, 1, 0);
            tableLayoutPanel2.Controls.Add(DirecotryPathTextBox, 0, 0);
            tableLayoutPanel2.Dock = DockStyle.Fill;
            tableLayoutPanel2.Location = new Point(3, 23);
            tableLayoutPanel2.Name = "tableLayoutPanel2";
            tableLayoutPanel2.RowCount = 1;
            tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel2.Size = new Size(625, 44);
            tableLayoutPanel2.TabIndex = 1;
            // 
            // BrowseButton
            // 
            BrowseButton.Dock = DockStyle.Fill;
            BrowseButton.Location = new Point(478, 3);
            BrowseButton.Name = "BrowseButton";
            BrowseButton.Size = new Size(144, 38);
            BrowseButton.TabIndex = 0;
            BrowseButton.Text = "Browse";
            BrowseButton.UseVisualStyleBackColor = true;
            BrowseButton.Click += BrowseButton_Click;
            // 
            // DirecotryPathTextBox
            // 
            DirecotryPathTextBox.Dock = DockStyle.Fill;
            DirecotryPathTextBox.Location = new Point(3, 3);
            DirecotryPathTextBox.Name = "DirecotryPathTextBox";
            DirecotryPathTextBox.Size = new Size(469, 23);
            DirecotryPathTextBox.TabIndex = 1;
            DirecotryPathTextBox.TextChanged += DirecotryPathTextBox_TextChanged;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Dock = DockStyle.Fill;
            label2.Location = new Point(3, 70);
            label2.Name = "label2";
            label2.Size = new Size(625, 20);
            label2.TabIndex = 2;
            label2.Text = "Baseline SCD key (e.g. sound/bpmloop.scd)";
            // 
            // BaselineScdTextBox
            // 
            BaselineScdTextBox.Dock = DockStyle.Fill;
            BaselineScdTextBox.Location = new Point(3, 93);
            BaselineScdTextBox.Name = "BaselineScdTextBox";
            BaselineScdTextBox.Size = new Size(625, 23);
            BaselineScdTextBox.TabIndex = 3;
            BaselineScdTextBox.TextChanged += BaselineScdTextBox_TextChanged;
            // 
            // checksTableLayoutPanel
            // 
            checksTableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200F));
            checksTableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            checksTableLayoutPanel.Controls.Add(NormalizeVolumeLabel, 0, 1);
            checksTableLayoutPanel.Controls.Add(NormalizeVolumeCheckBox, 1, 1);
            checksTableLayoutPanel.Controls.Add(OrganizeLibraryButton, 0, 0);
            checksTableLayoutPanel.Dock = DockStyle.Fill;
            checksTableLayoutPanel.Location = new Point(3, 113);
            checksTableLayoutPanel.Name = "checksTableLayoutPanel";
            checksTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 27F));
            checksTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 43F));
            checksTableLayoutPanel.Size = new Size(625, 55);
            checksTableLayoutPanel.TabIndex = 4;
            // 
            // NormalizeVolumeLabel
            // 
            NormalizeVolumeLabel.Dock = DockStyle.Fill;
            NormalizeVolumeLabel.Location = new Point(3, 27);
            NormalizeVolumeLabel.Name = "NormalizeVolumeLabel";
            NormalizeVolumeLabel.Size = new Size(194, 43);
            NormalizeVolumeLabel.TabIndex = 6;
            NormalizeVolumeLabel.Text = "Normalize Volume";
            // 
            // NormalizeVolumeCheckBox
            // 
            NormalizeVolumeCheckBox.Location = new Point(203, 30);
            NormalizeVolumeCheckBox.Name = "NormalizeVolumeCheckBox";
            NormalizeVolumeCheckBox.Size = new Size(18, 14);
            NormalizeVolumeCheckBox.TabIndex = 7;
            // 
            // OrganizeLibraryButton
            // 
            OrganizeLibraryButton.Dock = DockStyle.Fill;
            OrganizeLibraryButton.Location = new Point(3, 3);
            OrganizeLibraryButton.Name = "OrganizeLibraryButton";
            OrganizeLibraryButton.Size = new Size(194, 21);
            OrganizeLibraryButton.TabIndex = 8;
            OrganizeLibraryButton.Text = "Organize Library";
            OrganizeLibraryButton.UseVisualStyleBackColor = true;
            OrganizeLibraryButton.Click += OrganizeLibraryButton_Click;
            // 
            // OkButton
            // 
            OkButton.Dock = DockStyle.Fill;
            OkButton.Location = new Point(3, 174);
            OkButton.Name = "OkButton";
            OkButton.Size = new Size(625, 29);
            OkButton.TabIndex = 4;
            OkButton.Text = "Ok";
            OkButton.UseVisualStyleBackColor = true;
            OkButton.Click += OkButton_Click;
            // 
            // SettingsForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(631, 206);
            Controls.Add(tableLayoutPanel1);
            Icon = Properties.Resources.gearIcon;
            Name = "SettingsForm";
            Text = "Settings";
            tableLayoutPanel1.ResumeLayout(false);
            tableLayoutPanel1.PerformLayout();
            tableLayoutPanel2.ResumeLayout(false);
            tableLayoutPanel2.PerformLayout();
            checksTableLayoutPanel.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private TableLayoutPanel tableLayoutPanel1;
        private Label label1;
        private TableLayoutPanel tableLayoutPanel2;
        private TableLayoutPanel checksTableLayoutPanel;
        private Button BrowseButton;
        private TextBox DirecotryPathTextBox;
        private Label label2;
        private TextBox BaselineScdTextBox;
        private Button OkButton;

        // Checkbox for normalize volume
        private Label NormalizeVolumeLabel;
        private CheckBox NormalizeVolumeCheckBox;

        // Label + checkbox for organizing library on start
        private Button OrganizeLibraryButton;
    }
}
