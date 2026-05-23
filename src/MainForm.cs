using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinFormsTreeNode = System.Windows.Forms.TreeNode;

namespace DirStat
{
    public sealed class MainForm : Form
    {
        private readonly MenuStrip _menu;
        private readonly StatusStrip _status;
        private readonly ToolStripStatusLabel _statusLabel;
        private readonly ToolStripStatusLabel _statusFiles;
        private readonly ToolStripStatusLabel _statusBytes;
        private readonly ToolStripProgressBar _statusProgress;
        private readonly MinSizeControl _minSizeControl;
        private readonly ToolStripControlHost _minSizeHost;
        private readonly TreeView _tree;
        private readonly TreemapPanel _treemap;
        private readonly BreadcrumbBar _breadcrumb;
        private readonly ListView _extList;
        private readonly SplitContainer _outerSplit;
        private readonly SplitContainer _rightSplit;

        private CancellationTokenSource _scanCts;
        private Task<ScanResult> _scanTask;
        private DirNode _root;
        private long _minBytes; // current min-file-size filter, 0 = off; persists across rescans
        private long[] _snapPoints = new long[] { 0 };
        private bool _excludeSystem = true; // default: hide OS system files after scan
        private bool _rootIsSystem;         // scan root itself sits inside a system path
        private bool _hasSystemNodes;       // at least one descendant is a system node
        private ToolStripMenuItem _excludeSystemMenuItem;
        private readonly ExtensionColorMap _colorMap = new ExtensionColorMap();
        private readonly Dictionary<DirNode, WinFormsTreeNode> _nodeIndex = new Dictionary<DirNode, WinFormsTreeNode>();
        private bool _suppressTreeSelect;

        public MainForm()
        {
            Text = "DirStat " + AppVersion;
            Width = 1200;
            Height = 800;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(32, 32, 32);
            ForeColor = Color.White;

            // Menu
            _menu = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("&File");
            var openItem = new ToolStripMenuItem("&Open...", null, (s, e) => OnOpen())
            { ShortcutKeys = Keys.Control | Keys.O };
            var openFolder = new ToolStripMenuItem("Open Folder...", null, (s, e) => OnOpenFolder());
            var openDrives = new ToolStripMenuItem("Open Drive");
            PopulateDrivesMenu(openDrives);
            var upItem = new ToolStripMenuItem("&Up One Level", null, (s, e) => OnUp())
            { ShortcutKeys = Keys.Alt | Keys.Up };
            var refresh = new ToolStripMenuItem("&Refresh", null, (s, e) => OnRefresh())
            { ShortcutKeys = Keys.F5 };
            var stop = new ToolStripMenuItem("&Stop", null, (s, e) => OnStop());
            var exitItem = new ToolStripMenuItem("E&xit", null, (s, e) => Close());
            fileMenu.DropDownItems.AddRange(new ToolStripItem[] {
                openItem, openFolder, openDrives, new ToolStripSeparator(), upItem, refresh, stop,
                new ToolStripSeparator(), exitItem
            });

            var viewMenu = new ToolStripMenuItem("&View");
            var openExplorerItem = new ToolStripMenuItem("Show in &Explorer", null, (s, e) => OnShowInExplorer())
            { ShortcutKeys = Keys.Control | Keys.E };
            var zoomInItem = new ToolStripMenuItem("Zoom &In", null, (s, e) => OnZoomIn())
            { ShortcutKeys = Keys.Control | Keys.Oemplus };
            zoomInItem.ShortcutKeyDisplayString = "Ctrl+=";
            var zoomOutItem = new ToolStripMenuItem("Zoom &Out", null, (s, e) => _treemap.ZoomOut())
            { ShortcutKeys = Keys.Control | Keys.OemMinus };
            zoomOutItem.ShortcutKeyDisplayString = "Ctrl+-";
            var zoomResetItem = new ToolStripMenuItem("&Reset Zoom", null, (s, e) => _treemap.ZoomReset())
            { ShortcutKeys = Keys.Control | Keys.D0 };
            _excludeSystemMenuItem = new ToolStripMenuItem("Exclude &System Files",
                null, (s, e) => ToggleExcludeSystem())
            {
                ShortcutKeys = Keys.Control | Keys.Shift | Keys.H,
                CheckOnClick = false,
                Checked = _excludeSystem,
                Visible = false, // shown only after a scan, and only when meaningful
            };
            viewMenu.DropDownItems.AddRange(new ToolStripItem[] {
                openExplorerItem, new ToolStripSeparator(),
                zoomInItem, zoomOutItem, zoomResetItem,
                new ToolStripSeparator(),
                _excludeSystemMenuItem,
            });

            var helpMenu = new ToolStripMenuItem("&Help");
            var aboutItem = new ToolStripMenuItem("&About", null, (s, e) =>
                MessageBox.Show(this,
                    "DirStat " + AppVersion + "\n" +
                    "A tool to visualize disk usage, inspired by WinDirStat\n\n" +
                    "Copyright (C) 2026 Cristiano Sadun",
                    "About DirStat", MessageBoxButtons.OK, MessageBoxIcon.Information));
            helpMenu.DropDownItems.Add(aboutItem);

            _menu.Items.AddRange(new ToolStripItem[] { fileMenu, viewMenu, helpMenu });
            _menu.BackColor = Color.FromArgb(48, 48, 48);
            _menu.ForeColor = Color.White;
            _menu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            MainMenuStrip = _menu;
            ApplyDarkMenuColors(_menu.Items);

            // Status
            _status = new StatusStrip();
            _status.BackColor = Color.FromArgb(48, 48, 48);
            _status.ForeColor = Color.White;
            _statusLabel = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _statusProgress = new ToolStripProgressBar { Style = ProgressBarStyle.Marquee, Visible = false, Width = 120 };
            _statusFiles = new ToolStripStatusLabel("0 files");
            _statusBytes = new ToolStripStatusLabel("0 B");
            _minSizeControl = new MinSizeControl();
            _minSizeControl.Size = new Size(360, 22);
            _minSizeControl.ValueChanged += delegate(object s, EventArgs e)
            {
                if (_suppressFilterEvent) return;
                _minBytes = _minSizeControl.MinBytes;
                ApplyFilter();
            };
            _minSizeHost = new ToolStripControlHost(_minSizeControl);
            _minSizeHost.AutoSize = false;
            _minSizeHost.Size = new Size(360, 22);
            _minSizeHost.Margin = new Padding(0);
            _minSizeHost.Padding = new Padding(0);
            _status.Items.AddRange(new ToolStripItem[] {
                _statusLabel, _statusProgress, _statusFiles, _statusBytes, _minSizeHost
            });

            // Tree
            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                HideSelection = false,
                ShowLines = true,
                FullRowSelect = true,
            };
            _tree.BeforeExpand += Tree_BeforeExpand;
            _tree.AfterSelect += Tree_AfterSelect;
            _tree.NodeMouseClick += Tree_NodeMouseClick;

            // Treemap
            _treemap = new TreemapPanel { Dock = DockStyle.Fill, ColorMap = _colorMap };
            _treemap.SelectionChanged += Treemap_SelectionChanged;
            _treemap.NodeActivated += (s, n) => Win32.OpenInExplorer(n.FullPath);
            _treemap.ContextRequested += (n, screenPt) => ShowNodeContextMenu(n, screenPt);
            _treemap.ViewRootChanged += delegate(object s, DirNode vr) { UpdateBreadcrumb(); };

            // Breadcrumb showing the current zoom path; sits above the treemap.
            _breadcrumb = new BreadcrumbBar { Dock = DockStyle.Top };
            _breadcrumb.SegmentClicked += delegate(DirNode n)
            {
                if (n != null) _treemap.SetViewRoot(n);
            };
            _breadcrumb.ResetClicked += delegate() { _treemap.ZoomReset(); };

