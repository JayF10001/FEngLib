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
            using var dialog = new SaveFileDialog
            {
                Title = "保存容器工程",
                Filter = "FEng 工程 (*.fengproj)|*.fengproj",
                DefaultExt = "fengproj",
                FileName = Path.GetFileNameWithoutExtension(target ?? _containerProject.Index.SourcePath)
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            target = dialog.FileName;
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
        if (_savedFngEdits.Count == 0)
        {
            MessageBox.Show(this, "工程中没有已保存的 FNG 修改。", "生成容器副本", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var source = _containerProject.BaselinePath;
        var currentTextureReplacements = new List<ContainerTextureReplacement>();
        var externalTextureSources = new List<string>();
        var untargetedTextures = new List<string>();
        foreach (var texture in _textureOverrides.Values)
        {
            if (!texture.HasTarget)
            {
                untargetedTextures.Add(texture.RequestName ?? texture.RequestId.ToString("X8"));
                continue;
            }
            if (!ContainerSession.PathsEqual(texture.TargetArchivePath, source))
            {
                externalTextureSources.Add($"{texture.RequestName} → {texture.TargetArchivePath}");
                continue;
            }
            var compression = texture.TargetCompression?.ToUpperInvariant();
            if (compression is not ("TEXCOMP_DXTC1" or "TEXCOMP_DXTC3" or "TEXCOMP_DXTC5"))
            {
                MessageBox.Show(this, $"贴图 {texture.RequestName} 的格式 {texture.TargetCompression} 暂不支持写回。",
                    "贴图格式不支持", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            currentTextureReplacements.Add(new ContainerTextureReplacement
            {
                TpkName = texture.TargetTpkName,
                TpkOrdinal = texture.TargetTpkOrdinal,
                TextureOrdinal = texture.TargetTextureOrdinal,
                TextureName = texture.TargetTextureName,
                BinKey = texture.TargetBinKey,
                Compression = texture.TargetCompression,
                PngData = texture.PngData,
            });
        }
        if (externalTextureSources.Count > 0)
            MessageBox.Show(this,
                "以下贴图来自外部档案，不会包含在本次容器副本中：\n\n" +
                string.Join("\n", externalTextureSources) +
                "\n\n请用替换对话框的「写入档案副本…」单独生成。",
                "外部贴图未包含", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (untargetedTextures.Count > 0)
            MessageBox.Show(this,
                "以下贴图覆盖缺少目标 TPK 身份，无法写回：\n\n" +
                string.Join("\n", untargetedTextures) +
                "\n\n请在对应对象上重新选择 PNG 并应用覆盖。",
                "贴图覆盖缺少目标", MessageBoxButtons.OK, MessageBoxIcon.Warning);

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

        string ledger = null;
        try
        {
            UseWaitCursor = true;
            ContainerWriteBackReport report;
            using (var operation = ContainerProjectWriteBack.Prepare(_projectFilePath, dialog.FileName))
            {
                UseWaitCursor = false;
                var plan = operation.Plan;
                if (!ContainerSession.PathsEqual(plan.OriginalPath, source) ||
                    plan.WorkingCopySha256 != _containerProject.Index.SourceSha256)
                    throw new InvalidDataException("保存的工程来源与当前窗口不一致，请重新打开工程后再试。");
                using var preview = new WriteBackPreviewDialog(plan);
                if (preview.ShowDialog(this) != DialogResult.OK) return;
                ledger = operation.LedgerDirectory;
                UseWaitCursor = true;
                report = operation.Execute(message => _workspace.StatusMessage = message);
                UseWaitCursor = false;
            }
            ContainerTextureWriteBackReport textureReport = null;
            string textureFailure = null;
            if (currentTextureReplacements.Count > 0)
            {
                var texturedPath = dialog.FileName + ".texture-tmp";
                try
                {
                    UseWaitCursor = true;
                    textureReport = ContainerTextureWriteBack.Write(dialog.FileName, texturedPath,
                        currentTextureReplacements);
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
            var outputHash = textureReport?.OutputSha256 ?? report.Sha256After;
            var textureLine = currentTextureReplacements.Count == 0
                ? ""
                : textureReport != null
                    ? $"\n贴图替换 {textureReport.ReplacedCount} 个（已验证）。"
                    : $"\n贴图写回失败，当前副本仅包含 FNG 修改：\n{textureFailure}";
            _workspace.StatusMessage = textureFailure == null
                ? $"已写回 · {Path.GetFileName(report.WorkingCopyPath)}"
                : "FNG 已写回 · 贴图写回失败";
            MessageBox.Show(this,
                $"副本已生成并通过重读验证。\n\n{report.WorkingCopyPath}\n\n" +
                $"替换 {report.Items.Count} 个 FNG，验证 {report.VerifiedBlockCount} 个容器块。{textureLine}\n\n" +
                $"副本 SHA256\n{outputHash}\n\n写回账本及备份\n{ledger}" +
                (report.Warnings.Count == 0 ? "" : "\n\n" + string.Join("\n", report.Warnings)),
                textureFailure == null ? "容器副本已生成" : "容器副本已生成（贴图失败）",
                MessageBoxButtons.OK,
                textureFailure != null || report.Warnings.Count > 0
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
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
            _workspace.StatusMessage = "写回失败";
            MessageBox.Show(this,
                (published ? "副本已发布，发布后验证或登记失败。\n\n" : "") + error.Message +
                (ledger == null ? "" : $"\n\n写回账本\n{ledger}"),
                "容器写回失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            SetFormDetails();
        }
    }
}
