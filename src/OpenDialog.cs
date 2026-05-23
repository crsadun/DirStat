using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DirStat
{
    // WinDirStat-style "what do you want to analyze" dialog. Shown at startup
    // (and reachable from the File menu) so the user doesn't have to dig
    // through menus to pick a drive or a folder.
    public sealed class OpenDialog : Form
    {
        public enum SelectionMode { Drive, Folder }

        private readonly RadioButton _rbDrive;
        private readonly RadioButton _rbFolder;
        private readonly ListBox _driveList;
        private readonly TextBox _folderPath;
        private readonly Button _browseBtn;
        private readonly Button _okBtn;
        private readonly Button _cancelBtn;

        public SelectionMode Mode { get; private set; }
        public string SelectedPath { get; private set; }

        public OpenDialog()
        {
            Text = "DirStat — Open";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(480, 360);
            BackColor = Color.FromArgb(32, 32, 32);
            ForeColor = Color.White;
            KeyPreview = true;

            _rbDrive = new RadioButton
            {
                Text = "&Disk",
                Top = 12,
                Left = 14,
                Width = 100,
                Checked = true,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            _driveList = new ListBox
            {
                Top = 36,
                Left = 34,
                Width = 432,
                Height = 170,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
            };

            _rbFolder = new RadioButton
            {
                Text = "&Folder",
                Top = 220,
                Left = 14,
                Width = 100,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            _folderPath = new TextBox
            {
                Top = 244,
                Left = 34,
                Width = 348,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _folderPath.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _folderPath.AutoCompleteSource = AutoCompleteSource.FileSystem;

            _browseBtn = new Button
            {
                Text = "Browse…",
                Top = 242,
                Left = 388,
                Width = 78,
                BackColor = Color.FromArgb(56, 56, 56),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            _browseBtn.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);

            _okBtn = new Button
            {
                Text = "OK",
                Top = 314,
                Left = 304,
                Width = 76,
                BackColor = Color.FromArgb(56, 56, 56),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.OK,
            };
            _okBtn.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);

            _cancelBtn = new Button
            {
                Text = "Cancel",
                Top = 314,
                Left = 390,
                Width = 76,
                BackColor = Color.FromArgb(56, 56, 56),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel,
            };
            _cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);

            Controls.AddRange(new Control[] {
                _rbDrive, _driveList, _rbFolder, _folderPath, _browseBtn, _okBtn, _cancelBtn
            });

            AcceptButton = _okBtn;
            CancelButton = _cancelBtn;

            PopulateDrives();

            _rbDrive.CheckedChanged += delegate(object s, EventArgs e) { UpdateEnabled(); };
            _rbFolder.CheckedChanged += delegate(object s, EventArgs e) { UpdateEnabled(); };
            // Clicking inside either group switches the radio for free.
            _driveList.Enter += delegate(object s, EventArgs e) { _rbDrive.Checked = true; };
            _folderPath.Enter += delegate(object s, EventArgs e) { _rbFolder.Checked = true; };
            _driveList.DoubleClick += delegate(object s, EventArgs e)
            {
                if (_driveList.SelectedIndex >= 0 && ValidateAndCapture())
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            _browseBtn.Click += delegate(object s, EventArgs e) { OnBrowse(); };
            _okBtn.Click += delegate(object s, EventArgs e)
            {
                // Prevent the Form from closing if validation fails.
                if (!ValidateAndCapture()) DialogResult = DialogResult.None;
            };

            UpdateEnabled();
        }

        private void UpdateEnabled()
        {
            _driveList.Enabled = _rbDrive.Checked;
            _folderPath.Enabled = _rbFolder.Checked;
            _browseBtn.Enabled = _rbFolder.Checked;
        }

        private void PopulateDrives()
        {
            _driveList.Items.Clear();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { drives = new DriveInfo[0]; }

            int firstReady = -1;
            for (int i = 0; i < drives.Length; i++)
            {
                var d = drives[i];
                string label;
                bool ready = false;
                try { ready = d.IsReady; } catch { ready = false; }
                try
                {
                    if (ready)
                    {
                        label = string.Format("{0} ({1}) — {2} free of {3}",
                            d.Name.TrimEnd('\\'),
                            string.IsNullOrEmpty(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel,
                            MainForm.FormatBytes(d.AvailableFreeSpace),
                            MainForm.FormatBytes(d.TotalSize));
                        if (firstReady < 0) firstReady = i;
                    }
                    else
                    {
                        label = d.Name + " (not ready)";
                    }
                }
                catch
                {
                    label = d.Name;
                }
                _driveList.Items.Add(new DriveEntry { Drive = d, Label = label, Ready = ready });
            }
            _driveList.DisplayMember = "Label";
            if (firstReady >= 0) _driveList.SelectedIndex = firstReady;
        }

        private sealed class DriveEntry
        {
            public DriveInfo Drive;
            public string Label;
            public bool Ready;
            public override string ToString() { return Label; }
        }

        private void OnBrowse()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "Choose a folder to analyze";
                if (!string.IsNullOrEmpty(_folderPath.Text) && Directory.Exists(_folderPath.Text))
                    d.SelectedPath = _folderPath.Text;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    _folderPath.Text = d.SelectedPath;
                    _rbFolder.Checked = true;
                }
            }
        }

        private bool ValidateAndCapture()
        {
            if (_rbDrive.Checked)
            {
                var entry = _driveList.SelectedItem as DriveEntry;
                if (entry == null || !entry.Ready)
                {
                    MessageBox.Show(this, "Please select a ready drive.", "DirStat",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                Mode = SelectionMode.Drive;
                SelectedPath = entry.Drive.RootDirectory.FullName;
                return true;
            }

            string p = _folderPath.Text != null ? _folderPath.Text.Trim() : "";
            // Strip surrounding quotes that paste from Explorer often adds.
            if (p.Length >= 2 && p[0] == '"' && p[p.Length - 1] == '"')
                p = p.Substring(1, p.Length - 2);
            if (string.IsNullOrEmpty(p))
            {
                MessageBox.Show(this, "Please enter a path or browse to a folder.", "DirStat",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            // If the user typed a file, fall back to its containing folder.
            if (File.Exists(p))
            {
                string dir = Path.GetDirectoryName(p);
                if (string.IsNullOrEmpty(dir))
                {
                    MessageBox.Show(this, "Could not determine the containing folder for:\n" + p,
                        "DirStat", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                p = dir;
            }

            if (!Directory.Exists(p))
            {
                MessageBox.Show(this, "Folder does not exist:\n" + p, "DirStat",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            Mode = SelectionMode.Folder;
            SelectedPath = p;
            return true;
        }
    }
}
