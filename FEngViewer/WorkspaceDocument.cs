using System;
using System.IO;
using FEngLib.Packages;

namespace FEngViewer;

/// <summary>
/// One opened FNG document. It owns its model, unsaved state and editor state, so switching
/// tabs never shares or reloads another document's data.
/// </summary>
internal sealed class WorkspaceDocument
{
    public WorkspaceDocument(string path, bool isJson)
    {
        SourcePath = System.IO.Path.GetFullPath(path);
        FilePath = SourcePath;
        IsJson = isJson;
        CaptionName = DeriveCaptionName(FilePath);
    }

    /// <summary>Where the document came from; never changes, so preview textures stay where they were.</summary>
    public string SourcePath { get; }

    /// <summary>Current save target; Save As moves it. It is the identity used for tab de-duplication.</summary>
    public string FilePath { get; private set; }

    public bool IsJson { get; private set; }

    /// <summary>Loaded model, or <c>null</c> while the document is still lazy.</summary>
    public Package Package { get; private set; }

    /// <summary>Incremented whenever the model instance is replaced, so cached render trees can be dropped.</summary>
    public int ModelVersion { get; private set; }

    public bool IsDirty { get; private set; }

    public bool IsLoaded => Package != null;

    /// <summary>Last load failure for this document; cleared by a successful load.</summary>
    public string LoadError { get; private set; }

    /// <summary>File name shown on the tab; the FNG package name is preferred once the model is loaded.</summary>
    public string CaptionName { get; private set; }

    /// <summary>Tab caption: the file name plus the unsaved marker required by the design.</summary>
    public string Caption => IsDirty ? CaptionName + " *" : CaptionName;

    public string SelectedTreeKey { get; set; }

    public string SearchText { get; set; } = "";

    public int SearchIndex { get; set; } = -1;

    public string ToolTip
    {
        get
        {
            var text = FilePath + (IsDirty ? "（未保存）" : "");
            if (!string.Equals(SourcePath, FilePath, StringComparison.OrdinalIgnoreCase))
                text += "\n来源: " + SourcePath;
            if (!string.IsNullOrEmpty(LoadError))
                text += "\n读取失败: " + LoadError;
            return text;
        }
    }

    /// <summary>
    /// Preview textures belong to the imported location. Saving elsewhere must not make the
    /// preview silently point at an empty folder; the current folder is only a fallback.
    /// </summary>
    public string TextureDirectory
    {
        get
        {
            var fromSource = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SourcePath) ?? "", "textures");
            if (Directory.Exists(fromSource)) return fromSource;
            var fromCurrent = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(FilePath) ?? "", "textures");
            return Directory.Exists(fromCurrent) ? fromCurrent : fromSource;
        }
    }

    public bool MatchesPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return string.Equals(FilePath, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Loads the model once; a failure keeps the document lazy and records the reason.</summary>
    public Package EnsureLoaded(Func<string, bool, Package> loader)
    {
        if (Package != null) return Package;
        if (loader == null) throw new ArgumentNullException(nameof(loader));
        try
        {
            var package = loader(FilePath, IsJson) ?? throw new InvalidDataException("解析器没有返回包模型。");
            Package = package;
            ModelVersion++;
            LoadError = null;
            ApplyPackageName(package);
            return package;
        }
        catch (Exception error)
        {
            LoadError = error.Message;
            throw;
        }
    }

    /// <summary>Swaps in a freshly parsed model, e.g. after Reload; the dirty flag is cleared by design.</summary>
    public void ReplacePackage(Package package)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        ModelVersion++;
        IsDirty = false;
        LoadError = null;
        ApplyPackageName(package);
    }

    public void MarkDirty() => IsDirty = true;

    public void MarkSaved(string path, bool isJson)
    {
        FilePath = System.IO.Path.GetFullPath(path);
        IsJson = isJson;
        IsDirty = false;
        CaptionName = DeriveCaptionName(FilePath);
    }

    /// <summary>Drops the model of a clean document so the next activation reads the new file.</summary>
    public bool InvalidateCleanModel()
    {
        if (IsDirty) return false;
        Package = null;
        LoadError = null;
        ModelVersion++;
        return true;
    }

    public void RecordLoadFailure(Exception error) => LoadError = error?.Message ?? "未知错误";

    private void ApplyPackageName(Package package)
    {
        if (!string.IsNullOrWhiteSpace(package?.Name))
            CaptionName = DeriveCaptionName(package.Name);
    }

    /// <summary>
    /// Extracted container blocks are written as <c>026502D0_RapSheetLogin.fng</c>; the tab should
    /// read like the FNG it contains, so the offset prefix is not part of the caption.
    /// </summary>
    public static string DeriveCaptionName(string path)
    {
        var name = System.IO.Path.GetFileName(path ?? "");
        if (string.IsNullOrEmpty(name)) return "(untitled)";
        return HasOffsetPrefix(name) ? name[9..] : name;
    }

    private static bool HasOffsetPrefix(string name)
    {
        if (name.Length < 10 || name[8] != '_') return false;
        for (var i = 0; i < 8; i++)
        {
            var c = name[i];
            if (!char.IsDigit(c) && (c < 'A' || c > 'F') && (c < 'a' || c > 'f')) return false;
        }
        return true;
    }

    /// <summary>Used by the workspace index and by tests to compare the displayed name.</summary>
    public override string ToString() => Caption;
}
