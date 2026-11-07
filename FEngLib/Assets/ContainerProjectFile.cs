using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace FEngLib.Assets;

public sealed record ContainerProjectSnapshot(ContainerProject Project, IReadOnlyList<ContainerFngContent> Edits,
    IReadOnlyList<ContainerTextureOverride> Textures, IReadOnlyList<long> Tabs, long? ActiveTab,
    string SourcePath = null, string BasePath = null);

/// <summary>Versioned, sparse ZIP snapshot. ZIP paths are generated from logical offsets.</summary>
public static class ContainerProjectFile
{
    public const string Extension = ".fengproj";
    private const int CurrentVersion = 3;
    private const string ManifestName = "project.json";
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxFngBytes = 64 * 1024 * 1024;
    private const long MaxEditsBytes = 256L * 1024 * 1024;
    private const long MaxTexturesBytes = 256L * 1024 * 1024;

    private sealed record Manifest(int Version, string SourcePath, long SourceLength, string SourceSha256,
        string BasePath, long BaseLength, string BaseSha256,
        EditRecord[] Edits, TextureRecord[] Textures, long[] Tabs, long? ActiveTab);
    private sealed record EditRecord(long Offset, string PackageName, string OriginalSha256, string EditedSha256);
    private sealed record TextureRecord(long FngOffset, uint RequestId, string RequestName,
        int Width, int Height, string SourceName, string Sha256,
        string TargetArchivePath, string TargetTpkName, int TargetTpkOrdinal,
        int TargetTextureOrdinal, string TargetTextureName, uint TargetBinKey, string TargetCompression);

    public static ContainerFngContent CreateEdit(ContainerFngSource source, byte[] bytes)
    {
        var content = new ContainerFngContent(source, (byte[])bytes.Clone());
        ValidateEdit(content);
        return content;
    }

