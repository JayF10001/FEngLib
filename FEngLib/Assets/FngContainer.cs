using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FEngLib.Assets;

/// <summary>
/// Read-only MW05 chunk-stream index. Binary's optional codec decodes whole-file JDLZ and
/// compressed FNGs. Offsets identify original logical blocks; extracted hashes describe decoded FNGs.
/// </summary>
public sealed class FngContainer
{
    public const uint FngChunkId = 0x00030203;
    public const uint CompressedFngChunkId = 0x00030210;
    private const uint FengMagic = 0xE76E4546;
    private const uint PackageHeaderMagic = 0x64486B50;

    private FngContainer(string path, long length, string sha256, int blockCount,
        List<FngContainerEntry> entries)
    {
        SourcePath = path;
        SourceLength = length;
        SourceSha256 = sha256;
        BlockCount = blockCount;
        Entries = entries.AsReadOnly();
    }

    public string SourcePath { get; }
    public long SourceLength { get; }
    public string SourceSha256 { get; }
    public int BlockCount { get; }
    public IReadOnlyList<FngContainerEntry> Entries { get; }
    public bool RequiresRebuild { get; private set; }
    private byte[] decodedSource;

    public static FngContainer Open(string path)
    {
        path = Path.GetFullPath(path);
        using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sourceHash = Hash(source);
        source.Position = 0;
        using var markerReader = new BinaryReader(source, Encoding.UTF8, true);
        var marker = source.Length >= 4 ? markerReader.ReadUInt32() : 0;
        source.Position = 0;
        byte[] decoded = null;
        if (IsWholeFileWrapper(marker))
        {
            if (marker != 0x5A4C444A) throw new NotSupportedException("整文件压缩目前支持 JDLZ。");
            using var buffer = new MemoryStream(); source.CopyTo(buffer);
            decoded = BinaryRuntime.Decompress(buffer.ToArray());
        }
        using Stream stream = decoded == null ? File.OpenRead(path) : new MemoryStream(decoded);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        stream.Position = 0;
        var entries = new List<FngContainerEntry>();
        var blockCount = 0;
        while (stream.Position < stream.Length)
        {
            var offset = stream.Position;
            if (stream.Length - offset < 8)
                throw new InvalidDataException($"Truncated chunk header at 0x{offset:X}.");
            var id = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            if (offset == 0 && IsWholeFileWrapper(id))
                throw new NotSupportedException("Whole-file compression / VPAK is not supported yet.");
            var end = stream.Position + length;
            if (end > stream.Length)
                throw new InvalidDataException($"Chunk 0x{id:X8} at 0x{offset:X} exceeds file bounds.");
            blockCount++;
            if (id == FngChunkId || id == CompressedFngChunkId)
            {
                var entry = ReadEntry(reader, offset, id, length);
                if (entry.Storage == FngStorageKind.Compressed && BinaryRuntime.DirectoryPath != null)
                {
                    try
                    {
                        var codecOffset = offset + (id == CompressedFngChunkId ? 12 : 20);
                        stream.Position = codecOffset;
                        var body = BinaryRuntime.Decompress(reader.ReadBytes(checked((int)(end - codecOffset))));
                        using var bytes = new MemoryStream();
                        using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
                        writer.Write(FngChunkId); writer.Write(body.Length); writer.Write(body);
                        bytes.Position = 8;
                        using var decodedReader = new BinaryReader(bytes, Encoding.UTF8, true);
                        var canonical = ReadEntry(decodedReader, 0, FngChunkId, (uint)body.Length);
                        if (!canonical.CanExtract) throw new InvalidDataException("解压后的 FNG 无效：" + canonical.Diagnostic);
                        entry = new FngContainerEntry(offset, FngChunkId, bytes.Length, canonical.Name,
                            FngStorageKind.Uncompressed, "已由 Binary 解压；写回时重建容器", canonical.Sha256)
                            { DecodedBytes = bytes.ToArray() };
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException)
                    {
                        entry = new FngContainerEntry(offset, id, length + 8L, null, FngStorageKind.Compressed,
                            entry.Diagnostic + " 解压失败：" + error.Message, null);
                    }
                }
                entries.Add(entry);
            }
            stream.Position = end;
        }

        return new FngContainer(path, source.Length, sourceHash, blockCount, entries)
        { decodedSource = decoded, RequiresRebuild = decoded != null || entries.Any(e => e.DecodedBytes != null) };
    }

    internal static bool IsWholeFileWrapper(uint id)
    {
        return id is 0x5A4C444A or 0x46465548 or 0x57574152 or 0x4B504652 or 0x504D4F43 or 0x4B415056;
    }

