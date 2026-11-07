using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FEngLib.Assets;

/// <summary>One new-copy operation for an exact saved project revision.</summary>
public sealed class ContainerProjectWriteBack : IDisposable
{
    private readonly string projectPath;
    private readonly string projectSha256;
    private readonly string staging;
    private readonly ContainerSession session;
    private bool started;
    private bool disposed;

    private ContainerProjectWriteBack(string projectPath, string projectSha256, string staging,
        ContainerSession session, ContainerWritePlan plan)
    {
        this.projectPath = projectPath;
        this.projectSha256 = projectSha256;
        this.staging = staging;
        this.session = session;
        Plan = plan;
    }

    public ContainerWritePlan Plan { get; }
    public string LedgerDirectory => session.SessionDirectory;

    public static ContainerProjectWriteBack Prepare(string projectPath, string outputPath)
    {
        projectPath = Path.GetFullPath(projectPath);
        outputPath = Path.GetFullPath(outputPath);
        using var projectFile = File.OpenRead(projectPath);
        var snapshot = ContainerProjectFile.Load(projectPath);
        var index = snapshot.Project.Index;
        var baseline = snapshot.Project.BaselinePath;
        if (ContainerSession.PathsEqual(outputPath, baseline) ||
            ContainerSession.PathsEqual(outputPath, index.SourcePath) ||
            !string.Equals(Path.GetExtension(outputPath), Path.GetExtension(baseline), StringComparison.OrdinalIgnoreCase) ||
            ContainerSession.PathsEqual(outputPath, projectPath))
            throw new IOException("请为容器副本选择独立的新路径，并保留源容器扩展名。");
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
            throw new IOException("副本目标已存在，请选择新的文件名。");

        var attempt = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var ledger = Path.Combine(LedgerRoot(projectPath), attempt);
        var session = ContainerSession.ForNewCopy(index, ledger, outputPath, baseline);
        var staging = Path.Combine(Path.GetTempPath(), "feng-project-writeback-" + attempt);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var edit in snapshot.Edits)
            {
                var source = edit.Source;
                var replacement = Path.Combine(staging, $"{source.Block.Offset:X16}.fng");
                using (var bytes = edit.OpenRead())
                using (var file = File.Open(replacement, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    bytes.CopyTo(file);
                var binding = new ContainerFngBinding
                {
                    PackageName = source.PackageName,
                    BlockOffset = source.Block.Offset,
                    ChunkId = source.Block.ChunkId,
                    StoredLength = source.Block.TotalLength,
                    ExtractedSha256 = source.OriginalSha256
                };
                binding.RecordSaved(replacement, ContainerFileHash.OfFile(replacement));
                session.Bindings.Add(binding);
            }
            var plan = ContainerWritePlanner.Build(session, publishNewCopy: true);
            return new ContainerProjectWriteBack(projectPath, FngContainer.Hash(projectFile), staging, session, plan);
        }
        catch
        {
            Cleanup(staging);
            throw;
        }
    }

    public ContainerWriteBackReport Execute(Action<string> progress = null)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ContainerProjectWriteBack));
        if (started || !Plan.IsExecutable) throw new InvalidOperationException("请重新生成可执行的写回清单。");
        started = true;
        using var projectFile = File.OpenRead(projectPath);
        if (FngContainer.Hash(projectFile) != projectSha256)
            throw new InvalidDataException("工程在确认前发生变化，请重新生成写回计划。");

        Directory.CreateDirectory(LedgerDirectory);
        session.Save();
        AtomicJsonFile.Write(Path.Combine(LedgerDirectory, "plan.json"), Plan);
        try
        {
            var report = ContainerWriteBackExecutor.Execute(session, Plan, progress);
            RecordResult(report, failure: null);
            return report;
        }
        catch (ContainerWriteBackException error)
        {
            RecordResult(error.Report, error.Message);
            throw;
        }
    }

    private void RecordResult(ContainerWriteBackReport report, string failure)
    {
        try { AtomicJsonFile.Write(Path.Combine(LedgerDirectory, "result.json"), new { Report = report, Failure = failure }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report.Warnings.Add("结果记录写入失败，可核对会话和事务日志：" + error.Message);
        }
    }

    public static string LedgerRoot(string projectPath) => Path.GetFullPath(projectPath) + ".writeback";

    /// <summary>Reports the latest attempt matching the saved edits, independently of tab state.</summary>
    public static string SavedState(string projectPath, ContainerBlockIndex index,
        IReadOnlyCollection<ContainerFngContent> edits, string originalPath = null)
    {
        if (edits.Count == 0) return "工程已保存";
        var root = LedgerRoot(projectPath);
        if (!Directory.Exists(root)) return "待写回";
        var hashes = edits.ToDictionary(edit => edit.Source.Block.Offset, edit =>
        {
            using var stream = edit.OpenRead();
            return FngContainer.Hash(stream);
        });
        var expectedOriginal = originalPath;
        if (string.IsNullOrWhiteSpace(expectedOriginal))
        {
            try { expectedOriginal = ContainerProjectFile.Load(projectPath).Project.BaselinePath; }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
            {
                expectedOriginal = index.SourcePath;
            }
        }
        foreach (var directory in Directory.EnumerateDirectories(root).OrderByDescending(path => path, StringComparer.Ordinal))
        {
            if (!ContainerSession.ExistsIn(directory)) continue;
            var session = ContainerSession.Load(directory);
            if (session.OriginalSha256 != index.SourceSha256 ||
                (!ContainerSession.PathsEqual(session.OriginalPath, expectedOriginal) &&
                 !ContainerSession.PathsEqual(session.OriginalPath, index.SourcePath)) ||
                session.Bindings.Count != edits.Count ||
                edits.Any(edit => !session.Bindings.Any(binding =>
                    binding.BlockOffset == edit.Source.Block.Offset &&
                    binding.ExtractedSha256 == edit.Source.OriginalSha256 &&
                    binding.SavedSha256 == hashes[binding.BlockOffset])))
                continue;
            var resultPath = Path.Combine(directory, "result.json");
            if (File.Exists(resultPath))
            {
                using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
                if (result.RootElement.TryGetProperty("Failure", out var error) && error.ValueKind == JsonValueKind.String)
                    return "写回失败";
            }
            var journal = WriteBackJournal.Load(directory).LastOrDefault();
            if (journal?.Status == WriteBackJournal.StatusFailed) return "写回失败";
            if (journal?.WorkingCopySha256Result == null || !File.Exists(session.WorkingCopyPath))
                return "待写回";
            return ContainerFileHash.OfFile(session.WorkingCopyPath) == journal.WorkingCopySha256Result
                ? "已写回" : "待写回（副本已变化）";
        }
        return "待写回";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Cleanup(staging);
    }

    private static void Cleanup(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
