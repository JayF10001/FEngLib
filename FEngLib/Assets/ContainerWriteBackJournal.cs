using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FEngLib.Assets;

public sealed class WriteBackJournalItem
{
    public string PackageName { get; set; }
    public long BlockOffset { get; set; }
    public string ReplacementSha256 { get; set; }
}

/// <summary>
/// Operation journal for one write-back attempt. It is written before publication and completed
/// after the ledger update, so a crash in between leaves enough evidence to reconcile the session
/// ledger on the next run. Status flow: Prepared → Published → Completed, or Failed.
/// </summary>
public sealed class WriteBackJournalEntry
{
    public string Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string WorkingCopyPath { get; set; }
    public string BackupPath { get; set; }
    public string WorkingCopySha256Before { get; set; }

    /// <summary>Set once the staged candidate passed verification, before publication.</summary>
    public string WorkingCopySha256Result { get; set; }

    public List<WriteBackJournalItem> Items { get; set; } = new();
    public string Status { get; set; } = "Prepared";
    public string Detail { get; set; }
}

public static class WriteBackJournal
{
    public const string JournalFileName = "container-writeback-journal.json";
    public const string StatusPrepared = "Prepared";
    public const string StatusPublished = "Published";
    public const string StatusCompleted = "Completed";
    public const string StatusFailed = "Failed";

    public static string JournalFilePath(string sessionDirectory)
        => Path.Combine(Path.GetFullPath(sessionDirectory), JournalFileName);

    public static List<WriteBackJournalEntry> Load(string sessionDirectory)
    {
        var path = JournalFilePath(sessionDirectory);
        AtomicJsonFile.RecoverLegacyTemporary(path);
        if (!File.Exists(path)) return new List<WriteBackJournalEntry>();
        var entries = JsonSerializer.Deserialize<List<WriteBackJournalEntry>>(File.ReadAllText(path));
        return entries ?? new List<WriteBackJournalEntry>();
    }

    public static void Save(string sessionDirectory, List<WriteBackJournalEntry> entries)
    {
        var path = JournalFilePath(sessionDirectory);
        AtomicJsonFile.Write(path, entries);
    }

    /// <summary>
    /// Reconciles unfinished journal entries with the actual working copy before planning. A copy
    /// that holds the journaled result gets its ledger completed automatically; an untouched copy
    /// closes the entry as failed. Anything else stays pending and must block further write-backs.
    /// </summary>
    public static IReadOnlyList<string> Recover(ContainerSession session)
    {
        var entries = Load(session.SessionDirectory);
        var pending = entries.Where(e =>
            e.Status is StatusPrepared or StatusPublished).ToList();
        if (pending.Count == 0) return Array.Empty<string>();

        var report = new List<string>();
        var dirty = false;
        foreach (var entry in pending)
        {
            string hash = null;
            try
            {
                if (entry.WorkingCopyPath != null && File.Exists(entry.WorkingCopyPath))
                    hash = ContainerFileHash.OfFile(entry.WorkingCopyPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                report.Add($"写回日志 {entry.Id} 无法读取当前副本：{error.Message}");
                continue;
            }

            if (hash == null)
            {
                entry.Status = StatusFailed;
                entry.Detail = "恢复：工作副本缺失，无法核对";
                dirty = true;
                report.Add($"写回日志 {entry.Id}：工作副本缺失，已标记失败。");
                continue;
            }

            if (entry.WorkingCopySha256Result != null &&
                string.Equals(hash, entry.WorkingCopySha256Result, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(session.WorkingCopySha256, entry.WorkingCopySha256Before, StringComparison.OrdinalIgnoreCase))
            {
                session.CompleteWriteBack(entry);
                entry.Status = StatusCompleted;
                entry.Detail = "恢复：副本已发布但登记缺失，已自动补全";
                dirty = true;
                report.Add($"写回日志 {entry.Id}：副本已更新而登记未完成，已自动补全登记。");
            }
            else if (string.Equals(hash, entry.WorkingCopySha256Result, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(session.WorkingCopySha256, entry.WorkingCopySha256Result, StringComparison.OrdinalIgnoreCase))
            {
                entry.Status = StatusCompleted;
                entry.Detail = "恢复：登记已完成，仅补记日志状态";
                dirty = true;
                report.Add($"写回日志 {entry.Id}：登记已生效，日志状态已补全。");
            }
            else if (string.Equals(hash, entry.WorkingCopySha256Before, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(session.WorkingCopySha256, entry.WorkingCopySha256Before, StringComparison.OrdinalIgnoreCase))
            {
                entry.Status = StatusFailed;
                entry.Detail = "恢复：发布前中断，副本未改变";
                dirty = true;
                report.Add($"写回日志 {entry.Id}：发布前中断，副本未改变，已关闭该日志。");
            }
            else
            {
                report.Add($"写回日志 {entry.Id}：副本内容与日志记录不一致，保持待人工核对状态，" +
                           $"写回已被阻止。日志: {JournalFilePath(session.SessionDirectory)}");
            }
        }

        if (dirty) Save(session.SessionDirectory, entries);
        return report;
    }

}