    /// <summary>Shared with <see cref="ContainerBlockIndex"/> so both scanners classify blocks identically.</summary>
    internal static FngContainerEntry ReadEntry(BinaryReader reader, long offset, uint id, uint length)
    {
        var stream = reader.BaseStream;
        var payloadOffset = offset + 8;
        try
        {
            if (id == CompressedFngChunkId)
                return CompressedEntry(reader, offset, id, length, payloadOffset + 4);
            if (length < 4)
                throw new InvalidDataException("Short FNG payload.");
            var magic = reader.ReadUInt32();
            if (magic == CompressedFngChunkId)
                return CompressedEntry(reader, offset, id, length, payloadOffset + 12);
            if (magic != FengMagic || length < 40)
                throw new InvalidDataException("Missing FEng header.");
            var fengLength = reader.ReadUInt32();
            if ((long)fengLength + 8 > length || fengLength < 32)
                throw new InvalidDataException("FEng length exceeds its containing block or is too short.");
            if (reader.ReadUInt32() != PackageHeaderMagic)
                throw new InvalidDataException("Missing MW05 PkHd header.");
            var headerLength = reader.ReadUInt32();
            if (headerLength < 24 || (long)headerLength + 8 > fengLength)
                throw new InvalidDataException("Invalid PkHd length.");
            if (reader.ReadUInt32() != 0x20000 || reader.ReadUInt32() != 0)
                throw new InvalidDataException("Unsupported MW05 PkHd version.");
            stream.Position += 8; // resource and root object counts
            var nameLength = reader.ReadInt32();
            var filenameLength = reader.ReadInt32();
            if (nameLength < 2 || nameLength > 4096 || filenameLength < 1 ||
                24L + nameLength + filenameLength > headerLength)
                throw new InvalidDataException("Invalid package name / filename lengths.");
            var nameBytes = reader.ReadBytes(nameLength);
            if (nameBytes.Length != nameLength || nameBytes[nameLength - 1] != 0)
                throw new InvalidDataException("Package name is not null-terminated.");
            var name = new UTF8Encoding(false, true).GetString(nameBytes, 0, nameLength - 1);
            if (!name.EndsWith(".fng", StringComparison.OrdinalIgnoreCase) || name.Any(char.IsControl))
                throw new InvalidDataException("Invalid FNG package name.");
            stream.Position = offset;
            var hash = HashRange(stream, length + 8L);
            return new FngContainerEntry(offset, id, length + 8L, name,
                FngStorageKind.Uncompressed, null, hash);
        }
        catch (Exception error) when (error is InvalidDataException or DecoderFallbackException)
        {
            return new FngContainerEntry(offset, id, length + 8L, null,
                FngStorageKind.Invalid, error.Message, null);
        }
    }

    private static FngContainerEntry CompressedEntry(BinaryReader reader, long offset, uint id,
        uint length, long codecOffset)
    {
        if (codecOffset + 16 > offset + 8L + length)
            throw new InvalidDataException("Short compressed FNG wrapper.");
        reader.BaseStream.Position = codecOffset;
        var codec = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var known = codec is "JDLZ" or "HUFF" or "RAWW" or "RFPK" or "COMP";
        return new FngContainerEntry(offset, id, length + 8L, null, FngStorageKind.Compressed,
            known ? $"{codec}: decompression is not implemented yet." : "Unknown compressed FNG codec.", null);
    }

    /// <summary>
    /// Extracts original FNG bytes (decoded for compressed inputs) and SHA256 provenance.
    /// The physical container fingerprint stays attached to every decoded extraction.
    /// </summary>
    public IReadOnlyList<FngExtraction> Extract(IEnumerable<FngContainerEntry> selection, string directory)
        => Extract(selection, directory, ExistingFilePolicy.Fail).Extracted;

