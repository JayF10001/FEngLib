using System.Collections.Generic;
using System.Linq;

namespace FEngLib.Assets;

/// <summary>An entry that was deliberately left untouched because its destination exists.</summary>
public sealed class FngExtractionSkip
{
    internal FngExtractionSkip(FngContainerEntry entry, string outputPath)
    {
        Entry = entry;
        OutputPath = outputPath;
    }

    public FngContainerEntry Entry { get; }
    public string OutputPath { get; }
}

/// <summary>Reported once a block has been staged and verified, before publication.</summary>
public sealed class FngExtractionProgress
{
    internal FngExtractionProgress(int index, int total, FngContainerEntry entry, string outputPath)
    {
        Index = index;
        Total = total;
        Entry = entry;
        OutputPath = outputPath;
    }

    /// <summary>One-based position of the block inside the selection.</summary>
    public int Index { get; }

    public int Total { get; }
    public FngContainerEntry Entry { get; }
    public string OutputPath { get; }
}

public sealed class FngExtractionBatch
{
    internal FngExtractionBatch(List<FngExtraction> extracted, List<FngExtractionSkip> skipped, int overwrittenCount)
    {
        Extracted = extracted.AsReadOnly();
        Skipped = skipped.AsReadOnly();
        SkippedPaths = skipped.Select(s => s.OutputPath).ToList().AsReadOnly();
        OverwrittenCount = overwrittenCount;
    }

    public IReadOnlyList<FngExtraction> Extracted { get; }

    // A collision on either the FNG or its manifest skips the entire pair.
    public IReadOnlyList<FngExtractionSkip> Skipped { get; }
    public IReadOnlyList<string> SkippedPaths { get; }
    public int OverwrittenCount { get; }
}
