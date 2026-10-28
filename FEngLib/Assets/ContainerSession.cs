using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FEngLib.Assets;

/// <summary>
/// One extracted FNG block of a container session, tracking its extraction provenance and the
/// write-back ledger state. <see cref="SavedSha256"/> is the frozen hash recorded when the file
/// was last saved; a saved change is pending while it differs from the content the working copy
/// currently holds for that block.
/// </summary>
public sealed class ContainerFngBinding
{
    public string PackageName { get; set; }
    public long BlockOffset { get; set; }
    public uint ChunkId { get; set; }
    public long StoredLength { get; set; }
    public string ExtractedSha256 { get; set; }
    public string ExtractedPath { get; set; }
    public DateTimeOffset ExtractedAt { get; set; }

    /// <summary>File the user saves into; defaults to the extraction output.</summary>
    public string CurrentFile { get; set; }
    public string SavedSha256 { get; set; }
    public DateTimeOffset? SavedAt { get; set; }

    /// <summary>Content of the block in the working copy after the last successful write-back.</summary>
    public string LastWriteBackSha256 { get; set; }

    [JsonIgnore] public string EffectiveFile => CurrentFile ?? ExtractedPath;
    [JsonIgnore] public string WriteBackTargetSha256 => LastWriteBackSha256 ?? ExtractedSha256;
    [JsonIgnore] public bool HasSavedChange => SavedSha256 != null && SavedSha256 != WriteBackTargetSha256;

    public void RecordSaved(string path, string sha256, DateTimeOffset? savedAt = null)
    {
        CurrentFile = path;
        SavedSha256 = sha256;
        SavedAt = savedAt ?? DateTimeOffset.UtcNow;
    }
}

/// <summary>Completed (or recovered) write-back of one container working copy.</summary>
public sealed class ContainerWriteBackRecord
{
    public DateTimeOffset At { get; set; }
    public string WorkingCopySha256Before { get; set; }
    public string WorkingCopySha256After { get; set; }
    public string BackupPath { get; set; }
    public List<string> PackageNames { get; set; } = new();
    public string JournalId { get; set; }

    /// <summary>False when the copy was published but the ledger update failed; recovery completes it.</summary>
    public bool LedgerComplete { get; set; } = true;
}

/// <summary>
/// Persistent association between a read-only original container, its extraction directory and a
/// dedicated working copy. Identity comes from the original path plus the baseline fingerprint —
/// same-named containers in different directories are different sessions.
/// </summary>
public sealed class ContainerSession
{
    public const string SessionFileName = "container.session.json";

    public int SchemaVersion { get; set; } = 1;
    public string ContainerId { get; set; }
    public string OriginalPath { get; set; }
    public long OriginalLength { get; set; }
    public string OriginalSha256 { get; set; }
    public string WorkingCopyPath { get; set; }
    public long WorkingCopyLength { get; set; }
    public bool RebuiltWithBinary { get; set; }
    public string WorkingCopySha256 { get; set; }
    public List<ContainerFngBinding> Bindings { get; set; } = new();
    public List<ContainerWriteBackRecord> History { get; set; } = new();

    [JsonIgnore] public string SessionDirectory { get; private set; }
    [JsonIgnore] public IReadOnlyList<string> SaveRecoveryProblems { get; private set; } = Array.Empty<string>();

    public void RecoverSaves() => SaveRecoveryProblems = ContainerSaveReceipt.Recover(this);

    public static string SessionFilePath(string extractionDirectory)
        => Path.Combine(Path.GetFullPath(extractionDirectory), SessionFileName);

    public static bool ExistsIn(string extractionDirectory)
        => File.Exists(SessionFilePath(extractionDirectory)) || File.Exists(SessionFilePath(extractionDirectory) + ".tmp");

    /// <summary>Loads an existing session; throws when none or when the file is unusable.</summary>
    public static ContainerSession Load(string extractionDirectory)
    {
        var path = SessionFilePath(extractionDirectory);
        AtomicJsonFile.RecoverLegacyTemporary(path);
        var session = JsonSerializer.Deserialize<ContainerSession>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Empty container session file: {path}");
        session.SessionDirectory = Path.GetFullPath(extractionDirectory);
        if (string.IsNullOrWhiteSpace(session.OriginalPath) ||
            string.IsNullOrWhiteSpace(session.OriginalSha256))
            throw new InvalidDataException($"Container session is missing its original container binding: {path}");
        session.RecoverSaves();
        return session;
    }

