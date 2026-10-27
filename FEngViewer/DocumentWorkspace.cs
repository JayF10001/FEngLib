using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FEngLib.Packages;

namespace FEngViewer;

internal enum SavePromptResult
{
    Save,
    Discard,
    Cancel
}

/// <summary>UI decisions the workspace needs; implemented by <see cref="PackageView"/>.</summary>
internal interface IWorkspacePrompts
{
    Package ReadPackage(string path, bool isJson);

    /// <summary>Writes the document model; returns false after reporting its own error.</summary>
    bool WritePackage(WorkspaceDocument document, string path, bool isJson, bool offerOpenFolder);

    /// <summary>Asks for a Save As target; returns null when the user cancels.</summary>
    string ChooseSaveTarget(WorkspaceDocument document, out bool isJson);

    SavePromptResult AskToSave(WorkspaceDocument document, string reason);

    bool ConfirmDiscard(WorkspaceDocument document, string reason);

    void ShowError(string title, string message);
}

internal sealed class WorkspaceSaveContext : EventArgs
{
    public WorkspaceDocument Document { get; init; }
    public string PreviousPath { get; init; }
    public string TargetPath { get; init; }
    public bool IsJson { get; init; }
    public bool Succeeded { get; set; }
    public string Error { get; set; }
    public string Warning { get; set; }
    public object RecoveryState { get; set; }
}

/// <summary>One FNG known to the session. Being in the workspace does not mean it is parsed or open.</summary>
internal sealed class WorkspaceFile
{
    internal WorkspaceFile(string path, bool isJson)
    {
        Path = path;
        IsJson = isJson;
        Caption = WorkspaceDocument.DeriveCaptionName(path);
    }

    public string Path { get; }
    public string Caption { get; }
    public bool IsJson { get; }
    public bool IsMissing { get; internal set; }
}

/// <summary>Imports are grouped by their source (a container or a folder).</summary>
internal sealed class WorkspaceGroup
{
    internal WorkspaceGroup(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public List<WorkspaceFile> Files { get; } = new();
}

internal sealed class WorkspaceImportResult
{
    public WorkspaceGroup Group { get; internal set; }
    public List<WorkspaceFile> Added { get; } = new();
    public List<WorkspaceFile> Existing { get; } = new();
    public List<KeyValuePair<string, string>> Rejected { get; } = new();

    /// <summary>Clean documents whose model was dropped or re-read after a re-extraction.</summary>
    public int Refreshed { get; internal set; }

    /// <summary>Unsaved documents that were deliberately left untouched.</summary>
    public int KeptDirty { get; internal set; }

    public int GroupFileCount => Group?.Files.Count ?? 0;
}

/// <summary>
/// Session workspace: a lightweight index of imported FNGs plus the documents the user actually
/// opened. Only opened documents own a model, a dirty flag and a tab.
/// </summary>
internal sealed class DocumentWorkspace
{
    private readonly IWorkspacePrompts _prompts;
    private readonly List<WorkspaceDocument> _documents = new();
    private readonly List<WorkspaceGroup> _groups = new();
    private bool _closingAll;

    public DocumentWorkspace(IWorkspacePrompts prompts)
    {
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
    }

    /// <summary>Files or open documents were added, removed or reordered; the lists must be rebuilt.</summary>
    public event EventHandler Changed;

    /// <summary>Dirty/open state changed but the lists keep their identity.</summary>
    public event EventHandler StateChanged;

    /// <summary>The bound document changed; <c>null</c> means the editor must be cleared.</summary>
    public event EventHandler<WorkspaceDocument> ActiveChanged;

    /// <summary>A document was saved to disk; the file is final and its hash can be recorded.</summary>
    public event EventHandler<WorkspaceDocument> Saved;
    public event EventHandler<WorkspaceSaveContext> Saving;
    public event EventHandler<WorkspaceSaveContext> SaveFinished;

    public IReadOnlyList<WorkspaceDocument> Documents => _documents;
    public IReadOnlyList<WorkspaceGroup> Groups => _groups;
    public IEnumerable<WorkspaceFile> Files => _groups.SelectMany(g => g.Files);
    public WorkspaceDocument Active { get; private set; }
    public WorkspaceGroup CurrentGroup { get; private set; }
    public string StatusMessage { get; set; } = "就绪";

    public int FileCount => _groups.Sum(g => g.Files.Count);
    public int DirtyCount => _documents.Count(d => d.IsDirty);
    public int DocumentCount => _documents.Count;

