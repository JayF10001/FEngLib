namespace FEngLib.Assets;

/// <summary>Provenance for an extracted block; this is not a write-back instruction.</summary>
public sealed class FngExtraction
{
    public int SchemaVersion { get; init; } = 1;
    public string SourcePath { get; init; }
    public long SourceLength { get; init; }
    public string SourceSha256 { get; init; }
    public long Offset { get; init; }
    public uint ChunkId { get; init; }
    public long StoredLength { get; init; }
    public string PackageName { get; init; }
    public string OutputPath { get; init; }
    public string ExtractedSha256 { get; init; }
    public System.DateTimeOffset ExtractedAt { get; init; }
}
