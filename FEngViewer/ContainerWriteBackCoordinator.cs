using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>The session of the directory containing this file, or null when none exists.</summary>
    public ContainerSession SessionForFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        string directory;
        try { directory = Path.GetDirectoryName(Path.GetFullPath(filePath)); }
        catch (Exception) { return null; }
        if (directory == null) return null;
        if (_sessions.TryGetValue(directory, out var cached)) return cached;

        ContainerSession session = null;
        if (ContainerSession.ExistsIn(directory))
        {
            // A broken session file must not break the file list; planning reports it properly.
            try { session = ContainerSession.Load(directory); }
            catch (Exception) { session = null; }
        }
        _sessions[directory] = session;
        return session;
    }

    /// <summary>Records a completed save so its block becomes pending for write-back.</summary>
    public void RecordSaved(WorkspaceDocument document)
    {
        if (document?.Package == null) return;
        var session = SessionForFile(document.FilePath);
        var binding = session?.FindByFile(document.FilePath);
        if (binding == null) return;

        var wasPending = binding.HasSavedChange;
        string hash;
        try { hash = ContainerFileHash.OfFile(document.FilePath); }
        catch (Exception) { return; }
        binding.RecordSaved(document.FilePath, hash);
        try { session.Save(); }
        catch (Exception) { return; }

        if (binding.HasSavedChange != wasPending) PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsPending(string filePath)
    {
        var session = SessionForFile(filePath);
        var binding = session?.FindByFile(filePath);
        return binding is { HasSavedChange: true };
    }

    public int PendingCount(IEnumerable<WorkspaceFile> files)
    {
        var count = 0;
        foreach (var file in files)
            if (IsPending(file.Path)) count++;
        return count;
    }

    /// <summary>Drops cached sessions so external ledger changes become visible again.</summary>
    public void Invalidate(ContainerSession session)
    {
        if (session?.SessionDirectory != null) _sessions.Remove(session.SessionDirectory);
    }

    public void NotifyPendingChanged() => PendingChanged?.Invoke(this, EventArgs.Empty);
}
