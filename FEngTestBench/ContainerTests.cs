using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using FEngLib;
using FEngLib.Assets;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngLib.Structures;

namespace FEngTestBench;

/// <summary>Generated fixtures only; no copyrighted game data is required.</summary>
internal static class ContainerTests
{
    public static int Run(string outputDirectory)
    {
        var root = Path.Combine(Path.GetFullPath(outputDirectory), "container-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        void Reject<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new Exception(message);
        }
        string Save(string name, byte[] bytes)
        {
            var path = Path.Combine(root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        try
        {
            var fng = MakeFng("Duplicate.fng");
            // A signature inside another resource must not be treated as a top-level FNG.
            var unknown = Block(0x12345678, fng);
            var data = unknown.Concat(fng).Concat(fng).Concat(Block(0, new byte[4])).ToArray();
            var source = Save("fixture.bun", data);
            var container = FngContainer.Open(source);
            Check(container.BlockCount == 4 && container.Entries.Count == 2, "Wrong top-level block traversal.");
            Check(container.Entries.All(e => e.CanExtract && e.Name == "Duplicate.fng"), "FNG headers not recognized.");
            Check(container.Entries[0].Offset == unknown.Length, "Offset must include the outer header.");
            var extracted = container.Extract(container.Entries, Path.Combine(root, "extracted"));
            Check(extracted.Count == 2 && extracted[0].OutputPath != extracted[1].OutputPath, "Duplicate names collide.");
            foreach (var result in extracted)
            {
                Check(File.ReadAllBytes(result.OutputPath).SequenceEqual(fng), "Extraction changed bytes.");
                var manifest = JsonSerializer.Deserialize<FngExtraction>(File.ReadAllText(result.OutputPath + ".source.json"));
                Check(manifest.SourcePath == source && manifest.SourceSha256 == container.SourceSha256 &&
                    manifest.Offset == result.Offset && manifest.ExtractedSha256 == result.ExtractedSha256,
                    "Missing extraction provenance.");
                using var payload = new MemoryStream(File.ReadAllBytes(result.OutputPath).Skip(16).ToArray());
                using var reader = new BinaryReader(payload);
                Check(new FrontendPackageLoader(new HashResolver()).Load(reader).Name == "Duplicate.fng",
                    "Extracted FNG cannot be read by the existing model loader.");
            }
            Reject<IOException>(() => container.Extract(container.Entries, Path.Combine(root, "extracted")), "Overwrote existing outputs.");
            Check(File.ReadAllBytes(source).SequenceEqual(data), "Extraction modified source.");

            var firstPath = extracted[0].OutputPath;
            var secondPath = extracted[1].OutputPath;
            var edited = Encoding.ASCII.GetBytes("user-edited FNG working file");
            File.WriteAllBytes(firstPath, edited);
            File.WriteAllText(firstPath + ".source.json", "existing user metadata");
            var skipAll = container.Extract(container.Entries, Path.Combine(root, "extracted"), ExistingFilePolicy.Skip);
            Check(skipAll.Extracted.Count == 0 && skipAll.SkippedPaths.Count == 2, "All-existing skip did not complete successfully.");
            Check(File.ReadAllBytes(firstPath).SequenceEqual(edited) &&
                File.ReadAllText(firstPath + ".source.json") == "existing user metadata", "Skip changed existing working files.");
            File.Delete(secondPath);
            File.Delete(secondPath + ".source.json");
            var mixed = container.Extract(container.Entries, Path.Combine(root, "extracted"), ExistingFilePolicy.Skip);
            Check(mixed.Extracted.Count == 1 && mixed.SkippedPaths.Count == 1 && mixed.OverwrittenCount == 0,
                "Mixed skip blocked extraction of missing entries.");
            Check(File.ReadAllBytes(firstPath).SequenceEqual(edited) && File.ReadAllBytes(secondPath).SequenceEqual(fng),
                "Mixed skip did not preserve edits and extract missing entries.");
            var overwrite = container.Extract(container.Entries, Path.Combine(root, "extracted"), ExistingFilePolicy.Overwrite);
            Check(overwrite.Extracted.Count == 2 && overwrite.SkippedPaths.Count == 0 && overwrite.OverwrittenCount == 2,
                "Overwrite counts are incorrect.");
            Check(File.ReadAllBytes(firstPath).SequenceEqual(fng) &&
                JsonSerializer.Deserialize<FngExtraction>(File.ReadAllText(firstPath + ".source.json")).ExtractedSha256 == container.Entries[0].Sha256,
                "Overwrite did not replace both FNG and provenance.");

            File.Delete(firstPath);
            var manifestOnly = container.Extract(new[] { container.Entries[0] }, Path.GetDirectoryName(firstPath), ExistingFilePolicy.Skip);
            Check(manifestOnly.SkippedPaths.Count == 1 && !File.Exists(firstPath), "Skip created an FNG beside an existing orphan manifest.");
            container.Extract(new[] { container.Entries[0] }, Path.GetDirectoryName(firstPath), ExistingFilePolicy.Overwrite);
            Check(File.ReadAllBytes(firstPath).SequenceEqual(fng), "Overwrite failed to repair a manifest-only pair.");
            File.Delete(firstPath + ".source.json");
            var fngOnly = container.Extract(new[] { container.Entries[0] }, Path.GetDirectoryName(firstPath), ExistingFilePolicy.Skip);
            Check(fngOnly.SkippedPaths.Count == 1 && !File.Exists(firstPath + ".source.json"), "Skip invented provenance for an existing FNG.");
            container.Extract(new[] { container.Entries[0] }, Path.GetDirectoryName(firstPath), ExistingFilePolicy.Overwrite);

            // A failure late in publication must restore earlier overwritten pairs and remove new files.
            File.WriteAllBytes(firstPath, edited);
            File.WriteAllText(firstPath + ".source.json", "restore this metadata");
            using (var locked = new FileStream(secondPath + ".source.json", FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => container.Extract(container.Entries, Path.GetDirectoryName(firstPath), ExistingFilePolicy.Overwrite),
                    "Overwrite unexpectedly replaced a locked manifest.");
            Check(File.ReadAllBytes(firstPath).SequenceEqual(edited) && File.ReadAllText(firstPath + ".source.json") == "restore this metadata",
                "Failed overwrite destroyed earlier edited files.");
            Check(File.ReadAllBytes(secondPath).SequenceEqual(fng), "Failed overwrite did not restore the later FNG.");
            Check(!Directory.EnumerateDirectories(Path.GetDirectoryName(firstPath), ".fng-extract-*").Any(), "Successful rollback left staging files.");
            File.Delete(firstPath);
            File.Delete(firstPath + ".source.json");
            using (var locked = new FileStream(secondPath + ".source.json", FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => container.Extract(container.Entries, Path.GetDirectoryName(firstPath), ExistingFilePolicy.Overwrite),
                    "New-file rollback fixture did not fail at the locked manifest.");
            Check(!File.Exists(firstPath) && !File.Exists(firstPath + ".source.json"), "Failed batch left new files published.");

            var directoryCollision = Path.Combine(root, "directory-collision");
            Directory.CreateDirectory(Path.Combine(directoryCollision, Path.GetFileName(firstPath)));
            foreach (var policy in new[] { ExistingFilePolicy.Skip, ExistingFilePolicy.Overwrite })
                Reject<IOException>(() => container.Extract(container.Entries, directoryCollision, policy), "Treated a directory as an existing file.");
            Reject<ArgumentOutOfRangeException>(() => container.Extract(container.Entries, root, (ExistingFilePolicy)99), "Accepted invalid collision policy.");
            var selfSource = Save("00000000_Duplicate.fng", fng);
            var selfContainer = FngContainer.Open(selfSource);
            Reject<IOException>(() => selfContainer.Extract(selfContainer.Entries, root, ExistingFilePolicy.Overwrite), "Overwrote source container during extraction.");
            Check(File.ReadAllBytes(selfSource).SequenceEqual(fng), "Source protection failed.");
            Reject<InvalidOperationException>(() => container.Extract(new[] { container.Entries[0], container.Entries[0] }, root), "Accepted duplicate selection.");
            var other = FngContainer.Open(source);
            Reject<InvalidOperationException>(() => container.Extract(other.Entries, root), "Accepted foreign entries.");
            data[data.Length - 1] ^= 1;
            File.WriteAllBytes(source, data);
            Reject<InvalidDataException>(() => container.Extract(container.Entries, Path.Combine(root, "changed")), "Accepted changed source with same size.");
            Reject<InvalidDataException>(() => container.Extract(container.Entries, Path.Combine(root, "extracted"), ExistingFilePolicy.Skip), "Skip bypassed source fingerprint validation.");
            Check(!Directory.Exists(Path.Combine(root, "changed")), "Changed source created output directory.");

            var compressedPayload = new byte[20];
            Encoding.ASCII.GetBytes("JDLZ").CopyTo(compressedPayload, 4);
            var compressed = FngContainer.Open(Save("compressed.bun", Block(FngContainer.CompressedFngChunkId, compressedPayload)));
            Check(compressed.Entries.Single().Storage == FngStorageKind.Compressed &&
                compressed.Entries[0].Diagnostic.Contains("JDLZ"), "Compressed FNG was hidden or mislabeled.");
            Reject<InvalidOperationException>(() => compressed.Extract(compressed.Entries, root), "Extracted compressed bytes as a regular FNG.");
            var nested = FngContainer.Open(Save("nested.bun", Block(FngContainer.FngChunkId,
                Block(FngContainer.CompressedFngChunkId, compressedPayload))));
            Check(nested.Entries.Single().Storage == FngStorageKind.Compressed &&
                nested.Entries[0].Diagnostic.Contains("JDLZ"), "Nested compressed wrapper was not detected.");
            var invalid = FngContainer.Open(Save("invalid-fng.bun", Block(FngContainer.FngChunkId, new byte[40])));
            Check(invalid.Entries.Single().Storage == FngStorageKind.Invalid, "Malformed FNG was accepted.");
            Check(FngContainer.Open(Save("empty.bun", Array.Empty<byte>())).BlockCount == 0, "Empty container failed.");
            Reject<InvalidDataException>(() => FngContainer.Open(Save("short-header.bun", new byte[7])), "Accepted truncated header.");
            Reject<InvalidDataException>(() => FngContainer.Open(Save("short-payload.bun", fng.Take(fng.Length - 1).ToArray())), "Accepted truncated payload.");
            Reject<InvalidDataException>(() => FngContainer.Open(Save("huge-length.bun", new byte[] { 3, 2, 3, 0, 255, 255, 255, 255 })), "Accepted out-of-bounds unsigned length.");
            Reject<NotSupportedException>(() => FngContainer.Open(Save("whole-compressed.lzc", Encoding.ASCII.GetBytes("RAWW123456789012"))), "Misread whole-file compression as chunks.");
            var traversal = FngContainer.Open(Save("unsafe-name.bun", MakeFng("../unsafe.fng")));
            var safeDirectory = Path.Combine(root, "safe-names");
            var safe = traversal.Extract(traversal.Entries, safeDirectory).Single();
            Check(Path.GetDirectoryName(safe.OutputPath) == safeDirectory, "FNG name escaped output folder.");

            RunWriteBackChecks(root, Save, Check);

            Console.WriteLine($"PASS: {checks} container checks. Fixtures: {root}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL after {checks} checks: {error}\nFixtures: {root}");
            return 1;
        }
    }

    private static byte[] MakeFng(string name)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        new FrontendChunkWriter(new Package { Name = name, Filename = "fixture" }).Write(writer);
        return Block(FngContainer.FngChunkId, stream.ToArray());
    }

    /// <summary>Fixture with one Group so an equal-length float edit produces exactly four changed bytes.</summary>
    private static byte[] MakeFngWithObject(string name, float positionX)
    {
        var package = new Package { Name = name, Filename = "fixture" };
        package.Objects.Add(new Group(new ObjectData
        {
            Color = new Color4(255, 255, 255, 255),
            Pivot = new Vector3(1, 2, 3),
            Position = new Vector3(positionX, 5, 7),
            Rotation = Quaternion.Identity,
            Size = new Vector3(10, 20, 30)
        })
        {
            Guid = 0xABCD1234,
            Name = "probe",
            NameHash = 0x5678
        });
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        new FrontendChunkWriter(package).Write(writer);
        return Block(FngContainer.FngChunkId, stream.ToArray());
    }

    /// <summary>Nikki-style padding: marker + flags + 0x20 label + 0x20 watermark, then zeros.</summary>
    private static byte[] MakeNikkiPadding(int tailBytes)
    {
        var payload = new byte[ContainerBlockIndex.NikkiPaddingMetadataLength + tailBytes];
        BitConverter.GetBytes(ContainerBlockIndex.NikkiPaddingMarker).CopyTo(payload, 0);
        BitConverter.GetBytes(0).CopyTo(payload, 4);
        Encoding.ASCII.GetBytes("Padding Block").CopyTo(payload, 8);
        Encoding.ASCII.GetBytes("FEngLib-TestBench").CopyTo(payload, 40);
        return Block(0, payload);
    }

    private static void RunWriteBackChecks(string root, Func<string, byte[], string> save,
        Action<bool, string> check)
    {
        void RejectPlan(ContainerWritePlan plan, string needle, string message)
        {
            check(!plan.IsExecutable, message + " (plan unexpectedly executable)");
            check(plan.Blockers.Concat(plan.Items.SelectMany(i => i.Blockers))
                    .Any(b => b.Contains(needle, StringComparison.Ordinal)),
                message + $" (no blocker mentioning '{needle}'): " +
                string.Join(" | ", plan.Blockers.Concat(plan.Items.SelectMany(i => i.Blockers))));
        }

        var fngA = MakeFngWithObject("Alpha.fng", 1f);
        var fngB = MakeFng("Beta.fng");
        var containerBytes = Block(0x12345678, new byte[37])
            .Concat(fngA)
            .Concat(MakeNikkiPadding(4))
            .Concat(fngB)
            .Concat(Block(0, new byte[12]))
            .ToArray();
        var containerPath = save("writeback.bun", containerBytes);
        var container = FngContainer.Open(containerPath);
        check(container.Entries.Count == 2, "Write-back fixture FNG blocks not recognized.");

        var baseline = ContainerBlockIndex.Scan(containerPath);
        check(baseline.Blocks.Count == 5 && baseline.FngCount == 2, "Full-block index traversal wrong.");
        check(baseline.Blocks[0].Kind == ContainerBlockKind.Unknown, "Unknown block misclassified.");
        check(baseline.Blocks[2].Kind == ContainerBlockKind.Padding && baseline.Blocks[2].NikkiPadding &&
              baseline.Blocks[2].PaddingWatermark == "FEngLib-TestBench", "Nikki padding metadata not parsed.");
        check(baseline.Blocks[4].Kind == ContainerBlockKind.Padding && !baseline.Blocks[4].NikkiPadding,
            "Zero padding misclassified as Nikki padding.");
        check(baseline.Blocks[1].Sha256 != baseline.Blocks[3].Sha256, "Block hashes not distinct.");

        // The edited fixture must survive the real model loader and expose the float edit.
        var editedBytes = MakeFngWithObject("Alpha.fng", 42f);
        check(editedBytes.Length == fngA.Length, "Equal-length edit changed total length.");
        using (var payload = new MemoryStream(editedBytes.Skip(16).ToArray()))
        using (var reader = new BinaryReader(payload))
        {
            var loaded = new FrontendPackageLoader(new HashResolver()).Load(reader);
            check(loaded.Name == "Alpha.fng" && loaded.Objects.Count == 1 &&
                  Math.Abs(loaded.Objects[0].Data.Position.X - 42f) < 0.001f,
                "Edited fixture not loadable with the model loader.");
        }
        check(editedBytes.Zip(fngA, (a, b) => a != b).Count(different => different) is > 0 and <= 4,
            "Float edit should only change position bytes.");

        var extractDir = Path.Combine(root, "wb-extracted");
        var extracted = container.Extract(container.Entries, extractDir);
        check(extracted.Count == 2, "Write-back fixture extraction failed.");
        var alphaPath = extracted.Single(e => e.PackageName == "Alpha.fng").OutputPath;
        var alphaOffset = extracted.Single(e => e.PackageName == "Alpha.fng").Offset;

        var registration = ContainerSession.Register(containerPath, extractDir);
        var session = registration.Session;
        check(session.Bindings.Count == 2, "Bindings were not registered from manifests.");
        var workPath = Path.Combine(root, "writeback.work.bun");
        session.CreateWorkingCopy(workPath);
        check(File.ReadAllBytes(workPath).SequenceEqual(containerBytes), "Working copy differs from original.");

        var idlePlan = ContainerWritePlanner.Build(session);
        check(!idlePlan.HasPendingChanges && !idlePlan.IsExecutable && idlePlan.Blockers.Count == 0,
            "No pending changes must be reported as notes, not blockers.");

        var emptyDir = Path.Combine(root, "wb-extracted-empty");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllText(Path.Combine(emptyDir, "orphan.source.json"), "{}");
        var orphanRegistration = ContainerSession.Register(containerPath, emptyDir);
        check(orphanRegistration.AddedBindings == 0 && orphanRegistration.IgnoredManifests.Count == 1,
            "Orphan manifest was not ignored at registration.");
        var noCopyPlan = ContainerWritePlanner.Build(orphanRegistration.Session);
        check(!noCopyPlan.IsExecutable && noCopyPlan.Blockers.Any(b => b.Contains("工作副本")),
            "Missing working copy must block planning.");

        // ---- first equal-length write-back ----
        File.WriteAllBytes(alphaPath, editedBytes);
        var alphaBinding = session.FindByFile(alphaPath);
        check(alphaBinding != null, "Saved file did not match any binding.");
        alphaBinding.RecordSaved(alphaPath, ContainerFileHash.OfFile(alphaPath));
        check(alphaBinding.HasSavedChange, "Saved change is not pending.");

        var plan1 = ContainerWritePlanner.Build(session);
        check(plan1.IsExecutable && plan1.Items.Count == 1, "Plan with one equal-length edit must be executable.");
        check(plan1.Items[0].BlockOffset == alphaOffset && plan1.Items[0].StoredLength == fngA.Length &&
              plan1.Items[0].ReplacementStoredLength == fngA.Length, "Planned item identity is wrong.");
        check(!File.Exists(plan1.BackupPath), "Planner must not create the backup file.");

        var report = ContainerWriteBackExecutor.Execute(session, plan1);
        check(report.Success && report.Items.Count == 1 && report.VerifiedBlockCount == 5,
            "Write-back report is incomplete.");
        check(report.Sha256After != report.Sha256Before, "Write-back did not change the copy.");

        var after = ContainerBlockIndex.Scan(workPath);
        check(after.Blocks.Count == baseline.Blocks.Count &&
              after.Blocks.Zip(baseline.Blocks).All(p =>
                  p.First.Offset == p.Second.Offset && p.First.ChunkId == p.Second.ChunkId &&
                  p.First.PayloadLength == p.Second.PayloadLength),
            "Block layout changed during equal-length write-back.");
        check(after.Blocks.Where(b => b.Offset != alphaOffset)
                .Zip(baseline.Blocks.Where(b => b.Offset != alphaOffset))
                .All(p => p.First.Sha256 == p.Second.Sha256),
            "Non-target blocks changed.");
        check(after.Blocks.Single(b => b.Offset == alphaOffset).Sha256 == report.Items[0].ReplacementSha256,
            "Target block does not hold the replacement.");
        check(after.Blocks.Single(b => b.Offset == alphaOffset).PackageName == "Alpha.fng" &&
              after.Blocks.Single(b => b.Offset == alphaOffset).FngStorage == FngStorageKind.Uncompressed,
            "Replacement block is not a valid uncompressed FNG.");

        var workContainer = FngContainer.Open(workPath);
        var reExtracted = workContainer.Extract(new[] { workContainer.Entries[0] },
            Path.Combine(root, "wb-reextract"));
        check(File.ReadAllBytes(reExtracted[0].OutputPath).SequenceEqual(File.ReadAllBytes(alphaPath)),
            "Re-extracted target differs from the replacement file.");
        check(alphaBinding.LastWriteBackSha256 == report.Items[0].ReplacementSha256 &&
              !alphaBinding.HasSavedChange, "Ledger did not record the write-back.");
        check(session.WorkingCopySha256 == report.Sha256After, "Session copy fingerprint not updated.");
        check(File.Exists(report.BackupPath) && ContainerFileHash.OfFile(report.BackupPath) == report.Sha256Before,
            "Backup missing or does not hold the pre-write-back content.");
        check(!Directory.GetFiles(Path.GetDirectoryName(workPath), ".fng-writeback-*").Any(),
            "Staged candidate left behind.");
        check(WriteBackJournal.Load(session.SessionDirectory).Single(e => e.Id == report.JournalId).Status
            == WriteBackJournal.StatusCompleted, "Journal entry not completed.");

        // ---- second consecutive write-back on the already-modified copy ----
        File.WriteAllBytes(alphaPath, MakeFngWithObject("Alpha.fng", 84f));
        alphaBinding.RecordSaved(alphaPath, ContainerFileHash.OfFile(alphaPath));
        var plan2 = ContainerWritePlanner.Build(session);
        check(plan2.IsExecutable && plan2.Items[0].CurrentBlockSha256 == report.Items[0].ReplacementSha256,
            "Second plan must verify against the last written content.");
        var report2 = ContainerWriteBackExecutor.Execute(session, plan2);
        check(report2.Success && report2.Sha256Before == report.Sha256After,
            "Second write-back did not chain from the first result.");

        // ---- blocked plans leave everything untouched ----
        var currentWorkBytes = File.ReadAllBytes(workPath);
        var currentWorkSha = session.WorkingCopySha256;

        File.WriteAllBytes(alphaPath, MakeFng("Alpha.fng"));
        alphaBinding.RecordSaved(alphaPath, ContainerFileHash.OfFile(alphaPath));
        var variablePlan = ContainerWritePlanner.Build(session);
        check(variablePlan.RebuildWithBinary, "Different length must route through Binary rebuilding.");
        if (BinaryRuntime.DirectoryPath == null)
            RejectPlan(variablePlan, "配置 Binary", "A rebuild needs an explicitly configured Binary runtime.");
        else
            check(variablePlan.IsExecutable, "A valid variable-size FNG must be accepted by the rebuild planner.");
        File.WriteAllBytes(alphaPath, MakeFngWithObject("Alpha.fng", 84f));

        File.WriteAllBytes(alphaPath, MakeFngWithObject("Other.fng", 84f));
        alphaBinding.RecordSaved(alphaPath, ContainerFileHash.OfFile(alphaPath));
        RejectPlan(ContainerWritePlanner.Build(session), "包名不一致", "Package name mismatch must block.");

        var corrupted = File.ReadAllBytes(alphaPath);
        corrupted[16] ^= 0xFF;
        File.WriteAllBytes(alphaPath, corrupted);
        alphaBinding.RecordSaved(alphaPath, ContainerFileHash.OfFile(alphaPath));
        RejectPlan(ContainerWritePlanner.Build(session), "不是未压缩 FNG", "Corrupt replacement must block.");

        var stale = File.ReadAllBytes(alphaPath);
        alphaBinding.RecordSaved(alphaPath, ContainerFileHash.OfFile(alphaPath));
        stale[^5] ^= 0x01;
        File.WriteAllBytes(alphaPath, stale);
        RejectPlan(ContainerWritePlanner.Build(session), "与保存时记录的摘要不一致", "Post-save modification must block.");

        File.Delete(alphaPath);
        RejectPlan(ContainerWritePlanner.Build(session), "替换文件不可读", "Missing replacement must block.");
        File.WriteAllBytes(alphaPath, stale);
        alphaBinding.RecordSaved(alphaPath, ContainerFileHash.OfFile(alphaPath));

        var betaBinding = session.Bindings.Single(b => b.PackageName == "Beta.fng");
        betaBinding.CurrentFile = workPath;
        betaBinding.SavedSha256 = ContainerFileHash.OfFile(workPath);
        betaBinding.SavedAt = DateTimeOffset.UtcNow;
        RejectPlan(ContainerWritePlanner.Build(session), "替换文件路径与容器文件相同",
            "Replacement pointing at the container must block.");
        betaBinding.CurrentFile = null;
        betaBinding.SavedSha256 = null;
        betaBinding.SavedAt = null;

        using (var append = new FileStream(workPath, FileMode.Append, FileAccess.Write, FileShare.None))
            append.WriteByte(0x00);
        RejectPlan(ContainerWritePlanner.Build(session), "工作副本",
            "Externally changed working copy must block.");
        File.WriteAllBytes(workPath, currentWorkBytes);
        check(ContainerFileHash.OfFile(workPath) == currentWorkSha, "Restored working copy hash mismatch.");

        var movedContainer = containerPath + ".moved";
        File.Move(containerPath, movedContainer);
        RejectPlan(ContainerWritePlanner.Build(session), "原始容器", "Missing original container must block.");
        File.Move(movedContainer, containerPath);

        // locked working copy fails before anything is written or journaled
        var journalCountBefore = WriteBackJournal.Load(session.SessionDirectory).Count;
        using (var locked = new FileStream(workPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var blocked = false;
            try { ContainerWriteBackExecutor.Execute(session, plan2); }
            catch (IOException) { blocked = true; }
            check(blocked, "Locked working copy must fail the transaction.");
        }
        check(WriteBackJournal.Load(session.SessionDirectory).Count == journalCountBefore,
            "A failed transaction must not leave journal entries.");
        check(File.ReadAllBytes(workPath).SequenceEqual(currentWorkBytes), "Failed transaction changed the copy.");

        // ---- journal recovery ----
        File.WriteAllBytes(alphaPath, MakeFngWithObject("Alpha.fng", 126f));
        alphaBinding.RecordSaved(alphaPath, ContainerFileHash.OfFile(alphaPath));
        var crashBefore = session.WorkingCopySha256;
        var crashSha = ContainerFileHash.OfFile(alphaPath);
        WriteBackJournal.Save(session.SessionDirectory, new List<WriteBackJournalEntry>
        {
            new()
            {
                Id = "crashpre", StartedAt = DateTimeOffset.UtcNow, WorkingCopyPath = workPath,
                BackupPath = Path.Combine(root, "crash.bak"), WorkingCopySha256Before = crashBefore,
                Items = { new WriteBackJournalItem { PackageName = "Alpha.fng", BlockOffset = alphaOffset,
                    ReplacementSha256 = crashSha } },
                Status = WriteBackJournal.StatusPrepared
            }
        });
        var recoveredPlan = ContainerWritePlanner.Build(session);
        check(WriteBackJournal.Load(session.SessionDirectory).Single(e => e.Id == "crashpre").Status
            == WriteBackJournal.StatusFailed, "Interrupted-before-publish entry was not closed.");
        check(recoveredPlan.IsExecutable, "Recovery did not clear the way for planning.");

        // simulate a crash between publication and ledger update
        var plan3 = ContainerWritePlanner.Build(session);
        check(plan3.IsExecutable, "Recovered plan must be executable.");
        var report3 = ContainerWriteBackExecutor.Execute(session, plan3);
        check(report3.Success, "Post-recovery write-back failed.");
        var ledgerBeforeSha = session.WorkingCopySha256;
        var ledgerBeforeBinding = alphaBinding.LastWriteBackSha256;
        var historyCount = session.History.Count;
        alphaBinding.LastWriteBackSha256 = null;
        session.WorkingCopySha256 = report3.Sha256Before;
        session.History.RemoveAt(session.History.Count - 1);
        session.Save();
        WriteBackJournal.Save(session.SessionDirectory, new List<WriteBackJournalEntry>
        {
            new()
            {
                Id = "crashpost", StartedAt = DateTimeOffset.UtcNow, WorkingCopyPath = workPath,
                BackupPath = report3.BackupPath, WorkingCopySha256Before = report3.Sha256Before,
                WorkingCopySha256Result = report3.Sha256After,
                Items = { new WriteBackJournalItem { PackageName = "Alpha.fng", BlockOffset = alphaOffset,
                    ReplacementSha256 = report3.Items[0].ReplacementSha256 } },
                Status = WriteBackJournal.StatusPublished
            }
        });
        var reconciled = ContainerWritePlanner.Build(session);
        check(alphaBinding.LastWriteBackSha256 == ledgerBeforeBinding &&
              session.WorkingCopySha256 == ledgerBeforeSha && session.History.Count == historyCount,
            "Recovery did not complete the published write-back.");
        check(WriteBackJournal.Load(session.SessionDirectory).Single(e => e.Id == "crashpost").Status
            == WriteBackJournal.StatusCompleted, "Recovered journal entry not completed.");
        check(!reconciled.Blockers.Any(), "Reconciled plan must not carry blockers.");

        // unreconcilable journal state blocks planning until the journal is cleared
        alphaBinding.RecordSaved(alphaPath, ContainerFileHash.OfFile(alphaPath));
        WriteBackJournal.Save(session.SessionDirectory, new List<WriteBackJournalEntry>
        {
            new()
            {
                Id = "bogus", StartedAt = DateTimeOffset.UtcNow, WorkingCopyPath = workPath,
                BackupPath = Path.Combine(root, "bogus.bak"),
                WorkingCopySha256Before = "F".PadRight(64, '0'),
                WorkingCopySha256Result = "A".PadRight(64, '0'),
                Items = { new WriteBackJournalItem { PackageName = "Alpha.fng", BlockOffset = alphaOffset,
                    ReplacementSha256 = alphaBinding.SavedSha256 } },
                Status = WriteBackJournal.StatusPrepared
            }
        });
        RejectPlan(ContainerWritePlanner.Build(session), "需要人工核对", "Unreconcilable journal must block.");
        File.Delete(WriteBackJournal.JournalFilePath(session.SessionDirectory));
        var finalPlan = ContainerWritePlanner.Build(session);
        check(!finalPlan.Blockers.Any(), "Clearing the journal did not unblock planning.");
        RunWriteBackFailureChecks(root, check);
    }

    private static void RunWriteBackFailureChecks(string root, Action<bool, string> check)
    {
        var original = MakeFngWithObject("Faults.fng", 1f);
        var edited = MakeFngWithObject("Faults.fng", 42f);
        (ContainerSession Session, string File) Setup(string name)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "source.bun");
            File.WriteAllBytes(source, original);
            var container = FngContainer.Open(source);
            var extractDirectory = Path.Combine(directory, "extracted");
            var file = container.Extract(container.Entries, extractDirectory).Single().OutputPath;
            var session = ContainerSession.Register(source, extractDirectory).Session;
            session.CreateWorkingCopy(Path.Combine(directory, "copy.bun"));
            return (session, file);
        }
        void Record(ContainerSession session, string file, byte[] bytes)
        {
            File.WriteAllBytes(file, bytes);
            session.FindByFile(file).RecordSaved(file, ContainerFileHash.OfFile(file));
            session.Save();
        }

        var (invalid, invalidFile) = Setup("invalid-inner-model");
        var nextChunk = 24 + BitConverter.ToInt32(edited, 20);
        foreach (var corruption in new[] { "unknown-type", "negative-length", "out-of-bounds", "trailing-data" })
        {
            var bytes = (byte[])edited.Clone();
            switch (corruption)
            {
                case "unknown-type": BitConverter.GetBytes(0xDEADBEEFu).CopyTo(bytes, nextChunk); break;
                case "negative-length": BitConverter.GetBytes(-1).CopyTo(bytes, nextChunk + 4); break;
                case "out-of-bounds": BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, nextChunk + 4); break;
                case "trailing-data": BitConverter.GetBytes(bytes.Length - 20).CopyTo(bytes, 12); break;
            }
            Record(invalid, invalidFile, bytes);
            var plan = ContainerWritePlanner.Build(invalid);
            check(plan.IsBlocked && !plan.IsExecutable, "Invalid internal model accepted: " + corruption);
            check(File.ReadAllBytes(invalid.WorkingCopyPath).SequenceEqual(original), "Preflight changed the target.");
        }

        var (race, raceFile) = Setup("publish-race");
        Record(race, raceFile, edited);
        var external = (byte[])original.Clone();
        external[^1] ^= 1;
        ContainerWriteBackException failure = null;
        try
        {
            ContainerWriteBackExecutor.Execute(race, ContainerWritePlanner.Build(race), step =>
            {
                if (step == "发布新副本") File.WriteAllBytes(race.WorkingCopyPath, external);
            });
        }
        catch (ContainerWriteBackException error) { failure = error; }
        check(failure != null && !failure.Report.Published, "External modification was not blocked before publication.");
        check(File.ReadAllBytes(race.WorkingCopyPath).SequenceEqual(external), "External version was overwritten.");
        check(failure.Report.BackupPath != null && File.Exists(failure.Report.BackupPath), "Failure lost backup evidence.");
        check(race.Bindings.Single().HasSavedChange, "Rejected publication cleared pending state.");

        var (post, postFile) = Setup("post-publish-lock");
        Record(post, postFile, edited);
        FileStream publishedLock = null;
        failure = null;
        try
        {
            ContainerWriteBackExecutor.Execute(post, ContainerWritePlanner.Build(post), step =>
            {
                if (step == "复核发布结果") publishedLock = File.Open(post.WorkingCopyPath, FileMode.Open, FileAccess.Read, FileShare.None);
            });
        }
        catch (ContainerWriteBackException error) { failure = error; }
        finally { publishedLock?.Dispose(); }
        check(failure is { Report.Published: true, Report.Success: false, Report.LedgerComplete: false },
            "Post-publication read failure lost its published state.");
        check(File.ReadAllBytes(post.WorkingCopyPath).SequenceEqual(edited), "Published candidate unexpectedly absent.");
        check(failure.Report.JournalId != null && File.Exists(failure.Report.BackupPath), "Published failure lost recovery evidence.");
        var recovered = ContainerWritePlanner.Build(ContainerSession.Load(post.SessionDirectory));
        check(!recovered.IsBlocked && !recovered.HasPendingChanges, "Published-but-unverified operation did not recover.");

        var (ledger, ledgerFile) = Setup("ledger-failure-after-publish");
        Record(ledger, ledgerFile, edited);
        FileStream ledgerLock = null;
        ContainerWriteBackReport report;
        try
        {
            report = ContainerWriteBackExecutor.Execute(ledger, ContainerWritePlanner.Build(ledger), step =>
            {
                if (step == "复核发布结果") ledgerLock = File.Open(ContainerSession.SessionFilePath(ledger.SessionDirectory),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
            });
        }
        finally { ledgerLock?.Dispose(); }
        check(report.Published && report.Success && !report.LedgerComplete, "Ledger failure was not reported separately.");
        check(ledger.Bindings.Single().HasSavedChange && ledger.WorkingCopySha256 == report.Sha256Before,
            "Failed ledger publication left the in-memory session falsely committed.");
        var ledgerRecovery = ContainerWritePlanner.Build(ledger);
        check(!ledgerRecovery.IsBlocked && !ledger.Bindings.Single().HasSavedChange,
            "Retry in the same session failed to complete the ledger.");
        check(ContainerSession.Load(ledger.SessionDirectory).WorkingCopySha256 == report.Sha256After,
            "Recovered ledger was not persisted.");

        var (missing, missingFile) = Setup("missing-copy-pending");
        Record(missing, missingFile, edited);
        File.Move(missing.WorkingCopyPath, missing.WorkingCopyPath + ".moved");
        var missingPlan = ContainerWritePlanner.Build(missing);
        check(missingPlan.IsBlocked && missingPlan.HasPendingChanges && missingPlan.PendingChangeCount == 1 && missingPlan.Items.Count == 0,
            "Global blocker hid the pending change count.");

        var (save, saveFile) = Setup("durable-save-receipt");
        var saveBinding = save.Bindings.Single();
        var receipt = ContainerSaveReceipt.Prepare(save, saveBinding, saveFile);
        File.WriteAllBytes(saveFile, edited);
        receipt.Complete();
        var sessionPath = ContainerSession.SessionFilePath(save.SessionDirectory);
        var previousLedger = File.ReadAllBytes(sessionPath);
        using (var locked = File.Open(sessionPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            saveBinding.RecordSaved(saveFile, receipt.SavedSha256, receipt.CreatedAt);
            var rejected = false;
            try { save.Save(); } catch (IOException) { rejected = true; }
            check(rejected && File.ReadAllBytes(sessionPath).SequenceEqual(previousLedger), "Failed atomic save lost the old JSON.");
        }
        check(File.Exists(receipt.ReceiptPath), "Failed ledger save lost the independent save receipt.");
        var loadedSave = ContainerSession.Load(save.SessionDirectory);
        check(loadedSave.Bindings.Single().HasSavedChange && loadedSave.SaveRecoveryProblems.Count == 0,
            "Restart did not recover the saved FNG.");
        check(!File.Exists(receipt.ReceiptPath), "Recovered save receipt was not retired after ledger persistence.");

        var (interrupted, interruptedFile) = Setup("interrupted-save");
        var unchangedReceipt = ContainerSaveReceipt.Prepare(interrupted, interrupted.Bindings.Single(), interruptedFile);
        var unchanged = ContainerSession.Load(interrupted.SessionDirectory);
        check(!unchanged.SaveRecoveryProblems.Any() && !File.Exists(unchangedReceipt.ReceiptPath), "Untouched save intent was not safely discarded.");
        var interruptedReceipt = ContainerSaveReceipt.Prepare(interrupted, interrupted.Bindings.Single(), interruptedFile);
        File.WriteAllBytes(interruptedFile, edited);
        var unresolved = ContainerWritePlanner.Build(ContainerSession.Load(interrupted.SessionDirectory));
        check(unresolved.IsBlocked && unresolved.Blockers.Any(b => b.Contains("保存过程曾中断")), "Ambiguous interrupted save was silently ignored.");
        var successfulReceipt = ContainerSaveReceipt.Prepare(interrupted, interrupted.Bindings.Single(), interruptedFile);
        successfulReceipt.Complete();
        ContainerSaveReceipt.ClearOlderForTarget(interrupted, successfulReceipt);
        var resolved = ContainerSession.Load(interrupted.SessionDirectory);
        check(!resolved.SaveRecoveryProblems.Any() && resolved.Bindings.Single().HasSavedChange,
            "Explicit re-save failed to resolve its prior interrupted intent.");

        var (superseded, supersededFile) = Setup("superseded-save-receipt");
        var oldReceipt = ContainerSaveReceipt.Prepare(superseded, superseded.Bindings.Single(), supersededFile);
        File.WriteAllBytes(supersededFile, edited);
        oldReceipt.Complete();
        var alternate = Path.Combine(superseded.SessionDirectory, "alternate.fng");
        File.WriteAllBytes(alternate, original);
        superseded.Bindings.Single().RecordSaved(alternate, ContainerFileHash.OfFile(alternate), oldReceipt.CreatedAt.AddSeconds(1));
        superseded.Save();
        var newer = ContainerSession.Load(superseded.SessionDirectory);
        check(newer.Bindings.Single().CurrentFile == alternate && !File.Exists(oldReceipt.ReceiptPath),
            "Stale receipt overrode a later explicit version choice.");

        var journalPath = WriteBackJournal.JournalFilePath(save.SessionDirectory);
        var journal = new List<WriteBackJournalEntry> { new() { Id = "keep", Status = WriteBackJournal.StatusCompleted } };
        WriteBackJournal.Save(save.SessionDirectory, journal);
        using (var locked = File.Open(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var rejected = false;
            try { WriteBackJournal.Save(save.SessionDirectory, new List<WriteBackJournalEntry>()); }
            catch (IOException) { rejected = true; }
            check(rejected && WriteBackJournal.Load(save.SessionDirectory).Single().Id == "keep",
                "Failed journal replacement destroyed recovery evidence.");
        }
        File.Move(sessionPath, sessionPath + ".tmp");
        check(ContainerSession.ExistsIn(save.SessionDirectory) && ContainerSession.Load(save.SessionDirectory).Bindings.Count == 1 &&
              File.Exists(sessionPath), "Legacy session temporary file was not recovered.");
        File.Move(journalPath, journalPath + ".tmp");
        check(WriteBackJournal.Load(save.SessionDirectory).Single().Id == "keep" && File.Exists(journalPath),
            "Legacy journal temporary file was mistaken for an absent journal.");

        var (multi, multiFile) = Setup("multi-target-copy");
        // Existing tests cover consecutive operations; exercise multiple replacements in one publication too.
        var extra = MakeFngWithObject("Other.fng", 5f);
        File.WriteAllBytes(multi.OriginalPath, original.Concat(extra).ToArray());
        var multiSource = FngContainer.Open(multi.OriginalPath);
        var multiDir = Path.Combine(root, "multi-target-extracted");
        var multiExtracted = multiSource.Extract(multiSource.Entries, multiDir);
        multi = ContainerSession.Register(multi.OriginalPath, multiDir).Session;
        multi.CreateWorkingCopy(Path.Combine(root, "multi-target.work.bun"));
        foreach (var extracted in multiExtracted)
            Record(multi, extracted.OutputPath, MakeFngWithObject(extracted.PackageName, 99f));
        var multiReport = ContainerWriteBackExecutor.Execute(multi, ContainerWritePlanner.Build(multi));
        check(multiReport.Success && multiReport.Items.Count == 2 && !multi.Bindings.Any(b => b.HasSavedChange),
            "Multiple targets were not published and committed together.");
    }


    private static byte[] Block(uint id, byte[] payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(id);
        writer.Write(payload.Length);
        writer.Write(payload);
        return stream.ToArray();
    }
}
