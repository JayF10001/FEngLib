using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FEngLib.Assets;

/// <summary>Identity of an original FNG. Offsets are in the logical, outer-decoded stream.</summary>
public sealed record ContainerFngSource(ContainerBlock Block, string PackageName, string OriginalSha256);

public sealed class ContainerFngContent
{
    private readonly byte[] bytes;

    internal ContainerFngContent(ContainerFngSource source, byte[] bytes)
    {
        Source = source;
        this.bytes = bytes;
    }

    public ContainerFngSource Source { get; }
    internal int Length => bytes.Length;
    public Stream OpenRead() => new MemoryStream(bytes, writable: false);
}

/// <summary>Indexes a source container without decoding any of its individual resources.</summary>
public sealed class ContainerProject
{
    private const int CacheLimit = 8;
    private const int CacheByteLimit = 16 * 1024 * 1024;
    private readonly byte[] logicalSource;
    private readonly LinkedList<ContainerFngContent> cache = new();
    private readonly object cacheGate = new();
    private int cachedBytes;

    private ContainerProject(ContainerBlockIndex index, byte[] logicalSource, string baselinePath)
    {
        Index = index;
        this.logicalSource = logicalSource;
        BaselinePath = Path.GetFullPath(baselinePath);
    }

    public ContainerBlockIndex Index { get; }
    public string BaselinePath { get; private set; }
    public bool HasOuterCompression => logicalSource != null;

    public static ContainerProject Open(string path) => Open(path, null, null, path, path);

    internal static ContainerProject Open(string path, long? expectedLength, string expectedSha256,
        string sourcePath = null, string baselinePath = null)
    {
        sourcePath = Path.GetFullPath(sourcePath ?? path);
        baselinePath = Path.GetFullPath(baselinePath ?? path);
        using var source = OpenSource(baselinePath);
        var hash = FngContainer.Hash(source);
        if (expectedLength.HasValue && (source.Length != expectedLength || hash != expectedSha256))
            throw new InvalidDataException("工程基线的大小或 SHA256 与记录不符。");
        source.Position = 0;
        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        var marker = source.Length >= 4 ? reader.ReadUInt32() : 0;
        source.Position = 0;
        if (!FngContainer.IsWholeFileWrapper(marker))
            return new ContainerProject(ContainerBlockIndex.Scan(source, sourcePath, source.Length, hash, hashBlocks: false),
                null, baselinePath);

        if (marker != 0x5A4C444A)
            throw new NotSupportedException("整文件压缩目前支持 JDLZ。");
        var decoded = BinaryRuntime.Decompress(reader.ReadBytes(checked((int)source.Length)));
        using var logical = new MemoryStream(decoded, writable: false);
        return new ContainerProject(ContainerBlockIndex.Scan(logical, sourcePath, source.Length, hash, hashBlocks: false),
            decoded, baselinePath);
    }

    /// <summary>Points reads at a verified baseline without changing the displayed source path.</summary>
    public void RebindBaseline(string baselinePath)
    {
        baselinePath = Path.GetFullPath(baselinePath);
        if (!string.Equals(baselinePath, BaselinePath, StringComparison.OrdinalIgnoreCase))
        {
            using var source = OpenSource(baselinePath);
            if (source.Length != Index.SourceLength ||
                !string.Equals(FngContainer.Hash(source), Index.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("新基线的大小或 SHA256 与工程记录不符。");
        }
        BaselinePath = baselinePath;
    }

    public ContainerFngContent ReadFng(ContainerBlock block)
    {
        using var source = OpenVerifiedSource();
        return ReadFng(block, source);
    }

    /// <summary>
    /// Reads FNG package names without keeping the decoded FNG bodies. This is the batch path for
    /// compressed entries: the source is verified once, each compressed entry is decoded just far
    /// enough to parse its FNG header, and failures stay unknown instead of aborting the batch.
    /// </summary>
    public IReadOnlyDictionary<long, string> ReadFngNames(IEnumerable<ContainerBlock> blocks)
    {
        var result = new Dictionary<long, string>();
        if (blocks == null) return result;
        using var source = OpenVerifiedSource();
        foreach (var block in blocks)
        {
            if (block == null || block.Sequence < 0 || block.Sequence >= Index.Blocks.Count ||
                !ReferenceEquals(Index.Blocks[block.Sequence], block) ||
                block.FngStorage is not (FngStorageKind.Compressed or FngStorageKind.Uncompressed))
                continue;
            if (!string.IsNullOrWhiteSpace(block.PackageName))
            {
                result[block.Offset] = block.PackageName;
                continue;
            }
            if (block.FngStorage != FngStorageKind.Compressed) continue;
            try
            {
                using var logical = logicalSource == null ? null : new MemoryStream(logicalSource, writable: false);
                using var reader = new BinaryReader((Stream)logical ?? source, Encoding.UTF8, leaveOpen: true);
                FngContainer.DecodeEntry(reader, block.Offset, block.ChunkId,
                    block.Offset + block.TotalLength, out var decoded);
                if (!string.IsNullOrWhiteSpace(decoded.Name)) result[block.Offset] = decoded.Name;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
                or ArgumentException or BadImageFormatException)
            {
                // Keep this entry unnamed; the viewer leaves its FNG 0x... caption in place.
            }
        }
        return result;
    }

    internal ContainerFngContent ReadFng(ContainerBlock block, FileStream source)
    {
        if (block == null || block.Sequence < 0 || block.Sequence >= Index.Blocks.Count ||
            !ReferenceEquals(Index.Blocks[block.Sequence], block) ||
            block.FngStorage is not (FngStorageKind.Compressed or FngStorageKind.Uncompressed))
            throw new ArgumentException("请选择当前工程中有效的 FNG 块。", nameof(block));

        lock (cacheGate)
        {
            var cached = cache.FirstOrDefault(item => ReferenceEquals(item.Source.Block, block));
            if (cached != null)
            {
                cache.Remove(cached);
                cache.AddFirst(cached);
                return cached;
            }

            using var logical = logicalSource == null ? null : new MemoryStream(logicalSource, writable: false);
            using var reader = new BinaryReader((Stream)logical ?? source, Encoding.UTF8, leaveOpen: true);
            byte[] bytes;
            string name;
            string hash;
            if (block.FngStorage == FngStorageKind.Compressed)
            {
                bytes = FngContainer.DecodeEntry(reader, block.Offset, block.ChunkId,
                    block.Offset + block.TotalLength, out var decoded);
                name = decoded.Name;
                hash = decoded.Sha256;
            }
            else
            {
                reader.BaseStream.Position = block.Offset;
                bytes = reader.ReadBytes(checked((int)block.TotalLength));
                if (bytes.Length != block.TotalLength) throw new EndOfStreamException();
                name = block.PackageName;
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            }

            var content = new ContainerFngContent(new ContainerFngSource(block, name, hash), bytes);
            if (content.Length <= CacheByteLimit)
            {
                while (cache.Count >= CacheLimit || cachedBytes + content.Length > CacheByteLimit)
                {
                    cachedBytes -= cache.Last.Value.Length;
                    cache.RemoveLast();
                }
                cache.AddFirst(content);
                cachedBytes += content.Length;
            }
            return content;
        }
    }

    internal FileStream OpenVerifiedSource()
    {
        var source = OpenSource(BaselinePath);
        try
        {
            if (source.Length != Index.SourceLength || FngContainer.Hash(source) != Index.SourceSha256)
                throw new InvalidDataException("工程基线已改变，请检查基线文件。");
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static FileStream OpenSource(string path) =>
        File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
}
