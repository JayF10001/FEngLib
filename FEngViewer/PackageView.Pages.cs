using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FEngLib.Packages;
using FEngRender.Data;

namespace FEngViewer;

public partial class PackageView
{
    private WorkspaceFile _previewFile;
    private WorkspaceDocument _previewDocument;
    private RenderTree _previewTree;
    private bool _previewWasOpen;
    private int _previewVersion;
    private float _workspaceZoom = 1;
    private PointF _workspacePan;
    private bool _workspaceFit = true;
    private PreviewPlaybackState _workspacePlayback;
    private Panel _workspaceDetails;
    private TextBox _workspaceInfo;
    private Label _propertyTitle;
    private Button _workspacePause;

    private Package DisplayedPackage => _boundDocument?.Package ?? _previewDocument?.Package;

    private void BuildWorkspaceDetails()
    {
        _workspaceInfo = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = SystemColors.Window,
            Text = "选择左侧 FNG 文件以预览。\r\n双击文件进入编辑。"
        };
        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 6)
        };
        var play = new Button { Text = "重新预览 30 秒", AutoSize = true };
        play.Click += (_, _) => viewOutput.StartPackagePreview(DisplayedPackage);
        _workspacePause = new Button { Text = "暂停", AutoSize = true };
        _workspacePause.Click += (_, _) =>
        {
            if (viewOutput.PreviewPlaying) viewOutput.PausePreview();
            else viewOutput.ResumePreview();
        };
        var reset = new Button { Text = "初始布局", AutoSize = true };
        reset.Click += (_, _) => viewOutput.ResetPreview();
        controls.Controls.AddRange(new Control[] { play, _workspacePause, reset });
        _workspaceDetails = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), Visible = false };
        _workspaceDetails.Controls.Add(_workspaceInfo);
        _workspaceDetails.Controls.Add(controls);
        _innerSplit.Panel2.Controls.Add(_workspaceDetails);
        _workspaceDetails.BringToFront();
    }

    private void UpdatePagePanels()
    {
        if (_workspaceDetails == null) return;
        var files = _leftMode == WorkspaceLeftMode.Files;
        _workspaceDetails.Visible = files;
        _inspector.Visible = !files;
        _propertyEmptyLabel.Visible = !files && objectPropertyGrid.SelectedObject == null;
        groupBox1.Visible = !files;
        _propertyTitle.Text = files ? "文件信息（预览模式）" : "属性";
        UpdateWorkspaceInformation();
    }

    private void UpdateWorkspaceInformation()
    {
        if (_workspaceInfo == null) return;
        if (_previewDocument?.Package == null || _previewFile == null)
        {
            _workspaceInfo.Text = "选择左侧 FNG 文件以预览。\r\n双击文件进入编辑。";
            return;
        }
        var file = _previewFile;
        var document = FindWorkspaceDocument(file);
        if (file.Project != null)
        {
            var saved = _savedFngEdits.ContainsKey(file.Block.Offset);
            var state = document?.IsDirty == true ? "已修改" : saved ? "工程已保存" : "未修改";
            var baseline = file.Project.BaselinePath;
            var baselineInfo = string.Equals(baseline, file.Project.Index.SourcePath, StringComparison.OrdinalIgnoreCase)
                ? ""
                : $"\r\n\r\n工程基线：\r\n{baseline}";
            _workspaceInfo.Text = $"文件名：{file.Caption}\r\n\r\n源容器：\r\n{file.Project.Index.SourcePath}{baselineInfo}\r\n\r\n" +
                $"逻辑偏移：0x{file.Block.Offset:X8}\r\n块大小：{file.Block.TotalLength:N0} 字节\r\n" +
                $"对象数量：{_previewDocument.Package.Objects.Count}\r\n\r\n文档状态：{state}" +
                "\r\n\r\n预览只读；双击左侧文件进入编辑。";
            return;
        }
        string size;
        try { size = new FileInfo(file.Path).Length.ToString("N0") + " 字节"; }
        catch (IOException) { size = "不可用"; }
        catch (UnauthorizedAccessException) { size = "无法访问"; }
        _workspaceInfo.Text = $"文件名：{file.Caption}\r\n\r\n完整工作路径：\r\n{file.Path}\r\n\r\n" +
            $"来源分组：{_workspace.GroupOf(file)?.Name ?? "—"}\r\n来源目录：{Path.GetDirectoryName(file.Path)}\r\n\r\n" +
            $"格式：{(file.IsJson ? "JSON" : "FNG")}\r\n磁盘大小：{size}\r\n对象数量：{_previewDocument.Package.Objects.Count}\r\n\r\n" +
            $"文档状态：{(document == null ? "未打开" : document.IsDirty ? "已打开，未保存修改" : "已打开")}" +
            (document != null ? "\r\n预览使用当前内存模型。" : "") +
            "\r\n\r\n预览只读；双击左侧文件进入编辑。";
    }

    private void RememberWorkspaceView()
    {
        if (_leftMode != WorkspaceLeftMode.Files) return;
        _workspaceZoom = viewOutput.Zoom;
        _workspacePan = viewOutput.Pan;
        _workspaceFit = viewOutput.Fit;
        _workspacePlayback = viewOutput.CapturePreviewState();
    }

    private bool PreviewWorkspaceFile(WorkspaceFile file, bool reportError = true)
    {
        if (file == null || _workspace.Active != null) return false;
        var previous = _previewDocument;
        var previousFile = _previewFile;
        var previousTree = _previewTree;
        var previousPlayback = viewOutput.CapturePreviewState();
        var previousWasOpen = _previewWasOpen;
        var previousVersion = _previewVersion;
        try
        {
            // A preview-only document is never registered as an open editor. Prefer an open
            // model even when its disk file has disappeared or contains older, clean data.
            var opened = FindWorkspaceDocument(file);
            var document = opened ?? CreateWorkspaceDocument(file);
            document.EnsureLoaded((path, json) => json ? AppService.Instance.ReadJson(path) : AppService.Instance.ReadFile(path));
            var tree = RenderTree.Create(document.Package); // evaluation state is private to this preview
            RenderGeometry.Prepare(tree);
            PrepareTextureSources(document);
            viewOutput.Init(document.TextureDirectory);
            viewOutput.CancelInteraction();
            viewOutput.ResetPreview();
            _previewFile = file;
            _previewDocument = document;
            _previewTree = tree;
            _previewWasOpen = opened != null;
            _previewVersion = document.ModelVersion;
            viewOutput.ReadOnly = true;
            viewOutput.SelectedNode = null;
            viewOutput.Render(tree);
            viewOutput.StartPackagePreview(document.Package);
            ResolveWorkspaceCaption(file, document);
            UpdateWorkspaceInformation();
            UpdateHistoryButtons();
            SetFormDetails();
            return true;
        }
        catch (Exception error)
        {
            _previewDocument = previous;
            _previewFile = previousFile;
            _previewTree = previousTree;
            _previewWasOpen = previousWasOpen;
            _previewVersion = previousVersion;
            viewOutput.ResetPreview();
            viewOutput.Render(previousTree);
            if (previous?.Package != null)
            {
                try
                {
                    PrepareTextureSources(previous);
                    viewOutput.Init(previous.TextureDirectory);
                    viewOutput.RestorePreviewState(previous.Package, previousPlayback);
                }
                catch (Exception) { /* Keep the original failure and the last available model visible. */ }
            }
            _workspace.StatusMessage = $"预览失败：{file.Caption}；已保留上次预览。{error.Message}";
            if (_previewFile != null && _workspaceNodes.TryGetValue(_previewFile.Key, out var node))
                _workspaceTree.SelectedNode = node;
            if (reportError) MessageBox.Show(this, file.Location + "\n\n" + error.Message, "预览失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateWorkspaceInformation();
            UpdateHistoryButtons();
            SetFormDetails();
            return false;
        }
    }

    private bool PreviewFirstAvailable(IEnumerable<WorkspaceFile> files)
    {
        foreach (var file in files)
            if (PreviewWorkspaceFile(file, reportError: false))
            {
                if (_workspaceNodes.TryGetValue(file.Key, out var node)) _workspaceTree.SelectedNode = node;
                return true;
            }
        return false;
    }

    private void RestoreWorkspacePreview()
    {
        if (_previewFile == null) return;
        if (PreviewWorkspaceFile(_previewFile))
        {
            viewOutput.SetView(_workspaceZoom, _workspacePan, _workspaceFit);
            if (_workspacePlayback != null) viewOutput.RestorePreviewState(DisplayedPackage, _workspacePlayback);
            else viewOutput.ResetPreview();
        }
    }

    private void RestoreEditorPreview(WorkspaceDocument document) =>
        viewOutput.RestorePreviewState(document.Package, document.PreviewState);

    private void ResetWorkspacePreview()
    {
        ReleaseProjectTextureSource();
        _previewFile = null;
        _previewDocument = null;
        _previewTree = null;
        _previewWasOpen = false;
        _workspacePlayback = null;
        _workspaceZoom = 1;
        _workspacePan = PointF.Empty;
        _workspaceFit = true;
        viewOutput.ResetPreview();
        viewOutput.Render(null);
    }

    private void RefreshPreviewAfterWorkspaceChange()
    {
        if (_workspaceInfo == null) return;
        if (_previewFile != null && (_previewFile.Project == null
                ? _containerProject != null || _workspace.FindFile(_previewFile.Path) == null
                : !ReferenceEquals(_previewFile.Project, _containerProject)))
        {
            _previewFile = null;
            _previewDocument = null;
            _previewTree = null;
            if (_leftMode == WorkspaceLeftMode.Files) { viewOutput.ResetPreview(); viewOutput.Render(null); }
        }
        if (_leftMode == WorkspaceLeftMode.Files && _previewFile != null)
        {
            var open = FindWorkspaceDocument(_previewFile);
            if (open != null && (!ReferenceEquals(open, _previewDocument) || _previewVersion != open.ModelVersion) ||
                open == null && _previewWasOpen)
            {
                // Discard/Reload/Save As must not leave a closed model in the global preview.
                if (!PreviewWorkspaceFile(_previewFile, reportError: false))
                {
                    _previewDocument = null;
                    _previewTree = null;
                    viewOutput.ResetPreview(); viewOutput.Render(null);
                }
            }
        }
        UpdateWorkspaceInformation();
        UpdateHistoryButtons();
    }
}
