using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using FEngLib.Assets;

namespace FEngViewer;

public partial class PackageView
{
    private ContainerProject _containerProject;
    private WorkspaceGroup _containerGroup;
    private Dictionary<(long FngOffset, uint RequestId), ContainerTextureOverride> _textureOverrides = new();
    private bool _textureOverridesDirty;

    private IEnumerable<WorkspaceGroup> DisplayedWorkspaceGroups =>
        _containerGroup == null ? _workspace.Groups : new[] { _containerGroup };

    private void BuildProjectPages()
    {
        LoadRecentEntries();
        BuildStartPage();
    }

    private void UpdateProjectPage()
    {
        if (_startPage == null) return;
        var showStart = _workspace.Active == null && _containerProject == null && _workspace.FileCount == 0;
        _startPage.Visible = showStart;
        _splitOuter.Visible = !showStart;
        if (showStart) RefreshRecentProjects();
    }

    private static bool IsContainerPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".bun" or ".lzc";

    private sealed record LoadedContainerProject(ContainerProject Project, ContainerProjectSnapshot Snapshot,
        WorkspaceDocument[] Tabs, Dictionary<long, ContainerFngContent> Edits);

    private void OpenContainerProject(string path)
    {
        if (_projectFilePath != null && ContainerSession.PathsEqual(path, _projectFilePath))
        {
            _workspace.ShowWorkspace();
            return;
        }

        LoadedContainerProject loaded;
        var recovered = false;
        try
        {
            UseWaitCursor = true;
            loaded = LoadContainerProject(path);
        }
        catch (ProjectBaselineMissingException)
        {
            UseWaitCursor = false;
            if (!TryRecoverProjectBaseline(path, out var recoveredBaseline)) return;
            try
            {
                UseWaitCursor = true;
                loaded = LoadContainerProject(path, recoveredBaseline);
                recovered = true;
            }
            catch (Exception retryError)
            {
                ShowContainerOpenError(path, retryError);
                return;
            }
            finally { UseWaitCursor = false; }
        }
        catch (Exception error)
        {
            ShowContainerOpenError(path, error);
            return;
        }
        finally { UseWaitCursor = false; }

        var project = loaded.Project;
        var snapshot = loaded.Snapshot;
        var tabs = loaded.Tabs;
        var edits = loaded.Edits;
        if (!CloseWorkspaceTabs()) return;
        _containerProject = project;
        _projectFilePath = snapshot == null ? null : Path.GetFullPath(path);
        _savedFngEdits = edits;
        _textureOverrides = snapshot?.Textures?.ToDictionary(texture => (texture.FngOffset, texture.RequestId)) ?? new();
        _textureOverridesDirty = false;
        ResetWorkspacePreview();
        ClearWorkspaceFilter();
        _containerGroup = new WorkspaceGroup(Path.GetFileNameWithoutExtension(_projectFilePath ?? project.Index.SourcePath));
        var knownNames = edits.Values.Select(edit => edit.Source).Concat(tabs.Select(tab => tab.ContainerSource))
            .DistinctBy(source => source.Block.Offset).ToDictionary(source => source.Block.Offset, source => source.PackageName);
        foreach (var block in project.Index.Blocks.Where(IsFngBlock))
        {
            var file = new WorkspaceFile(project, block);
            if (knownNames.TryGetValue(block.Offset, out var name)) file.Caption = name;
            _containerGroup.Files.Add(file);
        }
        foreach (var document in tabs)
            _workspace.OpenDocument(document, activate: false, reportError: true);
        if (File.Exists(project.Index.SourcePath)) RememberRecentContainer(project.Index.SourcePath);
        if (_projectFilePath != null) RememberRecentProject(_projectFilePath);
        _workspace.StatusMessage = snapshot == null ? "选择左侧 FNG 以预览，双击进入编辑" :
            recovered ? $"工程已恢复 · {SavedProjectWriteBackState()}" :
            $"工程已打开 · {SavedProjectWriteBackState()}";
        RefreshWorkspaceStructure();
        ApplyCachedContainerFngNames(project);
        _ = ResolveContainerFngNamesAsync(project);
        if (snapshot?.ActiveTab is long activeOffset)
            _workspace.Activate(tabs.Single(tab => tab.Block.Offset == activeOffset));
        RefreshWorkspaceState();
        if (!string.IsNullOrWhiteSpace(snapshot?.BaselineWarning))
        {
            _workspace.StatusMessage = "工程已打开（基线缓存未建立）";
            MessageBox.Show(this,
                snapshot.BaselineWarning + "\n\n保存工程前请先修复缓存目录权限或磁盘空间，否则工程仍依赖现有容器文件。",
                "基线缓存未建立", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private LoadedContainerProject LoadContainerProject(string path, string baselineOverride = null)
    {
        var snapshot = IsProjectPath(path) ? ContainerProjectFile.Load(path, baselineOverride) : null;
        var project = snapshot?.Project ?? ContainerProject.Open(path);
        var edits = snapshot?.Edits.ToDictionary(edit => edit.Source.Block.Offset) ?? new();
        var tabs = snapshot?.Tabs.Select(offset =>
        {
            var block = project.Index.FindBlockAtOffset(offset);
            var document = new WorkspaceDocument(project, block, original => ParseProjectFng(original, edits));
            document.EnsureLoaded(null);
            return document;
        }).ToArray() ?? Array.Empty<WorkspaceDocument>();
        return new LoadedContainerProject(project, snapshot, tabs, edits);
    }

    private bool TryRecoverProjectBaseline(string path, out string baseline)
    {
        baseline = null;
        ContainerProjectInfo info;
        try
        {
            info = ContainerProjectFile.Inspect(path);
        }
        catch (Exception inspectError)
        {
            MessageBox.Show(this,
                path + "\n\n无法读取工程清单，无法进入基线恢复流程。\n" + inspectError.Message,
                "无法打开工程", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        using var dialog = new ProjectBaselineRecoveryDialog(path, info);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        baseline = dialog.RecoveredBaselinePath;
        if (string.IsNullOrWhiteSpace(baseline))
        {
            MessageBox.Show(this, "没有获得可用的基线缓存路径，工程未打开。",
                "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private void ShowContainerOpenError(string path, Exception error)
    {
        MessageBox.Show(this, path + "\n\n" + error.Message, "无法打开容器工程",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private bool LeaveContainerProject()
    {
        if (_containerProject == null) return true;
        if (!CloseWorkspaceTabs()) return false;
        _containerProject = null;
        _projectFilePath = null;
        _savedFngEdits.Clear();
        _textureOverrides.Clear();
        _textureOverridesDirty = false;
        _containerGroup = null;
        ResetWorkspacePreview();
        ClearWorkspaceFilter();
        RefreshWorkspaceStructure();
        _workspace.StatusMessage = "已关闭容器工程";
        ShowEmptyEditor();
        return true;
    }

    private static bool IsFngBlock(ContainerBlock block) =>
        block.Kind is ContainerBlockKind.Fng or ContainerBlockKind.CompressedFng;

    private void ClearWorkspaceFilter()
    {
        _fileFilterText = "";
        _syncingSearch = true;
        try { TxtSearch.Clear(); }
        finally { _syncingSearch = false; }
    }

    private WorkspaceDocument FindWorkspaceDocument(WorkspaceFile file) => file == null ? null :
        file.Project == null ? _workspace.FindDocument(file.Path) : _workspace.FindBlockDocument(file);

    private WorkspaceDocument CreateWorkspaceDocument(WorkspaceFile file)
    {
        if (file.Project == null) return new WorkspaceDocument(file.Path, file.IsJson);
        var edits = _savedFngEdits;
        return new WorkspaceDocument(file.Project, file.Block, original => ParseProjectFng(original, edits));
    }

    private void ResolveWorkspaceCaption(WorkspaceFile file, WorkspaceDocument document)
    {
        if (file.Project == null || document?.ContainerSource == null ||
            file.Caption == document.ContainerSource.PackageName) return;

        file.Caption = document.ContainerSource.PackageName;
        var matching = _containerGroup.Files.Where(item =>
            string.Equals(item.Caption, file.Caption, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var item in matching)
        {
            if (!_workspaceNodes.TryGetValue(item.Key, out var node)) continue;
            var caption = matching.Length > 1 ? $"{item.Caption}  ·  0x{item.Block.Offset:X8}" : item.Caption;
            RememberNodeCaption(node, caption);
            node.Text = caption;
            ApplyWorkspaceNodeState(node, item);
        }
        // Keep the node at the same screen position between the two clicks of a double-click.
        ApplyWorkspaceNodeTextWidths();
    }

    private void ShowContainerBlockIndex()
    {
        if (_containerProject == null) return;
        using var dialog = new Form
        {
            Text = "容器块索引 · " + Path.GetFileName(_containerProject.Index.SourcePath),
            ClientSize = new Size(850, 520), StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false, Icon = Icon
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        list.Columns.Add("类型", 150);
        list.Columns.Add("逻辑偏移", 150);
        list.Columns.Add("块 ID", 130);
        list.Columns.Add("块大小（字节）", 170);
        foreach (var block in _containerProject.Index.Blocks)
        {
            var kind = block.Kind switch
            {
                ContainerBlockKind.CompressedFng => "FNG（压缩）",
                ContainerBlockKind.Padding => "填充",
                ContainerBlockKind.Unknown => "其他",
                _ => block.Kind.ToString().ToUpperInvariant()
            };
            list.Items.Add(new ListViewItem(new[]
            {
                kind, $"0x{block.Offset:X8}", $"0x{block.ChunkId:X8}", block.TotalLength.ToString("N0")
            }));
        }
        dialog.Controls.Add(list);
        EditorTheme.Apply(dialog);
        dialog.ShowDialog(this);
    }

    private bool ExportFng(WorkspaceDocument document)
    {
        if (document?.Package == null) return false;
        using var dialog = new SaveFileDialog
        {
            Title = "导出独立 FNG",
            Filter = "FNG (*.fng)|*.fng",
            FileName = Path.GetFileNameWithoutExtension(document.CaptionName) + ".fng",
            InitialDirectory = Path.GetDirectoryName(document.SourcePath)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        var target = dialog.FileName;
        if (!string.Equals(Path.GetExtension(target), ".fng", StringComparison.OrdinalIgnoreCase) ||
            ContainerSession.PathsEqual(target, document.SourcePath) ||
            _workspace.FindDocument(target) != null)
        {
            MessageBox.Show(this, "请选择独立的 .fng 路径。", "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var staged = Path.Combine(Path.GetDirectoryName(target), ".fng-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            SavePackageToChunk(document.Package, staged);
            File.Move(staged, target, overwrite: true);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            try { File.Delete(staged); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        _workspace.StatusMessage = $"已导出 {Path.GetFileName(target)}";
        SetFormDetails();
        return true;
    }

    private void ApplyCachedContainerFngNames(ContainerProject project)
    {
        if (project == null) return;
        var cached = ContainerFngNameCache.Load(project.Index.SourceSha256);
        if (cached.Count > 0) ApplyContainerFngNames(project, cached);
    }

    private void ApplyContainerFngNames(ContainerProject project, IReadOnlyDictionary<long, string> names)
    {
        if (project == null || names == null || names.Count == 0 || _containerGroup == null ||
            !ReferenceEquals(_containerProject, project)) return;
        var changed = false;
        foreach (var file in _containerGroup.Files)
        {
            if (file.Block == null) continue;
            if (!names.TryGetValue(file.Block.Offset, out var name) || string.IsNullOrWhiteSpace(name)) continue;
            if (string.Equals(file.Caption, name, StringComparison.Ordinal)) continue;
            file.Caption = name;
            changed = true;
        }
        if (changed) RefreshContainerFileCaptions();
    }

    /// <summary>Refreshes only the captions of container files; keeps tree expansion and selection.</summary>
    private void RefreshContainerFileCaptions()
    {
        if (_containerGroup == null || _workspaceNodes.Count == 0) return;
        var changed = false;
        foreach (var file in _containerGroup.Files)
        {
            if (file.Block == null) continue;
            if (!_workspaceNodes.TryGetValue(file.Key, out var node)) continue;
            var caption = file.Caption;
            var duplicate = _containerGroup.Files.Count(other =>
                string.Equals(other.Caption, caption, StringComparison.OrdinalIgnoreCase)) > 1;
            var text = duplicate ? $"{caption}  ·  0x{file.Block.Offset:X8}" : caption;
            var stored = _workspaceNodeCaptions.TryGetValue(node, out var value) ? value : node.Text;
            if (!string.Equals(stored, text, StringComparison.Ordinal))
            {
                RememberNodeCaption(node, text);
                node.Text = text;
                changed = true;
            }
            ApplyWorkspaceNodeState(node, file);
        }
        if (changed)
        {
            ApplyWorkspaceNodeTextWidths();
            _workspaceTree.Invalidate();
        }
    }

    private async Task ResolveContainerFngNamesAsync(ContainerProject project)
    {
        if (project == null || !ReferenceEquals(_containerProject, project) || _containerGroup == null) return;
        var pending = _containerGroup.Files
            .Where(file => file.Block != null && file.Block.Kind == ContainerBlockKind.CompressedFng)
            .Where(file => string.Equals(file.Caption, $"FNG 0x{file.Block.Offset:X8}", StringComparison.Ordinal))
            .Select(file => file.Block)
            .Distinct()
            .ToArray();
        if (pending.Length == 0) return;

        string binaryDirectory;
        try { binaryDirectory = BinaryRuntime.DirectoryPath; }
        catch (Exception error)
        {
            _workspace.StatusMessage = "Binary 运行库配置无法读取：" + error.Message;
            SetFormDetails();
            return;
        }
        if (binaryDirectory == null)
        {
            _workspace.StatusMessage = "未配置 Binary 运行库，压缩 FNG 名称暂不可用";
            SetFormDetails();
            return;
        }

        _workspace.StatusMessage = $"正在解析压缩 FNG 名称…（{pending.Length} 个）";
        SetFormDetails();
        try
        {
            var names = await Task.Run(() => project.ReadFngNames(pending));
            if (IsDisposed || !ReferenceEquals(_containerProject, project)) return;
            ApplyContainerFngNames(project, names);
            if (names.Count > 0)
            {
                // Preserve names cached by an earlier partial run; only add the new ones.
                var merged = ContainerFngNameCache.Load(project.Index.SourceSha256);
                foreach (var pair in names) merged[pair.Key] = pair.Value;
                ContainerFngNameCache.Save(project.Index.SourceSha256, merged);
            }
            _workspace.StatusMessage = names.Count == pending.Length
                ? $"已解析 {names.Count} 个压缩 FNG 名称"
                : $"已解析 {names.Count} / {pending.Length} 个压缩 FNG 名称（部分失败）";
        }
        catch (Exception error)
        {
            if (!IsDisposed && ReferenceEquals(_containerProject, project))
                _workspace.StatusMessage = "压缩 FNG 名称解析失败：" + error.Message;
        }
        finally
        {
            if (!IsDisposed && ReferenceEquals(_containerProject, project))
            {
                RefreshContainerFileCaptions();
                SetFormDetails();
            }
        }
    }

    private void ConfigureBinaryRuntime()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择 Binary 2.9 安装目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            BinaryRuntime.Configure(dialog.SelectedPath);
            _workspace.StatusMessage = "已配置 Binary 运行库";
            SetFormDetails();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Binary 配置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