    public static string Save(string path, ContainerProjectSnapshot snapshot)
    {
        path = Path.GetFullPath(path);
        var project = snapshot.Project;
        var index = project.Index;
        var provenance = string.IsNullOrWhiteSpace(snapshot.SourcePath)
            ? index.SourcePath
            : Path.GetFullPath(snapshot.SourcePath);
        var baseline = snapshot.BasePath;
        if (string.IsNullOrWhiteSpace(baseline) ||
            !ProjectBaselineStore.Matches(baseline, index.SourceLength, index.SourceSha256))
            baseline = ProjectBaselineStore.Ensure(project.BaselinePath, index.SourceLength, index.SourceSha256);
        else
            baseline = Path.GetFullPath(baseline);
        if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase) ||
            ContainerSession.PathsEqual(path, index.SourcePath) ||
            ContainerSession.PathsEqual(path, baseline))
            throw new IOException("请选择独立的 .fengproj 文件路径。");
        using var source = File.Open(baseline, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateTabs(index, snapshot.Tabs, snapshot.ActiveTab);
        var edits = snapshot.Edits.OrderBy(edit => edit.Source.Block.Offset).ToArray();
        if (edits.Select(edit => edit.Source.Block.Offset).Distinct().Count() != edits.Length ||
            edits.Sum(edit => (long)edit.Length) > MaxEditsBytes)
            throw new InvalidDataException("工程包含重复或过大的修改记录。");
        foreach (var edit in edits)
        {
            var original = project.ReadFng(edit.Source.Block, source).Source;
            if (original != edit.Source) throw new InvalidDataException("FNG 修改记录与源块不符。");
        }
        var textures = (snapshot.Textures ?? Array.Empty<ContainerTextureOverride>())
            .Select(texture => RemapTextureTarget(texture, provenance, baseline))
            .ToArray();
        if (textures.Select(texture => (texture.FngOffset, texture.RequestId)).Distinct().Count() != textures.Length ||
            textures.Sum(texture => (long)texture.PngData.Length) > MaxTexturesBytes)
            throw new InvalidDataException("工程包含重复或过大的贴图覆盖记录。");
        foreach (var texture in textures)
        {
            FindFng(index, texture.FngOffset);
            if (texture.PngData == null || texture.PngData.Length > ContainerTextureOverride.MaxPngBytes)
                throw new InvalidDataException("工程中的贴图覆盖大小无效。");
        }
        var manifest = new Manifest(CurrentVersion, provenance, index.SourceLength, index.SourceSha256,
            baseline, index.SourceLength, index.SourceSha256,
            edits.Select(edit =>
            {
                using var stream = edit.OpenRead();
                return new EditRecord(edit.Source.Block.Offset, edit.Source.PackageName,
                    edit.Source.OriginalSha256, FngContainer.Hash(stream));
            }).ToArray(),
            textures.Select(texture => new TextureRecord(texture.FngOffset, texture.RequestId,
                texture.RequestName, texture.Width, texture.Height, texture.SourceName, texture.Sha256,
                texture.TargetArchivePath, texture.TargetTpkName, texture.TargetTpkOrdinal,
                texture.TargetTextureOrdinal, texture.TargetTextureName, texture.TargetBinKey,
                texture.TargetCompression)).ToArray(),
            snapshot.Tabs.ToArray(), snapshot.ActiveTab);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        if (manifestBytes.Length > MaxManifestBytes) throw new InvalidDataException("工程清单过大。");

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var entry = zip.CreateEntry(ManifestName).Open()) entry.Write(manifestBytes);
                    foreach (var edit in edits)
                    {
                        using var bytes = edit.OpenRead();
                        using var entry = zip.CreateEntry(EditPath(edit.Source.Block.Offset)).Open();
                        bytes.CopyTo(entry);
                    }
                    foreach (var texture in textures)
                    {
                        using var entry = zip.CreateEntry(TexturePath(texture.FngOffset, texture.RequestId)).Open();
                        entry.Write(texture.PngData);
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return baseline;
    }

    public static ContainerProjectSnapshot Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
            if (!entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("工程包含重复的 ZIP 条目。");

        var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry(entries, ManifestName, MaxManifestBytes));
        if (manifest == null || manifest.Version is not (1 or 2 or 3))
            throw new NotSupportedException("不支持该工程文件版本。");
        if (string.IsNullOrWhiteSpace(manifest.SourcePath) || !Path.IsPathFullyQualified(manifest.SourcePath) ||
            manifest.SourceLength < 0 || string.IsNullOrWhiteSpace(manifest.SourceSha256) ||
            manifest.Edits == null || manifest.Tabs == null)
            throw new InvalidDataException("工程清单缺少必填字段。");
        var hasBase = manifest.Version >= 3;
        var basePath = hasBase ? manifest.BasePath : manifest.SourcePath;
        var baseLength = hasBase ? manifest.BaseLength : manifest.SourceLength;
        var baseSha256 = hasBase ? manifest.BaseSha256 : manifest.SourceSha256;
        if (string.IsNullOrWhiteSpace(basePath) || !Path.IsPathFullyQualified(basePath) ||
            baseLength <= 0 || string.IsNullOrWhiteSpace(baseSha256) || baseSha256.Length != 64 ||
            !baseSha256.All(character => Uri.IsHexDigit(character)))
            throw new InvalidDataException("工程清单缺少有效的基线记录。");
        var baseline = ProjectBaselineStore.Resolve(baseSha256, baseLength, Path.GetFileName(manifest.SourcePath),
            basePath, manifest.SourcePath);
        var project = ContainerProject.Open(baseline, baseLength, baseSha256, manifest.SourcePath, baseline);
        using var source = project.OpenVerifiedSource();
        ValidateTabs(project.Index, manifest.Tabs, manifest.ActiveTab);
        var edits = new List<ContainerFngContent>();
        var remaining = MaxEditsBytes;
        foreach (var record in manifest.Edits)
        {
            if (record == null) throw new InvalidDataException("工程包含空的修改记录。");
            var block = FindFng(project.Index, record.Offset);
            var original = project.ReadFng(block, source).Source;
            if (original.PackageName != record.PackageName || original.OriginalSha256 != record.OriginalSha256)
                throw new InvalidDataException($"工程中的 FNG 来源不符（0x{record.Offset:X}）。");
            var bytes = ReadEntry(entries, EditPath(record.Offset), Math.Min(MaxFngBytes, remaining));
            remaining -= bytes.Length;
            if (Convert.ToHexString(SHA256.HashData(bytes)) != record.EditedSha256)
                throw new InvalidDataException($"工程中的 FNG 摘要不符（0x{record.Offset:X}）。");
            var edit = new ContainerFngContent(original, bytes);
            ValidateEdit(edit);
            edits.Add(edit);
        }
        var textures = new List<ContainerTextureOverride>();
        var remainingTextures = MaxTexturesBytes;
        var textureKeys = new HashSet<(long FngOffset, uint RequestId)>();
        foreach (var record in manifest.Textures ?? Array.Empty<TextureRecord>())
        {
            if (record == null) throw new InvalidDataException("工程包含空的贴图覆盖记录。");
            FindFng(project.Index, record.FngOffset);
            if (!textureKeys.Add((record.FngOffset, record.RequestId)))
                throw new InvalidDataException("工程包含重复的贴图覆盖记录。");
            var bytes = ReadEntry(entries, TexturePath(record.FngOffset, record.RequestId),
                Math.Min(ContainerTextureOverride.MaxPngBytes, remainingTextures));
            remainingTextures -= bytes.Length;
            var texture = ContainerTextureOverride.Load(record.FngOffset, record.RequestId, record.RequestName,
                record.Width, record.Height, record.SourceName, bytes, record.Sha256,
                record.TargetArchivePath, record.TargetTpkName, record.TargetTpkOrdinal,
                record.TargetTextureOrdinal, record.TargetTextureName, record.TargetBinKey,
                record.TargetCompression);
            textures.Add(RemapTextureTarget(texture, manifest.SourcePath, baseline));
        }
        if (entries.Count != 0) throw new InvalidDataException("工程包含未登记的 ZIP 条目。");
        return new ContainerProjectSnapshot(project, edits.AsReadOnly(), textures.AsReadOnly(),
            Array.AsReadOnly(manifest.Tabs), manifest.ActiveTab, manifest.SourcePath, baseline);
    }

    private static string EditPath(long offset) => $"fng/{offset:X16}.fng";
    private static string TexturePath(long fngOffset, uint requestId) =>
        $"textures/{fngOffset:X16}/{requestId:X8}.png";

    private static ContainerTextureOverride RemapTextureTarget(ContainerTextureOverride texture,
        string provenance, string baseline)
    {
        if (texture == null || !texture.HasTarget) return texture;
        if (ContainerSession.PathsEqual(provenance, baseline)) return texture;
        return ContainerSession.PathsEqual(texture.TargetArchivePath, provenance)
            ? texture.WithTargetArchive(baseline)
            : texture;
    }

    private static byte[] ReadEntry(Dictionary<string, ZipArchiveEntry> entries, string name, long limit)
    {
        if (!entries.Remove(name, out var entry) || entry.Length <= 0 || entry.Length > limit)
            throw new InvalidDataException($"工程条目缺失或超出大小限制：{name}");
        using var stream = entry.Open();
        using var reader = new BinaryReader(stream);
        var bytes = reader.ReadBytes((int)entry.Length);
        if (bytes.Length != entry.Length || stream.ReadByte() != -1)
            throw new InvalidDataException($"工程条目大小不符：{name}");
        return bytes;
    }

    private static void ValidateEdit(ContainerFngContent edit)
    {
        using var stream = edit.OpenRead();
        using var reader = new BinaryReader(stream);
        if (stream.Length < 8 || stream.Length > MaxFngBytes || reader.ReadUInt32() != FngContainer.FngChunkId)
            throw new InvalidDataException("工程修改必须是有效的独立 FNG。");
        var size = reader.ReadUInt32();
        var entry = FngContainer.ReadEntry(reader, 0, FngContainer.FngChunkId, size, hashEntry: false);
        if (size != stream.Length - 8 || !entry.CanExtract || entry.Name != edit.Source.PackageName)
            throw new InvalidDataException("工程中的 FNG 长度或包名与记录不符。");
    }

    private static ContainerBlock FindFng(ContainerBlockIndex index, long offset)
    {
        var block = index.FindBlockAtOffset(offset);
        if (block?.FngStorage is not (FngStorageKind.Compressed or FngStorageKind.Uncompressed))
            throw new InvalidDataException($"工程引用了不存在或无效的 FNG 块（0x{offset:X}）。");
        return block;
    }

    private static void ValidateTabs(ContainerBlockIndex index, IReadOnlyList<long> tabs, long? activeTab)
    {
        var distinct = tabs.ToHashSet();
        if (distinct.Count != tabs.Count || activeTab.HasValue && !distinct.Contains(activeTab.Value))
            throw new InvalidDataException("工程的标签记录不一致。");
        foreach (var offset in tabs) FindFng(index, offset);
    }
}
