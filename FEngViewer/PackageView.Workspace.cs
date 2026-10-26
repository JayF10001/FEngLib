using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FEngLib.Packages;
using FEngRender.Data;

namespace FEngViewer;

internal enum WorkspaceLeftMode
{
    Files,
    ObjectTree
}

public partial class PackageView
{
    private const int WorkspaceNodeLimit = 1200;

    private sealed class RenderCache
    {
        public RenderCache(RenderTree tree, int version)
        {
            Tree = tree;
            Version = version;
        }

        public RenderTree Tree { get; }
        public int Version { get; }
    }

    /// <summary>Forwards the workspace's UI questions to this form; keeps the state layer UI-neutral.</summary>
    private sealed class WorkspacePrompts : IWorkspacePrompts
    {
        private readonly PackageView _view;

        public WorkspacePrompts(PackageView view) => _view = view;

        public Package ReadPackage(string path, bool isJson) =>
            isJson ? AppService.Instance.ReadJson(path) : AppService.Instance.ReadFile(path);

        public bool WritePackage(WorkspaceDocument document, string path, bool isJson, bool offerOpenFolder) =>
            _view.WriteDocumentFile(document, path, isJson, offerOpenFolder);

        public string ChooseSaveTarget(WorkspaceDocument document, out bool isJson) =>
            _view.ChooseSaveTarget(document, out isJson);

        public SavePromptResult AskToSave(WorkspaceDocument document, string reason) =>
            _view.AskToSaveDocument(document, reason);

        public bool ConfirmDiscard(WorkspaceDocument document, string reason) =>
            _view.ConfirmDiscardDocument(document, reason);

        public void ShowError(string title, string message) =>
            MessageBox.Show(_view, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private DocumentWorkspace _workspace;
    private DocumentTabBar _tabBar;
    private TreeView _workspaceTree;
    private Panel _leftStack;
    private Button _filesModeButton;
    private Button _treeModeButton;
    private Panel _statusBar;
    private Label _statusLeft;
    private Label _statusRight;
    private ContextMenuStrip _workspaceMenu;
    private Font _boldNodeFont;
    private ImageList _workspaceIcons;

    private readonly Dictionary<WorkspaceDocument, RenderCache> _documentTrees = new();
    private readonly Dictionary<string, TreeNode> _workspaceNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recentDocuments = new();
    private WorkspaceDocument _boundDocument;
    private WorkspaceLeftMode _leftMode = WorkspaceLeftMode.ObjectTree;

    private WorkspaceDocument ActiveDocument => _workspace?.Active;

    private void InitializeWorkspace()
    {
        _workspace = new DocumentWorkspace(new WorkspacePrompts(this));
        _boldNodeFont = new Font(treeView1.Font, FontStyle.Bold);

        BuildTabBar();
        BuildLeftPanel();
        BuildMenus();
        BuildStatusBar();

        _workspace.Changed += (_, _) => RefreshWorkspaceStructure();
        _workspace.StateChanged += (_, _) => RefreshWorkspaceState();
        _workspace.ActiveChanged += (_, document) => BindDocument(document);

        objectPropertyGrid.PropertyValueChanged += (_, _) =>
        {
            if (_workspace.Active != null) _workspace.MarkDirty(_workspace.Active);
        };

        TxtSearch.TextChanged += TxtSearch_TextChanged;
        FormClosing += PackageView_FormClosing;
        SetLeftMode(WorkspaceLeftMode.ObjectTree);
        SetFormDetails();
        FitWindowToLayout();
    }

    /// <summary>
    /// The added tab strip, mode switch and status bar make the anchored table taller; grow the
    /// window so the status bar is not hidden behind the form's auto-scroll area.
    /// </summary>
    private void FitWindowToLayout()
    {
        tableLayoutPanel1.PerformLayout();
        var tableHeight = tableLayoutPanel1.PreferredSize.Height;
        if (tableHeight <= 0) return;
        var needed = tableLayoutPanel1.Top + tableHeight + 8;
        if (needed <= ClientSize.Height) return;
        var limit = Screen.FromControl(this).WorkingArea.Height - 40;
        needed = Math.Min(needed, limit);
        if (needed > ClientSize.Height) ClientSize = new Size(ClientSize.Width, needed);
    }

    #region UI construction

    private void BuildTabBar()
    {
        _tabBar = new DocumentTabBar { Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 2) };
        tableLayoutPanel1.RowCount++;
        tableLayoutPanel1.RowStyles.Insert(0, new RowStyle(SizeType.Absolute, 32));
        foreach (Control control in tableLayoutPanel1.Controls)
            tableLayoutPanel1.SetRow(control, tableLayoutPanel1.GetRow(control) + 1);
        tableLayoutPanel1.Controls.Add(_tabBar, 0, 0);
        tableLayoutPanel1.SetColumnSpan(_tabBar, 4);

        _tabBar.SelectionRequested += index =>
        {
            if (index < 0 || index >= _workspace.Documents.Count) return;
            // A failed activation keeps the previous document; the strip must show that, not the click.
            if (!_workspace.Activate(_workspace.Documents[index]))
                _tabBar.SelectedIndex = _workspace.IndexOfDocument(_workspace.Active);
        };
        _tabBar.CloseRequested += index =>
        {
            if (index < 0 || index >= _workspace.Documents.Count) return;
            _workspace.Close(_workspace.Documents[index]);
        };
        _tabBar.ContextMenuRequested += ShowTabContextMenu;
        _tabBar.NewTabRequested += OpenFilesDialog;
        _tabBar.MenuRequested += ShowTabMenu;
        _tabBar.ReorderRequested += (from, to) => _workspace.Move(from, to);
    }