    public int IndexOfDocument(WorkspaceDocument document) => document == null ? -1 : _documents.IndexOf(document);

    public string DisplayName => CurrentGroup?.Name ?? (_groups.Count > 0 ? _groups[^1].Name : null);

    public WorkspaceFile FindFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Files.FirstOrDefault(f => PathEquals(f.Path, path));
    }

    public WorkspaceDocument FindDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return _documents.FirstOrDefault(d => d.MatchesPath(path));
    }

    public WorkspaceGroup GroupOf(WorkspaceFile file)
    {
        if (file == null) return null;
        return _groups.FirstOrDefault(g => g.Files.Contains(file));
    }

    /// <summary>
    /// Adds usable paths to the workspace index without parsing them.
    /// <paramref name="refreshExisting"/> is used after a re-extraction: clean documents are
    /// re-read (or unloaded), unsaved documents are kept exactly as the user left them.
    /// </summary>
    public WorkspaceImportResult Import(IEnumerable<string> paths, bool isJson, string groupName, bool refreshExisting = false)
    {
        var result = new WorkspaceImportResult();
        WorkspaceGroup group = _groups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        result.Group = group;

        foreach (var rawPath in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(rawPath); }
            catch (Exception error) { result.Rejected.Add(new KeyValuePair<string, string>(rawPath, error.Message)); continue; }

            if (fullPath.EndsWith(".source.json", StringComparison.OrdinalIgnoreCase))
            {
                result.Rejected.Add(new KeyValuePair<string, string>(fullPath, "来源记录不是 FNG 文档。"));
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                result.Rejected.Add(new KeyValuePair<string, string>(fullPath, "这是目录，不是 FNG 文件。"));
                continue;
            }

            if (!File.Exists(fullPath))
            {
                result.Rejected.Add(new KeyValuePair<string, string>(fullPath, "文件不存在。"));
                continue;
            }

            var existing = FindFile(fullPath);
            if (existing != null)
            {
                result.Existing.Add(existing);
                if (refreshExisting) RefreshExisting(existing, result);
                continue;
            }

            if (group == null)
            {
                group = new WorkspaceGroup(groupName);
                _groups.Add(group);
                result.Group = group;
            }

            var file = new WorkspaceFile(fullPath, isJson);
            group.Files.Add(file);
            result.Added.Add(file);
        }

        if (group != null) CurrentGroup = group;
        StatusMessage = $"加入工作区 {result.Added.Count} 个文件（工作区共 {FileCount} 个）";
        if (result.Added.Count > 0 || result.Rejected.Count > 0) Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>Re-extraction touched the file: re-read clean documents, never unsaved ones.</summary>
    private void RefreshExisting(WorkspaceFile file, WorkspaceImportResult result)
    {
        var document = FindDocument(file.Path);
        if (document == null) return;
        if (document.IsDirty)
        {
            result.KeptDirty++;
            return;
        }

        if (ReferenceEquals(document, Active))
        {
            try
            {
                document.ReplacePackage(_prompts.ReadPackage(document.FilePath, document.IsJson));
            }
            catch (Exception error)
            {
                // Keep the model that is on screen; the next explicit Reload can report the reason.
                document.RecordLoadFailure(error);
                return;
            }

            result.Refreshed++;
            ActiveChanged?.Invoke(this, document);
            return;
        }

        if (document.InvalidateCleanModel()) result.Refreshed++;
    }

    /// <summary>Opens (or re-activates) the document for a workspace file; a broken file creates no tab.</summary>
    public WorkspaceDocument OpenFile(WorkspaceFile file, bool activate = true, bool reportError = true)
    {
        if (file == null) return null;
        var document = FindDocument(file.Path);
        if (document != null)
        {
            if (!document.IsLoaded && !TryLoad(document, reportError)) return null;
            if (!_documents.Contains(document)) return null;
            if (activate) CommitActive(document);
            return document;
        }

        var created = new WorkspaceDocument(file.Path, file.IsJson);
        if (!TryLoad(created, reportError)) return null;
        _documents.Add(created);
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (activate) CommitActive(created);
        return created;
    }

    /// <summary>
    /// Activates an already open document. The model is parsed first, so a failure leaves the
    /// previous document and the editor binding untouched.
    /// </summary>
    public bool Activate(WorkspaceDocument document, bool reportError = true)
    {
        if (document == null || !_documents.Contains(document)) return false;
        if (ReferenceEquals(document, Active) && document.IsLoaded) return true;
        if (!document.IsLoaded && !TryLoad(document, reportError)) return false;
        CommitActive(document);
        return true;
    }

    /// <summary>Opens the first file that parses; used after a batch import.</summary>
    public WorkspaceDocument OpenFirstAvailable(IEnumerable<WorkspaceFile> files, out int failed)
    {
        failed = 0;
        var errors = new List<string>();
        foreach (var file in files ?? Enumerable.Empty<WorkspaceFile>())
        {
            var document = FindDocument(file.Path);
            if (document == null)
            {
                document = new WorkspaceDocument(file.Path, file.IsJson);
                if (!TryLoad(document, reportError: false))
                {
                    errors.Add($"{file.Caption}：{document.LoadError}");
                    failed++;
                    continue;
                }

                _documents.Add(document);
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (!document.IsLoaded && !TryLoad(document, reportError: false))
            {
                errors.Add($"{file.Caption}：{document.LoadError}");
                failed++;
                continue;
            }

            CommitActive(document);
            return document;
        }

        if (errors.Count > 0) _prompts.ShowError("自动打开失败", JoinErrors(errors));
        return null;
    }

    public bool Close(WorkspaceDocument document)
    {
        if (document == null || !_documents.Contains(document)) return false;
        if (!ConfirmClose(document)) return false;

        var index = _documents.IndexOf(document);
        var wasActive = ReferenceEquals(document, Active);
        _documents.RemoveAt(index);
        if (wasActive)
        {
            Active = null;
            if (!_closingAll)
            {
                // Publish the empty state before trying the neighbour, so a broken neighbour
                // cannot leave the closed document's model on screen.
                ActiveChanged?.Invoke(this, null);
                var next = _documents.Count == 0 ? null : _documents[Math.Min(index, _documents.Count - 1)];
                if (next != null) Activate(next, reportError: true);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool CloseOthers(WorkspaceDocument keep)
    {
        foreach (var document in _documents.ToArray())
        {
            if (ReferenceEquals(document, keep)) continue;
            if (!Close(document)) return false;
        }

        return true;
    }

    public bool CloseAll()
    {
        _closingAll = true;
        try
        {
            foreach (var document in _documents.ToArray())
            {
                if (Close(document)) continue;
                // Cancelled: keep everything still open, and re-bind the editor to a real document.
                Active = null;
                var remaining = _documents.FirstOrDefault();
                if (remaining == null) ActiveChanged?.Invoke(this, null);
                else if (!Activate(remaining, reportError: false)) ActiveChanged?.Invoke(this, null);
                Changed?.Invoke(this, EventArgs.Empty);
                return false;
            }

            Active = null;
            ActiveChanged?.Invoke(this, null);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _closingAll = false;
        }
    }

    /// <summary>Saves the document at its current path, or asks for a target when <paramref name="chooseTarget"/>.</summary>
    public bool Save(WorkspaceDocument document, bool chooseTarget = false)
    {
        if (document?.Package == null)
        {
            StatusMessage = "没有可保存的文档";
            return false;
        }

        var path = document.FilePath;
        var isJson = document.IsJson;
        if (chooseTarget)
        {
            var chosen = _prompts.ChooseSaveTarget(document, out var chosenIsJson);
            if (string.IsNullOrWhiteSpace(chosen)) return false;
            path = chosen;
            isJson = chosenIsJson;
        }

        path = Path.GetFullPath(path);
        var owner = FindDocument(path);
        if (owner != null && !ReferenceEquals(owner, document))
        {
            _prompts.ShowError("保存失败",
                $"目标文件已在另一个标签页中打开：\n{path}\n\n请改用其他文件名，避免覆盖另一份文档及其未保存修改。");
            return false;
        }

        var previousPath = document.FilePath;
        var save = new WorkspaceSaveContext { Document = document, PreviousPath = previousPath, TargetPath = path, IsJson = isJson };
        Saving?.Invoke(this, save);
        if (!string.IsNullOrWhiteSpace(save.Error))
        {
            _prompts.ShowError("保存尚未开始", save.Error);
            return false;
        }
        if (!_prompts.WritePackage(document, path, isJson, offerOpenFolder: chooseTarget))
        {
            SaveFinished?.Invoke(this, save);
            return false;
        }

        document.MarkSaved(path, isJson);
        save.Succeeded = true;
        SaveFinished?.Invoke(this, save);
        Saved?.Invoke(this, document);
        StatusMessage = $"已保存 {Path.GetFileName(path)}" + (save.Warning == null ? "" : "；" + save.Warning);
        if (save.Warning != null) _prompts.ShowError("FNG 已保存，容器登记需要处理", save.Warning);
        if (!PathEquals(previousPath, path) && TrackSavedCopy(previousPath, document))
            Changed?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Reload(WorkspaceDocument document)
    {
        if (document == null) return false;
        if (document.IsDirty &&
            !_prompts.ConfirmDiscard(document, "重新读取会丢弃当前标签页的未保存修改，是否继续？"))
            return false;

        Package package;
        try
        {
            package = _prompts.ReadPackage(document.FilePath, document.IsJson) ??
                      throw new InvalidDataException("解析器没有返回包模型。");
        }
        catch (Exception error)
        {
            document.RecordLoadFailure(error);
            _prompts.ShowError("重新读取失败", $"{document.FilePath}\n\n{error.Message}\n\n已保留当前模型与未保存修改。");
            return false;
        }

        document.ReplacePackage(package);
        if (ReferenceEquals(document, Active)) ActiveChanged?.Invoke(this, document);
        StateChanged?.Invoke(this, EventArgs.Empty);
        StatusMessage = $"已重新读取 {document.CaptionName}";
        return true;
    }

    public void MarkDirty(WorkspaceDocument document)
    {
        if (document == null) return;
        document.MarkDirty();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refresh the tab order without re-reading any model.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= _documents.Count || to < 0 || to >= _documents.Count || from == to) return;
        var document = _documents[from];
        _documents.RemoveAt(from);
        _documents.Insert(to, document);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a file from the index. An open tab is a separate thing and stays open.</summary>
    public bool RemoveFile(WorkspaceFile file)
    {
        var group = GroupOf(file);
        if (group == null) return false;
        group.Files.Remove(file);
        if (group.Files.Count == 0)
        {
            _groups.Remove(group);
            if (ReferenceEquals(CurrentGroup, group)) CurrentGroup = _groups.LastOrDefault();
        }

        StatusMessage = $"已从工作区移除 {file.Caption}";
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Re-checks file presence without touching open models.</summary>
    public int RefreshPresence()
    {
        var missing = 0;
        foreach (var file in Files)
        {
            file.IsMissing = !File.Exists(file.Path);
            if (file.IsMissing) missing++;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return missing;
    }

    public void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private bool TryLoad(WorkspaceDocument document, bool reportError)
    {
        try
        {
            document.EnsureLoaded(_prompts.ReadPackage);
            return true;
        }
        catch (Exception error)
        {
            document.RecordLoadFailure(error);
            if (reportError)
                _prompts.ShowError("无法打开文档", $"{document.FilePath}\n\n{error.Message}");
            return false;
        }
    }

    private void CommitActive(WorkspaceDocument document)
    {
        var previous = Active;
        Active = document;
        try
        {
            ActiveChanged?.Invoke(this, document);
        }
        catch (Exception error)
        {
            Active = previous;
            _prompts.ShowError("切换文档失败", $"{document.FilePath}\n\n{error.Message}");
            try { ActiveChanged?.Invoke(this, previous); }
            catch (Exception) { /* the editor is already unusable; keep the original error visible */ }
            return;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool ConfirmClose(WorkspaceDocument document)
    {
        if (!document.IsDirty) return true;
        switch (_prompts.AskToSave(document, "关闭前是否保存对当前文档的修改？"))
        {
            case SavePromptResult.Cancel:
                return false;
            case SavePromptResult.Discard:
                return true;
            default:
                return Save(document);
        }
    }

    /// <summary>Save As leaves the new file on disk, so the index should know about it too.</summary>
    private bool TrackSavedCopy(string previousPath, WorkspaceDocument document)
    {
        if (FindFile(document.FilePath) != null) return false;
        var group = FindFile(previousPath) is { } previous ? GroupOf(previous) : CurrentGroup;
        if (group == null)
        {
            group = new WorkspaceGroup("另存的文档");
            _groups.Add(group);
        }

        group.Files.Add(new WorkspaceFile(document.FilePath, document.IsJson));
        return true;
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string JoinErrors(IReadOnlyCollection<string> errors)
    {
        const int limit = 6;
        var text = string.Join("\n", errors.Take(limit));
        if (errors.Count > limit)
            text += $"\n… 其余 {errors.Count - limit} 个文件无法解析，已跳过。";
        return text;
    }
}
