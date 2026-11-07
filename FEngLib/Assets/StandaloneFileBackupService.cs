using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FEngLib.Assets;

/// <summary>Base type for standalone-file safety failures; callers must not treat it as success.</summary>
public class StandaloneFileSafetyException : IOException
{
    public StandaloneFileSafetyException(string message, Exception inner = null) : base(message, inner) { }
}

/// <summary>The original backup is missing, unreadable or does not match its recorded hash.</summary>
public sealed class StandaloneBackupException : StandaloneFileSafetyException
{
    public StandaloneBackupException(string message, Exception inner = null) : base(message, inner) { }
}

/// <summary>The on-disk file no longer matches what the editor last wrote.</summary>
public sealed class StandaloneConflictException : StandaloneFileSafetyException
{
    public StandaloneExternalChange Change { get; }

    public StandaloneConflictException(StandaloneExternalChange change)
        : base("文件在编辑器之外被修改，保存需要先确认。") => Change = change;
}

/// <summary>The staged write failed; the previous target file is expected to be untouched.</summary>
public sealed class StandaloneWriteException : StandaloneFileSafetyException
{
    public StandaloneWriteException(string message, Exception inner = null) : base(message, inner) { }
}

public sealed class StandaloneExternalChange
{
    public string Path { get; set; }
    public string ExpectedSha256 { get; set; }
    public string ActualSha256 { get; set; }
    public DateTimeOffset? ExpectedWriteUtc { get; set; }
    public DateTimeOffset? ActualModifiedUtc { get; set; }
    public string OriginalBackupPath { get; set; }
}

public enum StandaloneConflictAction
{
    Cancel,
    BackupAndSave
}

public sealed class StandaloneSaveOptions
{
    /// <summary>Called for an external change; without it the service refuses to overwrite.</summary>
    public Func<StandaloneExternalChange, StandaloneConflictAction> ResolveConflict { get; set; }

    /// <summary>
    /// Save-as targets were chosen explicitly; snapshot the replaced file without a second prompt.
    /// </summary>
    public bool SnapshotInsteadOfPrompt { get; set; }
}

public sealed class StandaloneSaveResult
{
    public bool Written { get; internal set; }
    public bool Cancelled { get; internal set; }
    public string Sha256 { get; internal set; }
    public string OriginalBackupPath { get; internal set; }
    public string SnapshotPath { get; internal set; }
    public List<string> Warnings { get; } = new();
}

public sealed class StandaloneRestoreResult
{
    public bool Written { get; internal set; }
    public string RestoredSha256 { get; internal set; }
    public string SnapshotPath { get; internal set; }
    public StandaloneBackupInfo Backup { get; internal set; }
    public List<string> Warnings { get; } = new();
}

public sealed class StandaloneBackupInfo
{
    public string SourcePath { get; set; }
    public string BackupDirectory { get; set; }
    public string OriginalFilePath { get; set; }
    public string OriginalSha256 { get; set; }
    public long OriginalLength { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string LastWrittenSha256 { get; set; }
    public DateTimeOffset? LastWrittenUtc { get; set; }
    public int SnapshotCount { get; set; }
}

public sealed class StandaloneBackupRecord
{
    public int SchemaVersion { get; set; } = StandaloneFileBackupService.SchemaVersion;
    public string SourcePath { get; set; }
    public string SourcePathKey { get; set; }
    public StandaloneBackupEntry Original { get; set; }
    public string LastWrittenSha256 { get; set; }
    public DateTimeOffset? LastWrittenUtc { get; set; }
    public List<StandaloneBackupEntry> Snapshots { get; set; } = new();
}

public sealed class StandaloneBackupEntry
{
    public string Sha256 { get; set; }
    public long Length { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string RelativePath { get; set; }
    public string Reason { get; set; }
}

/// <summary>
/// Keeps one immutable original copy of a standalone FNG (plus snapshots taken around external
/// changes and restores) under the local application data directory, keyed by a hash of the
/// normalized source path so equal file names in different folders never collide.
///
/// Saving is a staged, verified replace: the backup and metadata land before the target changes,
/// and a failed write leaves the previous target in place.
/// </summary>
public sealed class StandaloneFileBackupService
{
    public const int SchemaVersion = 1;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FEngViewer", "backups", "standalone");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public StandaloneFileBackupService(string backupRoot = null)
    {
        Root = Path.GetFullPath(backupRoot ?? DefaultRoot);
    }

    public string Root { get; }

