using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Forms;
using FEngLib.Assets;
using FEngLib.Packages;

namespace FEngViewer;

public partial class PackageView
{
    private string _projectFilePath;
    private Dictionary<long, ContainerFngContent> _savedFngEdits = new();

    private static bool IsProjectPath(string path) =>
        string.Equals(Path.GetExtension(path), ContainerProjectFile.Extension, StringComparison.OrdinalIgnoreCase);

    private static string ProjectBaseName(string sourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(name)) name = "project";
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name;
    }

    /// <summary>
    /// Keeps the requested project name unless that file already exists. A collision gets a
    /// numeric suffix; Ctrl+S still overwrites the currently open project.
    /// </summary>
    private static string NextAvailableProjectPath(string path, string currentProjectPath)
    {
        if (!File.Exists(path) || ContainerSession.PathsEqual(path, currentProjectPath)) return path;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) return path;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension)) extension = ContainerProjectFile.Extension;
        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}_{index}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");
    }

    private static Package ParseProjectFng(ContainerFngContent original,
        IReadOnlyDictionary<long, ContainerFngContent> edits)
    {
        var content = edits.GetValueOrDefault(original.Source.Block.Offset) ?? original;
        using var stream = content.OpenRead();
        return AppService.Instance.ReadFng(stream, content.Source.PackageName);
    }

    private bool SaveContainerProject(bool chooseTarget)
    {
        if (_containerProject == null || !ValidateChildren()) return false;
        var target = _projectFilePath;
        if (target == null || chooseTarget)
        {
            var baseName = ProjectBaseName(_containerProject.Index.SourcePath);
            using var dialog = new SaveFileDialog
            {
                Title = "保存容器工程",
                Filter = "FEng 工程 (*.fengproj)|*.fengproj",
                DefaultExt = "fengproj",
                FileName = $"{baseName}_{DateTime.Now:yyyyMMdd_HHmm}",
                OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            target = NextAvailableProjectPath(dialog.FileName, _projectFilePath);
        }

        var documents = _workspace.Documents.Where(document => ReferenceEquals(document.Project, _containerProject)).ToArray();
        var modified = documents.Where(document => document.IsDirty).ToArray();
        var edits = new Dictionary<long, ContainerFngContent>(_savedFngEdits);
        var previousBaseline = _containerProject.BaselinePath;
        var provenance = _containerProject.Index.SourcePath;
        string baseline;
        try
        {
            UseWaitCursor = true;
            foreach (var document in modified)
            {
                var bytes = EncodePackageChunk(document.Package);
                var original = document.ContainerSource;
                if (Convert.ToHexString(SHA256.HashData(bytes)) == original.OriginalSha256)
                {
                    edits.Remove(document.Block.Offset);
                    continue;
                }
                var edit = ContainerProjectFile.CreateEdit(original, bytes);
                using var stream = edit.OpenRead();
                AppService.Instance.ReadFng(stream, original.PackageName);
                edits[document.Block.Offset] = edit;
            }
            baseline = ContainerProjectFile.Save(target, new ContainerProjectSnapshot(_containerProject, edits.Values.ToArray(),
                _textureOverrides.Values.ToArray(), documents.Select(document => document.Block.Offset).ToArray(),
                _workspace.Active?.Block?.Offset));
            RemapTextureOverrides(provenance, previousBaseline, baseline);
            RebindProjectBaseline(baseline);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "工程保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally { UseWaitCursor = false; }

        _projectFilePath = Path.GetFullPath(target);
        _savedFngEdits.Clear();
        foreach (var edit in edits) _savedFngEdits.Add(edit.Key, edit.Value);
        foreach (var document in modified) document.MarkEditsSaved();
        _textureOverridesDirty = false;
        RememberRecentContainer(_containerProject.Index.SourcePath);
        RememberRecentProject(_projectFilePath);
        var baselineCreated = !ContainerSession.PathsEqual(previousBaseline, baseline) &&
                              !ContainerSession.PathsEqual(provenance, baseline);
        _workspace.StatusMessage = baselineCreated
            ? $"已创建工程基线 · {Path.GetFileName(_projectFilePath)}"
            : $"{Path.GetFileName(_projectFilePath)} · {SavedProjectWriteBackState()}";
        _workspace.NotifyStateChanged();
        return true;
    }

    private void RemapTextureOverrides(string provenance, string previousBaseline, string baseline)
    {
        if (string.IsNullOrWhiteSpace(baseline)) return;
        foreach (var key in _textureOverrides.Keys.ToArray())
        {
            var texture = _textureOverrides[key];
            if (texture == null || !texture.HasTarget) continue;
            if (ContainerSession.PathsEqual(texture.TargetArchivePath, provenance) ||
                !string.IsNullOrWhiteSpace(previousBaseline) &&
                ContainerSession.PathsEqual(texture.TargetArchivePath, previousBaseline))
                _textureOverrides[key] = texture.WithTargetArchive(baseline);
        }
    }

    private void RebindProjectBaseline(string baseline)
    {
        if (_containerProject == null || string.IsNullOrWhiteSpace(baseline)) return;
        var changed = !ContainerSession.PathsEqual(_containerProject.BaselinePath, baseline);
        _containerProject.RebindBaseline(baseline);
        if (!changed || !ReferenceEquals(_textureProject, _containerProject)) return;
        BuildTextureResolver();
        if (_boundDocument != null) PrepareTextureSources(_boundDocument);
    }

    private bool CloseWorkspaceTabs()
    {
        if (_containerProject == null) return _workspace.CloseAll();
        if (!ValidateChildren()) return false;
        if (_workspace.DirtyCount > 0 || _textureOverridesDirty)
        {
            var answer = MessageBox.Show(this, "关闭前是否保存容器工程？", "工程有未保存的修改",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel || answer == DialogResult.Yes && !SaveContainerProject(chooseTarget: false))
                return false;
        }
        return _workspace.CloseAll(discardChanges: true);
    }

    private string SavedProjectWriteBackState()
    {
        try
        {
            return ContainerProjectWriteBack.SavedState(_projectFilePath, _containerProject.Index,
                _savedFngEdits.Values, _containerProject.BaselinePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return "写回记录无法读取";
        }
    }

    private void GenerateContainerCopy()
    {
        if (_containerProject == null || !ValidateChildren()) return;
        if (_projectFilePath == null || _workspace.DirtyCount > 0 || _textureOverridesDirty)
        {
            if (MessageBox.Show(this, "生成副本使用已保存的工程。是否先保存当前工程？", "生成容器副本",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes ||
                !SaveContainerProject(chooseTarget: false)) return;
        }
        if (_savedFngEdits.Count == 0 && _textureOverrides.Count == 0)
        {
            MessageBox.Show(this, "工程中没有可导出的 FNG 修改或贴图覆盖。", "生成容器副本",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var source = _containerProject.BaselinePath;

        var extension = Path.GetExtension(source);
        var sourceName = Path.GetFileName(_containerProject.Index.SourcePath);
        if (string.IsNullOrWhiteSpace(sourceName)) sourceName = Path.GetFileName(source);
        var projectDirectory = Path.GetDirectoryName(_projectFilePath) ?? "";
        var initialDirectory = projectDirectory;
        var proposedPath = Path.Combine(projectDirectory, sourceName);
        if (ContainerSession.PathsEqual(proposedPath, _containerProject.Index.SourcePath) ||
            ContainerSession.PathsEqual(proposedPath, source) ||
            File.Exists(proposedPath))
        {
            // The default name preserves the original container name. If that would point at
            // the source/baseline or an existing file, stage exports in a sibling folder rather
            // than adding a suffix to the name.
            initialDirectory = Path.Combine(projectDirectory, "Exports");
            if (File.Exists(Path.Combine(initialDirectory, sourceName)))
                initialDirectory = Path.Combine(initialDirectory,
                    DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try { Directory.CreateDirectory(initialDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                initialDirectory = projectDirectory;
            }
        }
        using var dialog = new SaveFileDialog
        {
            Title = "生成新的容器副本",
            Filter = $"容器副本 (*{extension})|*{extension}",
            DefaultExt = extension.TrimStart('.'),
            FileName = sourceName,
            InitialDirectory = initialDirectory,
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (File.Exists(dialog.FileName) || Directory.Exists(dialog.FileName))
        {
            MessageBox.Show(this, "输出位置已存在；导出不会覆盖现有文件，请选择新的文件名。", "生成容器副本",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (ContainerSession.PathsEqual(dialog.FileName, source) ||
            ContainerSession.PathsEqual(dialog.FileName, _containerProject.Index.SourcePath))
        {
            MessageBox.Show(this, "输出副本不能覆盖基线容器或工程记录的来源容器。", "生成容器副本",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!string.Equals(Path.GetExtension(dialog.FileName), Path.GetExtension(source),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"输出副本必须保留容器扩展名 {Path.GetExtension(source)}。", "生成容器副本",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ContainerProjectWriteBack operation = null;
        string ledger = null;
        try
        {
            UseWaitCursor = true;
            ContainerWritePlan fngPlan = null;
            if (_savedFngEdits.Count > 0)
            {
                operation = ContainerProjectWriteBack.Prepare(_projectFilePath, dialog.FileName);
                fngPlan = operation.Plan;
                if (!ContainerSession.PathsEqual(fngPlan.OriginalPath, source) ||
                    fngPlan.WorkingCopySha256 != _containerProject.Index.SourceSha256)
                    throw new InvalidDataException("保存的工程来源与当前窗口不一致，请重新打开工程后再试。");
            }

            bool binaryReady;
            try
            {
                binaryReady = BinaryRuntime.DirectoryPath != null &&
                              !string.IsNullOrWhiteSpace(BinaryRuntime.WorkerHostPath);
            }
            catch (Exception)
            {
                binaryReady = false;
            }
            var exportPlan = ContainerExportPlan.Build(source, _containerProject.Index.SourceLength,
                _containerProject.Index.SourceSha256, dialog.FileName, fngPlan,
                _textureOverrides.Values, binaryReady);
            exportPlan.LedgerDirectory = operation?.LedgerDirectory;
            UseWaitCursor = false;
            using (var preview = new ContainerExportPreviewDialog(exportPlan))
            {
                if (preview.ShowDialog(this) != DialogResult.OK) return;
            }

            ContainerWriteBackReport fngReport = null;
            if (exportPlan.HasFng)
            {
                ledger = operation.LedgerDirectory;
                UseWaitCursor = true;
                fngReport = operation.Execute(message => _workspace.StatusMessage = message);
                UseWaitCursor = false;
            }

            ContainerTextureWriteBackReport textureReport = null;
            string textureFailure = null;
            var replacements = exportPlan.Textures.Select(ToTextureReplacement).ToArray();
            if (replacements.Length > 0)
            {
                if (fngReport == null)
                {
                    UseWaitCursor = true;
                    try
                    {
                        textureReport = ContainerTextureWriteBack.Write(source, dialog.FileName, replacements);
                    }
                    catch (Exception textureError)
                    {
                        textureFailure = textureError.Message;
                    }
                    finally { UseWaitCursor = false; }
                }
                else
                {
                    var texturedPath = dialog.FileName + ".texture-tmp";
                    try
                    {
                        UseWaitCursor = true;
                        textureReport = ContainerTextureWriteBack.Write(dialog.FileName, texturedPath, replacements);
                        File.Move(texturedPath, dialog.FileName, true);
                    }
                    catch (Exception textureError)
                    {
                        textureFailure = textureError.Message;
                    }
                    finally
                    {
                        UseWaitCursor = false;
                        if (File.Exists(texturedPath))
                        {
                            try { File.Delete(texturedPath); }
                            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
                        }
                    }
                }
            }

            if (fngReport == null && textureReport == null)
            {
                _workspace.StatusMessage = "贴图写回失败，未生成容器副本";
                MessageBox.Show(this,
                    "贴图写回失败，未生成容器副本。\n\n" + (textureFailure ?? "没有可导出的内容。"),
                    "容器副本导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var outputHash = textureReport?.OutputSha256 ?? fngReport?.Sha256After;
            var fngLine = fngReport == null
                ? "无 FNG 修改。"
                : $"替换 {fngReport.Items.Count} 个 FNG，验证 {fngReport.VerifiedBlockCount} 个容器块。";
            var textureLine = replacements.Length == 0
                ? ""
                : textureReport != null
                    ? $"\n贴图替换 {textureReport.ReplacedCount} 个（已验证）。"
                    : $"\n贴图写回失败，当前副本仅包含 FNG 修改：\n{textureFailure}";
            _workspace.StatusMessage = textureFailure == null
                ? $"已导出 · {Path.GetFileName(exportPlan.OutputPath)}"
                : fngReport != null ? "FNG 已写入 · 贴图写回失败" : "贴图写回失败";
            var warnings = (fngReport?.Warnings ?? new List<string>())
                .Concat(textureReport?.Warnings ?? Array.Empty<string>()).ToArray();
            var summary = textureFailure == null
                ? "副本已生成并通过重读验证。"
                : "FNG 副本已生成并通过重读验证；贴图部分失败。";
            MessageBox.Show(this,
                $"{summary}\n\n{exportPlan.OutputPath}\n\n" +
                fngLine + textureLine + "\n\n" +
                $"当前副本 SHA256\n{outputHash}\n\n" +
                (ledger == null ? "" : $"写回账本及备份\n{ledger}\n\n") +
                (warnings.Length == 0 ? "" : string.Join("\n", warnings)),
                textureFailure == null ? "容器副本已导出" : "容器副本已导出（贴图失败）",
                MessageBoxButtons.OK,
                textureFailure != null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (ProjectBaselineMissingException)
        {
            UseWaitCursor = false;
            _workspace.StatusMessage = "工程基线缺失";
            MessageBox.Show(this,
                "工程基线缺失，无法生成容器副本。\n\n" +
                "请关闭并重新打开该工程，在“恢复工程资源”对话框中选择基线容器后再试。\n\n" +
                $"工程位置：{_projectFilePath}",
                "工程基线缺失", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception error)
        {
            UseWaitCursor = false;
            var published = error is ContainerWriteBackException writeBack && writeBack.Report.Published;
            _workspace.StatusMessage = "导出失败";
            MessageBox.Show(this,
                (published ? "副本已发布，发布后验证或登记失败。\n\n" : "") + error.Message +
                (ledger == null ? "" : $"\n\n写回账本\n{ledger}"),
                "容器副本导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            operation?.Dispose();
            UseWaitCursor = false;
            SetFormDetails();
        }
    }

    private static ContainerTextureReplacement ToTextureReplacement(ContainerTextureOverride texture) => new()
    {
        TpkName = texture.TargetTpkName,
        TpkOrdinal = texture.TargetTpkOrdinal,
        TextureOrdinal = texture.TargetTextureOrdinal,
        TextureName = texture.TargetTextureName,
        BinKey = texture.TargetBinKey,
        Compression = texture.TargetCompression,
        PngData = texture.PngData,
    };
}
