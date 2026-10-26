using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommandLine;
using FEngLib.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FEngCli;

[Verb("writeback-register", HelpText = "Register a container write-back session from extraction manifests and optionally create the working copy.")]
public sealed class WriteBackRegisterCommand : BaseCommand
{
    [Option('c', "container", Required = true, HelpText = "Original (read-only baseline) container path.")]
    public string ContainerPath { get; set; }
    [Option('e', "extract-dir", Required = true, HelpText = "Extraction directory holding *.source.json manifests and the extracted FNGs.")]
    public string ExtractDirectory { get; set; }
    [Option('w', "working-copy", HelpText = "Create the dedicated working copy at this path (must not exist).")]
    public string WorkingCopyPath { get; set; }

    public override int Execute()
    {
        try
        {
            var registration = ContainerSession.Register(ContainerPath, ExtractDirectory);
            var session = registration.Session;
            if (WorkingCopyPath != null)
            {
                if (session.WorkingCopyPath != null &&
                    !ContainerSession.PathsEqual(session.WorkingCopyPath, Path.GetFullPath(WorkingCopyPath)))
                    throw new InvalidOperationException(
                        $"Session already uses working copy {session.WorkingCopyPath}.");
                if (session.WorkingCopyPath == null) session.CreateWorkingCopy(WorkingCopyPath);
            }

            Console.WriteLine(JsonConvert.SerializeObject(new
            {
                Status = "ok",
                session.ContainerId,
                session.OriginalPath,
                session.OriginalSha256,
                session.WorkingCopyPath,
                Bindings = session.Bindings.Count,
                AddedBindings = registration.AddedBindings,
                IgnoredManifests = registration.IgnoredManifests
            }, Formatting.Indented, new StringEnumConverter()));
            return 0;
        }
        catch (Exception error) when (ContainerCommandErrors.IsExpected(error))
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

[Verb("writeback-record", HelpText = "Record the current file content of bound FNGs as their last saved state (same bookkeeping the Viewer does on save).")]
public sealed class WriteBackRecordCommand : BaseCommand
{
    [Option('e', "extract-dir", Required = true)] public string ExtractDirectory { get; set; }
    [Option('f', "file", Required = true, HelpText = "Saved FNG file(s) inside the session directory; repeatable.", Separator = ',')]
    public IEnumerable<string> Files { get; set; }

    public override int Execute()
    {
        try
        {
            var session = ContainerSession.Load(ExtractDirectory);
            var recorded = new List<object>();
            foreach (var raw in Files)
            {
                var path = Path.GetFullPath(raw);
                var binding = session.FindByFile(path)
                    ?? throw new InvalidOperationException($"No container binding for file: {path}");
                binding.RecordSaved(path, ContainerFileHash.OfFile(path));
                recorded.Add(new
                {
                    File = path, binding.PackageName, binding.BlockOffset,
                    binding.SavedSha256, PendingWriteBack = binding.HasSavedChange
                });
            }
            session.Save();
            Console.WriteLine(JsonConvert.SerializeObject(new { Status = "ok", Recorded = recorded },
                Formatting.Indented, new StringEnumConverter()));
            return 0;
        }
        catch (Exception error) when (ContainerCommandErrors.IsExpected(error))
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

[Verb("writeback-plan", HelpText = "Build and validate the write-back plan for a session; modifies nothing.")]
public sealed class WriteBackPlanCommand : BaseCommand
{
    [Option('e', "extract-dir", Required = true)] public string ExtractDirectory { get; set; }
    [Option('o', "output", HelpText = "New JSON report path; defaults to standard output.")]
    public string OutputPath { get; set; }

    public override int Execute()
    {
        try
        {
            var session = ContainerSession.Load(ExtractDirectory);
            var plan = ContainerWritePlanner.Build(session);
            Write(plan);
            return plan.IsExecutable ? 0 : plan.HasPendingChanges ? 2 : 0;
        }
        catch (Exception error) when (ContainerCommandErrors.IsExpected(error))
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private void Write(ContainerWritePlan plan)
    {
        var json = JsonConvert.SerializeObject(new
        {
            Status = plan.IsExecutable ? "executable" : plan.HasPendingChanges ? "blocked" : "nothing-pending",
            plan.CreatedAt,
            plan.OriginalPath,
            plan.WorkingCopyPath,
            plan.WorkingCopyLength,
            plan.WorkingCopySha256,
            plan.BlockCount,
            plan.BackupPath,
            plan.Blockers,
            plan.Notes,
            Items = plan.Items.Select(i => new
            {
                i.PackageName, i.BlockOffset, i.ChunkId, i.StoredLength, i.ReplacementStoredLength,
                i.ReplacementPath, i.ReplacementSha256, i.CurrentBlockSha256, i.Blockers, i.IsRunnable
            })
        }, Formatting.Indented, new StringEnumConverter());
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            Console.WriteLine(json);
        }
        else
        {
            using var output = new StreamWriter(new FileStream(OutputPath, FileMode.CreateNew, FileAccess.Write));
            output.WriteLine(json);
        }
    }
}

[Verb("writeback-execute", HelpText = "Build the plan and, when it is executable, perform the equal-length write-back transaction.")]
public sealed class WriteBackExecuteCommand : BaseCommand
{
    [Option('e', "extract-dir", Required = true)] public string ExtractDirectory { get; set; }

    public override int Execute()
    {
        try
        {
            var session = ContainerSession.Load(ExtractDirectory);
            var plan = ContainerWritePlanner.Build(session);
            if (!plan.IsExecutable)
            {
                Console.WriteLine(JsonConvert.SerializeObject(new
                {
                    Status = plan.HasPendingChanges ? "blocked" : "nothing-pending",
                    plan.Blockers,
                    plan.Notes,
                    Items = plan.Items.Select(i => new
                    {
                        i.PackageName, i.BlockOffset, i.Blockers, i.IsRunnable
                    })
                }, Formatting.Indented, new StringEnumConverter()));
                return plan.HasPendingChanges ? 2 : 0;
            }

            var report = ContainerWriteBackExecutor.Execute(session, plan);
            Console.WriteLine(JsonConvert.SerializeObject(new
            {
                Status = "published",
                report.WorkingCopyPath,
                report.BackupPath,
                report.Sha256Before,
                report.Sha256After,
                report.VerifiedBlockCount,
                report.LedgerComplete,
                report.Warnings,
                report.Items
            }, Formatting.Indented, new StringEnumConverter()));
            return 0;
        }
        catch (Exception error) when (ContainerCommandErrors.IsExpected(error))
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