    /// <summary>
    /// Registers (or merges) a session for a container from the extraction manifests found in
    /// <paramref name="extractionDirectory"/>. Manifests that do not match the container's current
    /// fingerprint are ignored and reported, never silently bound. Extraction manifests themselves
    /// stay untouched; the session ledger is kept separately.
    /// </summary>
    public static ContainerSessionRegistration Register(string originalContainerPath, string extractionDirectory)
    {
        originalContainerPath = Path.GetFullPath(originalContainerPath);
        extractionDirectory = Path.GetFullPath(extractionDirectory);
        var container = FngContainer.Open(originalContainerPath);

        var ignored = new List<string>();
        var manifests = new List<FngExtraction>();
        foreach (var manifestPath in Directory.GetFiles(extractionDirectory, "*.source.json"))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<FngExtraction>(File.ReadAllText(manifestPath));
                if (manifest?.SourceSha256 == null || manifest.OutputPath == null)
                    throw new InvalidDataException("incomplete manifest");
                if (!PathsEqual(manifest.SourcePath, originalContainerPath) ||
                    manifest.SourceLength != container.SourceLength ||
                    !string.Equals(manifest.SourceSha256, container.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("manifest belongs to a different container state");
                var entry = container.Entries.FirstOrDefault(e => e.Offset == manifest.Offset);
                if (entry == null || entry.ChunkId != manifest.ChunkId ||
                    entry.StoredLength != manifest.StoredLength ||
                    entry.Sha256 != manifest.ExtractedSha256 ||
                    entry.Name != manifest.PackageName)
                    throw new InvalidDataException("manifest does not match any current container block");
                manifests.Add(manifest);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or
                NotSupportedException or JsonException)
            {
                ignored.Add($"{Path.GetFileName(manifestPath)}: {error.Message}");
            }
        }

        var session = ExistsIn(extractionDirectory) ? Load(extractionDirectory) : null;
        if (session != null && (!PathsEqual(session.OriginalPath, originalContainerPath) ||
                session.OriginalLength != container.SourceLength ||
                !string.Equals(session.OriginalSha256, container.SourceSha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"A different container session already exists in {extractionDirectory} " +
                $"(bound to {session.OriginalPath}, SHA256 {session.OriginalSha256}).");
        if (session == null)
        {
            session = new ContainerSession
            {
                ContainerId = $"{Path.GetFileNameWithoutExtension(originalContainerPath)}-{container.SourceSha256[..12]}",
                OriginalPath = originalContainerPath,
                OriginalLength = container.SourceLength,
                OriginalSha256 = container.SourceSha256
            };
        }

        var added = 0;
        foreach (var manifest in manifests)
        {
            if (session.Bindings.Any(b => b.BlockOffset == manifest.Offset))
                continue;
            session.Bindings.Add(new ContainerFngBinding
            {
                PackageName = manifest.PackageName,
                BlockOffset = manifest.Offset,
                ChunkId = manifest.ChunkId,
                StoredLength = manifest.StoredLength,
                ExtractedSha256 = manifest.ExtractedSha256,
                ExtractedPath = manifest.OutputPath,
                ExtractedAt = manifest.ExtractedAt
            });
            added++;
        }

        session.SessionDirectory = extractionDirectory;
        session.Bindings.Sort((a, b) => a.BlockOffset.CompareTo(b.BlockOffset));
        session.Save();
        return new ContainerSessionRegistration(session, added, ignored);
    }

    /// <summary>Creates the dedicated working copy from the original; refuses to overwrite.</summary>
    public void CreateWorkingCopy(string targetPath)
    {
        if (WorkingCopyPath != null)
            throw new InvalidOperationException($"This session already has a working copy: {WorkingCopyPath}");
        targetPath = Path.GetFullPath(targetPath ?? throw new ArgumentNullException(nameof(targetPath)));
        if (PathsEqual(targetPath, OriginalPath))
            throw new InvalidOperationException("The working copy must not replace the original container.");
        if (File.Exists(targetPath))
            throw new IOException($"Working copy destination already exists: {targetPath}");
        if (PathsEqual(targetPath, SessionFilePath(SessionDirectory)))
            throw new InvalidOperationException("The working copy path collides with the session file.");

        File.Copy(OriginalPath, targetPath, overwrite: false);
        string copied;
        try
        {
            copied = ContainerFileHash.OfFile(targetPath);
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }
        if (copied != OriginalSha256)
        {
            TryDelete(targetPath);
            throw new InvalidDataException(
                "Working copy verification failed: the copy does not match the original container. " +
                "The incomplete copy was deleted.");
        }

        WorkingCopyPath = targetPath;
        WorkingCopyLength = OriginalLength;
        WorkingCopySha256 = copied;
        Save();
    }

    /// <summary>
    /// Finds the binding a saved file belongs to. Matching the extraction output as well as the
    /// current file means saving over any recorded version of the block makes that save the
    /// version pending for write-back; the plan always names the exact file and hash.
    /// </summary>
    public ContainerFngBinding FindByFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return null; }
        return Bindings.FirstOrDefault(b =>
            PathsEqual(b.CurrentFile, full) || PathsEqual(b.ExtractedPath, full));
    }

