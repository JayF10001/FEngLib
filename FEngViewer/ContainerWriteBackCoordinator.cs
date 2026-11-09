using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FEngLib.Assets;

namespace FEngViewer;

/// <summary>
/// Viewer-side access to the container write-back ledger: registers sessions after extraction,
/// records saved files, and answers the pending-write-back queries shown in the file list.
/// All decisions and file operations stay in FEngLib; this only caches sessions per directory
/// and forwards change notifications.
/// </summary>
internal sealed class ContainerWriteBackCoordinator
{
    private readonly Dictionary<string, ContainerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pending write-back state changed somewhere; the file list must refresh its markers.</summary>
    public event EventHandler PendingChanged;
    public Func<string, bool> ConfirmVersionSwitch { get; set; }

    /// <summary>Registers or merges the session of an extraction directory; never throws.</summary>
    public string RegisterAfterExtraction(string containerPath, string extractionDirectory)
    {
        try
        {
            var registration = ContainerSession.Register(containerPath, extractionDirectory);
            _sessions[Path.GetFullPath(extractionDirectory)] = registration.Session;
            PendingChanged?.Invoke(this, EventArgs.Empty);
            var ignored = registration.IgnoredManifests.Count == 0
                ? ""
                : $"，忽略 {registration.IgnoredManifests.Count} 条与容器不匹配的来源记录";
            return $"容器写回会话已登记：{registration.Session.Bindings.Count} 个 FNG 绑定" +
                   (registration.AddedBindings > 0 ? $"（新增 {registration.AddedBindings}）" : "") + ignored;
        }
        catch (Exception error)
        {
            return "容器写回会话登记失败：" + error.Message;
        }
    }

    /// <summary>Resolve a verified binding, including the durable locator written by Save As.</summary>
    public ContainerSession SessionForFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (directory == null) return null;
        foreach (var cached in _sessions.Values.Where(s => s != null).Distinct())
            if (cached.FindByFile(filePath) != null) return cached;
        var session = LoadSession(directory);
        if (session?.FindByFile(filePath) != null) return session;
        var link = ContainerFileLink.Read(filePath);
        if (link != null)
        {
            session = LoadSession(link.SessionDirectory);
            if (session != null && link.FindBinding(session) != null) return session;
            throw new InvalidDataException("另存文件的容器来源记录无法核实: " + filePath);
        }
        return null;
    }

    private ContainerSession LoadSession(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (_sessions.TryGetValue(directory, out var cached) && cached != null) return cached;
        if (!ContainerSession.ExistsIn(directory)) return null;
        var session = ContainerSession.Load(directory);
        _sessions[directory] = session;
        return session;
    }

    public ContainerSession SessionForDocument(WorkspaceDocument document)
        => document == null ? null : SessionForFile(document.FilePath) ?? SessionForFile(document.SourcePath);

    private static ContainerFngBinding FindBinding(ContainerSession session, string path)
        => session?.FindByFile(path) ?? (session == null ? null : ContainerFileLink.Read(path)?.FindBinding(session));

    public void PrepareSave(WorkspaceSaveContext save)
    {
        try
        {
            var session = SessionForDocument(save.Document);
            var binding = FindBinding(session, save.PreviousPath) ?? FindBinding(session, save.Document.SourcePath);
            if (binding == null) return;
            if (ContainerSession.PathsEqual(save.TargetPath, session.OriginalPath) ||
                ContainerSession.PathsEqual(save.TargetPath, session.WorkingCopyPath) ||
                ContainerSession.PathsEqual(save.TargetPath, ContainerSession.SessionFilePath(session.SessionDirectory)) ||
                ContainerSession.PathsEqual(save.TargetPath, WriteBackJournal.JournalFilePath(session.SessionDirectory)))
                throw new InvalidOperationException("FNG 保存目标不能覆盖来源容器、工作副本或写回记录。");
            if (save.IsJson) return; // JSON is an independent export, never a binary replacement
            var otherSession = SessionForFile(save.TargetPath);
            var otherBinding = FindBinding(otherSession, save.TargetPath);
            if (otherBinding != null && (!ContainerSession.PathsEqual(otherSession.SessionDirectory, session.SessionDirectory) ||
                otherBinding.BlockOffset != binding.BlockOffset))
                throw new InvalidOperationException("保存目标属于另一个容器块，请选择其他文件名。");
            if (binding.HasSavedChange && !ContainerSession.PathsEqual(binding.EffectiveFile, save.TargetPath) &&
                ConfirmVersionSwitch?.Invoke($"{binding.PackageName} 已有待写回版本：\n{binding.EffectiveFile}\n\n是否以本次保存的版本替换它？\n{save.TargetPath}") != true)
                throw new InvalidOperationException("已取消切换待写回版本，文件尚未保存。");
            if (!ContainerSession.PathsEqual(save.TargetPath, binding.ExtractedPath))
                ContainerFileLink.Write(save.TargetPath, session, binding);
            var receipt = ContainerSaveReceipt.Prepare(session, binding, save.TargetPath);
            save.RecoveryState = (session, binding, receipt);
        }
        catch (Exception error)
        {
            save.Error = "无法准备容器来源或保存恢复记录，文件尚未写入：\n" + error.Message;
        }
    }

    public void FinishSave(WorkspaceSaveContext save)
    {
        if (save.RecoveryState is not ValueTuple<ContainerSession, ContainerFngBinding, ContainerSaveReceipt> state) return;
        var (session, binding, receipt) = state;
        try
        {
            if (!save.Succeeded)
            {
                // Keep the intent if a failed writer partially changed its target.
                var hash = File.Exists(save.TargetPath) ? ContainerFileHash.OfFile(save.TargetPath) : null;
                if (hash == receipt.BeforeSha256) receipt.Discard();
                return;
            }
            receipt.Complete();
            ContainerSaveReceipt.ClearOlderForTarget(session, receipt);
            binding.RecordSaved(save.TargetPath, receipt.SavedSha256, receipt.CreatedAt);
            session.Save();
            receipt.Discard();
        }
        catch (Exception error)
        {
            save.Warning = "FNG 已保存，但容器登记未完成。恢复记录已保留，重新打开时会核对；" +
                           "请勿把独立 FNG 保存视为容器已更新。\n" + error.Message + "\n" + receipt.ReceiptPath;
        }
        finally { PendingChanged?.Invoke(this, EventArgs.Empty); }
    }

    public bool IsPending(string filePath)
    {
        try
        {
            var session = SessionForFile(filePath);
            var binding = FindBinding(session, filePath);
            return binding is { HasSavedChange: true };
        }
        catch (Exception) { return false; } // save/write-back commands surface errors, painting must remain usable
    }

    public int PendingCount(IEnumerable<WorkspaceFile> files)
    {
        var bindings = new System.Collections.Generic.HashSet<ContainerFngBinding>();
        foreach (var file in files)
        {
            try
            {
                var session = SessionForFile(file.Path);
                var binding = FindBinding(session, file.Path);
                if (binding is { HasSavedChange: true }) bindings.Add(binding);
            }
            catch (Exception) { }
        }
        return bindings.Count;
    }

    /// <summary>Drops cached sessions so external ledger changes become visible again.</summary>
    public void Invalidate(ContainerSession session)
    {
        if (session?.SessionDirectory != null) _sessions.Remove(session.SessionDirectory);
    }

    public void NotifyPendingChanged() => PendingChanged?.Invoke(this, EventArgs.Empty);
}
