using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FEngLib.Assets;

/// <summary>A durable save intent, completed before the session ledger is updated.</summary>
public sealed class ContainerSaveReceipt
{
    public string OriginalPath { get; set; }
    public string OriginalSha256 { get; set; }
    public long BlockOffset { get; set; }
    public string PackageName { get; set; }
    public string TargetPath { get; set; }
    public string BeforeSha256 { get; set; }
    public string SavedSha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string ReceiptPath { get; private set; }

    public static ContainerSaveReceipt Prepare(ContainerSession session, ContainerFngBinding binding, string targetPath)
    {
        var receipt = new ContainerSaveReceipt
        {
            OriginalPath = session.OriginalPath, OriginalSha256 = session.OriginalSha256,
            BlockOffset = binding.BlockOffset, PackageName = binding.PackageName,
            TargetPath = Path.GetFullPath(targetPath),
            BeforeSha256 = File.Exists(targetPath) ? ContainerFileHash.OfFile(targetPath) : null,
            CreatedAt = DateTimeOffset.UtcNow, Status = "Prepared"
        };
        receipt.ReceiptPath = Path.Combine(session.SessionDirectory, ".fng-save-" + Guid.NewGuid().ToString("N") + ".json");
        AtomicJsonFile.Write(receipt.ReceiptPath, receipt);
        return receipt;
    }

    public void Complete()
    {
        SavedSha256 = ContainerFileHash.OfFile(TargetPath);
        Status = "Saved";
        AtomicJsonFile.Write(ReceiptPath, this);
    }

    public void Discard() => File.Delete(ReceiptPath);

    /// <summary>Only a later successful, explicitly bound save may supersede an unfinished save.</summary>
    public static void ClearOlderForTarget(ContainerSession session, ContainerSaveReceipt completed)
    {
        foreach (var path in Directory.GetFiles(session.SessionDirectory, ".fng-save-*.json"))
        {
            if (path == completed.ReceiptPath) continue;
            var old = JsonSerializer.Deserialize<ContainerSaveReceipt>(File.ReadAllText(path));
            if (old != null && old.OriginalSha256 == session.OriginalSha256 &&
                old.BlockOffset == completed.BlockOffset && old.CreatedAt <= completed.CreatedAt &&
                ContainerSession.PathsEqual(old.TargetPath, completed.TargetPath)) File.Delete(path);
        }
    }

    /// <summary>Replay receipts into memory; persist the ledger before removing recovery evidence.</summary>
    public static IReadOnlyList<string> Recover(ContainerSession session)
    {
        var problems = new List<string>();
        var receipts = Directory.GetFiles(session.SessionDirectory, ".fng-save-*.json")
            .Select(path =>
            {
                var value = JsonSerializer.Deserialize<ContainerSaveReceipt>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Empty save receipt: " + path);
                value.ReceiptPath = path;
                return value;
            }).OrderBy(r => r.CreatedAt).ToList();
        foreach (var receipt in receipts)
        {
            if (!ContainerSession.PathsEqual(receipt.OriginalPath, session.OriginalPath) ||
                receipt.OriginalSha256 != session.OriginalSha256)
            {
                problems.Add("保存恢复记录的来源不匹配: " + receipt.ReceiptPath);
                continue;
            }
            var binding = session.Bindings.SingleOrDefault(b => b.BlockOffset == receipt.BlockOffset && b.PackageName == receipt.PackageName);
            if (binding == null)
            {
                problems.Add("保存恢复记录缺少块绑定: " + receipt.ReceiptPath);
                continue;
            }
            if (binding.SavedAt > receipt.CreatedAt)
            {
                // A later explicit save has already chosen the replacement version.
                receipt.Discard();
                continue;
            }
            var hash = File.Exists(receipt.TargetPath) ? ContainerFileHash.OfFile(receipt.TargetPath) : null;
            if (receipt.Status == "Prepared")
            {
                if (hash == receipt.BeforeSha256) { receipt.Discard(); continue; }
                problems.Add("保存过程曾中断，文件可能已改变，请核对并重新保存该文件: " + receipt.TargetPath);
                continue;
            }
            if (receipt.Status != "Saved" || hash != receipt.SavedSha256)
            {
                problems.Add("保存恢复文件与记录摘要不符，请核对: " + receipt.TargetPath);
                continue;
            }
            binding.RecordSaved(receipt.TargetPath, hash, receipt.CreatedAt);
            try
            {
                session.Save();
                receipt.Discard();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problems.Add("FNG 已保存，恢复记录仍待登记: " + error.Message);
            }
        }
        return problems;
    }
}

/// <summary>Save As provenance locator. It never changes the original extraction manifest.</summary>
public sealed class ContainerFileLink
{
    public string SessionDirectory { get; set; }
    public string OriginalPath { get; set; }
    public string OriginalSha256 { get; set; }
    public long BlockOffset { get; set; }
    public string PackageName { get; set; }

    public static string LinkPath(string file) => Path.GetFullPath(file) + ".container-link.json";
    public static ContainerFileLink Read(string file)
        => File.Exists(LinkPath(file)) ? JsonSerializer.Deserialize<ContainerFileLink>(File.ReadAllText(LinkPath(file))) : null;
    public ContainerFngBinding FindBinding(ContainerSession session)
        => ContainerSession.PathsEqual(OriginalPath, session.OriginalPath) && OriginalSha256 == session.OriginalSha256
            ? session.Bindings.SingleOrDefault(b => b.BlockOffset == BlockOffset && b.PackageName == PackageName) : null;
    public static void Write(string file, ContainerSession session, ContainerFngBinding binding)
        => AtomicJsonFile.Write(LinkPath(file), new ContainerFileLink
        {
            SessionDirectory = session.SessionDirectory, OriginalPath = session.OriginalPath,
            OriginalSha256 = session.OriginalSha256, BlockOffset = binding.BlockOffset, PackageName = binding.PackageName
        });
}