    public void Save()
    {
        if (SessionDirectory == null)
            throw new InvalidOperationException("Session was not loaded or registered through a directory.");
        var path = SessionFilePath(SessionDirectory);
        AtomicJsonFile.Write(path, this);
    }

    internal void CompleteWriteBack(WriteBackJournalEntry entry)
    {
        var previousHash = WorkingCopySha256;
        var previousLength = WorkingCopyLength;
        var previousRebuilt = RebuiltWithBinary;
        var previous = Bindings.Select(b => b.LastWriteBackSha256).ToArray();
        var historyCount = History.Count;
        try
        {
            foreach (var item in entry.Items)
            {
                var binding = Bindings.SingleOrDefault(b => b.BlockOffset == item.BlockOffset)
                    ?? throw new InvalidDataException($"日志指向未绑定块 0x{item.BlockOffset:X}。");
                binding.LastWriteBackSha256 = item.ReplacementSha256;
            }
            WorkingCopySha256 = entry.WorkingCopySha256Result;
            WorkingCopyLength = new FileInfo(WorkingCopyPath).Length;
            RebuiltWithBinary |= entry.RebuiltWithBinary;
            if (!History.Any(h => h.JournalId == entry.Id))
                History.Add(new ContainerWriteBackRecord
                {
                    At = DateTimeOffset.UtcNow,
                    WorkingCopySha256Before = entry.WorkingCopySha256Before,
                    WorkingCopySha256After = entry.WorkingCopySha256Result,
                    BackupPath = entry.BackupPath,
                    PackageNames = entry.Items.Select(i => i.PackageName).ToList(),
                    JournalId = entry.Id
                });
            Save();
        }
        catch
        {
            WorkingCopySha256 = previousHash;
            WorkingCopyLength = previousLength;
            RebuiltWithBinary = previousRebuilt;
            for (var i = 0; i < Bindings.Count; i++) Bindings[i].LastWriteBackSha256 = previous[i];
            if (History.Count > historyCount) History.RemoveRange(historyCount, History.Count - historyCount);
            throw;
        }
    }

    public static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* verification already failed; keep the evidence */ }
    }
}

public sealed class ContainerSessionRegistration
{
    internal ContainerSessionRegistration(ContainerSession session, int addedBindings, List<string> ignoredManifests)
    {
        Session = session;
        AddedBindings = addedBindings;
        IgnoredManifests = ignoredManifests.AsReadOnly();
    }

    public ContainerSession Session { get; }
    public int AddedBindings { get; }
    public IReadOnlyList<string> IgnoredManifests { get; }
}

/// <summary>SHA256 helper shared by the session, the planner, the executor and the CLI.</summary>
public static class ContainerFileHash
{
    public static string OfFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return FngContainer.Hash(stream);
    }
}