    private void BuildLeftPanel()
    {
        // The mode switch reuses the existing object tree control next to the workspace file list.
        _filesModeButton = CreateModeButton("FNG 文件", (_, _) => SetLeftMode(WorkspaceLeftMode.Files));
        _treeModeButton = CreateModeButton("当前对象树", (_, _) => SetLeftMode(WorkspaceLeftMode.ObjectTree));
        var modeLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 4)
        };
        modeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        modeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _filesModeButton.Dock = DockStyle.Fill;
        _treeModeButton.Dock = DockStyle.Fill;
        modeLayout.Controls.Add(_filesModeButton, 0, 0);
        modeLayout.Controls.Add(_treeModeButton, 1, 0);

        tableLayoutPanel2.RowCount++;
        tableLayoutPanel2.RowStyles.Insert(0, new RowStyle(SizeType.AutoSize));
        foreach (Control control in tableLayoutPanel2.Controls)
            tableLayoutPanel2.SetRow(control, tableLayoutPanel2.GetRow(control) + 1);
        tableLayoutPanel2.Controls.Add(modeLayout, 0, 0);
        tableLayoutPanel2.SetColumnSpan(modeLayout, 2);

        var treeRow = tableLayoutPanel1.GetRow(treeView1);
        var treeSpan = tableLayoutPanel1.GetRowSpan(treeView1);
        tableLayoutPanel1.Controls.Remove(treeView1);

        _workspaceTree = new TreeView
        {
            Name = "workspaceTree",
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            HideSelection = false,
            FullRowSelect = true,
            ShowLines = false,
            ShowRootLines = true,
            ShowNodeToolTips = true,
            BorderStyle = BorderStyle.FixedSingle
        };
        BuildWorkspaceIcons();
        _workspaceTree.ImageList = _workspaceIcons;
        treeView1.Dock = DockStyle.Fill;
        treeView1.Margin = new Padding(0);

        _leftStack = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        _leftStack.Controls.Add(_workspaceTree);
        _leftStack.Controls.Add(treeView1);
        tableLayoutPanel1.Controls.Add(_leftStack, 0, treeRow);
        tableLayoutPanel1.SetRowSpan(_leftStack, treeSpan);

