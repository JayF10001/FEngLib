using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FEngLib.Assets;

public sealed class WriteBackItemResult
{
    public string PackageName { get; set; }
    public long BlockOffset { get; set; }
    public long StoredLength { get; set; }
    public string ReplacementPath { get; set; }
    public string ReplacementSha256 { get; set; }
}

public sealed class ContainerWriteBackReport
{
    public bool Success { get; internal set; }
    public string WorkingCopyPath { get; internal set; }
    public string BackupPath { get; internal set; }
    public string Sha256Before { get; internal set; }
    public string Sha256After { get; internal set; }
    public int VerifiedBlockCount { get; internal set; }
    public List<WriteBackItemResult> Items { get; internal set; } = new();
    public List<string> Warnings { get; internal set; } = new();
    public bool LedgerComplete { get; internal set; } = true;
    public string JournalId { get; internal set; }
    public DateTimeOffset StartedAt { get; internal set; }
    public DateTimeOffset FinishedAt { get; internal set; }
}

/// <summary>
/// Executes a validated equal-length write-back plan as one transaction per container copy:
/// freeze inputs again, back up, build and fully verify a staged candidate, publish with an
/// atomic same-volume replace, re-read the result, then update the session ledger. The original
/// container is never written. A candidate that fails verification is never published.
/// </summary>
public static class ContainerWriteBackExecutor
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static ContainerWriteBackReport Execute(ContainerSession session, ContainerWritePlan plan,
        Action<string> progress = null)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (!plan.IsExecutable)
            throw new InvalidOperationException(
                "The plan is not executable: " + string.Join(" / ",
                    plan.Blockers.Concat(plan.Items.SelectMany(i => i.Blockers))));

        var workingCopy = Path.GetFullPath(session.WorkingCopyPath);
        var gate = Locks.GetOrAdd(workingCopy.ToUpperInvariant(), _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
            throw new IOException("另一个写回操作正在处理同一工作副本，请等待其完成。");
        try
        {
            return ExecuteLocked(session, plan, workingCopy, progress);
        }
        finally
        {
            gate.Release();
        }
    }

    private static ContainerWriteBackReport ExecuteLocked(ContainerSession session, ContainerWritePlan plan,
        string workingCopy, Action<string> progress)
    {
        var report = new ContainerWriteBackReport
        {
            StartedAt = DateTimeOffset.UtcNow,
            WorkingCopyPath = workingCopy
        };
        var entries = WriteBackJournal.Load(session.SessionDirectory);
        var entry = new WriteBackJournalEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            StartedAt = report.StartedAt,
            WorkingCopyPath = workingCopy,
            BackupPath = plan.BackupPath,
            Items = plan.Items.Select(i => new WriteBackJournalItem
            {
                PackageName = i.PackageName,
                BlockOffset = i.BlockOffset,
                ReplacementSha256 = i.ReplacementSha256
            }).ToList()
        };
        progress?.Invoke($"冻结输入: {plan.Items.Count} 个替换项");

        // 1. Freeze: the plan is only as good as the bytes that were hashed when it was built.
        var shaBefore = ContainerFileHash.OfFile(workingCopy);
        if (shaBefore != plan.WorkingCopySha256)
            throw new InvalidDataException("工作副本在生成计划后发生了变化，计划已过期，请重新生成。");
        foreach (var item in plan.Items)
        {
            var hash = ContainerFileHash.OfFile(item.ReplacementPath);
            if (hash != item.ReplacementSha256)
                throw new InvalidDataException(
                    $"替换文件在生成计划后发生了变化: {item.ReplacementPath}，计划已过期，请重新生成。");
        }

        // 2. Backup the copy as it is now; never overwrite an existing backup.
        progress?.Invoke("备份当前副本");
        Directory.CreateDirectory(Path.GetDirectoryName(plan.BackupPath));
        File.Copy(workingCopy, plan.BackupPath, overwrite: false);
        try
        {
            if (ContainerFileHash.OfFile(plan.BackupPath) != shaBefore)
                throw new InvalidDataException("备份校验失败，已中止（未修改工作副本）。");
        }
        catch
        {
            TryDelete(plan.BackupPath);
            throw;
        }

        entry.WorkingCopySha256Before = shaBefore;
        entries.Add(entry);
        WriteBackJournal.Save(session.SessionDirectory, entries);
        report.BackupPath = plan.BackupPath;
        report.Sha256Before = shaBefore;
        report.JournalId = entry.Id;

        var candidate = Path.Combine(Path.GetDirectoryName(workingCopy) ?? ".",
            ".fng-writeback-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // 3. Build the candidate from a full copy, then apply exactly the planned ranges.
            progress?.Invoke("构建候选副本");
            File.Copy(workingCopy, candidate, overwrite: false);
            foreach (var item in plan.Items)
            {
                var replacement = File.ReadAllBytes(item.ReplacementPath);
                using var hashCheck = System.Security.Cryptography.IncrementalHash.CreateHash(
                    System.Security.Cryptography.HashAlgorithmName.SHA256);
                hashCheck.AppendData(replacement);
                if (Convert.ToHexString(hashCheck.GetHashAndReset()) != item.ReplacementSha256)
                    throw new InvalidDataException($"替换文件校验失败: {item.ReplacementPath}");
                using var target = File.Open(candidate, FileMode.Open, FileAccess.Write, FileShare.None);
                target.Position = item.BlockOffset;
                target.Write(replacement, 0, replacement.Length);
            }

            // 4. Independent verification: walk every block of the candidate.
            progress?.Invoke("验证候选副本");
            var expectedAfter = ContainerFileHash.OfFile(candidate);
            VerifyCandidate(candidate, plan);
            entry.WorkingCopySha256Result = expectedAfter;
            WriteBackJournal.Save(session.SessionDirectory, entries);

            // 5. Publish: atomic same-volume replace; readers never see a partial file.
            progress?.Invoke("发布新副本");
            File.Replace(candidate, workingCopy, null);
        }
        catch (Exception error)
        {
            entry.Status = WriteBackJournal.StatusFailed;
            entry.Detail = "失败: " + error.Message;
            try { WriteBackJournal.Save(session.SessionDirectory, entries); }
            catch (Exception) { /* the pre-publish entry stays on disk; recovery re-checks it */ }
            TryDelete(candidate);
            throw;
        }

        // Published: from here the copy holds the candidate, whatever later steps report.
        try
        {
            entry.Status = WriteBackJournal.StatusPublished;
            WriteBackJournal.Save(session.SessionDirectory, entries);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report.Warnings.Add("副本已发布，但日志状态更新失败：" + error.Message);
        }

        // 6. Post-publish verification on the final file, then ledger update.
        progress?.Invoke("复核发布结果");
        var shaAfter = ContainerFileHash.OfFile(workingCopy);
        if (shaAfter != entry.WorkingCopySha256Result)
            throw new InvalidDataException("发布后复核失败：副本内容与候选不一致。");
        VerifyCandidate(workingCopy, plan);

        report.Success = true;
        report.Sha256After = shaAfter;
        report.VerifiedBlockCount = plan.ExpectedBlocks.Count;
        report.Items = plan.Items.Select(i => new WriteBackItemResult
        {
            PackageName = i.PackageName,
            BlockOffset = i.BlockOffset,
            StoredLength = i.StoredLength,
            ReplacementPath = i.ReplacementPath,
            ReplacementSha256 = i.ReplacementSha256
        }).ToList();

        try
        {
            session.WorkingCopySha256 = shaAfter;
            session.WorkingCopyLength = plan.WorkingCopyLength;
            foreach (var item in plan.Items)
            {
                var binding = session.Bindings.Single(b => b.BlockOffset == item.BlockOffset);
                binding.LastWriteBackSha256 = item.ReplacementSha256;
            }
            session.History.Add(new ContainerWriteBackRecord
            {
                At = DateTimeOffset.UtcNow,
                WorkingCopySha256Before = shaBefore,
                WorkingCopySha256After = shaAfter,
                BackupPath = plan.BackupPath,
                PackageNames = plan.Items.Select(i => i.PackageName).ToList(),
                JournalId = entry.Id,
                LedgerComplete = true
            });
            session.Save();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report.LedgerComplete = false;
            report.Warnings.Add("副本已更新，但状态登记未完成：" + error.Message +
                $"。日志保留在 {WriteBackJournal.JournalFilePath(session.SessionDirectory)}，下次写回前会自动核对恢复。");
        }

        if (report.LedgerComplete)
        {
            // Ledger and journal are separate files; only mark the journal done when the ledger
            // really landed, otherwise recovery must still see this entry as pending.
            entry.Status = WriteBackJournal.StatusCompleted;
            try { WriteBackJournal.Save(session.SessionDirectory, entries); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                report.Warnings.Add("写回完成，但日志收尾失败：" + error.Message + "（下次写回前会自动核对）");
            }
        }

        report.FinishedAt = DateTimeOffset.UtcNow;
        return report;
    }

    /// <summary>
    /// Every block of the candidate must match the frozen plan: same sequence, boundaries and
    /// content — except the planned targets, which must equal their replacement hashes.
    /// </summary>
    private static void VerifyCandidate(string candidatePath, ContainerWritePlan plan)
    {
        var index = ContainerBlockIndex.Scan(candidatePath);
        if (index.Blocks.Count != plan.ExpectedBlocks.Count)
            throw new InvalidDataException(
                $"候选块数 {index.Blocks.Count} 与预期 {plan.ExpectedBlocks.Count} 不一致。");
        if (index.SourceLength != plan.WorkingCopyLength)
            throw new InvalidDataException("候选文件长度与计划不符。");

        var targets = plan.Items.Where(i => i.IsRunnable).ToDictionary(i => i.BlockOffset, i => i);
        foreach (var (block, expected) in index.Blocks.Zip(plan.ExpectedBlocks))
        {
            if (block.Sequence != expected.Sequence || block.Offset != expected.Offset ||
                block.ChunkId != expected.ChunkId || block.PayloadLength != expected.PayloadLength)
                throw new InvalidDataException(
                    $"候选块布局在序号 {block.Sequence}（偏移 0x{block.Offset:X}）处与计划不一致。");
            if (targets.TryGetValue(block.Offset, out var target))
            {
                if (block.Sha256 != target.ReplacementSha256)
                    throw new InvalidDataException(
                        $"候选中的目标块 {target.PackageName} 与替换内容不一致。");
            }
            else if (block.Sha256 != expected.Sha256)
            {
                throw new InvalidDataException(
                    $"候选在序号 {block.Sequence}（偏移 0x{block.Offset:X}）处改动了非目标内容。");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* surfaced by the caller's error handling; never blocks the report */ }
    }
}
