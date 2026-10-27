using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FEngLib.Assets;

/// <summary>One planned equal-length replacement of a single FNG block.</summary>
public sealed class WriteBackPlanItem
{
    public string PackageName { get; set; }
    public long BlockOffset { get; set; }
    public uint ChunkId { get; set; }
    public long StoredLength { get; set; }
    public long ReplacementStoredLength { get; set; }
    public string ReplacementPath { get; set; }
    public string ReplacementSha256 { get; set; }
    public string CurrentBlockSha256 { get; set; }
    public List<string> Blockers { get; } = new();

    public bool IsRunnable => Blockers.Count == 0;
}

/// <summary>Expected content of a non-target block; the candidate must reproduce it byte for byte.</summary>
public sealed class WriteBackPlanBlock
{
    public int Sequence { get; set; }
    public long Offset { get; set; }
    public uint ChunkId { get; set; }
    public long PayloadLength { get; set; }
    public string Sha256 { get; set; }
}

/// <summary>
/// Write-back plan snapshot. Inputs are fingerprinted at build time: the working copy
/// fingerprint, the replacement file hashes, and the expected block layout of the result.
/// Executing a stale plan fails instead of adapting.
/// </summary>
public sealed class ContainerWritePlan
{
    public DateTimeOffset CreatedAt { get; set; }
    public string OriginalPath { get; set; }
    public string WorkingCopyPath { get; set; }
    public long WorkingCopyLength { get; set; }
    public string WorkingCopySha256 { get; set; }
    public int BlockCount { get; set; }
    public int PendingChangeCount { get; internal set; }
    public string BackupPath { get; set; }
    public List<WriteBackPlanItem> Items { get; set; } = new();
    public List<WriteBackPlanBlock> ExpectedBlocks { get; set; } = new();
    public List<string> Blockers { get; } = new();
    public List<string> Notes { get; } = new();

    public bool IsExecutable => Blockers.Count == 0 && Items.Count > 0 && Items.All(i => i.IsRunnable);
    public bool HasPendingChanges => PendingChangeCount > 0;
    public bool IsBlocked => Blockers.Count > 0 || Items.Any(i => !i.IsRunnable);
}

/// <summary>Reconciles unfinished operations, then builds and validates a write-back plan.</summary>
public static class ContainerWritePlanner
{
    public static ContainerWritePlan Build(ContainerSession session)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        var plan = new ContainerWritePlan
        {
            CreatedAt = DateTimeOffset.UtcNow,
            OriginalPath = session.OriginalPath,
            WorkingCopyPath = session.WorkingCopyPath,
            WorkingCopyLength = session.WorkingCopyLength,
            WorkingCopySha256 = session.WorkingCopySha256
        };

        foreach (var line in WriteBackJournal.Recover(session))
            plan.Notes.Add(line);
        session.RecoverSaves();
        plan.Blockers.AddRange(session.SaveRecoveryProblems);
        plan.PendingChangeCount = session.Bindings.Count(b => b.HasSavedChange);

        if (WriteBackJournal.Load(session.SessionDirectory)
                .Any(e => e.Status is WriteBackJournal.StatusPrepared or WriteBackJournal.StatusPublished))
        {
            plan.Blockers.Add("存在未完成的写回日志，需要人工核对后才能再次写回。" +
                $"日志: {WriteBackJournal.JournalFilePath(session.SessionDirectory)}");
            return plan;
        }

        if (session.WorkingCopyPath == null)
        {
            plan.Blockers.Add("此会话还没有工作副本：写回目标是专门创建的副本，不会写入原始容器。" +
                "请先创建工作副本（Viewer 中首次写回时会提示选择路径）。");
            return plan;
        }
        if (!File.Exists(session.WorkingCopyPath))
        {
            plan.Blockers.Add($"工作副本不存在: {session.WorkingCopyPath}");
            return plan;
        }
        if (ContainerSession.PathsEqual(session.WorkingCopyPath, session.OriginalPath))
            plan.Blockers.Add("工作副本不能指向原始容器。");