        _workspaceTree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is WorkspaceFile file) OpenWorkspaceFile(file);
        };
        _workspaceTree.KeyDown += WorkspaceTree_KeyDown;
        _workspaceTree.MouseDown += WorkspaceTree_MouseDown;
        _workspaceTree.AfterSelect += (_, _) => UpdateWorkspaceMenuState();

        BuildWorkspaceMenu();
    }

    private static Button CreateModeButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            Height = 26,
            Margin = new Padding(0, 0, 2, 0),
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = SystemColors.ControlDark;
        button.FlatAppearance.BorderSize = 1;
        button.Click += onClick;
        return button;
    }

    private void BuildWorkspaceIcons()
    {
        _workspaceIcons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        _workspaceIcons.Images.Add("group", DrawFolderIcon());
        _workspaceIcons.Images.Add("idle", DrawStatusIcon(Color.FromArgb(176, 176, 176)));
        _workspaceIcons.Images.Add("open", DrawStatusIcon(Color.FromArgb(46, 160, 67)));
        _workspaceIcons.Images.Add("active", DrawStatusIcon(Color.FromArgb(28, 105, 212)));
        _workspaceIcons.Images.Add("missing", DrawStatusIcon(Color.FromArgb(200, 60, 50)));
    }

    private static Image DrawStatusIcon(Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 3, 3, 10, 10);
        return bitmap;
    }

    private static Image DrawFolderIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var tab = new SolidBrush(Color.FromArgb(196, 154, 46));
        using var body = new SolidBrush(Color.FromArgb(226, 186, 78));
        g.FillRectangle(tab, 1, 3, 7, 3);
        g.FillRectangle(body, 1, 5, 14, 9);
        return bitmap;
    }

    private void BuildStatusBar()
    {
        _statusLeft = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(4, 0, 4, 0)
        };
        _statusRight = new Label
        {
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(4, 0, 6, 0)
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(_statusLeft, 0, 0);
        layout.Controls.Add(_statusRight, 1, 0);

        _statusBar = new Panel { Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 0) };
        _statusBar.Controls.Add(layout);
        _statusBar.Paint += (_, e) => e.Graphics.DrawLine(SystemPens.ControlDark, 0, 0, _statusBar.Width, 0);

        tableLayoutPanel1.RowCount++;
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        tableLayoutPanel1.Controls.Add(_statusBar, 0, tableLayoutPanel1.RowCount - 1);
        tableLayoutPanel1.SetColumnSpan(_statusBar, 4);
    }

    private void BuildMenus()
    {
        var items = FileMenuItem.DropDownItems;
        items.Clear();
        items.Add(CreateMenuItem("Open FNG...", Keys.Control | Keys.O, (_, _) => OpenFilesDialog()));
        items.Add(CreateMenuItem("Open container...", Keys.None, OpenContainerMenuItem_Click));
        items.Add(CreateMenuItem("Open FNG folder...", Keys.None, (_, _) => OpenFngFolder()));
        items.Add(new ToolStripSeparator());
        items.Add(CreateMenuItem("Save", Keys.Control | Keys.S, (_, _) => SaveActiveDocument(false)));
        items.Add(CreateMenuItem("Save as...", Keys.Control | Keys.Shift | Keys.S, (_, _) => SaveActiveDocument(true)));
        items.Add(CreateMenuItem("Reload", Keys.Control | Keys.Shift | Keys.R, (_, _) => ReloadActiveDocument()));
        items.Add(new ToolStripSeparator());
        items.Add(CreateMenuItem("Close tab", Keys.Control | Keys.W, (_, _) => CloseActiveDocument()));
        items.Add(CreateMenuItem("Close all tabs", Keys.Control | Keys.Shift | Keys.W, (_, _) => _workspace.CloseAll()));
        items.Add(new ToolStripSeparator());
        items.Add(CreateMenuItem("Cleanup and save", Keys.None, CleanupAndSaveToolStripMenuItem_Click));
        items.Add(new ToolStripSeparator());
        items.Add(CreateMenuItem("Exit", Keys.None, (_, _) => Close()));
    }

    private static ToolStripMenuItem CreateMenuItem(string text, Keys shortcut, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text, null, onClick);
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        return item;
    }

    private void BuildWorkspaceMenu()
    {
        _workspaceMenu = new ContextMenuStrip(components);
        _workspaceMenu.Items.Add(new ToolStripMenuItem("打开") { Font = new Font(_workspaceTree.Font, FontStyle.Bold) });
        _workspaceMenu.Items.Add(new ToolStripMenuItem("关闭文档标签"));
        _workspaceMenu.Items.Add(new ToolStripSeparator());
        _workspaceMenu.Items.Add(new ToolStripMenuItem("在文件夹中显示"));
        _workspaceMenu.Items.Add(new ToolStripMenuItem("查看来源信息"));
        _workspaceMenu.Items.Add(new ToolStripSeparator());
        _workspaceMenu.Items.Add(new ToolStripMenuItem("从工作区移除"));
        _workspaceMenu.Items.Add(new ToolStripMenuItem("刷新状态"));

        _workspaceMenu.Items[0].Click += (_, _) => OpenWorkspaceFile(SelectedWorkspaceFile);
        _workspaceMenu.Items[1].Click += (_, _) =>
        {
            var document = _workspace.FindDocument(SelectedWorkspaceFile?.Path);
            if (document != null) _workspace.Close(document);
        };
        _workspaceMenu.Items[3].Click += (_, _) => RevealWorkspaceFile(SelectedWorkspaceFile);
        _workspaceMenu.Items[4].Click += (_, _) => ShowSourceInfo(SelectedWorkspaceFile);
        _workspaceMenu.Items[6].Click += (_, _) =>
        {
            var file = SelectedWorkspaceFile;
            if (file == null) return;
            if (_workspace.RemoveFile(file))
                _workspace.StatusMessage = file.Caption + " 已从工作区移除（已打开的标签保持不变）";
        };
        _workspaceMenu.Items[7].Click += (_, _) =>
        {
            var missing = _workspace.RefreshPresence();
            _workspace.StatusMessage = missing == 0 ? "工作区文件均存在" : $"{missing} 个文件已缺失";
        };
        _workspaceTree.ContextMenuStrip = _workspaceMenu;
        UpdateWorkspaceMenuState();
    }

    private WorkspaceFile SelectedWorkspaceFile => _workspaceTree?.SelectedNode?.Tag as WorkspaceFile;

    private void UpdateWorkspaceMenuState()
    {
        if (_workspaceMenu == null) return;
        var file = SelectedWorkspaceFile;
        var document = file == null ? null : _workspace.FindDocument(file.Path);
        _workspaceMenu.Items[0].Enabled = file != null;
        _workspaceMenu.Items[1].Enabled = document != null;
        _workspaceMenu.Items[3].Enabled = file != null;
        _workspaceMenu.Items[4].Enabled = file != null && File.Exists(file.Path + ".source.json");
        _workspaceMenu.Items[6].Enabled = file != null;
    }

    #endregion

    #region Workspace refresh and binding

    private void RefreshWorkspaceStructure()
    {
        UpdateTabBar();
        RebuildWorkspaceTree();
        SetFormDetails();
    }

    private void RefreshWorkspaceState()
    {
        UpdateTabBar();
        UpdateWorkspaceNodeStates();
        UpdateWorkspaceMenuState();
        SetFormDetails();
    }

    private void UpdateTabBar()
    {
        if (_tabBar == null) return;
        var items = _workspace.Documents
            .Select(document => new DocumentTabBar.Item(document.Caption, document.ToolTip, document.LoadError != null))
            .ToArray();
        if (_tabBar.ItemCount == items.Length) _tabBar.UpdateItems(items);
        else _tabBar.SetItems(items);
        var index = _workspace.IndexOfDocument(_workspace.Active);
        _tabBar.SelectedIndex = index;
    }

    private void PackageView_FormClosing(object sender, FormClosingEventArgs e)
    {
        if (_workspace == null) return;
        if (!_workspace.CloseAll()) e.Cancel = true;
    }

    private void BindDocument(WorkspaceDocument document)
    {
        RememberEditorState();
        if (document?.Package == null)
        {
            ShowEmptyEditor();
            return;
        }

        var previous = _boundDocument;
        try
        {
            _boundDocument = document;
            _currentPackage = document.Package;
            _currentRenderTree = GetRenderTree(document);
            AppService.Instance.ActivatePackage(document.Package, document.FilePath, document.IsJson);
            viewOutput.Init(document.TextureDirectory);
            _packageViewExtensions.ObjectSelected = null;
            _packageViewExtensions.ShouldCopyObject = false;
            _packageViewExtensions.SearchText = document.SearchText;
            _packageViewExtensions.SearchIndex = document.SearchIndex;
            TxtSearch.Text = document.SearchText;
            objectPropertyGrid.SelectedObject = null;
            viewOutput.SelectedNode = null;
            PopulateTreeView(document.Package, _currentRenderTree);
            RestoreTreeSelection(document.SelectedTreeKey);
            Render();
            RememberRecentDocument(document);
        }
        catch (Exception error)
        {
            // A binding failure must not leave the previous document's model on screen.
            _boundDocument = previous;
            MessageBox.Show(this, $"无法显示文档：\n{document.FilePath}\n\n{error.Message}", "Document binding failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowEmptyEditor();
            return;
        }

        RefreshWorkspaceState();
    }

    private RenderTree GetRenderTree(WorkspaceDocument document)
    {
        if (_documentTrees.TryGetValue(document, out var cache) && cache.Version == document.ModelVersion)
            return cache.Tree;
        var tree = RenderTree.Create(document.Package);
        _documentTrees[document] = new RenderCache(tree, document.ModelVersion);
        return tree;
    }

    private void RestoreTreeSelection(string treeKey)
    {
        if (string.IsNullOrWhiteSpace(treeKey)) return;
        var node = treeView1.Nodes.Find(treeKey, true).FirstOrDefault();
        if (node == null) return;
        treeView1.SelectedNode = node;
        node.EnsureVisible();
    }

    private void RememberEditorState()
    {
        var document = _boundDocument;
        if (document == null) return;
        document.SelectedTreeKey = treeView1.SelectedNode?.Name;
        document.SearchText = TxtSearch.Text;
        document.SearchIndex = _packageViewExtensions.SearchIndex;
    }

    private void ShowEmptyEditor()
    {
        _boundDocument = null;
        _currentPackage = null;
        _currentRenderTree = null;
        AppService.Instance.ActivatePackage(null, null, false);
        _packageViewExtensions.ObjectSelected = null;
        _packageViewExtensions.ShouldCopyObject = false;
        _packageViewExtensions.SearchText = null;
        _packageViewExtensions.SearchIndex = -1;
        _rootNode = null;
        TxtSearch.Text = "";
        treeView1.BeginUpdate();
        treeView1.Nodes.Clear();
        treeView1.EndUpdate();
        objectPropertyGrid.SelectedObject = null;
        viewOutput.SelectedNode = null;
        viewOutput.Render(null);
        LblItemIndex.Text = "Index: 00000";
        LblChildsCount.Text = "Children: 00000";
        RefreshWorkspaceState();
    }

    private void RememberRecentDocument(WorkspaceDocument document)
    {
        _recentDocuments.RemoveAll(path => string.Equals(path, document.FilePath, StringComparison.OrdinalIgnoreCase));
        _recentDocuments.Insert(0, document.FilePath);
        while (_recentDocuments.Count > 10) _recentDocuments.RemoveAt(_recentDocuments.Count - 1);
    }

    #endregion

    #region Workspace file list

    private void SetLeftMode(WorkspaceLeftMode mode)
    {
        _leftMode = mode;
        if (_workspaceTree == null || treeView1 == null) return;
        _workspaceTree.Visible = mode == WorkspaceLeftMode.Files;
        treeView1.Visible = mode == WorkspaceLeftMode.ObjectTree;
        var active = mode == WorkspaceLeftMode.Files;
        _filesModeButton.BackColor = active ? SystemColors.Highlight : SystemColors.Control;
        _filesModeButton.ForeColor = active ? SystemColors.HighlightText : SystemColors.ControlText;
        _treeModeButton.BackColor = active ? SystemColors.Control : SystemColors.Highlight;
        _treeModeButton.ForeColor = active ? SystemColors.ControlText : SystemColors.HighlightText;
        TxtSearch.PlaceholderText = active ? "搜索 FNG 文件..." : "搜索节点...";
        BtnPrevious.Enabled = !active;
        BtnNext.Enabled = !active;
        if (active) RebuildWorkspaceTree();
    }

    private void TxtSearch_TextChanged(object sender, EventArgs e)
    {
        if (_leftMode != WorkspaceLeftMode.Files) return;
        RebuildWorkspaceTree();
    }

    private bool MatchesWorkspaceFilter(WorkspaceFile file, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return file.Caption.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               file.Path.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildWorkspaceTree()
    {
        if (_workspaceTree == null) return;
        var filter = (_leftMode == WorkspaceLeftMode.Files ? TxtSearch.Text : "")?.Trim() ?? "";
        _workspaceTree.BeginUpdate();
        try
        {
            _workspaceTree.Nodes.Clear();
            _workspaceNodes.Clear();
            foreach (var group in _workspace.Groups)
            {
                var files = group.Files.Where(file => MatchesWorkspaceFilter(file, filter)).ToList();
                if (files.Count == 0 && filter.Length > 0) continue;
                var root = new TreeNode($"{group.Name} ({files.Count})")
                {
                    ImageKey = "group",
                    SelectedImageKey = "group",
                    ToolTipText = $"{group.Name}：{group.Files.Count} 个 FNG"
                };
                _workspaceTree.Nodes.Add(root);
                foreach (var file in files.Take(WorkspaceNodeLimit))
                {
                    var node = new TreeNode(file.Caption) { Name = file.Path, Tag = file };
                    ApplyWorkspaceNodeState(node, file);
                    root.Nodes.Add(node);
                    _workspaceNodes[file.Path] = node;
                }

                if (files.Count > WorkspaceNodeLimit)
                    root.Nodes.Add(new TreeNode($"… 其余 {files.Count - WorkspaceNodeLimit} 个文件，请用搜索过滤"));
                root.Expand();
            }
        }
        finally
        {
            _workspaceTree.EndUpdate();
        }

        RevealActiveWorkspaceNode();
    }

    private void UpdateWorkspaceNodeStates()
    {
        if (_workspaceTree == null) return;
        foreach (var pair in _workspaceNodes)
        {
            if (pair.Value.Tag is not WorkspaceFile file) continue;
            ApplyWorkspaceNodeState(pair.Value, file);
        }

        _workspaceTree.Invalidate();
    }

    private void ApplyWorkspaceNodeState(TreeNode node, WorkspaceFile file)
    {
        var document = _workspace.FindDocument(file.Path);
        var isActive = document != null && ReferenceEquals(document, _workspace.Active);
        node.ImageKey = node.SelectedImageKey = file.IsMissing ? "missing"
            : document == null ? "idle"
            : isActive ? "active" : "open";
        node.NodeFont = document == null ? null : _boldNodeFont;
        var tooltip = file.Path;
        if (file.IsMissing) tooltip += "（文件缺失）";
        else if (document != null) tooltip += document.IsDirty ? "（已打开，未保存）" : "（已打开）";
        else tooltip += "（未打开）";
        node.ToolTipText = tooltip;
    }

    private void RevealActiveWorkspaceNode()
    {
        if (_leftMode != WorkspaceLeftMode.Files || _workspace.Active == null) return;
        if (!_workspaceNodes.TryGetValue(_workspace.Active.FilePath, out var node)) return;
        _workspaceTree.SelectedNode = node;
        node.EnsureVisible();
    }

    private void WorkspaceTree_MouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var node = _workspaceTree.GetNodeAt(e.X, e.Y);
        _workspaceTree.SelectedNode = node;
        UpdateWorkspaceMenuState();
    }

    private void WorkspaceTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            OpenWorkspaceFile(SelectedWorkspaceFile);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete && SelectedWorkspaceFile != null)
        {
            _workspace.RemoveFile(SelectedWorkspaceFile);
            e.Handled = true;
        }
    }

    private void OpenWorkspaceFile(WorkspaceFile file)
    {
        if (file == null) return;
        if (file.IsMissing)
        {
            MessageBox.Show(this, $"文件已不在磁盘上：\n{file.Path}\n\n请重新导入，或从工作区移除。", "文件缺失",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _workspace.OpenFile(file);
        SetFormDetails();
    }

    private void RevealWorkspaceFile(WorkspaceFile file)
    {
        if (file == null) return;
        try
        {
            var path = File.Exists(file.Path) ? file.Path : Path.GetDirectoryName(file.Path) ?? file.Path;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法打开资源管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowSourceInfo(WorkspaceFile file)
    {
        if (file == null) return;
        var manifest = file.Path + ".source.json";
        if (!File.Exists(manifest))
        {
            MessageBox.Show(this, $"没有来源记录：\n{manifest}", "来源信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            string Value(string name) => root.TryGetProperty(name, out var value) ? value.ToString() : "—";
            var offsetText = root.TryGetProperty("Offset", out var offset) && offset.TryGetInt64(out var offsetValue)
                ? $"{offsetValue}（0x{offsetValue:X}）"
                : "—";
            var text = $"容器: {Value("SourcePath")}\n" +
                       $"容器 SHA256: {Value("SourceSha256")}\n" +
                       $"块偏移: {offsetText}\n" +
                       $"块长度: {Value("StoredLength")}\n" +
                       $"包名: {Value("PackageName")}\n" +
                       $"提取 SHA256: {Value("ExtractedSha256")}\n" +
                       $"提取时间: {Value("ExtractedAt")}";
            MessageBox.Show(this, text, "来源信息 — " + file.Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "来源记录无法解析：\n" + error.Message, "来源信息", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    #endregion

    #region Commands

    private void OpenFilesDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "FNG Files (*.fng, *.bin)|*.fng;*.bin|JSON Files (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImportAndOpen(dialog.FileNames, dialog.FilterIndex == 2, activateAll: true);
    }

    private void OpenFngFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择已经包含 FNG 的文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(dialog.SelectedPath, "*.fng")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Open FNG folder failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (paths.Length == 0)
        {
            MessageBox.Show(this, "此文件夹中没有 FNG 文件。请选择直接包含 FNG 的文件夹。", "Open FNG folder");
            return;
        }

        var name = new DirectoryInfo(dialog.SelectedPath).Name;
        var import = _workspace.Import(paths, false, name);
        SetLeftMode(WorkspaceLeftMode.Files);
        _workspace.StatusMessage = $"文件夹 {name}：加入 {import.Added.Count} 个文件（工作区共 {_workspace.FileCount} 个）";
        if (_workspace.Active == null)
        {
            var opened = _workspace.OpenFirstAvailable(import.Added.Concat(import.Existing), out var failed);
            if (opened != null) _workspace.StatusMessage += $"；已打开 {opened.Caption}";
            else if (failed > 0) _workspace.StatusMessage += $"；{failed} 个文件无法解析";
        }

        RefreshWorkspaceStructure();
    }

    /// <summary>Adds the paths to the workspace, then opens documents only when the user asked to see them.</summary>
    private string ImportAndOpen(IReadOnlyList<string> paths, bool isJson, bool activateAll, string groupName = null)
    {
        groupName ??= GroupNameForFiles(paths);
        var import = _workspace.Import(paths, isJson, groupName);
        if (paths.Count > 1) SetLeftMode(WorkspaceLeftMode.Files);
        var candidates = import.Added.Concat(import.Existing).ToList();
        var opened = 0;
        var failed = 0;
        WorkspaceDocument first = null;
        foreach (var file in candidates)
        {
            var document = _workspace.OpenFile(file, activate: !activateAll, reportError: candidates.Count <= 5);
            if (document == null)
            {
                failed++;
                continue;
            }

            opened++;
            first ??= document;
            if (!activateAll) break;
        }

        if (activateAll && first != null) _workspace.Activate(first);
        RefreshWorkspaceStructure();

        var summary = import.Added.Count == 0 && import.Existing.Count == 0
            ? "没有可加入工作区的 FNG。"
            : $"加入工作区 {import.Added.Count} 个文件" +
              (import.Existing.Count > 0 ? $"，已在工作区 {import.Existing.Count} 个" : "") +
              (opened > 0 ? $"，已打开 {opened} 个文档" : "") +
              (failed > 0 ? $"，{failed} 个无法解析" : "");
        _workspace.StatusMessage = summary;
        SetFormDetails();
        return summary;
    }

    private static string GroupNameForFiles(IEnumerable<string> paths)
    {
        try
        {
            var directories = paths.Select(Path.GetDirectoryName).Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (directories.Count == 1) return new DirectoryInfo(directories[0]).Name;
        }
        catch (Exception)
        {
            // fall through to the generic group name
        }

        return "已打开的文件";
    }

    /// <summary>Called by the extraction progress window once blocks are on disk.</summary>
    private string ImportExtractedFiles(string groupName, IReadOnlyList<string> paths)
    {
        var import = _workspace.Import(paths, false, groupName, refreshExisting: true);
        SetLeftMode(WorkspaceLeftMode.Files);
        var opened = _workspace.OpenFirstAvailable(import.Added.Concat(import.Existing), out var failed);
        var text = $"已加入工作区 {import.Added.Count} 个文件";
        if (import.Existing.Count > 0) text += $"，已在工作区 {import.Existing.Count} 个";
        if (import.Refreshed > 0) text += $"，已重新读取 {import.Refreshed} 个已打开文档";
        if (import.KeptDirty > 0) text += $"，保留 {import.KeptDirty} 个未保存文档";
        text += opened == null
            ? failed > 0 ? $"；{failed} 个文件无法解析，未自动打开" : "；没有可打开的文档"
            : $"；已打开 {opened.Caption}";
        _workspace.StatusMessage = text;
        RefreshWorkspaceStructure();
        return text;
    }

    private void OpenContainerMenuItem_Click(object sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "MW05 containers (*.bun, *.lzc)|*.bun;*.lzc",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        using var browser = new ContainerBrowser(dialog.FileName, ImportExtractedFiles) { Icon = Icon };
        if (browser.ShowDialog(this) != DialogResult.OK) return;
        if (!string.IsNullOrWhiteSpace(browser.ExtractionSummary)) _workspace.StatusMessage = browser.ExtractionSummary;
        SetFormDetails();
    }

    private void SaveActiveDocument(bool chooseTarget)
    {
        if (_workspace.Active == null)
        {
            MessageBox.Show(this, "当前没有打开的文档。", "Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _workspace.Save(_workspace.Active, chooseTarget);
    }

    private void ReloadActiveDocument()
    {
        if (_workspace.Active == null) return;
        _workspace.Reload(_workspace.Active);
    }

    private void CloseActiveDocument()
    {
        if (_workspace.Active != null) _workspace.Close(_workspace.Active);
    }

    private void ShowTabContextMenu(int index)
    {
        if (index < 0 || index >= _workspace.Documents.Count) return;
        var document = _workspace.Documents[index];
        var menu = new ContextMenuStrip(components);
        var close = new ToolStripMenuItem("关闭标签");
        close.Click += (_, _) => _workspace.Close(document);
        var closeOthers = new ToolStripMenuItem("关闭其他标签") { Enabled = _workspace.Documents.Count > 1 };
        closeOthers.Click += (_, _) => _workspace.CloseOthers(document);
        var closeAll = new ToolStripMenuItem("关闭所有标签") { Enabled = _workspace.Documents.Count > 0 };
        closeAll.Click += (_, _) => _workspace.CloseAll();
        var reveal = new ToolStripMenuItem("在文件夹中显示");
        reveal.Click += (_, _) => RevealWorkspaceFile(new WorkspaceFile(document.FilePath, document.IsJson));
        menu.Items.AddRange(new ToolStripItem[] { close, closeOthers, closeAll, new ToolStripSeparator(), reveal });
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(Cursor.Position);
    }

    private void ShowTabMenu(Point screenPoint)
    {
        var menu = new ContextMenuStrip(components);
        menu.Items.Add(new ToolStripMenuItem("打开 FNG 文件...", null, (_, _) => OpenFilesDialog()));
        if (_recentDocuments.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("最近打开的文档") { Enabled = false });
            foreach (var path in _recentDocuments.Take(8))
            {
                var item = new ToolStripMenuItem(Path.GetFileName(path)) { ToolTipText = path };
                item.Click += (_, _) => OpenWorkspacePath(path);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("关闭其他标签", null, (_, _) => _workspace.CloseOthers(_workspace.Active))
        {
            Enabled = _workspace.Documents.Count > 1
        });
        menu.Items.Add(new ToolStripMenuItem("关闭所有标签", null, (_, _) => _workspace.CloseAll())
        {
            Enabled = _workspace.Documents.Count > 0
        });
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(_tabBar, _tabBar.PointToClient(screenPoint));
    }

    private void OpenWorkspacePath(string path)
    {
        var file = _workspace.FindFile(path);
        if (file == null)
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(this, $"文件已不在磁盘上：\n{path}", "文件缺失", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var isJson = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            var import = _workspace.Import(new[] { path }, isJson, GroupNameForFiles(new[] { path }));
            file = import.Added.FirstOrDefault() ?? import.Existing.FirstOrDefault();
        }

        if (file != null) OpenWorkspaceFile(file);
    }

    private void CleanupAndSaveToolStripMenuItem_Click(object sender, EventArgs e)
    {
        var document = _workspace.Active;
        if (document?.Package == null) return;
        foreach (var fngObject in document.Package.Objects.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
            fngObject.Name = null;
        _workspace.MarkDirty(document);
        _workspace.Save(document, chooseTarget: true);
    }

    #endregion

    #region Prompts and file IO

    private bool WriteDocumentFile(WorkspaceDocument document, string path, bool isJson, bool offerOpenFolder)
    {
        try
        {
            if (isJson) SavePackageToJson(document.Package, path);
            else SavePackageToChunk(document.Package, path);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        if (offerOpenFolder) OpenRecentlySaved(path, isJson);
        return true;
    }

    private string ChooseSaveTarget(WorkspaceDocument document, out bool isJson)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "FNG Files (*.fng)|*.fng|BIN Files (*.bin)|*.bin|JSON Files (*.json)|*.json",
            FileName = Path.GetFileNameWithoutExtension(document.CaptionName),
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(document.FilePath)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            isJson = document.IsJson;
            return null;
        }

        isJson = dialog.FilterIndex == 3;
        return dialog.FileName;
    }

    private SavePromptResult AskToSaveDocument(WorkspaceDocument document, string reason)
    {
        var answer = MessageBox.Show(this, $"{reason}\n\n{document.FilePath}", $"未保存的修改 — {document.CaptionName}",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => SavePromptResult.Save,
            DialogResult.No => SavePromptResult.Discard,
            _ => SavePromptResult.Cancel
        };
    }

    private bool ConfirmDiscardDocument(WorkspaceDocument document, string reason)
    {
        return MessageBox.Show(this, $"{reason}\n\n{document.FilePath}", "重新读取", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;
    }

    #endregion

    #region Layout refresh

    private void SetFormDetails()
    {
        if (_statusLeft == null || _workspace == null) return;
        var active = _workspace.Active;
        var parts = new List<string>
        {
            string.IsNullOrWhiteSpace(_workspace.StatusMessage) ? "就绪" : _workspace.StatusMessage
        };
        if (_workspace.FileCount > 0 || _workspace.Groups.Count > 0)
        {
            var name = _workspace.DisplayName ?? "Workspace";
            var extraGroups = _workspace.Groups.Count > 1 ? $" +{_workspace.Groups.Count - 1}" : "";
            parts.Add($"工作区: {name}{extraGroups}");
            parts.Add($"文件: {_workspace.FileCount}");
        }

        parts.Add($"打开的文档: {_workspace.DocumentCount}");
        if (_workspace.DirtyCount > 0) parts.Add($"已修改: {_workspace.DirtyCount}");
        if (active != null && NeedsTextureWarning(active)) parts.Add("无预览贴图");
        _statusLeft.Text = string.Join("  |  ", parts);
        _statusRight.Text = active == null ? "未打开文档" : $"{active.Caption}    {active.FilePath}";

        var title = "FEngViewer";
        if (_workspace.FileCount > 0)
            title += $" - {_workspace.DisplayName ?? "Workspace"} Workspace ({_workspace.FileCount} files)";
        else if (active != null)
            title += $" - {active.CaptionName}";
#if DEBUG
        title += " - Debug";
#endif
        Text = title;
    }

    private static bool NeedsTextureWarning(WorkspaceDocument document)
    {
        var requests = document.Package?.ResourceRequests;
        if (requests?.Any(request => request.Type is ResourceType.Image or ResourceType.MultiImage) != true)
            return false;
        return !Directory.Exists(document.TextureDirectory);
    }

    #endregion
}