            // Extension list
            _extList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                OwnerDraw = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            _extList.Columns.Add("", 28);
            _extList.Columns.Add("Extension", 110);
            _extList.Columns.Add("Size", 110, HorizontalAlignment.Right);
            _extList.Columns.Add("% Bytes", 70, HorizontalAlignment.Right);
            _extList.Columns.Add("Files", 90, HorizontalAlignment.Right);
            _extList.DrawColumnHeader += (s, e) =>
            {
                e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(56, 56, 56)), e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, _extList.Font, e.Bounds, Color.White,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.LeftAndRightPadding);
            };
            _extList.DrawSubItem += ExtList_DrawSubItem;
            _extList.SelectedIndexChanged += ExtList_SelectedIndexChanged;

            // Layout: outer split tree | (treemap / extlist)
            _outerSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 320,
                BackColor = Color.FromArgb(48, 48, 48),
            };
            _outerSplit.Panel1.Controls.Add(_tree);

            _rightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = Color.FromArgb(48, 48, 48),
            };
            // Treemap fills, breadcrumb docks above. Add treemap FIRST so the
            // breadcrumb (Dock=Top) ends up above it after z-order resolution.
            _rightSplit.Panel1.Controls.Add(_treemap);
            _rightSplit.Panel1.Controls.Add(_breadcrumb);
            _rightSplit.Panel2.Controls.Add(_extList);
            _outerSplit.Panel2.Controls.Add(_rightSplit);

            Controls.Add(_outerSplit);
            Controls.Add(_status);
            Controls.Add(_menu);

            Load += (s, e) =>
            {
                _outerSplit.SplitterDistance = 320;
                _rightSplit.SplitterDistance = (int)(_rightSplit.Height * 0.72);
            };

            KeyPreview = true;
            KeyDown += MainForm_KeyDown;

            FormClosing += (s, e) => OnStop();
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && !e.Control && !e.Alt)
            {
                DirNode n = _tree.SelectedNode != null ? _tree.SelectedNode.Tag as DirNode : null;
                if (n != null && !string.IsNullOrEmpty(n.FullPath))
                {
                    DeleteNode(n, e.Shift);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                return;
            }
            // Backspace zooms out one level (file-browser style). No text inputs
            // in this form make this safe to capture form-wide.
            if (e.KeyCode == Keys.Back && !e.Control && !e.Alt && !e.Shift)
            {
                _treemap.ZoomOut();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void OnZoomIn()
        {
            // Prefer the tree selection as the zoom target; falls back to the
            // largest child of the current view root inside ZoomInOnTarget.
            DirNode target = _tree.SelectedNode != null ? _tree.SelectedNode.Tag as DirNode : null;
            _treemap.ZoomInOnTarget(target);
        }

        // Refresh the breadcrumb bar to reflect the current zoom path
        // (scan root → ... → current view root).
        private void UpdateBreadcrumb()
        {
            if (_root == null || _treemap.ViewRoot == null)
            {
                _breadcrumb.SetPath(null);
                return;
            }
            var path = new List<DirNode>();
            for (var n = _treemap.ViewRoot; n != null; n = n.Parent)
            {
                path.Add(n);
                if (n == _root) break;
            }
            path.Reverse();
            _breadcrumb.SetPath(path);
        }

        private void DeleteNode(DirNode node, bool permanent)
        {
            if (node == null) return;
            string path = node.FullPath;
            if (string.IsNullOrEmpty(path)) return;

            string what = node.IsDirectory ? "folder and all its contents" : "file";
            string title;
            string msg;
            MessageBoxIcon icon;
            if (permanent)
            {
                title = "Delete permanently";
                msg = string.Format(
                    "Permanently delete this {0}?\n\n{1}\n\nThis cannot be undone.",
                    what, path);
                icon = MessageBoxIcon.Warning;
            }
            else
            {
                title = "Delete to Recycle Bin";
                msg = string.Format(
                    "Move this {0} to the Recycle Bin?\n\n{1}", what, path);
                icon = MessageBoxIcon.Question;
            }

            var result = MessageBox.Show(this, msg, title,
                MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;

            // Cancel any in-progress scan so we don't fight it for handles.
            OnStop();

            bool ok = Win32.Delete(this.Handle, path, !permanent);
            if (!ok)
            {
                MessageBox.Show(this,
                    "Could not delete:\n" + path,
                    "DirStat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Update the in-memory tree in place — do NOT rescan, otherwise the
            // tree collapses back to the root and the user loses their place.
            if (_root == null || node == _root)
            {
                // The deleted node was itself the scan root — nothing to show.
                _tree.Nodes.Clear();
                _nodeIndex.Clear();
                _treemap.SetRoot(null);
                _extList.Items.Clear();
                _root = null;
                Text = "DirStat " + AppVersion;
            }
            else
            {
                RemoveNodeInPlace(node);
            }

            _statusLabel.Text = (permanent ? "Permanently deleted: " : "Recycled: ") + path;
        }

        // Remove `node` from its parent's children and update the UI in place.
        // Size is bubbled up the ancestor chain; FileCount/DirCount only need to
        // change on the immediate parent (they track immediate-children counts).
        // Selection moves to the parent, lazy-expanding the path if needed.
        private void RemoveNodeInPlace(DirNode node)
        {
            DirNode parent = node.Parent;
            if (parent == null) return;

            if (parent.Children != null)
                parent.Children.Remove(node);

            long delta = -node.Size;
            for (var p = parent; p != null; p = p.Parent)
                p.Size += delta;

            if (node.IsDirectory) parent.DirCount = Math.Max(0, parent.DirCount - 1);
            else parent.FileCount = Math.Max(0, parent.FileCount - 1);

            // Refresh Display* before touching the tree — ancestor labels read it.
            _root.RecomputeDisplay(_minBytes, EffectiveExcludeSystem);

            _tree.BeginUpdate();
            try
            {
                WinFormsTreeNode nodeTn;
                if (_nodeIndex.TryGetValue(node, out nodeTn))
                {
                    RemoveSubtreeIndex(nodeTn);
                    _nodeIndex.Remove(node);
                    if (nodeTn.Parent != null) nodeTn.Parent.Nodes.Remove(nodeTn);
                    else _tree.Nodes.Remove(nodeTn);
                }

                for (var p = parent; p != null; p = p.Parent)
                {
                    WinFormsTreeNode ptn;
                    if (_nodeIndex.TryGetValue(p, out ptn))
                        ptn.Text = FormatTreeLabel(p);
                }
            }
            finally { _tree.EndUpdate(); }

            // Rebuild extension stats from the surviving tree.
            var stats = new ExtensionStats();
            AccumulateExtensions(_root, stats);
            _colorMap.Build(stats);
            PopulateExtList(stats, _root.Size);

            // If the zoomed view root was the deleted node or one of its
            // descendants, the reference is now stale — fall back to its parent.
            if (IsAncestorOrSelf(node, _treemap.ViewRoot))
                _treemap.SetViewRoot(parent);

            // Re-render (preserve any active zoom) and move selection/highlight.
            _treemap.Rerender();
            _treemap.SetHighlight(parent);
            Treemap_SelectionChanged(this, parent);

            UpdateFilteredStatus();
        }

        // True if `ancestor` equals `node` or is one of its ancestors.
        private static bool IsAncestorOrSelf(DirNode ancestor, DirNode node)
        {
            for (var c = node; c != null; c = c.Parent)
                if (c == ancestor) return true;
            return false;
        }

        // ------- Drive menu -------

        private void PopulateDrivesMenu(ToolStripMenuItem parent)
        {
            // Populate eagerly so the submenu indicator appears, then refresh on each open
            // (so USB/network drive changes are reflected).
            RefreshDrivesMenu(parent);
            parent.DropDownOpening += delegate(object s, EventArgs e) { RefreshDrivesMenu(parent); };
        }

        private void RefreshDrivesMenu(ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { drives = new DriveInfo[0]; }

            if (drives.Length == 0)
            {
                var none = new ToolStripMenuItem("(no drives detected)") { Enabled = false };
                parent.DropDownItems.Add(none);
                return;
            }

            foreach (var d in drives)
            {
                string label;
                try
                {
                    if (d.IsReady)
                    {
                        label = string.Format("{0} ({1}) — {2} free of {3}",
                            d.Name.TrimEnd('\\'),
                            string.IsNullOrEmpty(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel,
                            FormatBytes(d.AvailableFreeSpace), FormatBytes(d.TotalSize));
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
                string rootPath = d.RootDirectory.FullName;
                var item = new ToolStripMenuItem(label);
                item.ToolTipText = rootPath;
                item.Enabled = d.IsReady;
                item.Click += delegate(object sender, EventArgs args) { StartScan(rootPath); };
                parent.DropDownItems.Add(item);
            }
            ApplyDarkMenuColors(parent.DropDownItems);
        }

        private static readonly Color MenuBg = Color.FromArgb(48, 48, 48);
        private static readonly Color MenuFg = Color.White;
        private static readonly Color MenuFgDisabled = Color.FromArgb(140, 140, 140);

        private static void ApplyDarkMenuColors(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                it.BackColor = MenuBg;
                it.ForeColor = it.Enabled ? MenuFg : MenuFgDisabled;
                var drop = it as ToolStripDropDownItem;
                if (drop != null)
                {
                    drop.DropDown.BackColor = MenuBg;
                    drop.DropDown.ForeColor = MenuFg;
                    if (drop.HasDropDownItems)
                        ApplyDarkMenuColors(drop.DropDownItems);
                }
            }
        }

        // ------- Scan -------

        private void OnOpenFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Choose a folder to analyze";
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                {
                    StartScan(dlg.SelectedPath);
                }
            }
        }

        private void OnOpen()
        {
            using (var dlg = new OpenDialog())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                    StartScan(dlg.SelectedPath);
            }
        }

        // Public entry point so Program.cs (and external callers) don't need
        // reflection to start a scan.
        public void BeginScan(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            StartScan(path);
        }

        private void OnRefresh()
        {
            if (_root != null) StartScan(_root.Name);
        }

        private void OnUp()
        {
            if (_root == null) return;
            string current = _root.Name;
            if (string.IsNullOrEmpty(current)) return;
            string parent;
            try { parent = System.IO.Path.GetDirectoryName(current); }
            catch { parent = null; }
            if (string.IsNullOrEmpty(parent))
            {
                _statusLabel.Text = "Already at the top — " + current + " has no parent.";
                return;
            }
            StartScan(parent);
        }

        // Re-stat a single file node and propagate the size delta up its ancestors.
        // Falls back to rescanning the parent directory if the file is missing or
        // unreadable, so the tree stays in sync with disk.
        private void RefreshFileNode(DirNode fileNode)
        {
            if (fileNode == null || fileNode.IsDirectory) return;
            string path = fileNode.FullPath;

            FileInfo fi;
            try { fi = new FileInfo(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not refresh:\n" + path + "\n\n" + ex.Message,
                    "DirStat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!fi.Exists)
            {
                if (fileNode.Parent != null)
                    StartScan(fileNode.Parent.FullPath);
                return;
            }

            long newSize;
            try { newSize = fi.Length; }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not read size of:\n" + path + "\n\n" + ex.Message,
                    "DirStat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            long delta = newSize - fileNode.Size;
            fileNode.Size = newSize;

            WinFormsTreeNode tn;
            if (_nodeIndex.TryGetValue(fileNode, out tn))
                tn.Text = FormatTreeLabel(fileNode);

            for (var p = fileNode.Parent; p != null; p = p.Parent)
            {
                p.Size += delta;
                WinFormsTreeNode ptn;
                if (_nodeIndex.TryGetValue(p, out ptn))
                    ptn.Text = FormatTreeLabel(p);
            }

            // Re-render treemap so the rectangles reflect the new size.
            _treemap.SetRoot(_root);

            _statusLabel.Text = string.Format("Refreshed {0} ({1})", path,
                delta == 0 ? "no change"
                : (delta > 0 ? "+" : "-") + FormatBytes(Math.Abs(delta)));
        }

        // Rescan a directory subtree in place: replace its children with a fresh scan
        // while preserving the visible root. Sizes/labels on ancestors are updated.
        private void RescanSubtree(DirNode targetNode)
        {
            if (targetNode == null || !targetNode.IsDirectory) return;
            string path = targetNode.FullPath;
            if (string.IsNullOrEmpty(path)) return;

            // Rescanning the visible root is equivalent to a full refresh.
            if (targetNode == _root)
            {
                StartScan(path);
                return;
            }

            OnStop();
            _scanCts = new CancellationTokenSource();
            var ct = _scanCts.Token;

            _statusLabel.Text = "Rescanning " + path + "…";
            _statusProgress.Visible = true;

            var scanner = new Scanner(path, OnProgressFromScanner, ct);
            _scanTask = scanner.RunAsync();
            DirNode captured = targetNode;
            _scanTask.ContinueWith(t => OnSubtreeRescanDone(t, captured, path),
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OnSubtreeRescanDone(Task<ScanResult> t, DirNode targetNode, string path)
        {
            _statusProgress.Visible = false;
            if (t.IsFaulted)
            {
                string emsg = "unknown";
                if (t.Exception != null) emsg = t.Exception.GetBaseException().Message;
                _statusLabel.Text = "Rescan failed: " + emsg;
                return;
            }
            if (t.IsCanceled)
            {
                _statusLabel.Text = "Rescan canceled.";
                return;
            }
            var r = t.Result;

            long oldSize = targetNode.Size;

            // Re-parent the fresh children onto the existing targetNode and adopt the
            // new size/counts. Only Size propagates to ancestors (FileCount/DirCount
            // are immediate-children counts, matching how the scanner records them).
            var newChildren = r.Root.Children != null ? r.Root.Children : new List<DirNode>();
            foreach (var c in newChildren) ReparentRecursive(c, targetNode);

            targetNode.Children = newChildren;
            targetNode.Size = r.Root.Size;
            targetNode.FileCount = r.Root.FileCount;
            targetNode.DirCount = r.Root.DirCount;

            long sizeDelta = r.Root.Size - oldSize;
            for (var p = targetNode.Parent; p != null; p = p.Parent)
                p.Size += sizeDelta;

            // Re-mark system flags on the new children (their IsSystem inherits
            // from the rescanned targetNode if it was system, else is computed).
            if (targetNode.IsSystem)
            {
                foreach (var c in newChildren) MarkAllSystem(c);
            }
            else
            {
                string basePath = targetNode.FullPath;
                foreach (var c in newChildren)
                {
                    string sep = (basePath.EndsWith("\\") || basePath.EndsWith("/")) ? "" : "\\";
                    MarkSystemRec(c, basePath + sep + c.Name);
                }
            }

            // Refresh Display* before reading labels and rebuilding tree nodes.
            _root.RecomputeDisplay(_minBytes, EffectiveExcludeSystem);

            // Rebuild the WinForms tree under targetNode and refresh ancestor labels.
            WinFormsTreeNode tn;
            if (_nodeIndex.TryGetValue(targetNode, out tn))
            {
                RemoveSubtreeIndex(tn);

                _tree.BeginUpdate();
                try
                {
                    bool wasExpanded = tn.IsExpanded;
                    tn.Nodes.Clear();
                    if (targetNode.Children != null && HasVisibleChildren(targetNode))
                    {
                        if (wasExpanded)
                        {
                            var visible = new List<WinFormsTreeNode>(targetNode.Children.Count);
                            for (int i = 0; i < targetNode.Children.Count; i++)
                            {
                                if (targetNode.Children[i].DisplaySize <= 0) continue;
                                visible.Add(MakeTreeNode(targetNode.Children[i]));
                            }
                            if (visible.Count > 0)
                                tn.Nodes.AddRange(visible.ToArray());
                            tn.Expand();
                        }
                        else
                        {
                            tn.Nodes.Add(new WinFormsTreeNode("…"));
                        }
                    }
                    tn.Text = FormatTreeLabel(targetNode);
                    for (var p = targetNode.Parent; p != null; p = p.Parent)
                    {
                        WinFormsTreeNode ptn;
                        if (_nodeIndex.TryGetValue(p, out ptn))
                            ptn.Text = FormatTreeLabel(p);
                    }
                }
                finally { _tree.EndUpdate(); }
            }

            // Rebuild extension stats from the entire tree (the scanner's stats only
            // cover the rescanned subtree).
            var stats = new ExtensionStats();
            AccumulateExtensions(_root, stats);
            _colorMap.Build(stats);
            PopulateExtList(stats, _root.Size);

            // If the user was zoomed into a node inside the rescanned subtree, the
            // old descendant DirNodes are gone — fall back the view root to the
            // (still-valid) rescanned node itself.
            if (_treemap.ViewRoot != targetNode && IsAncestorOrSelf(targetNode, _treemap.ViewRoot))
                _treemap.SetViewRoot(targetNode);

            // Re-render (preserve any active zoom). Reset the highlight to targetNode
            // so any stale reference into the old subtree is dropped.
            _treemap.Rerender();
            _treemap.SetHighlight(targetNode);

            UpdateFilteredStatus();
            _statusLabel.Text = string.Format("Rescanned {0}. {1}, {2} files.",
                path, FormatBytes(targetNode.DisplaySize),
                targetNode.DisplayFileCount.ToString("N0"));
        }

        private static void ReparentRecursive(DirNode n, DirNode newParent)
        {
            n.Parent = newParent;
            if (n.IsDirectory && n.Children != null)
            {
                foreach (var c in n.Children)
                    ReparentRecursive(c, n);
            }
        }

        private void RemoveSubtreeIndex(WinFormsTreeNode tn)
        {
            foreach (WinFormsTreeNode child in tn.Nodes)
            {
                DirNode dn = child.Tag as DirNode;
                if (dn != null) _nodeIndex.Remove(dn);
                RemoveSubtreeIndex(child);
            }
        }

        private static void AccumulateExtensions(DirNode n, ExtensionStats stats)
        {
            if (n == null) return;
            if (!n.IsDirectory)
            {
                string ext = System.IO.Path.GetExtension(n.Name);
                if (string.IsNullOrEmpty(ext)) ext = "<no extension>";
                stats.Add(ext, n.Size);
                return;
            }
            if (n.Children == null) return;
            foreach (var c in n.Children)
                AccumulateExtensions(c, stats);
        }

        private void OnStop()
        {
            try
            {
                if (_scanCts != null) _scanCts.Cancel();
            }
            catch { }
        }

        private void OnShowInExplorer()
        {
            var sel = _tree.SelectedNode;
            DirNode n = sel != null ? sel.Tag as DirNode : null;
            if (n != null) Win32.OpenInExplorer(n.FullPath);
        }

        private void StartScan(string path)
        {
            OnStop();
            _scanCts = new CancellationTokenSource();
            var ct = _scanCts.Token;

            _tree.Nodes.Clear();
            _nodeIndex.Clear();
            _treemap.SetRoot(null);
            _extList.Items.Clear();

            _statusLabel.Text = "Scanning " + path + "…";
            _statusFiles.Text = "0 files";
            _statusBytes.Text = "0 B";
            _statusProgress.Visible = true;
            Text = "DirStat " + AppVersion + " — " + path;

            var scanner = new Scanner(path, OnProgressFromScanner, ct);
            _scanTask = scanner.RunAsync();
            _scanTask.ContinueWith(t => OnScanDone(t, path), TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OnProgressFromScanner(ScanProgress p)
        {
            // Called from scan threadpool; marshal to UI.
            long files = p.FilesScanned, bytes = p.BytesScanned;
            string current = p.CurrentPath;
            BeginInvoke((MethodInvoker)(() =>
            {
                _statusFiles.Text = files.ToString("N0") + " files";
                _statusBytes.Text = FormatBytes(bytes);
                if (!string.IsNullOrEmpty(current))
                {
                    string truncated = current.Length > 100 ? "…" + current.Substring(current.Length - 99) : current;
                    _statusLabel.Text = "Scanning: " + truncated;
                }
            }));
        }

        private void OnScanDone(Task<ScanResult> t, string path)
        {
            _statusProgress.Visible = false;
            if (t.IsFaulted)
            {
                string emsg = "unknown";
                if (t.Exception != null) emsg = t.Exception.GetBaseException().Message;
                _statusLabel.Text = "Scan failed: " + emsg;
                return;
            }
            if (t.IsCanceled)
            {
                _statusLabel.Text = "Scan canceled.";
                return;
            }
            var r = t.Result;
            _root = r.Root;

            // Mark system-path nodes once per scan. The menu toggle is then
            // shown only when there's something to exclude AND the scan root
            // isn't itself inside a system directory (in which case excluding
            // would hide everything the user picked).
            MarkSystemNodes(_root);
            _rootIsSystem = _root.IsSystem;
            _hasSystemNodes = !_rootIsSystem && AnySystemDescendant(_root);
            _excludeSystemMenuItem.Visible = _hasSystemNodes;
            _excludeSystemMenuItem.Checked = _excludeSystem;
            _breadcrumb.SetSystemExcluded(EffectiveExcludeSystem);

            // Compute adaptive snap points from the actual file-size distribution,
            // then re-pick the closest snap to whatever threshold the user had
            // before — that persists the filter setting across rescans.
            long maxFile = ComputeMaxFileSize(_root);
            _snapPoints = BuildSnapPoints(maxFile);
            _suppressFilterEvent = true;
            try
            {
                _minSizeControl.SnapPoints = _snapPoints;
                _minSizeControl.SetClosestTo(_minBytes);
                _minBytes = _minSizeControl.MinBytes;
            }
            finally { _suppressFilterEvent = false; }

            // Populate Display* (immediate FileCount/DirCount won't survive the
            // recursive count semantics we want for labels) BEFORE rendering.
            _root.RecomputeDisplay(_minBytes, EffectiveExcludeSystem);

            // Build extension stats UI and color map.
            _colorMap.Build(r.Extensions);
            PopulateExtList(r.Extensions, r.Root.Size);

            // Tell the treemap about the new scan root (resets view root and zoom),
            // then build the tree and refresh the status bar.
            _treemap.SetRoot(r.Root);
            RebuildTreeFromRoot();
            UpdateFilteredStatus();

            _statusLabel.Text = string.Format("Done. {0} files, {1} dirs, {2}, {3:F1}s",
                _root.DisplayFileCount.ToString("N0"),
                _root.DisplayDirCount.ToString("N0"),
                FormatBytes(_root.DisplaySize),
                r.Elapsed.TotalSeconds);
        }

        // ------- Size filter -------

        private bool _suppressFilterEvent;

        // Adaptive log-spaced snap points using the 1-1.2-1.5-2-2.5-3-4-5-6-8
        // "nice" mantissa series, aligned to binary units (KB=1024, MB=1024²,
        // GB=1024³, ...) so FormatBytes prints clean labels like "1.5 MB" rather
        // than "1.4 MB" from drift. ~1.25× per step on average.
        private static readonly int[] NiceMantissas = new int[] {
            10, 12, 15, 20, 25, 30, 40, 50, 60, 80
        };
        private static long[] BuildSnapPoints(long maxFileSize)
        {
            var list = new List<long>();
            list.Add(0); // Off

            long[] units = new long[] {
                1024L,                                            // KB
                1024L * 1024L,                                    // MB
                1024L * 1024L * 1024L,                            // GB
                1024L * 1024L * 1024L * 1024L,                    // TB
            };
            int[] subTiers = new int[] { 1, 10, 100 };
            foreach (long unit in units)
            {
                foreach (int sub in subTiers)
                {
                    long baseV = unit * sub;
                    if (baseV < 0) return list.ToArray(); // overflow guard
                    foreach (int m in NiceMantissas)
                    {
                        long value = (m * baseV) / 10;
                        if (value > maxFileSize) return list.ToArray();
                        if (value >= 1024) list.Add(value);
                    }
                }
            }
            return list.ToArray();
        }

        private static long ComputeMaxFileSize(DirNode root)
        {
            long max = 0;
            ComputeMaxFileSize(root, ref max);
            return max;
        }

        private static void ComputeMaxFileSize(DirNode n, ref long max)
        {
            if (n == null) return;
            if (!n.IsDirectory)
            {
                if (n.Size > max) max = n.Size;
                return;
            }
            if (n.Children == null) return;
            for (int i = 0; i < n.Children.Count; i++)
                ComputeMaxFileSize(n.Children[i], ref max);
        }

        // The system-files exclusion is suppressed when the scan root is itself
        // inside a system directory — otherwise we'd hide everything the user
        // just chose to look at.
        private bool EffectiveExcludeSystem
        {
            get { return _excludeSystem && !_rootIsSystem; }
        }

        private void ToggleExcludeSystem()
        {
            _excludeSystem = !_excludeSystem;
            _excludeSystemMenuItem.Checked = _excludeSystem;
            ApplyFilter();
            _breadcrumb.SetSystemExcluded(EffectiveExcludeSystem);
        }

        // Walk the tree once after a scan to mark which nodes sit inside an
        // OS system path. Uses the parent-chain path to avoid quadratic
        // FullPath rebuilds.
        private static void MarkSystemNodes(DirNode root)
        {
            if (root == null) return;
            MarkSystemRec(root, root.Name);
        }

        private static void MarkSystemRec(DirNode n, string fullPath)
        {
            n.IsSystem = SystemPaths.IsSystem(fullPath);
            if (n.Children == null) return;
            if (n.IsSystem)
            {
                // Descendants of a system root are also system — no further checks.
                for (int i = 0; i < n.Children.Count; i++)
                    MarkAllSystem(n.Children[i]);
                return;
            }
            for (int i = 0; i < n.Children.Count; i++)
            {
                var c = n.Children[i];
                string sep = (fullPath.EndsWith("\\") || fullPath.EndsWith("/")) ? "" : "\\";
                MarkSystemRec(c, fullPath + sep + c.Name);
            }
        }

        private static void MarkAllSystem(DirNode n)
        {
            n.IsSystem = true;
            if (n.Children == null) return;
            for (int i = 0; i < n.Children.Count; i++) MarkAllSystem(n.Children[i]);
        }

        private static bool AnySystemDescendant(DirNode n)
        {
            if (n == null || n.Children == null) return false;
            for (int i = 0; i < n.Children.Count; i++)
            {
                var c = n.Children[i];
                if (c.IsSystem) return true;
                if (AnySystemDescendant(c)) return true;
            }
            return false;
        }

        // Recompute Display* on the whole tree, rebuild the TreeView under the
        // current scan root, re-render the treemap, and refresh status totals.
        private void ApplyFilter()
        {
            if (_root == null) return;
            _root.RecomputeDisplay(_minBytes, EffectiveExcludeSystem);
            RebuildTreeFromRoot();
            if (_treemap.ViewRoot != null && _treemap.ViewRoot.DisplaySize <= 0)
                _treemap.SetViewRoot(_root);
            _treemap.Rerender();
            UpdateFilteredStatus();
        }

        // Rebuild the TreeView from the current scan root, honouring the size
        // filter (skips nodes with DisplaySize == 0). Tries to restore the
        // previously selected DirNode.
        private void RebuildTreeFromRoot()
        {
            DirNode prevSelection = _tree.SelectedNode != null
                ? _tree.SelectedNode.Tag as DirNode : null;

            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                _nodeIndex.Clear();
                if (_root != null && (_root.DisplaySize > 0 || _minBytes == 0))
                {
                    var rootNode = MakeTreeNode(_root);
                    rootNode.Expand();
                    _tree.Nodes.Add(rootNode);
                    _tree.SelectedNode = rootNode;
                }
            }
            finally { _tree.EndUpdate(); }

            if (prevSelection != null && prevSelection != _root && prevSelection.DisplaySize > 0)
                Treemap_SelectionChanged(this, prevSelection);
        }

        private void UpdateFilteredStatus()
        {
            if (_root == null)
            {
                _minSizeControl.Detail = "";
                return;
            }
            string filtered = _root.DisplayFileCount.ToString("N0") + " files / "
                              + FormatBytes(_root.DisplaySize);
            _statusFiles.Text = _root.DisplayFileCount.ToString("N0") + " files";
            _statusBytes.Text = FormatBytes(_root.DisplaySize);
            _minSizeControl.Detail = "(" + filtered + ")";
        }

        // ------- Tree -------

        private WinFormsTreeNode MakeTreeNode(DirNode n)
        {
            var t = new WinFormsTreeNode(FormatTreeLabel(n)) { Tag = n };
            if (n.IsDirectory && HasVisibleChildren(n))
            {
                t.Nodes.Add(new WinFormsTreeNode("…")); // placeholder; expanded lazily
            }
            _nodeIndex[n] = t;
            return t;
        }

        // True if `n` is a directory with at least one descendant that survives
        // the current size filter — i.e. it should show an expander in the tree.
        private static bool HasVisibleChildren(DirNode n)
        {
            if (!n.IsDirectory || n.Children == null) return false;
            for (int i = 0; i < n.Children.Count; i++)
                if (n.Children[i].DisplaySize > 0) return true;
            return false;
        }

        private static string FormatTreeLabel(DirNode n)
        {
            if (n.IsDirectory)
                return n.Name + "  (" + FormatBytes(n.DisplaySize) + ", " +
                       n.DisplayFileCount.ToString("N0") + " files)";
            return n.Name + "  (" + FormatBytes(n.DisplaySize) + ")";
        }

        private void Tree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            DirNode dn = e.Node != null ? e.Node.Tag as DirNode : null;
            if (dn != null && e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag == null)
            {
                e.Node.Nodes.Clear();
                if (dn.Children != null)
                {
                    // Skip children that are entirely below the size filter.
                    var visible = new List<WinFormsTreeNode>(dn.Children.Count);
                    for (int i = 0; i < dn.Children.Count; i++)
                    {
                        if (dn.Children[i].DisplaySize <= 0) continue;
                        visible.Add(MakeTreeNode(dn.Children[i]));
                    }
                    if (visible.Count > 0)
                        e.Node.Nodes.AddRange(visible.ToArray());
                }
            }
        }

        private void Tree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (_suppressTreeSelect) return;
            DirNode n = e.Node != null ? e.Node.Tag as DirNode : null;
            if (n != null)
            {
                _treemap.SetHighlight(n);
                _statusLabel.Text = n.FullPath;
            }
        }

        private void Tree_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            DirNode n = e.Node != null ? e.Node.Tag as DirNode : null;
            if (n == null) return;
            _tree.SelectedNode = e.Node;
            ShowNodeContextMenu(n, _tree.PointToScreen(new Point(e.X, e.Y)));
        }

        // ------- Context menu -------

        private void ShowNodeContextMenu(DirNode node, Point screenPoint)
        {
            if (node == null) return;
            string path = node.FullPath;
            if (string.IsNullOrEmpty(path)) return;

            string parentDir;
            try { parentDir = System.IO.Path.GetDirectoryName(path); }
            catch { parentDir = null; }

            var menu = new ContextMenuStrip();
            menu.BackColor = MenuBg;
            menu.ForeColor = MenuFg;
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());

            // Rescan items first.
            DirNode capturedNode = node;
            DirNode parentNode = node.Parent;
            if (node.IsDirectory)
            {
                menu.Items.Add(NewItem("&Rescan this folder",
                    delegate { RescanSubtree(capturedNode); }));
            }
            else
            {
                menu.Items.Add(NewItem("&Refresh", delegate { RefreshFileNode(capturedNode); }));
                var rescanParent = NewItem("&Rescan parent folder", delegate
                {
                    if (parentNode != null && parentNode.IsDirectory)
                        RescanSubtree(parentNode);
                });
                rescanParent.Enabled = parentNode != null && parentNode.IsDirectory
                                       && !string.IsNullOrEmpty(parentNode.FullPath);
                menu.Items.Add(rescanParent);
            }
            menu.Items.Add(new ToolStripSeparator());

            if (node.IsDirectory)
            {
                menu.Items.Add(NewItem("Open in &Explorer", delegate { ShellOpen(path); }));
                menu.Items.Add(NewItem("Open in &Command Prompt", delegate { OpenTerminal(path, false); }));
                menu.Items.Add(NewItem("Open in &PowerShell", delegate { OpenTerminal(path, true); }));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(NewItem("&Show in Explorer", delegate { Win32.OpenInExplorer(path); }));
            }
            else
            {
                menu.Items.Add(NewItem("&Open", delegate { ShellOpen(path); }));
                menu.Items.Add(NewItem("&Show in Explorer", delegate { Win32.OpenInExplorer(path); }));
                menu.Items.Add(new ToolStripSeparator());
                if (!string.IsNullOrEmpty(parentDir))
                {
                    menu.Items.Add(NewItem("Open containing &folder", delegate { ShellOpen(parentDir); }));
                    menu.Items.Add(NewItem("Open containing folder in &Command Prompt",
                        delegate { OpenTerminal(parentDir, false); }));
                    menu.Items.Add(NewItem("Open containing folder in &PowerShell",
                        delegate { OpenTerminal(parentDir, true); }));
                }
            }
            menu.Items.Add(new ToolStripSeparator());
            DirNode capturedDeleteNode = node;
            var deleteToBin = NewItem("&Delete", delegate { DeleteNode(capturedDeleteNode, false); });
            deleteToBin.ShortcutKeyDisplayString = "Del";
            menu.Items.Add(deleteToBin);
            var deletePerma = NewItem("Delete &permanently", delegate { DeleteNode(capturedDeleteNode, true); });
            deletePerma.ShortcutKeyDisplayString = "Shift+Del";
            menu.Items.Add(deletePerma);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(NewItem("Copy &path", delegate
            {
                try { Clipboard.SetText(path); } catch { /* clipboard can fail under remote sessions */ }
            }));

            ApplyDarkMenuColors(menu.Items);
            menu.Show(screenPoint);
        }

        private static ToolStripMenuItem NewItem(string text, EventHandler onClick)
        {
            var it = new ToolStripMenuItem(text);
            it.Click += onClick;
            return it;
        }

        private void ShellOpen(string path)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open:\n" + path + "\n\n" + ex.Message,
                    "DirStat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenTerminal(string dir, bool powershell)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = powershell ? "powershell.exe" : "cmd.exe",
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not start terminal in:\n" + dir + "\n\n" + ex.Message,
                    "DirStat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------- Extension List -------

        private void PopulateExtList(ExtensionStats stats, long totalBytes)
        {
            _extList.Items.Clear();
            if (stats == null) return;
            var list = stats.GetSortedBySize();
            foreach (var e in list)
            {
                Color c;
                _colorMap.Map.TryGetValue(e.Extension, out c);
                if (c == Color.Empty) c = _colorMap.Fallback;

                var lvi = new ListViewItem(""); // color swatch column
                lvi.UseItemStyleForSubItems = false;
                lvi.SubItems.Add(e.Extension);
                lvi.SubItems.Add(FormatBytes(e.TotalSize));
                lvi.SubItems.Add(totalBytes > 0
                    ? (100.0 * e.TotalSize / totalBytes).ToString("F2") + "%"
                    : "");
                lvi.SubItems.Add(e.FileCount.ToString("N0"));
                lvi.Tag = c;
                _extList.Items.Add(lvi);
            }
        }

        private void ExtList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_extList.SelectedItems.Count == 0)
            {
                _treemap.SetExtensionHighlight(null);
                return;
            }
            var lvi = _extList.SelectedItems[0];
            // SubItems[1] is the Extension column.
            string ext = lvi.SubItems.Count > 1 ? lvi.SubItems[1].Text : null;
            _treemap.SetExtensionHighlight(ext);
            if (!string.IsNullOrEmpty(ext))
                _statusLabel.Text = "Highlighting " + ext;
        }

        private void ExtList_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            // Background
            var bg = e.ItemIndex % 2 == 0 ? Color.FromArgb(40, 40, 40) : Color.FromArgb(46, 46, 46);
            if (e.Item.Selected) bg = Color.FromArgb(60, 90, 140);
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);

            if (e.ColumnIndex == 0)
            {
                // Color swatch
                Color swatch = (e.Item.Tag is Color) ? (Color)e.Item.Tag : _colorMap.Fallback;
                var r = e.Bounds; r.Inflate(-4, -3);
                using (var b = new SolidBrush(swatch)) e.Graphics.FillRectangle(b, r);
                using (var p = new Pen(Color.FromArgb(20, 20, 20))) e.Graphics.DrawRectangle(p, r);
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _extList.Font, e.Bounds,
                    Color.White,
                    TextFormatFlags.VerticalCenter
                    | (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left)
                    | TextFormatFlags.LeftAndRightPadding
                    | TextFormatFlags.EndEllipsis);
            }
        }

        // ------- Treemap selection -> tree -------

        private void Treemap_SelectionChanged(object sender, DirNode node)
        {
            if (node == null) return;
            // Walk parent chain to ensure the path is expanded in the tree.
            var stack = new Stack<DirNode>();
            for (var c = node; c != null; c = c.Parent) stack.Push(c);
            WinFormsTreeNode current = null;
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (current == null)
                {
                    if (_tree.Nodes.Count > 0 && _tree.Nodes[0].Tag == d) current = _tree.Nodes[0];
                    else return;
                }
                else
                {
                    if (current.Nodes.Count == 1 && current.Nodes[0].Tag == null)
                    {
                        // Trigger lazy-load.
                        current.Expand();
                    }
                    WinFormsTreeNode found = null;
                    foreach (WinFormsTreeNode tn in current.Nodes)
                    {
                        if (tn.Tag == d) { found = tn; break; }
                    }
                    if (found == null) return;
                    current = found;
                }
            }
            if (current != null)
            {
                _suppressTreeSelect = true;
                try
                {
                    current.EnsureVisible();
                    _tree.SelectedNode = current;
                }
                finally { _suppressTreeSelect = false; }
                _statusLabel.Text = node.FullPath;
            }
        }

        // ------- Helpers -------

        public const string AppVersion = "0.7";

        public static string FormatBytes(long bytes)
        {
            const double K = 1024.0;
            if (bytes < K) return bytes + " B";
            double v = bytes / K;
            if (v < K) return v.ToString("F1") + " KB";
            v /= K;
            if (v < K) return v.ToString("F1") + " MB";
            v /= K;
            if (v < K) return v.ToString("F2") + " GB";
            v /= K;
            return v.ToString("F2") + " TB";
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Color.FromArgb(70, 70, 70); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(96, 96, 96); } }
            public override Color MenuBorder { get { return Color.FromArgb(64, 64, 64); } }
            public override Color ToolStripDropDownBackground { get { return Color.FromArgb(48, 48, 48); } }
            public override Color ImageMarginGradientBegin { get { return Color.FromArgb(48, 48, 48); } }
            public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(48, 48, 48); } }
            public override Color ImageMarginGradientEnd { get { return Color.FromArgb(48, 48, 48); } }
            public override Color MenuItemPressedGradientBegin { get { return Color.FromArgb(60, 60, 60); } }
            public override Color MenuItemPressedGradientEnd { get { return Color.FromArgb(60, 60, 60); } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(70, 70, 70); } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(70, 70, 70); } }
        }
    }

    // Custom panel that owns the rendered treemap bitmap and handles hit-testing.
    public sealed class TreemapPanel : Control
    {
        private DirNode _root;       // scan root (top of the loaded tree)
        private DirNode _viewRoot;   // node currently filling the panel (drill-down zoom)
        private DirNode _highlightNode;
        private string _highlightExt;
        private Bitmap _bmp;
        private readonly TreemapRenderer _renderer = new TreemapRenderer();
        private RectangleF? _highlightRect;
        private bool _renderPending;
        private int _wheelAccum;

        // Wheel delta units required for one zoom step. One wheel notch is 120
        // units, so 240 means "two notches per zoom step" — half as aggressive
        // as the default. Exposed so a future Settings UI can tune it.
        public int WheelZoomThreshold = 240;

        public ExtensionColorMap ColorMap { get; set; }
        public event EventHandler<DirNode> SelectionChanged;
        public event EventHandler<DirNode> NodeActivated;
        public event Action<DirNode, Point> ContextRequested; // location is in screen coords
        public event EventHandler<DirNode> ViewRootChanged;

        public DirNode Root { get { return _root; } }
        public DirNode ViewRoot { get { return _viewRoot; } }

        public TreemapPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                     | ControlStyles.Selectable, true);
            TabStop = true; // accept focus so wheel events arrive
            BackColor = Color.FromArgb(28, 28, 28);
        }

        public void SetRoot(DirNode root)
        {
            _root = root;
            _viewRoot = root;
            _highlightNode = root;
            _highlightExt = null;
            ScheduleRender();
            var h = ViewRootChanged;
            if (h != null) h(this, _viewRoot);
        }

        // Trigger a re-render without resetting view root / highlight. Use this
        // after in-place edits (delete, subtree rescan) that change the tree
        // contents but should preserve any zoom the user has set.
        public void Rerender()
        {
            ScheduleRender();
        }

        // Set the drill-down view root. Pass null to reset to the scan root.
        public void SetViewRoot(DirNode node)
        {
            if (node == null) node = _root;
            if (node == null) return;
            if (_viewRoot == node) return;
            _viewRoot = node;
            _highlightNode = node;
            ScheduleRender();
            var vr = ViewRootChanged;
            if (vr != null) vr(this, _viewRoot);
            var sc = SelectionChanged;
            if (sc != null) sc(this, _viewRoot);
        }

        // Zoom in toward a panel-local point: find the item at that point and
        // walk its ancestor chain up to the child of the current view root.
        public void ZoomToward(PointF panelPoint)
        {
            if (_viewRoot == null || _renderer.Items == null) return;
            var item = _renderer.ItemAt(panelPoint);
            if (item == null) return;
            var candidate = item.Node;
            while (candidate != null && candidate.Parent != _viewRoot)
                candidate = candidate.Parent;
            if (candidate != null && candidate.IsDirectory
                && candidate.Children != null && candidate.Children.Count > 0)
                SetViewRoot(candidate);
        }

        // Zoom into the path of `target`. If target is null or doesn't descend
        // from the current view root, zoom into the largest child of the view root.
        public void ZoomInOnTarget(DirNode target)
        {
            if (_viewRoot == null) return;
            DirNode candidate = null;
            if (target != null)
            {
                candidate = target;
                while (candidate != null && candidate.Parent != _viewRoot)
                    candidate = candidate.Parent;
            }
            if (candidate == null && _viewRoot.Children != null && _viewRoot.Children.Count > 0)
                candidate = _viewRoot.Children[0]; // largest (children sorted by size desc)
            if (candidate != null && candidate.IsDirectory
                && candidate.Children != null && candidate.Children.Count > 0)
                SetViewRoot(candidate);
        }

        public void ZoomOut()
        {
            if (_viewRoot == null || _viewRoot.Parent == null) return;
            SetViewRoot(_viewRoot.Parent);
        }

        public void ZoomReset()
        {
            if (_root == null) return;
            SetViewRoot(_root);
        }

        public bool IsZoomed { get { return _viewRoot != null && _viewRoot != _root; } }

        public void SetHighlight(DirNode node)
        {
            _highlightNode = node;
            UpdateHighlightRect();
            Invalidate();
        }

        public void SetExtensionHighlight(string extension)
        {
            if (_highlightExt == extension) return;
            _highlightExt = extension;
            Invalidate();
        }

        private static bool ExtensionMatches(DirNode node, string ext)
        {
            if (node == null || node.IsDirectory) return false;
            string e = System.IO.Path.GetExtension(node.Name);
            if (string.IsNullOrEmpty(e)) e = "<no extension>";
            return string.Equals(e, ext, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateHighlightRect()
        {
            _highlightRect = null;
            if (_highlightNode == null || _renderer.Items == null) return;
            // For directories, compute union of all descendant rects belonging to them.
            // Simpler: iterate items, intersect with node ancestry.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool found = false;
            foreach (var it in _renderer.Items)
            {
                if (IsDescendant(it.Node, _highlightNode))
                {
                    found = true;
                    var r = it.Rect;
                    if (r.X < minX) minX = r.X;
                    if (r.Y < minY) minY = r.Y;
                    if (r.Right > maxX) maxX = r.Right;
                    if (r.Bottom > maxY) maxY = r.Bottom;
                }
            }
            if (found)
                _highlightRect = new RectangleF(minX, minY, maxX - minX, maxY - minY);
        }

        private static bool IsDescendant(DirNode candidate, DirNode root)
        {
            var c = candidate;
            while (c != null)
            {
                if (c == root) return true;
                c = c.Parent;
            }
            return false;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ScheduleRender();
        }

        private void ScheduleRender()
        {
            if (_renderPending) return;
            if (!IsHandleCreated)
            {
                // Defer: render once the handle exists.
                EventHandler once = null;
                once = delegate(object s, EventArgs e)
                {
                    HandleCreated -= once;
                    ScheduleRender();
                };
                HandleCreated += once;
                return;
            }
            _renderPending = true;
            BeginInvoke((MethodInvoker)delegate()
            {
                _renderPending = false;
                RenderNow();
                Invalidate();
            });
        }

        private void RenderNow()
        {
            DirNode renderRoot = _viewRoot != null ? _viewRoot : _root;
            if (Width < 4 || Height < 4 || renderRoot == null)
            {
                if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
                return;
            }
            int w = Width, h = Height;
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            Func<DirNode, Color> picker;
            if (ColorMap != null)
                picker = delegate(DirNode n) { return ColorMap.GetForNode(n); };
            else
                picker = delegate(DirNode n) { return Color.Gray; };
            _renderer.Render(bmp, renderRoot, picker, BackColor);
            if (_bmp != null) _bmp.Dispose();
            _bmp = bmp;
            UpdateHighlightRect();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_bmp != null)
            {
                e.Graphics.DrawImageUnscaled(_bmp, 0, 0);
            }
            else
            {
                using (var b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
                TextRenderer.DrawText(e.Graphics, "No data — open a folder or drive to begin.", Font,
                    ClientRectangle, Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            if (!string.IsNullOrEmpty(_highlightExt) && _renderer.Items != null)
            {
                using (var fill = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
                using (var border = new Pen(Color.White, 1.5f))
                {
                    foreach (var it in _renderer.Items)
                    {
                        if (!ExtensionMatches(it.Node, _highlightExt)) continue;
                        var r = it.Rect;
                        if (r.Width < 1f || r.Height < 1f) continue;
                        e.Graphics.FillRectangle(fill, r);
                        if (r.Width >= 3f && r.Height >= 3f)
                            e.Graphics.DrawRectangle(border, r.X, r.Y, r.Width - 1f, r.Height - 1f);
                    }
                }
            }
            if (_highlightRect.HasValue)
            {
                using (var p = new Pen(Color.White, 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
                {
                    var r = _highlightRect.Value;
                    e.Graphics.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                }
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_renderer.Items == null) return;
            var item = _renderer.ItemAt(new PointF(e.X, e.Y));
            if (item == null) return;

            if (e.Button == MouseButtons.Left)
            {
                _highlightNode = item.Node;
                UpdateHighlightRect();
                Invalidate();
                var h = SelectionChanged;
                if (h != null) h(this, item.Node);
            }
            else if (e.Button == MouseButtons.Right)
            {
                // Highlight too, so users see what they're acting on.
                _highlightNode = item.Node;
                UpdateHighlightRect();
                Invalidate();
                var sel = SelectionChanged;
                if (sel != null) sel(this, item.Node);
                var ctx = ContextRequested;
                if (ctx != null) ctx(item.Node, PointToScreen(new Point(e.X, e.Y)));
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_renderer.Items == null) return;
            var item = _renderer.ItemAt(new PointF(e.X, e.Y));
            if (item != null)
            {
                var h = NodeActivated;
                if (h != null) h(this, item.Node);
            }
        }

        // Grab focus on mouse enter so Ctrl+wheel zoom works without an extra click.
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (CanFocus && !Focused) Focus();
        }

        // Ctrl + wheel up: zoom toward the cursor; Ctrl + wheel down: zoom out.
        // Wheel deltas accumulate; one zoom step fires every WheelZoomThreshold
        // units (default 240 → two notches per step). Drops the sensitivity so
        // a casual scroll doesn't drill several levels deep instantly.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((Control.ModifierKeys & Keys.Control) != Keys.Control) return;
            if (_viewRoot == null) return;

            // Reset accumulator if direction changed.
            if ((_wheelAccum > 0 && e.Delta < 0) || (_wheelAccum < 0 && e.Delta > 0))
                _wheelAccum = 0;
            _wheelAccum += e.Delta;
            int threshold = Math.Max(120, WheelZoomThreshold);
            while (_wheelAccum >= threshold)
            {
                _wheelAccum -= threshold;
                ZoomToward(new PointF(e.X, e.Y));
            }
            while (_wheelAccum <= -threshold)
            {
                _wheelAccum += threshold;
                ZoomOut();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _bmp != null) _bmp.Dispose();
            base.Dispose(disposing);
        }
    }

    // Clickable breadcrumb showing the current zoom path from the scan root down
    // to the current view root. Click any segment to jump back to that level;
    // click the × at the end to reset zoom.
    public sealed class BreadcrumbBar : Control
    {
        private DirNode[] _segments = new DirNode[0]; // scanRoot ... viewRoot
        private Rectangle[] _segRects = new Rectangle[0];
        private Rectangle _resetRect;
        private int _hoverIndex = -2; // -2 = none, -1 = reset, >=0 = segment
        private bool _systemExcluded;

        public event Action<DirNode> SegmentClicked;
        public event Action ResetClicked;

        public void SetSystemExcluded(bool value)
        {
            if (_systemExcluded == value) return;
            _systemExcluded = value;
            Invalidate();
        }

        public BreadcrumbBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(40, 40, 40);
            ForeColor = Color.FromArgb(200, 200, 200);
            Height = 26;
        }

        public void SetPath(IList<DirNode> path)
        {
            if (path == null) _segments = new DirNode[0];
            else
            {
                _segments = new DirNode[path.Count];
                for (int i = 0; i < path.Count; i++) _segments[i] = path[i];
            }
            _hoverIndex = -2;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

            if (_segments.Length == 0)
            {
                TextRenderer.DrawText(g, "No scan loaded", Font, ClientRectangle,
                    Color.FromArgb(140, 140, 140),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left
                    | TextFormatFlags.LeftAndRightPadding);
                _segRects = new Rectangle[0];
                _resetRect = Rectangle.Empty;
                return;
            }

            _segRects = new Rectangle[_segments.Length];

            int padX = 8, sepW = 12;
            int x = padX;
            int y = 0, h = ClientSize.Height;
            int availableW = ClientSize.Width - padX * 2 - sepW; // reserve for × at end
            int rightLimit = padX + availableW;

            // Pre-measure segments to decide if we need to collapse with "…".
            string[] labels = new string[_segments.Length];
            int[] widths = new int[_segments.Length];
            int totalNeeded = 0;
            for (int i = 0; i < _segments.Length; i++)
            {
                labels[i] = LabelFor(_segments[i], i == 0);
                widths[i] = TextRenderer.MeasureText(g, labels[i], Font).Width + 12;
                totalNeeded += widths[i] + sepW;
            }
            // If we don't fit, drop oldest middle segments (keep first and last few).
            int dropFrom = 1, dropTo = 0; // exclusive range that gets collapsed to "…"
            if (totalNeeded > availableW && _segments.Length > 2)
            {
                int ellipsisW = TextRenderer.MeasureText(g, "…", Font).Width + 12 + sepW;
                int used = widths[0] + sepW + widths[_segments.Length - 1] + sepW + ellipsisW;
                dropFrom = 1;
                dropTo = _segments.Length - 1;
                // Try to add segments back, prefer the rightmost (closest to current).
                for (int i = _segments.Length - 2; i >= 1; i--)
                {
                    if (used + widths[i] + sepW <= availableW)
                    {
                        used += widths[i] + sepW;
                        dropTo = i;
                    }
                    else break;
                }
            }

            Color sepColor = Color.FromArgb(110, 110, 110);
            for (int i = 0; i < _segments.Length; i++)
            {
                if (i >= dropFrom && i < dropTo)
                {
                    // Skip — collapsed into single ellipsis after the first segment.
                    if (i == dropFrom)
                    {
                        x = DrawSeparator(g, x, y, h, sepColor);
                        var er = new Rectangle(x, y + 2, TextRenderer.MeasureText(g, "…", Font).Width + 12, h - 4);
                        TextRenderer.DrawText(g, "…", Font, er, Color.FromArgb(140, 140, 140),
                            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                        x = er.Right;
                    }
                    _segRects[i] = Rectangle.Empty;
                    continue;
                }

                if (i > 0) x = DrawSeparator(g, x, y, h, sepColor);

                bool isLast = (i == _segments.Length - 1);
                bool isHover = (_hoverIndex == i);
                Color fg = isLast
                    ? Color.White
                    : (isHover ? Color.White : ForeColor);
                Color seg_bg = isHover ? Color.FromArgb(56, 56, 56) : Color.Transparent;

                var rect = new Rectangle(x, y + 2, widths[i], h - 4);
                if (rect.Right > rightLimit) rect.Width = rightLimit - rect.X;
                if (rect.Width < 4) { _segRects[i] = Rectangle.Empty; break; }

                if (seg_bg != Color.Transparent)
                    using (var b = new SolidBrush(seg_bg)) g.FillRectangle(b, rect);
                TextRenderer.DrawText(g, labels[i], Font, rect, fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter
                    | TextFormatFlags.EndEllipsis);
                _segRects[i] = rect;
                x = rect.Right;
            }

            // Reset × at the far right (only when zoomed).
            int rightX = ClientSize.Width - padX;
            if (_segments.Length > 1)
            {
                int rx = rightX - 18;
                _resetRect = new Rectangle(rx, y + 4, 16, h - 8);
                Color rfg = (_hoverIndex == -1) ? Color.White : Color.FromArgb(160, 160, 160);
                Color rbg = (_hoverIndex == -1) ? Color.FromArgb(56, 56, 56) : Color.Transparent;
                if (rbg != Color.Transparent)
                    using (var b = new SolidBrush(rbg)) g.FillRectangle(b, _resetRect);
                TextRenderer.DrawText(g, "×", Font, _resetRect, rfg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                rightX = rx - 8;
            }
            else _resetRect = Rectangle.Empty;

            // Small "system hidden" badge to the left of the reset button.
            if (_systemExcluded)
            {
                string txt = "system hidden";
                int tw = TextRenderer.MeasureText(g, txt, Font).Width + 8;
                var br = new Rectangle(rightX - tw, y + 4, tw, h - 8);
                using (var bb = new SolidBrush(Color.FromArgb(60, 50, 30)))
                    g.FillRectangle(bb, br);
                TextRenderer.DrawText(g, txt, Font, br, Color.FromArgb(220, 190, 120),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            }
        }

        private int DrawSeparator(Graphics g, int x, int y, int h, Color color)
        {
            int w = 12;
            var r = new Rectangle(x, y + 2, w, h - 4);
            TextRenderer.DrawText(g, "›", Font, r, color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            return x + w;
        }

        private static string LabelFor(DirNode n, bool isRoot)
        {
            if (n == null) return "";
            string s = n.Name;
            if (string.IsNullOrEmpty(s)) return isRoot ? "(root)" : "";
            return s;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int newHover = HitTest(e.X, e.Y);
            if (newHover != _hoverIndex)
            {
                _hoverIndex = newHover;
                Cursor = (newHover != -2) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -2)
            {
                _hoverIndex = -2;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int hit = HitTest(e.X, e.Y);
            if (hit == -1)
            {
                var rh = ResetClicked;
                if (rh != null) rh();
            }
            else if (hit >= 0 && hit < _segments.Length)
            {
                var sh = SegmentClicked;
                if (sh != null) sh(_segments[hit]);
            }
        }

        private int HitTest(int x, int y)
        {
            if (_resetRect.Width > 0 && _resetRect.Contains(x, y)) return -1;
            for (int i = 0; i < _segRects.Length; i++)
            {
                if (_segRects[i].Width > 0 && _segRects[i].Contains(x, y))
                    return i;
            }
            return -2;
        }
    }

    // Min-file-size threshold control hosted in the status bar. Drawn as a
    // horizontal slider with a label to the right ("Min: 4 MB (12k / 380 GB)").
    // Left-click on the track jumps the thumb; drag scrubs; wheel/arrow keys
    // step one snap at a time. Raises ValueChanged when the index changes.
    public sealed class MinSizeControl : Control
    {
        private long[] _snapPoints = new long[] { 0 };
        private int _index = 0;
        private string _detail = "";
        private bool _hover;
        private bool _dragging;

        // Layout: a track on the left, a text label on the right.
        private const int LabelAreaW = 170;
        private const int TrackPadL = 6;
        private const int TrackPadR = 6;
        private const int TrackThickness = 4;
        private const int ThumbW = 8;
        private const int ThumbH = 14;

        public event EventHandler ValueChanged;

        public MinSizeControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.FromArgb(56, 56, 56);
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            Size = new Size(360, 22);
        }

        public long[] SnapPoints
        {
            get { return _snapPoints; }
            set
            {
                _snapPoints = (value != null && value.Length > 0) ? value : new long[] { 0 };
                if (_index >= _snapPoints.Length) _index = _snapPoints.Length - 1;
                if (_index < 0) _index = 0;
                Invalidate();
            }
        }

        public long MinBytes { get { return _snapPoints[_index]; } }

        public void SetClosestTo(long bytes)
        {
            if (_snapPoints.Length == 0) return;
            int best = 0;
            long bestDiff = Math.Abs(_snapPoints[0] - bytes);
            for (int i = 1; i < _snapPoints.Length; i++)
            {
                long d = Math.Abs(_snapPoints[i] - bytes);
                if (d < bestDiff) { bestDiff = d; best = i; }
            }
            if (best != _index)
            {
                _index = best;
                OnValueChanged();
            }
            Invalidate();
        }

        public string Detail
        {
            get { return _detail; }
            set { _detail = value ?? ""; Invalidate(); }
        }

        private int TrackX0 { get { return TrackPadL; } }
        private int TrackX1 { get { return ClientSize.Width - LabelAreaW - TrackPadR; } }

        private void OnValueChanged()
        {
            var h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

            int cy = ClientSize.Height / 2;
            int x0 = TrackX0;
            int x1 = TrackX1;
            if (x1 - x0 < 10) x1 = x0 + 10;

            // Track
            var trackRect = new Rectangle(x0, cy - TrackThickness / 2, x1 - x0, TrackThickness);
            using (var b = new SolidBrush(Color.FromArgb(28, 28, 28))) g.FillRectangle(b, trackRect);
            using (var p = new Pen(Color.FromArgb(96, 96, 96))) g.DrawRectangle(p, trackRect);

            // Filled portion left of the thumb
            int thumbX = ThumbXForIndex(_index);
            if (thumbX > x0)
            {
                var filledRect = new Rectangle(x0, cy - TrackThickness / 2, thumbX - x0, TrackThickness);
                using (var b = new SolidBrush(Color.FromArgb(0, 102, 204))) g.FillRectangle(b, filledRect);
            }

            // Thumb
            var thumbRect = new Rectangle(thumbX - ThumbW / 2, cy - ThumbH / 2, ThumbW, ThumbH);
            Color tFill = (_hover || _dragging) ? Color.FromArgb(220, 220, 220) : Color.FromArgb(180, 180, 180);
            using (var b = new SolidBrush(tFill)) g.FillRectangle(b, thumbRect);
            using (var p = new Pen(Color.FromArgb(40, 40, 40))) g.DrawRectangle(p, thumbRect);

            // Right-hand label
            string text = "Min: " + FormatSnap(MinBytes);
            if (!string.IsNullOrEmpty(_detail)) text += "  " + _detail;
            var labelRect = new Rectangle(x1 + TrackPadR, 0,
                ClientSize.Width - x1 - TrackPadR, ClientSize.Height);
            TextRenderer.DrawText(g, text, Font, labelRect, ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private int ThumbXForIndex(int idx)
        {
            if (_snapPoints.Length < 2) return TrackX0;
            double r = (double)idx / (_snapPoints.Length - 1);
            return TrackX0 + (int)Math.Round(r * (TrackX1 - TrackX0));
        }

        private int IndexForX(int x)
        {
            if (_snapPoints.Length < 2) return 0;
            int x0 = TrackX0, x1 = TrackX1;
            if (x <= x0) return 0;
            if (x >= x1) return _snapPoints.Length - 1;
            double r = (x - x0) / (double)(x1 - x0);
            int idx = (int)Math.Round(r * (_snapPoints.Length - 1));
            if (idx < 0) idx = 0;
            if (idx >= _snapPoints.Length) idx = _snapPoints.Length - 1;
            return idx;
        }

        private void SetIndex(int idx, bool fire)
        {
            if (idx < 0) idx = 0;
            if (idx >= _snapPoints.Length) idx = _snapPoints.Length - 1;
            if (idx == _index) return;
            _index = idx;
            Invalidate();
            if (fire) OnValueChanged();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            if (CanFocus && !Focused) Focus();
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_snapPoints.Length < 2) return;
            // Clicks anywhere in the track area jump to that position. Clicks in
            // the label area are ignored.
            if (e.X > TrackX1 + TrackPadR) return;
            _dragging = true;
            Capture = true;
            SetIndex(IndexForX(e.X), true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetIndex(IndexForX(e.X), true);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging)
            {
                _dragging = false;
                Capture = false;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_snapPoints.Length < 2) return;
            int delta = e.Delta > 0 ? +1 : -1;
            SetIndex(_index + delta, true);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right
                || keyData == Keys.Home || keyData == Keys.End) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_snapPoints.Length < 2) return;
            if (e.KeyCode == Keys.Left) { SetIndex(_index - 1, true); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { SetIndex(_index + 1, true); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { SetIndex(0, true); e.Handled = true; }
            else if (e.KeyCode == Keys.End) { SetIndex(_snapPoints.Length - 1, true); e.Handled = true; }
        }

        public static string FormatSnap(long bytes)
        {
            if (bytes <= 0) return "Off";
            return MainForm.FormatBytes(bytes);
        }
    }
}
