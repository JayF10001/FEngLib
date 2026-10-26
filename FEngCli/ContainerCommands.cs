using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CommandLine;
using FEngLib.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FEngCli;

[Verb("scan-container", HelpText = "Index top-level FNG blocks in a BUN/LZC file or directory (read-only).")]
public sealed class ScanContainerCommand : BaseCommand
{
    [Option('i', "input", Required = true)] public string InputPath { get; set; }
    [Option('o', "output", HelpText = "New JSON report path; defaults to standard output.")]
    public string OutputPath { get; set; }

    public override int Execute()
    {
        try
        {
            var input = Path.GetFullPath(InputPath);
            var reports = new List<object>();
            var incomplete = false;
            var files = Directory.Exists(input) ? FindContainers(input) : new[] { input };
            foreach (var file in files)
            {
                try
                {
                    var container = FngContainer.Open(file);
                    var unsupported = container.Entries.Count(e => !e.CanExtract);
                    incomplete |= unsupported != 0;
                    reports.Add(new
                    {
                        Status = container.SourceLength == 0 ? "empty" : unsupported == 0 ? "ok" : "partial",
                        Container = container
                    });
                }
                catch (Exception error) when (ContainerCommandErrors.IsExpected(error))
                {
                    incomplete = true;
                    reports.Add(new { Status = "error", SourcePath = file, Error = error.Message });
                }
            }
            var json = JsonConvert.SerializeObject(new { SchemaVersion = 1, Reports = reports },
                Formatting.Indented, new StringEnumConverter());
            if (string.IsNullOrWhiteSpace(OutputPath))
                Console.WriteLine(json);
            else
            {
                using var output = new StreamWriter(new FileStream(OutputPath, FileMode.CreateNew, FileAccess.Write));
                output.WriteLine(json);
            }
            return incomplete ? 2 : 0;
        }
        catch (Exception error) when (ContainerCommandErrors.IsExpected(error))
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static IEnumerable<string> FindContainers(string root)
    {
        // Explicit input may live in .local; recursive traversal never enters nested research/build folders.
        foreach (var file in Directory.EnumerateFiles(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            if (Path.GetExtension(file).Equals(".bun", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(file).Equals(".lzc", StringComparison.OrdinalIgnoreCase))
                yield return file;
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (new[] { ".local", ".git", "bin", "obj" }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;
            foreach (var file in FindContainers(directory))
                yield return file;
        }
    }
}

[Verb("extract-container", HelpText = "Extract supported FNGs with source manifests; existing files are never overwritten.")]
public sealed class ExtractContainerCommand : BaseCommand
{
    [Option('i', "input", Required = true)] public string InputPath { get; set; }
    [Option('o', "output", Required = true, HelpText = "Explicit extraction directory.")]
    public string OutputDirectory { get; set; }
    [Option("offset", HelpText = "Select one block by decimal or 0x-prefixed byte offset.")]
    public string Offset { get; set; }

    public override int Execute()
    {
        try
        {
            var container = FngContainer.Open(InputPath);
            var entries = container.Entries.Where(e => e.CanExtract).ToArray();
            if (Offset != null)
            {
                var offset = Offset.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? long.Parse(Offset.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                    : long.Parse(Offset, NumberStyles.None, CultureInfo.InvariantCulture);
                var entry = container.Entries.SingleOrDefault(e => e.Offset == offset);
                if (entry == null) throw new InvalidOperationException($"No FNG block at 0x{offset:X}.");
                if (!entry.CanExtract) throw new NotSupportedException(entry.Diagnostic);
                entries = new[] { entry };
            }
            var results = container.Extract(entries, OutputDirectory);
            foreach (var result in results) Console.WriteLine(result.OutputPath);
            var skipped = Offset == null ? container.Entries.Count(e => !e.CanExtract) : 0;
            if (skipped > 0) Console.Error.WriteLine($"Skipped {skipped} compressed / invalid FNG blocks. See scan-container report.");
            return skipped > 0 ? 2 : 0;
        }
        catch (Exception error) when (ContainerCommandErrors.IsExpected(error))
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

internal static class ContainerCommandErrors
{
    public static bool IsExpected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        NotSupportedException or ArgumentException or InvalidOperationException or FormatException or OverflowException;
}
