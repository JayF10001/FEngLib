using System;
using System.Collections.Generic;
using System.IO;

namespace FEngLib.Assets;

/// <summary>Publishes staged extraction files with rollback of previously replaced destinations.</summary>
internal sealed class ExtractionTransaction
{
    private sealed class Change
    {
        public string Target;
        public string Backup;
        public bool Published;
    }

    private readonly List<Change> _changes = new();

    public void Publish(string staged, string target, bool overwrite)
    {
        var change = new Change { Target = target };
        if (overwrite && File.Exists(target))
        {
            change.Backup = staged + ".previous";
            _changes.Add(change);
            File.Replace(staged, target, change.Backup);
        }
        else
        {
            _changes.Add(change);
            File.Move(staged, target); // Reject a destination created since preflight.
        }
        change.Published = true;
    }

    public void Rollback()
    {
        var errors = new List<Exception>();
        for (var i = _changes.Count - 1; i >= 0; i--)
        {
            var change = _changes[i];
            try
            {
                if (change.Backup != null && File.Exists(change.Backup))
                    File.Move(change.Backup, change.Target, true);
                else if (change.Published)
                    File.Delete(change.Target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                errors.Add(error);
            }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    public static void Cleanup(string stagingDirectory)
    {
        // The private staging directory contains only files created by this extraction.
        foreach (var path in Directory.GetFiles(stagingDirectory)) File.Delete(path);
        Directory.Delete(stagingDirectory);
    }
}
