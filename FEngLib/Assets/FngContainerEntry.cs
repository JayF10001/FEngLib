namespace FEngLib.Assets;

public enum FngStorageKind
{
    Uncompressed,
    Compressed,
    Invalid
}

/// <summary>A top-level FNG block. Offset includes its eight-byte container header.</summary>
public sealed class FngContainerEntry
{
    internal FngContainerEntry(long offset, uint chunkId, long storedLength, string name,
        FngStorageKind storage, string diagnostic, string sha256)
    {
        Offset = offset;
        ChunkId = chunkId;
        StoredLength = storedLength;
        Name = name;
        Storage = storage;
        Diagnostic = diagnostic;
        Sha256 = sha256;
    }

    public long Offset { get; }
    public uint ChunkId { get; }
    public long StoredLength { get; }
    public string Name { get; }
    public FngStorageKind Storage { get; }
    public string Diagnostic { get; }
    public string Sha256 { get; }
    public bool CanExtract => Storage == FngStorageKind.Uncompressed;
}
