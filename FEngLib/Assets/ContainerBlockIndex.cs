using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FEngLib.Assets;

public enum ContainerBlockKind
{
    Fng,
    CompressedFng,
    Padding,
    Unknown,
    Tpk
}

/// <summary>
/// One top-level block with its exact boundaries and classification. Padding blocks written by
/// Nikki-style tools carry a metadata header ("gMpB" marker, label and watermark); they are
/// structured data, never free zero-filled space.
/// </summary>
public sealed class ContainerBlock
{
    internal ContainerBlock(int sequence, long offset, uint chunkId, long payloadLength,
        ContainerBlockKind kind, FngStorageKind fngStorage, string packageName, string diagnostic,
        bool nikkiPadding, string paddingWatermark, string sha256)
    {
        Sequence = sequence;
        Offset = offset;
        ChunkId = chunkId;
        PayloadLength = payloadLength;
        TotalLength = payloadLength + 8;
        Kind = kind;
        FngStorage = fngStorage;
        PackageName = packageName;
        Diagnostic = diagnostic;
        NikkiPadding = nikkiPadding;
        PaddingWatermark = paddingWatermark;
        Sha256 = sha256;
    }

    /// <summary>Zero-based position in the block stream.</summary>
    public int Sequence { get; }

    /// <summary>Byte offset of the block header; <see cref="TotalLength"/> reaches the next block.</summary>
    public long Offset { get; }

    public uint ChunkId { get; }
    public long PayloadLength { get; }
    public long TotalLength { get; }
    public ContainerBlockKind Kind { get; }

    /// <summary>Only meaningful for <see cref="ContainerBlockKind.Fng"/> blocks.</summary>
    public FngStorageKind FngStorage { get; }
    public string PackageName { get; }
    public string Diagnostic { get; }
    public bool NikkiPadding { get; }
    public string PaddingWatermark { get; }
    public string Sha256 { get; }
}

/// <summary>
/// Read-only index of <em>every</em> top-level block of a chunk-stream container, including
/// padding and unknown resources. Write-back verification walks this index instead of trusting
/// individual FNG entries. Whole-file compression / VPAK containers are rejected.
/// </summary>
public sealed class ContainerBlockIndex
{
    public const uint NikkiPaddingMarker = 0x42704D67; // "gMpB"
    public const int NikkiPaddingMetadataLength = 72; // marker + flags + 0x20 label + 0x20 watermark

    private ContainerBlockIndex(string sourcePath, long sourceLength, string sha256,
        List<ContainerBlock> blocks)
    {
        SourcePath = sourcePath;
        SourceLength = sourceLength;
        SourceSha256 = sha256;
        Blocks = blocks.AsReadOnly();
    }

    public string SourcePath { get; }
    public long SourceLength { get; }
    public string SourceSha256 { get; }
    public IReadOnlyList<ContainerBlock> Blocks { get; }

    public int FngCount => Blocks.Count(b => b.Kind == ContainerBlockKind.Fng);

    public static ContainerBlockIndex Scan(string path)
    {
        path = Path.GetFullPath(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sourceHash = FngContainer.Hash(stream);
        stream.Position = 0;
        return Scan(stream, path, stream.Length, sourceHash, hashBlocks: true);
    }

    internal static ContainerBlockIndex Scan(Stream stream, string path, long sourceLength,
        string sourceHash, bool hashBlocks)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var blocks = new List<ContainerBlock>();
        var sequence = 0;
        while (stream.Position < stream.Length)
        {
            var offset = stream.Position;
            if (stream.Length - offset < 8)
                throw new InvalidDataException($"Truncated chunk header at 0x{offset:X} in {path}.");
            var id = reader.ReadUInt32();
            var payloadLength = reader.ReadUInt32();
            if (offset == 0 && FngContainer.IsWholeFileWrapper(id))
                throw new NotSupportedException(
                    "Whole-file compression / VPAK containers are not supported yet.");
            if (stream.Position + payloadLength > stream.Length)
                throw new InvalidDataException(
                    $"Chunk 0x{id:X8} at 0x{offset:X} exceeds file bounds in {path}.");

            stream.Position = offset;
            var sha = hashBlocks ? FngContainer.HashRange(stream, payloadLength + 8L) : null;
            blocks.Add(Classify(reader, stream, sequence, offset, id, payloadLength, sha));
            stream.Position = offset + 8 + payloadLength;
            sequence++;
        }

        return new ContainerBlockIndex(path, sourceLength, sourceHash, blocks);
    }

    public ContainerBlock FindBlockAtOffset(long offset)
        => Blocks.FirstOrDefault(b => b.Offset == offset);

    private static ContainerBlock Classify(BinaryReader reader, Stream stream, int sequence,
        long offset, uint id, long payloadLength, string sha)
    {
        // HashRange left the stream at the end of the block; all classification reads below
        // position the stream explicitly.
        if (id == 0)
        {
            var nikki = false;
            var watermark = (string)null;
            if (payloadLength >= NikkiPaddingMetadataLength)
            {
                stream.Position = offset + 8;
                nikki = reader.ReadUInt32() == NikkiPaddingMarker;
                if (nikki) watermark = ReadWatermark(reader, offset);
            }

            return new ContainerBlock(sequence, offset, id, payloadLength, ContainerBlockKind.Padding,
                FngStorageKind.Invalid, null, null, nikki, watermark, sha);
        }

        if (id == FngContainer.FngChunkId || id == FngContainer.CompressedFngChunkId)
        {
            stream.Position = offset + 8;
            var entry = FngContainer.ReadEntry(reader, offset, id, (uint)payloadLength, hashEntry: false);
            var kind = entry.Storage == FngStorageKind.Compressed
                ? ContainerBlockKind.CompressedFng
                : ContainerBlockKind.Fng;
            return new ContainerBlock(sequence, offset, id, payloadLength, kind, entry.Storage,
                entry.Name, entry.Diagnostic, false, null, sha);
        }

        var resourceKind = id == 0xB3300000 ? ContainerBlockKind.Tpk : ContainerBlockKind.Unknown;
        return new ContainerBlock(sequence, offset, id, payloadLength, resourceKind,
            FngStorageKind.Invalid, null, null, false, null, sha);
    }

    /// <summary>Nikki padding layout: 4 marker + 4 flags + 0x20 label + 0x20 watermark.</summary>
    private static string ReadWatermark(BinaryReader reader, long offset)
    {
        try
        {
            reader.BaseStream.Position = offset + 8 + 8 + 32;
            var bytes = reader.ReadBytes(32);
            var end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            return new UTF8Encoding(false, true).GetString(bytes, 0, end);
        }
        catch (Exception error) when (error is DecoderFallbackException or EndOfStreamException)
        {
            return null;
        }
    }
}