        try
        {
            if (ContainerFileHash.OfFile(session.OriginalPath) != session.OriginalSha256)
                plan.Blockers.Add($"原始容器与登记摘要不一致，基线已变化: {session.OriginalPath}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            plan.Blockers.Add($"原始容器无法读取: {session.OriginalPath}（{error.Message}）");
        }

        ContainerBlockIndex index;
        try
        {
            index = ContainerBlockIndex.Scan(session.WorkingCopyPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            UnauthorizedAccessException or NotSupportedException)
        {
            plan.Blockers.Add($"工作副本无法作为块流容器读取: {error.Message}");
            return plan;
        }

        plan.WorkingCopyLength = index.SourceLength;
        plan.WorkingCopySha256 = index.SourceSha256;
        plan.BlockCount = index.Blocks.Count;

        if (session.WorkingCopySha256 == null)
        {
            plan.Blockers.Add("会话缺少工作副本的登记摘要，无法验证副本状态，请重新登记。");
            return plan;
        }
        if (!string.Equals(session.WorkingCopySha256, index.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            plan.Blockers.Add("工作副本与登记摘要不一致（可能被外部程序修改或登记已过期），" +
                "需要重新核实后更新会话，不能在未知内容上写回。");
            return plan;
        }

        plan.BackupPath = PlanBackupPath(session, session.WorkingCopyPath);
        plan.ExpectedBlocks = index.Blocks.Select(b => new WriteBackPlanBlock
        {
            Sequence = b.Sequence,
            Offset = b.Offset,
            ChunkId = b.ChunkId,
            PayloadLength = b.PayloadLength,
            Sha256 = b.Sha256
        }).ToList();

        var pending = session.Bindings.Where(b => b.HasSavedChange).ToList();
        foreach (var binding in pending)
            plan.Items.Add(BuildItem(session, binding, index));

        var duplicated = plan.Items.GroupBy(i => i.BlockOffset).Where(g => g.Count() > 1);
        foreach (var group in duplicated)
            foreach (var item in group)
                item.Blockers.Add("同一目标块出现在多个待写回条目中，不能一次发布。");

        if (plan.Items.Count == 0)
            plan.Notes.Add("当前没有待写回的已保存修改。");
        return plan;
    }

    private static WriteBackPlanItem BuildItem(ContainerSession session, ContainerFngBinding binding,
        ContainerBlockIndex index)
    {
        var item = new WriteBackPlanItem
        {
            PackageName = binding.PackageName,
            BlockOffset = binding.BlockOffset,
            ChunkId = binding.ChunkId,
            StoredLength = binding.StoredLength,
            ReplacementPath = binding.EffectiveFile
        };

        string replacementHash;
        try
        {
            if (ContainerSession.PathsEqual(item.ReplacementPath, session.WorkingCopyPath) ||
                ContainerSession.PathsEqual(item.ReplacementPath, session.OriginalPath))
            {
                item.Blockers.Add("替换文件路径与容器文件相同，拒绝写入。");
                return item;
            }
            replacementHash = ContainerFileHash.OfFile(item.ReplacementPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentNullException or ArgumentException)
        {
            item.Blockers.Add($"替换文件不可读: {item.ReplacementPath ?? "(未设置)"}（{error.Message}）");
            return item;
        }

        if (!string.Equals(replacementHash, binding.SavedSha256, StringComparison.OrdinalIgnoreCase))
        {
            item.Blockers.Add("替换文件与保存时记录的摘要不一致（保存后又被修改）。" +
                "请重新保存该 FNG 或重新登记，再生成写回计划。");
            return item;
        }

        FngContainerEntry parsed;
        try
        {
            parsed = InspectChunkFile(item.ReplacementPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            UnauthorizedAccessException or EndOfStreamException)
        {
            item.Blockers.Add($"替换文件无法作为单个 FNG 块解析: {error.Message}");
            return item;
        }

        if (parsed.Storage != FngStorageKind.Uncompressed)
        {
            item.Blockers.Add($"替换文件不是未压缩 FNG: {parsed.Diagnostic}");
            return item;
        }
        if (!string.Equals(parsed.Name, binding.PackageName, StringComparison.OrdinalIgnoreCase))
        {
            item.Blockers.Add($"包名不一致: 文件为 {parsed.Name}，来源块为 {binding.PackageName}。");
            return item;
        }

        item.ReplacementStoredLength = parsed.StoredLength;
        item.ReplacementSha256 = replacementHash;
        if (parsed.StoredLength != binding.StoredLength)
        {
            item.Blockers.Add(
                $"长度不同（原块 {binding.StoredLength} 字节，替换 {parsed.StoredLength} 字节）。" +
                "当前版本仅支持等长替换，变长重建尚未支持。");
        }

        var block = index.FindBlockAtOffset(binding.BlockOffset);
        if (block == null)
        {
            item.Blockers.Add("来源块偏移在当前副本中不存在，布局已变化，不能按旧位置写回。");
            return item;
        }
        if (block.ChunkId != binding.ChunkId || block.TotalLength != binding.StoredLength)
        {
            item.Blockers.Add(
                $"副本中该块与登记不符（块 0x{block.ChunkId:X8} 长度 {block.TotalLength}，" +
                $"登记 0x{binding.ChunkId:X8} 长度 {binding.StoredLength}）。");
            return item;
        }
        if (block.Kind != ContainerBlockKind.Fng || block.FngStorage != FngStorageKind.Uncompressed)
        {
            item.Blockers.Add($"目标块当前不是未压缩 FNG，不支持替换: {block.Diagnostic ?? block.Kind.ToString()}");
            return item;
        }
        if (!string.Equals(block.Sha256, binding.WriteBackTargetSha256, StringComparison.OrdinalIgnoreCase))
        {
            item.Blockers.Add("副本中该块的内容与上次写回/初始提取不一致，来源已变化。" +
                "请重新核实绑定（必要时从当前副本重新提取）后再写回。");
            return item;
        }

        item.CurrentBlockSha256 = block.Sha256;
        return item;
    }

    private static string PlanBackupPath(ContainerSession session, string workingCopyPath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(workingCopyPath) ?? ".",
            "writeback-backups");
        var name = Path.GetFileName(workingCopyPath);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(directory, $"{name}.pre-{stamp}-{Guid.NewGuid().ToString("N")[..8]}.bak");
    }

    /// <summary>Validates a standalone FNG chunk file (block header + payload) like an extracted block.</summary>
    internal static FngContainerEntry InspectChunkFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 8)
            throw new InvalidDataException("File is shorter than a block header.");
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        var id = reader.ReadUInt32();
        var payloadLength = reader.ReadUInt32();
        if ((long)payloadLength + 8 != stream.Length)
            throw new InvalidDataException(
                $"Declared payload length {payloadLength} does not match file size {stream.Length}.");
        if (id != FngContainer.FngChunkId && id != FngContainer.CompressedFngChunkId)
            throw new InvalidDataException($"Not an FNG block (chunk id 0x{id:X8}).");
        stream.Position = 8;
        var entry = FngContainer.ReadEntry(reader, 0, id, payloadLength);
        if (entry.Storage == FngStorageKind.Uncompressed)
            FngWriteBackValidator.Validate(stream, 0, stream.Length, entry.Name);
        return entry;
    }
}