    public static string KeyForPath(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizePath(path)))).ToLowerInvariant();

    public string BackupDirectoryFor(string path) => Path.Combine(Root, KeyForPath(path));

    /// <summary>Cheap metadata probe for menu state; never throws and never hashes the backup.</summary>
    public StandaloneBackupInfo TryReadBackupInfo(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var record = LoadRecord(path);
            return ToInfo(path, record);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public StandaloneSaveResult Save(string path, byte[] content, StandaloneSaveOptions options = null)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        path = Path.GetFullPath(path);
        options ??= new StandaloneSaveOptions();

        var contentSha = Convert.ToHexString(SHA256.HashData(content));
        var result = new StandaloneSaveResult { Sha256 = contentSha };
        var exists = File.Exists(path);
        var diskSha = exists ? ComputeFileHash(path) : null;

        // Nothing changed: no write, no extra backup.
        if (exists && string.Equals(diskSha, contentSha, StringComparison.OrdinalIgnoreCase))
        {
            var unchanged = LoadRecord(path);
            result.OriginalBackupPath = unchanged?.Original == null
                ? null : ResolveEntry(path, unchanged.Original)?.FullName;
            return result;
        }

        var record = LoadRecord(path);
        if (record?.Original != null) ValidateEntry(path, record.Original, verifyHash: true);

        var expectedSha = record?.LastWrittenSha256 ?? record?.Original?.Sha256;
        if (record != null && expectedSha == null)
            throw new StandaloneBackupException(
                $"备份元数据缺少版本信息，已停止保存：{MetadataPath(path)}");

        var external = record != null && exists && expectedSha != null &&
                       !string.Equals(diskSha, expectedSha, StringComparison.OrdinalIgnoreCase);

        if (external)
        {
            var change = new StandaloneExternalChange
            {
                Path = path,
                ExpectedSha256 = expectedSha,
                ActualSha256 = diskSha,
                ExpectedWriteUtc = record.LastWrittenUtc,
                ActualModifiedUtc = SafeLastWriteUtc(path),
                OriginalBackupPath = record.Original == null ? null : ResolveEntry(path, record.Original)?.FullName
            };
            var action = options.SnapshotInsteadOfPrompt
                ? StandaloneConflictAction.BackupAndSave
                : options.ResolveConflict?.Invoke(change) ?? throw new StandaloneConflictException(change);
            if (action == StandaloneConflictAction.Cancel)
            {
                result.Cancelled = true;
                return result;
            }

            var snapshot = CreateSnapshot(path, diskSha, "external-change", record);
            result.SnapshotPath = snapshot.Path;
            PersistRecord(path, record); // the external version is safe before anything is written
        }

        if (record == null)
        {
            record = new StandaloneBackupRecord
            {
                SourcePath = path,
                SourcePathKey = KeyForPath(path),
                Snapshots = new List<StandaloneBackupEntry>()
            };
            if (exists)
            {
                var backup = CreateOriginalBackup(path, diskSha);
                record.Original = backup.Entry;
                result.OriginalBackupPath = backup.Path;
                PersistRecord(path, record); // backup must be durable before the first write
            }
        }

        Publish(path, content, contentSha, diskSha);
        result.Written = true;

        record.LastWrittenSha256 = contentSha;
        record.LastWrittenUtc = DateTimeOffset.UtcNow;
        try
        {
            PersistRecord(path, record);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add("文件已写入，但备份元数据更新失败：" + error.Message +
                                "；下次保存会把当前文件当作可能的外部修改。");
        }

        result.OriginalBackupPath ??= record.Original == null
            ? null : ResolveEntry(path, record.Original)?.FullName;
        return result;
    }