    /// <summary>Handles each FNG and its manifest as a pair when resolving file collisions.</summary>
    public FngExtractionBatch Extract(IEnumerable<FngContainerEntry> selection, string directory, ExistingFilePolicy policy,
        IProgress<FngExtractionProgress> progress = null)
    {
        if (!Enum.IsDefined(typeof(ExistingFilePolicy), policy))
            throw new ArgumentOutOfRangeException(nameof(policy));
        var entries = selection.ToArray();
        if (entries.Length == 0)
            throw new InvalidOperationException("No extractable FNG entries selected.");
        if (entries.Distinct().Count() != entries.Length || entries.Any(e => !Entries.Contains(e) || !e.CanExtract))
            throw new InvalidOperationException("Selection contains duplicate, unsupported or foreign entries.");

        directory = Path.GetFullPath(directory);
        var paths = entries.Select(e => Path.Combine(directory, $"{e.Offset:X8}_{SafeName(e.Name)}")).ToArray();
        var skipped = new List<FngExtractionSkip>();
        for (var i = 0; i < entries.Length; i++)
        {
            var path = paths[i];
            if (string.Equals(path, SourcePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path + ".source.json", SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The extraction destination must not replace the source container.");
            if (Directory.Exists(path) || Directory.Exists(path + ".source.json"))
                throw new IOException($"A directory occupies an extraction file path: {path}");
            var exists = File.Exists(path) || File.Exists(path + ".source.json");
            if (exists && policy == ExistingFilePolicy.Fail)
                throw new IOException($"Extraction destination already exists: {path}");
            if (exists && policy == ExistingFilePolicy.Skip) skipped.Add(new FngExtractionSkip(entries[i], path));
        }
        var skippedPaths = new HashSet<string>(skipped.Select(s => s.OutputPath), StringComparer.OrdinalIgnoreCase);

        using var source = File.Open(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length != SourceLength || Hash(source) != SourceSha256)
            throw new InvalidDataException("Source changed since scanning. Scan the container again.");

        var results = new List<FngExtraction>();
        if (skipped.Count == entries.Length) return new FngExtractionBatch(results, skipped, 0);

        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, ".fng-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var transaction = new ExtractionTransaction();
        var overwrittenCount = 0;
        try
        {
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var path = paths[i];
                if (skippedPaths.Contains(path)) continue;
                var staged = Path.Combine(staging, i + ".fng");
                using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (entry.DecodedBytes != null) output.Write(entry.DecodedBytes);
                    else if (decodedSource != null)
                        output.Write(decodedSource, checked((int)entry.Offset), checked((int)entry.StoredLength));
                    else
                    {
                        source.Position = entry.Offset;
                        CopyRange(source, output, entry.StoredLength);
                    }
                }
                using (var output = File.OpenRead(staged))
                    if (Hash(output) != entry.Sha256)
                        throw new InvalidDataException($"Extracted block failed SHA256 verification: {path}");

                var result = new FngExtraction
                {
                    SourcePath = SourcePath, SourceLength = SourceLength, SourceSha256 = SourceSha256,
                    Offset = entry.Offset, ChunkId = entry.ChunkId, StoredLength = entry.StoredLength,
                    PackageName = entry.Name, OutputPath = path, ExtractedSha256 = entry.Sha256,
                    ExtractedAt = DateTimeOffset.UtcNow
                };
                using (var metadata = new FileStream(staged + ".source.json", FileMode.CreateNew, FileAccess.Write))
                {
                    JsonSerializer.Serialize(metadata, result, new JsonSerializerOptions { WriteIndented = true });
                }
                results.Add(result);
                progress?.Report(new FngExtractionProgress(results.Count, entries.Length, entry, path));
            }

            for (var i = 0; i < entries.Length; i++)
            {
                var path = paths[i];
                if (skippedPaths.Contains(path)) continue;
                var staged = Path.Combine(staging, i + ".fng");
                if (File.Exists(path) || File.Exists(path + ".source.json")) overwrittenCount++;
                transaction.Publish(staged, path, policy == ExistingFilePolicy.Overwrite);
                transaction.Publish(staged + ".source.json", path + ".source.json", policy == ExistingFilePolicy.Overwrite);
            }
        }
        catch (Exception error)
        {
            try
            {
                transaction.Rollback();
            }
            catch (AggregateException rollbackError)
            {
                throw new IOException($"Extraction failed and rollback was incomplete. Recovery files remain in: {staging}",
                    new AggregateException(error, rollbackError));
            }
            try { ExtractionTransaction.Cleanup(staging); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Extraction failed; original files were restored. Temporary files remain in: {staging}", error);
            }
            throw;
        }
        try { ExtractionTransaction.Cleanup(staging); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Extraction completed, but temporary files could not be removed: {staging}", error);
        }
        return new FngExtractionBatch(results, skipped, overwrittenCount);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());
    }

    internal static string Hash(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    internal static string HashRange(Stream stream, long length)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        while (length > 0)
        {
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (count == 0) throw new EndOfStreamException();
            sha.AppendData(buffer, 0, count);
            length -= count;
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void CopyRange(Stream source, Stream destination, long length)
    {
        var buffer = new byte[65536];
        while (length > 0)
        {
            var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (count == 0) throw new EndOfStreamException();
            destination.Write(buffer, 0, count);
            length -= count;
        }
    }
}
