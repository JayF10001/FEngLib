using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FEngLib.Assets;

namespace FEngTestBench;

/// <summary>
/// Standalone FNG safety net: automatic first-save backup, path-keyed metadata, external-change
/// handling, staged verified writes and restore. These tests exercise only the file service; the
/// editor UI is checked separately.
/// </summary>
internal static class StandaloneSaveTests
{
    internal static int Run(string directory)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
            checks++;
        }

        try
        {
            var root = Path.Combine(directory, "standalone-save");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var files = Path.Combine(root, "files");
            var backupRoot = Path.Combine(root, "backups");
            Directory.CreateDirectory(files);
            var service = new StandaloneFileBackupService(backupRoot);

            // 1. First save copies the pre-save file and records both hashes.
            var alpha = Path.Combine(files, "alpha.fng");
            var originalAlpha = Bytes("alpha-original", 4000);
            var editedAlpha = Bytes("alpha-edited", 4100);
            File.WriteAllBytes(alpha, originalAlpha);
            var first = service.Save(alpha, editedAlpha);
            Check(first.Written, "first save did not write");
            Check(first.OriginalBackupPath != null && File.Exists(first.OriginalBackupPath),
                "first save did not create an original backup");
            Check(File.ReadAllBytes(first.OriginalBackupPath).SequenceEqual(originalAlpha),
                "original backup does not hold the pre-save bytes");
            Check(File.ReadAllBytes(alpha).SequenceEqual(editedAlpha), "target does not hold the edited bytes");
            var info = service.TryReadBackupInfo(alpha);
            Check(info != null, "backup metadata was not readable");
            Check(info.OriginalSha256 == Sha(originalAlpha), "metadata original SHA256 mismatch");
            Check(info.LastWrittenSha256 == Sha(editedAlpha), "metadata last-written SHA256 mismatch");
            Check(File.Exists(Path.Combine(service.BackupDirectoryFor(alpha), "metadata.json")),
                "metadata.json missing");

            // 2. Repeated saves never replace the original backup.
            var editedAlpha2 = Bytes("alpha-edited-2", 4200);
            var second = service.Save(alpha, editedAlpha2);
            Check(second.Written, "second save did not write");
            Check(File.ReadAllBytes(first.OriginalBackupPath).SequenceEqual(originalAlpha),
                "second save overwrote the original backup");

            // 3. A fresh service instance recognises the backup after a simulated restart.
            var restarted = new StandaloneFileBackupService(backupRoot);
            var infoAfterRestart = restarted.TryReadBackupInfo(alpha);
            Check(infoAfterRestart != null && infoAfterRestart.OriginalSha256 == Sha(originalAlpha),
                "restart did not recognise the original backup");
            var third = restarted.Save(alpha, Bytes("alpha-edited-3", 4300));
            Check(third.Written, "save after restart did not write");
            Check(File.ReadAllBytes(first.OriginalBackupPath).SequenceEqual(originalAlpha),
                "save after restart overwrote the original backup");

            // 4. Equal file names in different directories get separate backups.
            var dirOne = Path.Combine(files, "one");
            var dirTwo = Path.Combine(files, "two");
            Directory.CreateDirectory(dirOne);
            Directory.CreateDirectory(dirTwo);
            var sameOne = Path.Combine(dirOne, "same.fng");
            var sameTwo = Path.Combine(dirTwo, "same.fng");
            var originalOne = Bytes("same-one", 1000);
            var originalTwo = Bytes("same-two", 1000);
            File.WriteAllBytes(sameOne, originalOne);
            File.WriteAllBytes(sameTwo, originalTwo);
            var savedOne = service.Save(sameOne, Bytes("same-one-edited", 1100));
            var savedTwo = service.Save(sameTwo, Bytes("same-two-edited", 1100));
            Check(savedOne.OriginalBackupPath != savedTwo.OriginalBackupPath,
                "same-named files share one backup path");
            Check(File.ReadAllBytes(savedOne.OriginalBackupPath).SequenceEqual(originalOne),
                "first same-named backup has the wrong content");
            Check(File.ReadAllBytes(savedTwo.OriginalBackupPath).SequenceEqual(originalTwo),
                "second same-named backup has the wrong content");

            // 5. External changes are not overwritten silently; the resolver decides and the
            //    external version is snapshotted while the original backup stays intact.
            var external = Bytes("external", 4400);
            File.WriteAllBytes(alpha, external);
            var resolverCalls = 0;
            var afterExternal = service.Save(alpha, Bytes("editor-after-external", 4500),
                new StandaloneSaveOptions
                {
                    ResolveConflict = change =>
                    {
                        resolverCalls++;
                        Check(change.ExpectedSha256 == Sha(Bytes("alpha-edited-3", 4300)),
                            "conflict carries the wrong expected hash");
                        Check(change.ActualSha256 == Sha(external), "conflict carries the wrong actual hash");
                        return StandaloneConflictAction.BackupAndSave;
                    }
                });
            Check(resolverCalls == 1, "external change did not consult the resolver");
            Check(afterExternal.Written, "save after external change did not write");
            Check(afterExternal.SnapshotPath != null && File.Exists(afterExternal.SnapshotPath),
                "external version was not snapshotted");
            Check(File.ReadAllBytes(afterExternal.SnapshotPath).SequenceEqual(external),
                "snapshot does not hold the external bytes");
            Check(File.ReadAllBytes(first.OriginalBackupPath).SequenceEqual(originalAlpha),
                "original backup changed after an external edit");

            // Cancelling a conflict leaves the file untouched.
            var externalTwo = Bytes("external-2", 4600);
            File.WriteAllBytes(alpha, externalTwo);
            var cancelled = service.Save(alpha, Bytes("should-not-be-written", 4700),
                new StandaloneSaveOptions { ResolveConflict = _ => StandaloneConflictAction.Cancel });
            Check(cancelled.Cancelled && !cancelled.Written, "cancel did not cancel");
            Check(File.ReadAllBytes(alpha).SequenceEqual(externalTwo), "cancel modified the file");

            // Without a resolver the service refuses to overwrite.
            var refused = false;
            try { service.Save(alpha, Bytes("unresolved", 4800)); }
            catch (StandaloneConflictException) { refused = true; }
            Check(refused, "external change without a resolver was not refused");

            // 6. An unwritable backup location aborts before the target is touched.
            var blockedRoot = Path.Combine(root, "blocked-root");
            File.WriteAllText(blockedRoot, "not a directory");
            var blockedService = new StandaloneFileBackupService(Path.Combine(blockedRoot, "backups"));
            var blockedFile = Path.Combine(files, "blocked.fng");
            var blockedOriginal = Bytes("blocked-original", 2000);
            File.WriteAllBytes(blockedFile, blockedOriginal);
            var blockedThrew = false;
            try { blockedService.Save(blockedFile, Bytes("blocked-edited", 2100)); }
            catch (StandaloneFileSafetyException) { blockedThrew = true; }
            Check(blockedThrew, "unwritable backup directory did not abort the save");
            Check(File.ReadAllBytes(blockedFile).SequenceEqual(blockedOriginal),
                "unwritable backup directory still modified the target");

            // 7. A failed publish leaves the target intact and no temp file behind.
            var locked = Path.Combine(files, "locked.fng");
            var lockedOriginal = Bytes("locked-original", 3000);
            File.WriteAllBytes(locked, lockedOriginal);
            service.Save(locked, Bytes("locked-edited", 3100));
            var lockedBefore = File.ReadAllBytes(locked);
            var writeFailed = false;
            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { service.Save(locked, Bytes("locked-edited-2", 3200)); }
                catch (StandaloneFileSafetyException) { writeFailed = true; }
            }
            Check(writeFailed, "locked target did not fail the save");
            Check(File.ReadAllBytes(locked).SequenceEqual(lockedBefore), "failed write damaged the target");
            Check(Directory.GetFiles(Path.GetDirectoryName(locked) ?? ".", "*.tmp").Length == 0,
                "failed write left a temporary file");

            // 8. Restore returns the original bytes and snapshots the version it replaces.
            var beforeRestore = File.ReadAllBytes(alpha);
            var restored = service.Restore(alpha);
            Check(restored.Written, "restore did not write");
            Check(File.ReadAllBytes(alpha).SequenceEqual(originalAlpha), "restore content mismatch");
            Check(restored.SnapshotPath != null && File.Exists(restored.SnapshotPath),
                "restore did not snapshot the replaced version");
            Check(File.ReadAllBytes(restored.SnapshotPath).SequenceEqual(beforeRestore),
                "restore snapshot does not hold the replaced version");
            Check(service.TryReadBackupInfo(alpha)?.OriginalSha256 == Sha(originalAlpha),
                "restore changed the original backup record");

            // 9. Saving identical content performs no write and adds no backup.
            var snapshotCount = service.TryReadBackupInfo(alpha)?.SnapshotCount ?? -1;
            var noChange = service.Save(alpha, File.ReadAllBytes(alpha));
            Check(!noChange.Written && !noChange.Cancelled, "unchanged content was rewritten");
            Check((service.TryReadBackupInfo(alpha)?.SnapshotCount ?? -2) == snapshotCount,
                "unchanged save added a snapshot");
            Check(noChange.OriginalBackupPath != null, "unchanged save lost the original backup path");

            // 10. A brand-new file has no original to restore but still gets a metadata record.
            var fresh = Path.Combine(files, "fresh.fng");
            var freshResult = service.Save(fresh, Bytes("fresh", 500));
            Check(freshResult.Written, "new file was not written");
            Check(freshResult.OriginalBackupPath == null, "new file unexpectedly produced an original backup");
            Check(service.TryReadBackupInfo(fresh) == null, "new file exposes a restore entry without an original");
            var noRestore = false;
            try { service.Restore(fresh); }
            catch (StandaloneBackupException) { noRestore = true; }
            Check(noRestore, "restore without an original was not refused");

            // 11. Save-as to an explicit target snapshots a changed target without prompting.
            var saveAsTarget = Path.Combine(files, "save-as-target.fng");
            File.WriteAllBytes(saveAsTarget, Bytes("save-as-old", 1200));
            service.Save(saveAsTarget, Bytes("save-as-new", 1300),
                new StandaloneSaveOptions { SnapshotInsteadOfPrompt = true });
            File.WriteAllBytes(saveAsTarget, Bytes("save-as-external", 1350));
            var saveAsResult = service.Save(saveAsTarget, Bytes("save-as-new-2", 1400),
                new StandaloneSaveOptions { SnapshotInsteadOfPrompt = true });
            Check(saveAsResult.Written && saveAsResult.SnapshotPath != null,
                "save-as over a changed target was not snapshotted");

            Console.WriteLine($"Standalone save tests passed: {checks}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static byte[] Bytes(string label, int length)
    {
        var seed = Encoding.UTF8.GetBytes(label);
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
            bytes[index] = (byte)(seed[index % seed.Length] + index % 7);
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