    /// <summary>
    /// Replaces the target with the original backup. The current disk version is snapshotted first,
    /// so a restore never destroys the only copy of the current file.
    /// </summary>
    public StandaloneRestoreResult Restore(string path)
    {
        path = Path.GetFullPath(path);
        var record = LoadRecord(path)
                     ?? throw new StandaloneBackupException("没有找到该文件的备份记录，无法恢复。");
        if (record.Original == null)
            throw new StandaloneBackupException("该文件没有原始备份（保存时文件尚不存在），无法恢复。");
        ValidateEntry(path, record.Original, verifyHash: true);

        var originalPath = ResolveEntry(path, record.Original).FullName;
        var bytes = File.ReadAllBytes(originalPath);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(sha, record.Original.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new StandaloneBackupException("备份内容与记录的 SHA256 不一致，已停止恢复。");

        var result = new StandaloneRestoreResult
        {
            Backup = ToInfo(path, record),
            RestoredSha256 = sha
        };
        var exists = File.Exists(path);
        var diskSha = exists ? ComputeFileHash(path) : null;
        if (exists && string.Equals(diskSha, sha, StringComparison.OrdinalIgnoreCase))
        {
            result.Written = false;
            return result;
        }

        if (exists)
        {
            var snapshot = CreateSnapshot(path, diskSha, "before-restore", record);
            result.SnapshotPath = snapshot.Path;
            PersistRecord(path, record); // current version is safe before it is replaced
        }

        Publish(path, bytes, sha, diskSha);
        result.Written = true;

        record.LastWrittenSha256 = sha;
        record.LastWrittenUtc = DateTimeOffset.UtcNow;
        try
        {
            PersistRecord(path, record);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add("文件已恢复，但备份元数据更新失败：" + error.Message);
        }

        result.Backup = ToInfo(path, record) ?? result.Backup;
        return result;
    }

    private StandaloneBackupRecord LoadRecord(string path)
    {
        var metadata = MetadataPath(path);
        if (!File.Exists(metadata)) return null;

        StandaloneBackupRecord record;
        try
        {
            record = JsonSerializer.Deserialize<StandaloneBackupRecord>(File.ReadAllText(metadata), JsonOptions);
        }
        catch (Exception error)
        {
            throw new StandaloneBackupException("备份元数据无法读取：" + metadata + "\n" + error.Message, error);
        }

        if (record == null)
            throw new StandaloneBackupException("备份元数据为空：" + metadata);
        if (!string.Equals(record.SourcePathKey, KeyForPath(path), StringComparison.OrdinalIgnoreCase))
            throw new StandaloneBackupException("备份元数据与目标文件不匹配，已停止操作：" + metadata);
        record.Snapshots ??= new List<StandaloneBackupEntry>();
        return record;
    }

    private StandaloneBackupInfo ToInfo(string path, StandaloneBackupRecord record)
    {
        if (record?.Original == null) return null;
        var original = ResolveEntry(path, record.Original);
        if (original == null || !original.Exists) return null;
        return new StandaloneBackupInfo
        {
            SourcePath = record.SourcePath ?? path,
            BackupDirectory = BackupDirectoryFor(path),
            OriginalFilePath = original.FullName,
            OriginalSha256 = record.Original.Sha256,
            OriginalLength = record.Original.Length,
            CreatedUtc = record.Original.CreatedUtc,
            LastWrittenSha256 = record.LastWrittenSha256,
            LastWrittenUtc = record.LastWrittenUtc,
            SnapshotCount = record.Snapshots.Count
        };
    }

    private (StandaloneBackupEntry Entry, string Path) CreateOriginalBackup(
        string sourcePath, string expectedSha)
    {
        var directory = EnsureBackupDirectory(sourcePath);
        var name = Path.GetFileName(sourcePath);
        var relative = Path.Combine("original", name);
        var candidate = ResolveRelative(path: sourcePath, relative);
        if (File.Exists(candidate))
            relative = Path.Combine("original",
                Path.GetFileNameWithoutExtension(name) + "-" + expectedSha.Substring(0, 8) +
                Path.GetExtension(name));
        candidate = ResolveRelative(path: sourcePath, relative);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(candidate));
            File.Copy(sourcePath, candidate, overwrite: false);
            var copiedSha = ComputeFileHash(candidate);
            if (!string.Equals(copiedSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new StandaloneBackupException("备份校验失败：备份内容与原文件不一致，已停止保存。");
            return (new StandaloneBackupEntry
            {
                Sha256 = expectedSha,
                Length = new FileInfo(candidate).Length,
                CreatedUtc = DateTimeOffset.UtcNow,
                RelativePath = relative,
                Reason = "original"
            }, candidate);
        }
        catch (StandaloneBackupException)
        {
            TryDelete(candidate);
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDelete(candidate);
            throw new StandaloneBackupException("无法创建原始备份：" + candidate + "\n" + error.Message, error);
        }
    }

    private (StandaloneBackupEntry Entry, string Path) CreateSnapshot(
        string sourcePath, string sha, string reason, StandaloneBackupRecord record)
    {
        var directory = EnsureBackupDirectory(sourcePath);
        var name = Path.GetFileName(sourcePath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var relative = Path.Combine("snapshots", stamp + "-" + sha.Substring(0, 12), name);
        var candidate = ResolveRelative(sourcePath, relative);
        var index = 2;
        while (File.Exists(candidate))
        {
            relative = Path.Combine("snapshots", stamp + "-" + sha.Substring(0, 12) + "-" + index, name);
            candidate = ResolveRelative(sourcePath, relative);
            index++;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(candidate));
            File.Copy(sourcePath, candidate, overwrite: false);
            if (!string.Equals(ComputeFileHash(candidate), sha, StringComparison.OrdinalIgnoreCase))
                throw new StandaloneBackupException("快照校验失败：备份内容与当前文件不一致，已停止保存。");
            var entry = new StandaloneBackupEntry
            {
                Sha256 = sha,
                Length = new FileInfo(candidate).Length,
                CreatedUtc = DateTimeOffset.UtcNow,
                RelativePath = relative,
                Reason = reason
            };
            record.Snapshots.Add(entry);
            return (entry, candidate);
        }
        catch (StandaloneBackupException)
        {
            TryDelete(candidate);
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDelete(candidate);
            throw new StandaloneBackupException("无法创建备份快照：" + candidate + "\n" + error.Message, error);
        }
    }

    private void ValidateEntry(string path, StandaloneBackupEntry entry, bool verifyHash)
    {
        var file = ResolveEntry(path, entry);
        if (file == null || !file.Exists)
            throw new StandaloneBackupException(
                "原始备份文件缺失，无法确认可恢复性，已停止保存。\n" + BackupDirectoryFor(path));
        if (file.Length != entry.Length)
            throw new StandaloneBackupException("原始备份大小与记录不一致，已停止保存。\n" + file.FullName);
        if (verifyHash && !string.Equals(ComputeFileHash(file.FullName), entry.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new StandaloneBackupException("原始备份 SHA256 与记录不一致，已停止保存。\n" + file.FullName);
    }

    private static void Publish(string path, byte[] content, string contentSha, string expectedDiskSha)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) throw new StandaloneWriteException("无法确定目标目录：" + path);
        var temporary = Path.Combine(directory,
            "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 10) + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(flushToDisk: true);
            }

            var stagedSha = ComputeFileHash(temporary);
            if (!string.Equals(stagedSha, contentSha, StringComparison.OrdinalIgnoreCase))
                throw new StandaloneWriteException("临时文件校验失败，未替换原文件。");

            if (File.Exists(path))
            {
                if (expectedDiskSha == null ||
                    !string.Equals(ComputeFileHash(path), expectedDiskSha, StringComparison.OrdinalIgnoreCase))
                    throw new StandaloneWriteException("目标文件在保存过程中发生变化，已停止写入。");
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }

            if (!string.Equals(ComputeFileHash(path), contentSha, StringComparison.OrdinalIgnoreCase))
                throw new StandaloneWriteException("写入后校验失败：目标文件内容与新内容不一致。");
        }
        catch (StandaloneFileSafetyException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new StandaloneWriteException("无法写入文件：" + error.Message, error);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void PersistRecord(string path, StandaloneBackupRecord record)
    {
        var directory = EnsureBackupDirectory(path);
        try
        {
            AtomicJsonFile.Write(MetadataPath(path), record);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new StandaloneBackupException("无法写入备份元数据：" + MetadataPath(path) + "\n" + error.Message,
                error);
        }
    }

    private string EnsureBackupDirectory(string sourcePath)
    {
        var directory = BackupDirectoryFor(sourcePath);
        try
        {
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new StandaloneBackupException(
                "无法创建备份目录，已停止保存：\n" + directory + "\n" + error.Message, error);
        }
    }

    private string MetadataPath(string path) => Path.Combine(BackupDirectoryFor(path), "metadata.json");

    private FileInfo ResolveEntry(string path, StandaloneBackupEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.RelativePath)) return null;
        try
        {
            var file = new FileInfo(ResolveRelative(path, entry.RelativePath));
            return file.Exists ? file : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string ResolveRelative(string path, string relative)
    {
        var directory = BackupDirectoryFor(path);
        var full = Path.GetFullPath(Path.Combine(directory, relative));
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new StandaloneBackupException("备份元数据包含非法路径：" + relative);
        return full;
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).Replace('/', '\\').ToLowerInvariant();

    private static DateTimeOffset? SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception) { return null; }
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* the caller reports the primary failure; a stale .tmp is harmless */ }
    }
}
