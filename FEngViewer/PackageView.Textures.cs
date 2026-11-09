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
    private TextureResolver _textureResolver;
    private TextureSourceSettings _textureSourceSettings = new();
    private ContainerProject _textureProject;

    private void InitializeTextureResolver()
    {
        _textureSourceSettings = TextureSourceStore.Load();
        BuildTextureResolver();
    }

    private void BuildTextureResolver()
    {
        var sources = new List<ITextureSource>();
        var projectPath = _textureProject?.BaselinePath;
        if (projectPath != null)
            sources.Add(new ArchiveTextureSource(new[] { projectPath }));
        var configured = _textureSourceSettings.Archives.Where(path =>
            !string.Equals(path, projectPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (configured.Length > 0)
            sources.Add(new ArchiveTextureSource(configured));
        _textureResolver = new TextureResolver(sources);
        viewOutput.TextureProvider = _textureResolver;
        if (_inspector != null) _inspector.TextureResolver = _textureResolver;
    }

    /// <summary>
    /// Keeps loose PNG overrides and uses the document's own container ahead of configured archives.
    /// </summary>
    private void PrepareTextureSources(WorkspaceDocument document)
    {
        if (_textureResolver == null || !ReferenceEquals(_textureProject, document?.Project))
        {
            _textureProject = document?.Project;
            BuildTextureResolver();
        }
        _textureResolver.SetTextureDirectory(document?.TextureDirectory ?? "");
        var overrideSource = document?.Project != null && document.Block != null
            ? new ProjectTextureOverrideSource(document.Block.Offset, _textureOverrides.Values)
            : null;
        _textureResolver.SetProjectOverrideSource(overrideSource?.Count > 0 ? overrideSource : null);
        var previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            _textureResolver.Preload();
            var errors = _textureResolver.Sources.OfType<ArchiveTextureSource>().SelectMany(archive => archive.Errors).Take(3).ToArray();
            if (errors.Length > 0)
                _workspace.StatusMessage = "贴图来源错误：" + string.Join("；", errors);
        }
        finally
        {
            Cursor = previousCursor;
        }
    }

    private void ReleaseProjectTextureSource()
    {
        if (_textureProject == null || ReferenceEquals(_textureProject, _containerProject)) return;
        _textureProject = null;
        BuildTextureResolver();
    }

    private void ConfigureTextureSources()
    {
        using var dialog = new TextureSourcesDialog(_textureSourceSettings.Archives);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _textureSourceSettings.Archives = TextureSourceStore.Normalize(dialog.Result);
        TextureSourceStore.Save(_textureSourceSettings);

        Cursor = Cursors.WaitCursor;
        try
        {
            BuildTextureResolver();
            PrepareTextureSources(_boundDocument ?? _previewDocument);
            RefreshTextureSources();
            _workspace.StatusMessage = $"贴图来源：{_textureSourceSettings.Archives.Count} 个档案";
            SetFormDetails();
        }
        catch (Exception error)
        {
            _workspace.StatusMessage = "贴图来源加载失败：" + error.Message;
            SetFormDetails();
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void RefreshTextureSources()
    {
        if (_boundDocument != null)
        {
            viewOutput.Init(_boundDocument.TextureDirectory);
            Render();
            _inspector?.RefreshAll();
        }
        else if (_previewDocument != null)
        {
            RestoreWorkspacePreview();
        }
        else
        {
            viewOutput.Init("");
            Render();
        }
    }

    private ContainerTextureOverride CreateTextureOverride(ResourceRequest request, byte[] data,
        string sourceName, TextureInputKind inputKind, TextureLookupResult underlying)
    {
        var document = _boundDocument ?? ActiveDocument;
        if (request == null || !IsContainerEditDocument() || document?.Block == null)
            throw new InvalidOperationException("当前不是容器 FNG 编辑标签。");
        var entry = underlying?.Entry;
        if (inputKind == TextureInputKind.Dds)
            return ContainerTextureOverride.CreateDds(document.Block.Offset, request.ID,
                request.Name ?? "", sourceName, data, entry?.ArchivePath, entry?.TpkName,
                entry?.TpkOrdinal ?? -1, entry?.TextureOrdinal ?? -1, entry?.Name, entry?.BinKey ?? 0,
                entry?.Compression, entry?.Width ?? 0, entry?.Height ?? 0, entry?.MipCount ?? -1);
        return ContainerTextureOverride.Create(document.Block.Offset, request.ID, request.Name ?? "",
            sourceName, data, entry?.ArchivePath, entry?.TpkName, entry?.TpkOrdinal ?? -1,
            entry?.TextureOrdinal ?? -1, entry?.Name, entry?.BinKey ?? 0, entry?.Compression);
    }

    private TextureLookupResult ResolveUnderlying(ResourceRequest request)
    {
        if (_textureResolver == null) return null;
        var sources = _textureResolver.Sources
            .Where(source => source is not ProjectTextureOverrideSource)
            .ToArray();
        return new TextureResolver(sources).Resolve(request);
    }

    private string WriteTextureCopy(ContainerTextureOverride replacement, string targetPath)
    {
        if (_containerProject == null || replacement == null || !replacement.HasTarget)
            return "没有可导出的 TPK 目标。";
        var sourcePath = replacement.TargetArchivePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return "目标档案不存在，无法导出贴图档案副本。";
        if (ContainerSession.PathsEqual(sourcePath, targetPath))
            return "输出档案不能覆盖目标档案。";
        var compression = replacement.TargetCompression?.ToUpperInvariant();
        if (compression is not ("TEXCOMP_DXTC1" or "TEXCOMP_DXTC3" or "TEXCOMP_DXTC5"))
            return $"暂不支持导出格式：{replacement.TargetCompression}。";
        try
        {
            UseWaitCursor = true;
            if (ContainerSession.PathsEqual(sourcePath, _containerProject.BaselinePath))
            {
                using var source = File.OpenRead(sourcePath);
                using var sha = SHA256.Create();
                var sourceHash = Convert.ToHexString(sha.ComputeHash(source));
                if (!string.Equals(sourceHash, _containerProject.Index.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    return "来源容器已改变，请重新打开容器工程。";
            }
            var report = ContainerTextureWriteBack.Write(sourcePath, targetPath,
                new[]
                {
                    new ContainerTextureReplacement
                    {
                        TpkName = replacement.TargetTpkName,
                        TpkOrdinal = replacement.TargetTpkOrdinal,
                        TextureOrdinal = replacement.TargetTextureOrdinal,
                        TextureName = replacement.TargetTextureName,
                        BinKey = replacement.TargetBinKey,
                        Compression = replacement.TargetCompression,
                        InputKind = replacement.InputKind,
                        Data = replacement.Data,
                        Format = replacement.Format,
                        Width = replacement.Width,
                        Height = replacement.Height,
                        MipCount = replacement.MipCount,
                    }
                });
            _workspace.StatusMessage = $"已导出贴图档案副本 · {Path.GetFileName(report.OutputPath)}";
            SetFormDetails();
            return null;
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private string ApplyTextureOverride(ContainerTextureOverride replacement)
    {
        if (replacement == null) return "没有可应用的贴图覆盖。";
        if (!IsContainerEditDocument() || _boundDocument?.Block == null) return "当前不是容器 FNG 编辑标签。";
        _textureOverrides[(replacement.FngOffset, replacement.RequestId)] = replacement;
        _textureOverridesDirty = true;
        RefreshTextureOverrideSources();
        _workspace.StatusMessage = "已应用工程贴图覆盖（未保存工程）";
        RefreshWorkspaceState();
        SetFormDetails();
        return null;
    }

    private string RemoveTextureOverride(ResourceRequest request)
    {
        if (request == null) return "没有可移除的贴图覆盖。";
        if (!IsContainerEditDocument() || _boundDocument?.Block == null) return "当前不是容器 FNG 编辑标签。";
        if (!_textureOverrides.Remove((_boundDocument.Block.Offset, request.ID))) return null;
        _textureOverridesDirty = true;
        RefreshTextureOverrideSources();
        _workspace.StatusMessage = "已移除工程贴图覆盖（未保存工程）";
        RefreshWorkspaceState();
        SetFormDetails();
        return null;
    }

    private void RefreshTextureOverrideSources()
    {
        if (_boundDocument == null) return;
        PrepareTextureSources(_boundDocument);
        viewOutput.Init(_boundDocument.TextureDirectory);
        Render();
        _inspector?.RefreshAll();
    }

    /// <summary>Container FNG edit tabs only; independent FNGs keep their existing preview only.</summary>
    private bool IsContainerEditDocument()
    {
        var document = _boundDocument ?? ActiveDocument;
        return _containerProject != null && document?.Project != null &&
               ReferenceEquals(document.Project, _containerProject);
    }

    private void ShowTextureSource(FEngLib.Objects.IObject<FEngLib.Objects.ObjectData> selected,
        FEngLib.Packages.ResourceRequest request)
    {
        if (!IsContainerEditDocument() || _textureResolver == null || request == null) return;
        try
        {
            var resolution = _textureResolver.Resolve(request);
            ArchiveTextureSource archiveSource = null;
            if (resolution.Entry != null)
            {
                foreach (var source in _textureResolver.Sources.OfType<ArchiveTextureSource>())
                {
                    if (source.GetCatalog(resolution.Entry.ArchivePath) == null) continue;
                    archiveSource = source;
                    break;
                }
            }

            var objectName = selected?.Name;
            if (string.IsNullOrWhiteSpace(objectName)) objectName = $"0x{selected?.Guid:X8}";
            using var dialog = new TextureSourceDialog(resolution, request, archiveSource,
                _containerProject.BaselinePath, _boundDocument?.CaptionName ?? "", objectName);
            dialog.ShowDialog(this);
            _workspace.StatusMessage = $"贴图来源：{resolution.Status}（只读浏览，未修改工程）";
            SetFormDetails();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法查看贴图来源", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowTextureReplace(FEngLib.Objects.IObject<FEngLib.Objects.ObjectData> selected,
        FEngLib.Packages.ResourceRequest request)
    {
        if (!IsContainerEditDocument() || _textureResolver == null || request == null ||
            _boundDocument?.Block == null) return;
        try
        {
            var current = _textureResolver.Resolve(request);
            var underlying = ResolveUnderlying(request);
            _textureOverrides.TryGetValue((_boundDocument.Block.Offset, request.ID), out var existing);
            var objectName = selected?.Name;
            if (string.IsNullOrWhiteSpace(objectName)) objectName = $"0x{selected?.Guid:X8}";
            using var dialog = new TextureReplaceDialog(objectName, _boundDocument.CaptionName, request,
                current, underlying, existing, _containerProject.BaselinePath, WriteTextureCopy,
                () => _textureResolver.Resolve(request),
                (data, sourceName, kind) => CreateTextureOverride(request, data, sourceName, kind, underlying),
                ApplyTextureOverride,
                () => RemoveTextureOverride(request));
            dialog.ShowDialog(this);
            _inspector?.RefreshAll();
            Render();
            SetFormDetails();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法替换贴图", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
