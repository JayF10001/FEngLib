using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FEngLib.Assets;
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
    private ContainerWriteBackCoordinator _writeBack;
    private DocumentTabBar _tabBar;
    private TreeView _workspaceTree;
    private TreeNode _workspaceTopNode;
    private Panel _leftStack;
    private Button _filesModeButton;
    private Button _treeModeButton;
    private Panel _statusBar;
    private Label _statusLeft;
    private Label _statusRight;
    private ContextMenuStrip _workspaceMenu;
    private ContextMenuStrip _tabContextMenu;
    private ToolStripMenuItem _recentDocumentsMenu;
    private Font _boldNodeFont;
    private ImageList _workspaceIcons;

    private readonly Dictionary<WorkspaceDocument, RenderCache> _documentTrees = new();
    private readonly Dictionary<string, TreeNode> _workspaceNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TreeNode, string> _workspaceNodeCaptions = new();
    private readonly Dictionary<TreeNode, int> _workspaceNodeWidths = new();
    private readonly List<string> _recentDocuments = new();
    private WorkspaceDocument _boundDocument;
    private WorkspaceLeftMode _leftMode = WorkspaceLeftMode.ObjectTree;
    private string _fileFilterText = "";
    private bool _syncingSearch;

    private WorkspaceDocument ActiveDocument => _workspace?.Active;

    private void InitializeWorkspace()
    {
        _workspace = new DocumentWorkspace(new WorkspacePrompts(this));
        _writeBack = new ContainerWriteBackCoordinator();
        _writeBack.PendingChanged += (_, _) => RefreshWorkspaceStructure();
        _workspace.Saving += (_, save) => _writeBack.PrepareSave(save);
        _workspace.SaveFinished += (_, save) => _writeBack.FinishSave(save);
        _writeBack.ConfirmVersionSwitch = message => MessageBox.Show(this, message,
            "切换待写回版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        _boldNodeFont = new Font(treeView1.Font, FontStyle.Bold);

        BuildShell();
        BuildTabBar();
        BuildLeftPanel();
        BuildStatusBar();
        BuildMenus();

        _workspace.Changed += (_, _) => RefreshWorkspaceStructure();
        _workspace.StateChanged += (_, _) => RefreshWorkspaceState();
        _workspace.ActiveChanged += (_, document) => BindDocument(document);

        objectPropertyGrid.PropertyValueChanged += (_, _) =>
        {
            if ((_boundDocument ?? _workspace.Active) is { } document)
            {
                document.History?.Commit("编辑高级属性");
                CurrentPackageWasModified(treeView1.SelectedNode?.Name);
            }
        };

        TxtSearch.TextChanged += TxtSearch_TextChanged;
        FormClosing += PackageView_FormClosing;
        ApplyMetricHeights();
        SetLeftMode(WorkspaceLeftMode.ObjectTree);
        FinishLayoutToolTips();
        SetFormDetails();
    }

    #region UI construction

    private void BuildTabBar()
    {
        _tabBar = new DocumentTabBar { Dock = DockStyle.Top, Height = 32, Margin = Padding.Empty };
        _host.Controls.Add(_tabBar);

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
            Margin = new Padding(0, 0, 0, 2)
        };
        modeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        modeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _filesModeButton.Dock = DockStyle.Fill;
        _treeModeButton.Dock = DockStyle.Fill;
        modeLayout.Controls.Add(_filesModeButton, 0, 0);
        modeLayout.Controls.Add(_treeModeButton, 1, 0);

        // FNG mode: filter + sort. Object tree mode: previous/next + expand toggle. Only one set is
        // visible at a time, so no dead buttons take up toolbar height.
        _fileSortCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(2, 0, 0, 0)
        };
        _fileSortCombo.Items.AddRange(new object[] { "导入顺序", "按名称" });
        _fileSortCombo.SelectedIndex = 0;
        _fileSortCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_leftMode == WorkspaceLeftMode.Files) RebuildWorkspaceTree();
        };
        _fileExtras = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        _fileExtras.Controls.Add(_fileSortCombo);

        ConfigureToolButton(BtnPrevious, "◀");
        ConfigureToolButton(BtnNext, "▶");
        _expandTreeButton = new Button();
        ConfigureToolButton(_expandTreeButton, "⊞");
        _expandTreeButton.Click += (_, _) => ToggleTreeExpansion();
        _treeExtras = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        _treeExtras.Controls.Add(BtnPrevious);
        _treeExtras.Controls.Add(BtnNext);
        _treeExtras.Controls.Add(_expandTreeButton);

        TxtSearch.Dock = DockStyle.Fill;
        TxtSearch.Margin = Padding.Empty;

        _searchRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty
        };
        _searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _searchRow.Controls.Add(TxtSearch, 0, 0);
        _searchRow.Controls.Add(_fileExtras, 1, 0);
        _searchRow.Controls.Add(_treeExtras, 1, 0);

        _leftToolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 0, 0, 2),
            Margin = Padding.Empty
        };
        _leftToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _leftToolbar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _leftToolbar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _leftToolbar.Controls.Add(modeLayout, 0, 0);
        _leftToolbar.Controls.Add(_searchRow, 0, 1);

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
            // Keep vertical scrolling enabled; long names are shortened separately below.
            Scrollable = true,
            BorderStyle = BorderStyle.FixedSingle
        };
        BuildWorkspaceIcons();
        _workspaceTree.ImageList = _workspaceIcons;
        _workspaceTree.ClientSizeChanged += (_, _) => ApplyWorkspaceNodeTextWidths();
        treeView1.Dock = DockStyle.Fill;
        treeView1.Margin = new Padding(0);

        _leftStack = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        _leftStack.Controls.Add(_workspaceTree);
        _leftStack.Controls.Add(treeView1);
        _splitOuter.Panel1.Controls.Add(_leftStack);
        _splitOuter.Panel1.Controls.Add(_leftToolbar);

        _workspaceTree.NodeMouseClick += WorkspaceTree_NodeMouseClick;
        _workspaceTree.NodeMouseDoubleClick += WorkspaceTree_NodeMouseDoubleClick;
        _workspaceTree.KeyDown += WorkspaceTree_KeyDown;
        _workspaceTree.MouseDown += WorkspaceTree_MouseDown;
        _workspaceTree.AfterSelect += (_, _) => UpdateWorkspaceMenuState();

        BuildWorkspaceMenu();
    }

    private void ToggleTreeExpansion()
    {
        if (treeView1.Nodes.Count == 0) return;
        var expand = !treeView1.Nodes[0].IsExpanded;
        if (expand) treeView1.ExpandAll();
        else treeView1.CollapseAll();
        _expandTreeButton.Text = expand ? "⊟" : "⊞";
    }

    /// <summary>Mode buttons size themselves from the font so a scaled UI never clips the label.</summary>
    private Button CreateModeButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, LineHeight + 10),
            Margin = new Padding(0, 0, 2, 0),
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = SystemColors.ControlDark;
        button.FlatAppearance.BorderSize = 1;
        button.Click += onClick;
        return button;
    }

    private void ConfigureToolButton(Button button, string text)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(LineHeight + 12, LineHeight + 8);
        button.Margin = new Padding(0, 0, 2, 0);
        button.TabStop = false;
    }

    private void BuildWorkspaceIcons()
    {
        _workspaceIcons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        _workspaceIcons.Images.Add("group", DrawFolderIcon());
        _workspaceIcons.Images.Add("idle", DrawStatusIcon(Color.FromArgb(176, 176, 176)));
        _workspaceIcons.Images.Add("open", DrawStatusIcon(Color.FromArgb(46, 160, 67)));
        _workspaceIcons.Images.Add("active", DrawStatusIcon(Color.FromArgb(28, 105, 212)));
        _workspaceIcons.Images.Add("missing", DrawStatusIcon(Color.FromArgb(200, 60, 50)));
        _workspaceIcons.Images.Add("pending", DrawPendingCheckIcon());
    }

    /// <summary>Green outline with a check mark: the file holds saved changes waiting for write-back.</summary>
    private static Image DrawPendingCheckIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = new Pen(Color.FromArgb(46, 160, 67), 1.6f);
        g.DrawEllipse(outline, 3, 3, 10, 10);
        using var check = new Pen(Color.FromArgb(46, 160, 67), 2f);
        check.StartCap = LineCap.Round;
        check.EndCap = LineCap.Round;
        g.DrawLine(check, 5, 8, 7, 10);
        g.DrawLine(check, 7, 10, 11, 5);
        return bitmap;
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

        _statusBar = new Panel { Dock = DockStyle.Bottom, Height = 24, Margin = Padding.Empty };
        _statusBar.Controls.Add(layout);
        _statusBar.Paint += (_, e) => e.Graphics.DrawLine(SystemPens.ControlDark, 0, 0, _statusBar.Width, 0);
        _host.Controls.Add(_statusBar);
    }

    private void BuildMenus()
    {
        var items = FileMenuItem.DropDownItems;
        items.Clear();
        items.Add(CreateMenuItem("Open FNG...", Keys.Control | Keys.O, (_, _) => OpenFilesDialog()));
        items.Add(CreateMenuItem("Open container...", Keys.None, OpenContainerMenuItem_Click));
        items.Add(CreateMenuItem("Open FNG folder...", Keys.None, (_, _) => OpenFngFolder()));
        _recentDocumentsMenu = new ToolStripMenuItem("最近打开(&R)") { Enabled = false };
        items.Add(_recentDocumentsMenu);
        FileMenuItem.DropDownOpening += (_, _) => RefreshRecentDocumentsMenu();
        items.Add(new ToolStripSeparator());
        items.Add(CreateMenuItem("Save", Keys.Control | Keys.S, (_, _) => SaveActiveDocument(false)));
        items.Add(CreateMenuItem("Save as...", Keys.Control | Keys.Shift | Keys.S, (_, _) => SaveActiveDocument(true)));
        items.Add(CreateMenuItem("Save all modified FNG", Keys.None, (_, _) => SaveAllModifiedDocuments()));
        items.Add(CreateMenuItem("Reload", Keys.Control | Keys.Shift | Keys.R, (_, _) => ReloadActiveDocument()));
        items.Add(new ToolStripSeparator());
        items.Add(CreateMenuItem("Write back current container copy...", Keys.None, (_, _) => WriteBackCurrentContainer()));
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
        var opened = _workspace.Documents.ToHashSet();
        foreach(var document in _documentTrees.Keys.Where(d=>!opened.Contains(d)).ToArray()) _documentTrees.Remove(document);
        foreach(var key in _treeViews.Keys.Where(k=>!opened.Contains(k.Item1)).ToArray()) _treeViews.Remove(key);
        UpdateTabBar();
        RebuildWorkspaceTree();
        UpdateFileCountHint();
        SetFormDetails();
    }

    private void RefreshWorkspaceState()
    {
        UpdateTabBar();
        UpdateWorkspaceNodeStates();
        UpdateWorkspaceMenuState();
        UpdateFileCountHint();
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
        _tabBar.Visible = items.Length > 0;
    }

    private void PackageView_FormClosing(object sender, FormClosingEventArgs e)
    {
        if (_workspace == null) return;
        if (!_workspace.CloseAll())
        {
            e.Cancel = true;
            return;
        }

        SaveLayoutState();
    }

    private void BindDocument(WorkspaceDocument document)
    {
        ValidateChildren();
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
            RenderGeometry.Prepare(_currentRenderTree);
            viewOutput.ResetPreview();
            viewOutput.SetView(document.CanvasZoom <= 0 ? 1 : document.CanvasZoom, document.CanvasPan, document.CanvasFit);
            AppService.Instance.ActivatePackage(document.Package, document.FilePath, document.IsJson);
            viewOutput.Init(document.TextureDirectory);
            _packageViewExtensions.ObjectSelected = null;
            _packageViewExtensions.ShouldCopyObject = false;
            _packageViewExtensions.SearchText = document.SearchText;
            _packageViewExtensions.SearchIndex = document.SearchIndex;
            if (_leftMode == WorkspaceLeftMode.ObjectTree)
            {
                _syncingSearch = true;
                try { TxtSearch.Text = document.SearchText; }
                finally { _syncingSearch = false; }
            }
            SetPropertyTarget(null);
            viewOutput.SelectedNode = null;
            PopulateTreeView(document.Package, _currentRenderTree);
            RestoreTreeSelection(document.SelectedTreeKey);
            RestoreTreeView();
            Render();
            if(_leftMode==WorkspaceLeftMode.Files)viewOutput.StartPackagePreview(document.Package);
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
        viewOutput.CancelInteraction();
        document.CanvasZoom = viewOutput.Zoom;
        document.CanvasPan = viewOutput.Pan;
        document.CanvasFit = viewOutput.Fit;
        RememberTreeView();
        document.SelectedTreeKey = treeView1.SelectedNode?.Name;
        if (_leftMode == WorkspaceLeftMode.ObjectTree) document.SearchText = TxtSearch.Text;
        document.SearchIndex = _packageViewExtensions.SearchIndex;
    }

    private void ShowEmptyEditor()
    {
        viewOutput.CancelInteraction();
        viewOutput.ResetPreview();
        _boundDocument = null;
        _currentPackage = null;
        _currentRenderTree = null;
        AppService.Instance.ActivatePackage(null, null, false);
        _packageViewExtensions.ObjectSelected = null;
        _packageViewExtensions.ShouldCopyObject = false;
        _packageViewExtensions.SearchText = null;
        _packageViewExtensions.SearchIndex = -1;
        _rootNode = null;
        _syncingSearch = true;
        try { TxtSearch.Text = ""; }
        finally { _syncingSearch = false; }
        treeView1.BeginUpdate();
        treeView1.Nodes.Clear();
        treeView1.EndUpdate();
        SetPropertyTarget(null);
        viewOutput.SelectedNode = null;
        viewOutput.Render(null);
        LblItemIndex.Text = "索引：00000";
        LblChildsCount.Text = "子对象：00000";
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
        var previous = _leftMode;
        if (previous == WorkspaceLeftMode.Files && mode != previous && _workspaceTree != null)
            _workspaceTopNode = _workspaceTree.TopNode;
        if (_leftToolbar != null && !_syncingSearch && previous != mode)
        {
            if (previous == WorkspaceLeftMode.Files) _fileFilterText = TxtSearch.Text;
            else PersistNodeSearchText();
        }

        _leftMode = mode;
        if (_workspaceTree == null || treeView1 == null) return;

        _workspaceTree.Visible = mode == WorkspaceLeftMode.Files;
        treeView1.Visible = mode == WorkspaceLeftMode.ObjectTree;
        var files = mode == WorkspaceLeftMode.Files;
        _filesModeButton.BackColor = files ? SystemColors.Highlight : EditorTheme.Surface;
        _filesModeButton.ForeColor = files ? SystemColors.HighlightText : EditorTheme.Text;
        _treeModeButton.BackColor = files ? EditorTheme.Surface : SystemColors.Highlight;
        _treeModeButton.ForeColor = files ? EditorTheme.Text : SystemColors.HighlightText;
        _fileExtras.Visible = files;
        _treeExtras.Visible = !files;
        UpdateFileCountHint();

        _syncingSearch = true;
        try { TxtSearch.Text = files ? _fileFilterText : (NodeSearchText() ?? ""); }
        finally { _syncingSearch = false; }

        if (files)
        {
            // Switching views must not rebuild, expand groups or scroll to the active file.
            ApplyWorkspaceNodeTextWidths();
            if (previous != mode && _workspaceTopNode?.TreeView == _workspaceTree)
                _workspaceTree.TopNode = _workspaceTopNode;
        }
        else
        {
            _packageViewExtensions.SearchText = TxtSearch.Text;
            _packageViewExtensions.SearchIndex = _workspace.Active?.SearchIndex ?? -1;
        }
        UpdatePackagePreviewControls();
        if(previous!=mode && _currentPackage!=null)
        {
            if(files)viewOutput.StartPackagePreview(_currentPackage);
            else
            {
                viewOutput.ResetPreview();
                viewOutput.SelectedNode=FindTreeOwner(treeView1.SelectedNode);
                _inspector?.RefreshGeometry();
            }
        }
    }

    private string NodeSearchText() => _workspace?.Active?.SearchText;

    private void PersistNodeSearchText()
    {
        var document = _workspace?.Active;
        if (document == null) return;
        document.SearchText = TxtSearch.Text;
        document.SearchIndex = _packageViewExtensions.SearchIndex;
    }

    private void TxtSearch_TextChanged(object sender, EventArgs e)
    {
        if (_syncingSearch) return;
        if (_leftMode != WorkspaceLeftMode.Files) return;
        _fileFilterText = TxtSearch.Text;
        RebuildWorkspaceTree();
    }

    /// <summary>The file count lives in the search placeholder and the mode tooltip, not in a row.</summary>
    private void UpdateFileCountHint()
    {
        if (TxtSearch == null) return;
        var count = _workspace?.FileCount ?? 0;
        var opened = _workspace?.DocumentCount ?? 0;
        TxtSearch.PlaceholderText = _leftMode == WorkspaceLeftMode.Files
            ? $"搜索 FNG 文件（共 {count} 个）..."
            : "搜索节点...";
        if (_layoutToolTip != null && _filesModeButton != null)
            _layoutToolTip.SetToolTip(_filesModeButton,
                $"工作区 {count} 个 FNG，已打开 {opened} 个。\n状态点：灰=未打开，绿=已打开，蓝=当前文档，红=文件缺失");
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
        var filter = (_leftMode == WorkspaceLeftMode.Files ? TxtSearch.Text : _fileFilterText)?.Trim() ?? "";
        var expandedGroups = _workspaceTree.Nodes.Cast<TreeNode>()
            .ToDictionary(node => node.Name, node => node.IsExpanded, StringComparer.OrdinalIgnoreCase);
        var selectedKey = _workspaceTree.SelectedNode?.Name;
        var topKey = (_leftMode == WorkspaceLeftMode.Files ? _workspaceTree.TopNode : _workspaceTopNode)?.Name;
        var sortByName = _fileSortCombo != null && _fileSortCombo.SelectedIndex == 1;
        var duplicateNames = new HashSet<string>(
            _workspace.Files.GroupBy(file => file.Caption, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key),
            StringComparer.OrdinalIgnoreCase);

        _workspaceTree.BeginUpdate();
        try
        {
            _workspaceTree.Nodes.Clear();
            _workspaceNodes.Clear();
            _workspaceNodeCaptions.Clear();
            _workspaceNodeWidths.Clear();
            foreach (var group in _workspace.Groups)
            {
                var files = group.Files.Where(file => MatchesWorkspaceFilter(file, filter)).ToList();
                if (sortByName) files = files.OrderBy(file => file.Caption, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count == 0 && filter.Length > 0) continue;
                var pendingInGroup = _writeBack?.PendingCount(group.Files) ?? 0;
                var rootCaption = $"{group.Name} ({files.Count})" +
                                  (pendingInGroup > 0 ? $"　· 待写回 {pendingInGroup}" : "");
                var root = new TreeNode(rootCaption)
                {
                    Name = "group:" + group.Name,
                    ImageKey = "group",
                    SelectedImageKey = "group",
                    ToolTipText = $"{group.Name}：{group.Files.Count} 个 FNG" +
                                  (pendingInGroup > 0 ? $"，{pendingInGroup} 个已保存待写回" : "")
                };
                _workspaceTree.Nodes.Add(root);
                RememberNodeCaption(root, rootCaption);
                foreach (var file in files.Take(WorkspaceNodeLimit))
                {
                    var caption = file.Caption;
                    // Same file name in different folders must stay distinguishable without hovering.
                    if (duplicateNames.Contains(file.Caption))
                        caption += "  ·  " + ParentFolderName(file.Path);
                    var node = new TreeNode(caption) { Name = file.Path, Tag = file };
                    ApplyWorkspaceNodeState(node, file);
                    RememberNodeCaption(node, caption);
                    root.Nodes.Add(node);
                    _workspaceNodes[file.Path] = node;
                }

                if (files.Count > WorkspaceNodeLimit)
                {
                    var more = $"— 其余 {files.Count - WorkspaceNodeLimit} 个文件，请用搜索过滤 —";
                    root.Nodes.Add(new TreeNode(more) { ForeColor = SystemColors.GrayText });
                    RememberNodeCaption(root.Nodes[^1], more);
                }

                if (!expandedGroups.TryGetValue(root.Name, out var expanded) || expanded)
                    root.Expand();
            }
        }
        finally
        {
            _workspaceTree.EndUpdate();
        }

        ApplyWorkspaceNodeTextWidths();
        if (selectedKey != null)
            _workspaceTree.SelectedNode = _workspaceTree.Nodes.Find(selectedKey, true).FirstOrDefault();
        // Selecting a hidden child can expand its parent in the native tree. Preserve the
        // user's collapsed groups even when a workspace refresh restores that selection.
        foreach (TreeNode root in _workspaceTree.Nodes)
            if (expandedGroups.TryGetValue(root.Name, out var expanded) && !expanded) root.Collapse();
        _workspaceTopNode = topKey == null ? null : _workspaceTree.Nodes.Find(topKey, true).FirstOrDefault();
        if (_workspaceTopNode != null) _workspaceTree.TopNode = _workspaceTopNode;
    }

    private static string ParentFolderName(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(directory) ? "" : new DirectoryInfo(directory).Name;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private void RememberNodeCaption(TreeNode node, string caption)
    {
        _workspaceNodeCaptions[node] = caption;
        var width = TextRenderer.MeasureText(caption, _workspaceTree.Font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        _workspaceNodeWidths[node] = width;
    }

    /// <summary>Long names are shortened with an ellipsis; the full name stays in the tooltip.</summary>
    private void ApplyWorkspaceNodeTextWidths()
    {
        if (_workspaceTree == null || _workspaceNodeCaptions.Count == 0) return;
        if (!_workspaceTree.Visible) return;
        var available = Math.Max(60, _workspaceTree.ClientSize.Width - 26);
        var changed = false;
        foreach (var pair in _workspaceNodeCaptions)
        {
            var node = pair.Key;
            var caption = pair.Value;
            var width = _workspaceNodeWidths.TryGetValue(node, out var measured) ? measured : 0;
            var text = Ellipsize(caption, width, available);
            if (!string.Equals(node.Text, text, StringComparison.Ordinal))
            {
                node.Text = text;
                changed = true;
            }
        }

        if (changed) _workspaceTree.Invalidate();
    }

    private static string Ellipsize(string text, int measuredWidth, int maxWidth)
    {
        if (measuredWidth <= maxWidth || text.Length <= 2) return text;
        var keep = (int)Math.Floor((text.Length - 1) * Math.Max(8, maxWidth - 8) / (double)measuredWidth);
        keep = Math.Clamp(keep, 1, text.Length - 1);
        return text[..keep] + "…";
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
        // The check mark means "saved changes pending write-back"; unsaved edits keep the
        // existing open-state dot and the * suffix, so the two states never blend into one icon.
        var pending = !file.IsMissing && document?.IsDirty != true &&
                      _writeBack != null && _writeBack.IsPending(file.Path);
        node.ImageKey = node.SelectedImageKey = file.IsMissing ? "missing"
            : pending ? "pending"
            : document == null ? "idle"
            : isActive ? "active" : "open";
        node.NodeFont = document == null ? null : _boldNodeFont;
        var tooltip = file.Caption + "\n" + file.Path;
        if (file.IsMissing) tooltip += "\n状态：文件缺失（红点）";
        else if (pending) tooltip += "\n状态：有已保存修改，待写回容器副本（绿色勾）";
        else if (document == null) tooltip += "\n状态：未打开（灰点）";
        else if (isActive) tooltip += document.IsDirty ? "\n状态：当前文档，未保存（蓝点）" : "\n状态：当前文档（蓝点）";
        else tooltip += document.IsDirty ? "\n状态：已打开，未保存（绿点）" : "\n状态：已打开（绿点）";
        node.ToolTipText = tooltip;
    }

    private void WorkspaceTree_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button == MouseButtons.Left && e.Node?.Tag is WorkspaceFile file)
            OpenWorkspaceFile(file);
    }

    private void WorkspaceTree_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button == MouseButtons.Left && e.Node?.Tag is WorkspaceFile file && OpenWorkspaceFile(file))
            SetLeftMode(WorkspaceLeftMode.ObjectTree);
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

    private bool OpenWorkspaceFile(WorkspaceFile file)
    {
        if (file == null) return false;
        if (file.IsMissing)
        {
            MessageBox.Show(this, $"文件已不在磁盘上：\n{file.Path}\n\n请重新导入，或从工作区移除。", "文件缺失",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // The first click already bound the active document. A second click should only
        // change the navigation view, without rebuilding its object tree or textures.
        var existing = _workspace.FindDocument(file.Path);
        if (existing != null && ReferenceEquals(existing, _workspace.Active) && ReferenceEquals(existing, _boundDocument))
        {
            if(_leftMode==WorkspaceLeftMode.Files && !viewOutput.PackagePreviewActive)
                viewOutput.StartPackagePreview(existing.Package);
            return true;
        }
        var document = _workspace.OpenFile(file);
        SetFormDetails();
        return document != null && ReferenceEquals(_boundDocument, document);
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
    private string ImportExtractedFiles(string containerPath, string extractionDirectory, string groupName,
        IReadOnlyList<string> paths)
    {
        // Bind provenance before opening documents: saves must already have their session,
        // independently of how the extraction/browser dialogs are subsequently closed.
        var registration = _writeBack.RegisterAfterExtraction(containerPath, extractionDirectory);
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
        text += "；" + registration;
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

    /// <summary>
    /// Batch save of every dirty document. Documents that fail (or get cancelled) stay dirty;
    /// already-saved ones are not reset when a later save fails.
    /// </summary>
    private void SaveAllModifiedDocuments()
    {
        var dirty = _workspace.Documents.Where(d => d.IsDirty).ToList();
        if (dirty.Count == 0)
        {
            _workspace.StatusMessage = "没有需要保存的文档";
            return;
        }

        var saved = 0;
        var failed = new List<string>();
        foreach (var document in dirty)
        {
            if (_workspace.Save(document)) saved++;
            else failed.Add(document.CaptionName);
        }

        _workspace.StatusMessage = $"保存全部：{saved}/{dirty.Count} 个已保存" +
                                   (failed.Count > 0 ? $"，{failed.Count} 个未保存" : "");
        if (failed.Count > 0)
            MessageBox.Show(this,
                "以下文档保存失败或被取消，仍保留未保存修改：\n" + string.Join("\n", failed),
                "保存全部已修改 FNG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Collects every saved-but-pending change of the active document's container (not just the
    /// current tab), previews the plan, then publishes it to the working copy as one transaction.
    /// </summary>
    private void WriteBackCurrentContainer()
    {
        var document = _workspace.Active;
        if (document == null)
        {
            MessageBox.Show(this, "当前没有打开的文档。", "写回容器副本", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ContainerSession session;
        try { session = _writeBack.SessionForDocument(document); }
        catch (Exception error)
        {
            MessageBox.Show(this, "容器来源或恢复记录无法读取：\n" + error.Message,
                "写回容器副本", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (session == null)
        {
            MessageBox.Show(this,
                "当前文档没有容器写回会话（其所在目录没有 container.session.json）。\n" +
                "只有从容器提取并登记过的 FNG 才能写回；来源不明的文件不能按名称猜测关联。",
                "写回容器副本", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        List<WorkspaceDocument> unsaved;
        try
        {
            unsaved = _workspace.Documents
                .Where(d => d.IsDirty && ContainerSession.PathsEqual(
                    _writeBack.SessionForDocument(d)?.SessionDirectory, session.SessionDirectory))
                .ToList();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "无法核对未保存文档的来源，写回已取消：\n" + error.Message,
                "写回容器副本", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (unsaved.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"此容器还有 {unsaved.Count} 个 FNG 存在未保存修改：\n" +
                string.Join("\n", unsaved.Select(d => "  " + d.Caption)) +
                "\n\n先保存这些 FNG，再重新生成写回清单？",
                "写回容器副本", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                _workspace.StatusMessage = "已取消容器写回（存在未保存修改）";
                return;
            }
            foreach (var pending in unsaved)
                if (!_workspace.Save(pending))
                {
                    _workspace.StatusMessage = "已取消容器写回：有 FNG 未能保存";
                    return;
                }
        }

        if (session.WorkingCopyPath == null)
        {
            using var copyDialog = new SaveFileDialog
            {
                Title = "选择工作副本保存位置（原始容器的完整副本，写回只修改它）",
                Filter = "容器文件 (*.bun, *.lzc)|*.bun;*.lzc|所有文件 (*.*)|*.*",
                FileName = Path.GetFileNameWithoutExtension(session.OriginalPath) +
                           ".work" + Path.GetExtension(session.OriginalPath),
                InitialDirectory = session.SessionDirectory
            };
            if (copyDialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                session.CreateWorkingCopy(copyDialog.FileName);
                _workspace.StatusMessage = "工作副本已创建并校验：" + session.WorkingCopyPath;
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "工作副本创建失败：\n" + error.Message,
                    "写回容器副本", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        ContainerWritePlan plan;
        try
        {
            plan = ContainerWritePlanner.Build(session);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "生成写回计划失败：\n" + error.Message,
                "写回容器副本", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (!plan.HasPendingChanges && !plan.IsBlocked)
        {
            MessageBox.Show(this, "此容器当前没有待写回的已保存修改。", "写回容器副本",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using (var preview = new WriteBackPreviewDialog(plan))
        {
            if (preview.ShowDialog(this) != DialogResult.OK) return;
        }

        ContainerWriteBackReport report;
        try
        {
            UseWaitCursor = true;
            report = ContainerWriteBackExecutor.Execute(session, plan,
                message => _workspace.StatusMessage = "容器写回：" + message);
        }
        catch (Exception error)
        {
            var evidence = (error as ContainerWriteBackException)?.Report;
            var detail = evidence?.Published == true
                ? "副本已发布，但后续验证或登记未完成。请核对恢复记录，不要盲目重复写回。"
                : "本次操作未发布候选副本。";
            if (evidence?.BackupPath != null) detail += "\n备份: " + evidence.BackupPath;
            if (evidence != null) detail += "\n副本: " + evidence.WorkingCopyPath + "\n日志 ID: " + evidence.JournalId;
            detail += "\n恢复日志: " + WriteBackJournal.JournalFilePath(session.SessionDirectory);
            _writeBack.Invalidate(session);
            _writeBack.NotifyPendingChanged();
            MessageBox.Show(this,
                detail + "\n" + error.Message,
                "写回容器副本", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _workspace.StatusMessage = evidence?.Published == true ? "副本已发布，后续验证或登记待恢复" : "本次写回未发布候选副本";
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }

        _writeBack.Invalidate(session);
        _writeBack.NotifyPendingChanged();
        var summary = report.LedgerComplete
            ? $"容器写回成功：{report.Items.Count} 个 FNG 已写入工作副本，{report.VerifiedBlockCount} 个块验证通过"
            : "容器写回已发布，但状态登记未完成，下次写回前会自动核对恢复";
        if (report.Warnings.Count > 0) summary += "（" + string.Join("；", report.Warnings) + "）";
        _workspace.StatusMessage = summary;
        MessageBox.Show(this,
            $"{summary}\n\n工作副本: {report.WorkingCopyPath}\n备份: {report.BackupPath}",
            "写回容器副本", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        var menu = PrepareTabMenu(ref _tabContextMenu);
        var close = new ToolStripMenuItem("关闭标签");
        close.Click += (_, _) => _workspace.Close(document);
        var closeOthers = new ToolStripMenuItem("关闭其他标签") { Enabled = _workspace.Documents.Count > 1 };
        closeOthers.Click += (_, _) => _workspace.CloseOthers(document);
        var closeAll = new ToolStripMenuItem("关闭所有标签") { Enabled = _workspace.Documents.Count > 0 };
        closeAll.Click += (_, _) => _workspace.CloseAll();
        var reveal = new ToolStripMenuItem("在文件夹中显示");
        reveal.Click += (_, _) => RevealWorkspaceFile(new WorkspaceFile(document.FilePath, document.IsJson));
        menu.Items.AddRange(new ToolStripItem[] { close, closeOthers, closeAll, new ToolStripSeparator(), reveal });
        EditorTheme.Apply(menu);
        menu.Show(Cursor.Position);
    }

    private void RefreshRecentDocumentsMenu()
    {
        while (_recentDocumentsMenu.DropDownItems.Count > 0)
            _recentDocumentsMenu.DropDownItems[0].Dispose();
        _recentDocumentsMenu.Enabled = _recentDocuments.Count > 0;
        foreach (var path in _recentDocuments.Take(8))
        {
            var item = new ToolStripMenuItem(Path.GetFileName(path)) { ToolTipText = path };
            item.Click += (_, _) => OpenWorkspacePath(path);
            _recentDocumentsMenu.DropDownItems.Add(item);
        }
        EditorTheme.Apply(_recentDocumentsMenu.DropDown);
    }

    private ContextMenuStrip PrepareTabMenu(ref ContextMenuStrip menu)
    {
        // WinForms still reparents the dropdown after raising Closed. Disposing it in that
        // event breaks the active menu message filter. Keep it owned by the form instead.
        menu ??= new ContextMenuStrip(components);
        while (menu.Items.Count > 0) menu.Items[0].Dispose();
        return menu;
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
            title += $" - {_workspace.DisplayName ?? "Workspace"} 工作区（{_workspace.FileCount} 个文件）";
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
