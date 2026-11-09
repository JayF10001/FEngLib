using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace FEngLib.Assets;

/// <summary>Stored payload kind of one .fengproj texture override.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextureInputKind
{
    Png = 0,
    Dds = 1,
}

/// <summary>
/// One texture override stored in a .fengproj. The override keeps the original input bytes (PNG or
/// DDS), is scoped to one FNG block and one resource request, and never touches the container or
/// the game directory. The optional target fields identify the underlying TPK entry for a later
/// container-copy write-back.
/// </summary>
public sealed class ContainerTextureOverride
{
    public const int MaxPngBytes = 32 * 1024 * 1024;
    public const int MaxDdsBytes = 64 * 1024 * 1024;
    private const int MaxDimension = 16384;
    private byte[] _previewPng;

    private ContainerTextureOverride(long fngOffset, uint requestId, string requestName, int width,
        int height, string sourceName, TextureInputKind inputKind, string format, int mipCount,
        byte[] data, string sha256, string targetArchivePath, string targetTpkName,
        int targetTpkOrdinal, int targetTextureOrdinal, string targetTextureName, uint targetBinKey,
        string targetCompression)
    {
        FngOffset = fngOffset;
        RequestId = requestId;
        RequestName = requestName ?? "";
        Width = width;
        Height = height;
        SourceName = sourceName ?? "";
        InputKind = inputKind;
        Format = format ?? "";
        MipCount = mipCount;
        Data = data;
        Sha256 = sha256;
        TargetArchivePath = targetArchivePath;
        TargetTpkName = targetTpkName;
        TargetTpkOrdinal = targetTpkOrdinal;
        TargetTextureOrdinal = targetTextureOrdinal;
        TargetTextureName = targetTextureName;
        TargetBinKey = targetBinKey;
        TargetCompression = targetCompression;
    }

    public long FngOffset { get; }
    public uint RequestId { get; }
    public string RequestName { get; }
    public int Width { get; }
    public int Height { get; }
    public string SourceName { get; }

    /// <summary>Raw stored payload; for <see cref="TextureInputKind.Dds"/> these are the untouched DDS bytes.</summary>
    public byte[] Data { get; }
    public string Sha256 { get; }

    public TextureInputKind InputKind { get; }
    /// <summary>"PNG" or the DDS FourCC (DXT1/DXT3/DXT5).</summary>
    public string Format { get; }
    public int MipCount { get; }

    public bool IsDds => InputKind == TextureInputKind.Dds;
    public bool IsPng => InputKind == TextureInputKind.Png;
    public string FormatDescription => IsDds ? "DDS " + Format : "PNG";
    public string StoredExtension => IsDds ? ".dds" : ".png";
    public int StoredLength => Data?.Length ?? 0;

    /// <summary>Compatibility alias: only a PNG override has a stored PNG payload.</summary>
    public byte[] PngData => IsPng ? Data : null;

    public string TargetArchivePath { get; }
    public string TargetTpkName { get; }
    public int TargetTpkOrdinal { get; }
    public int TargetTextureOrdinal { get; }
    public string TargetTextureName { get; }
    public uint TargetBinKey { get; }
    public string TargetCompression { get; }

    public bool HasTarget =>
        !string.IsNullOrWhiteSpace(TargetArchivePath) &&
        !string.IsNullOrWhiteSpace(TargetTpkName) &&
        !string.IsNullOrWhiteSpace(TargetTextureName);

    public static int MaxBytes(TextureInputKind kind) =>
        kind == TextureInputKind.Dds ? MaxDdsBytes : MaxPngBytes;

    /// <summary>Decodes the stored payload for preview. DDS is decoded through Binary's ILWrapper/DevIL.</summary>
    public byte[] GetPreviewPng()
    {
        if (IsPng) return Data;
        return _previewPng ??= BinaryRuntime.DecodeTextureDds(Data);
    }

    public ContainerTextureOverride WithTargetArchive(string targetArchivePath) =>
        new(FngOffset, RequestId, RequestName, Width, Height, SourceName, InputKind, Format,
            MipCount, Data, Sha256, targetArchivePath, TargetTpkName, TargetTpkOrdinal,
            TargetTextureOrdinal, TargetTextureName, TargetBinKey, TargetCompression);

    public static ContainerTextureOverride Create(long fngOffset, uint requestId, string requestName,
        string sourceName, byte[] pngData, string targetArchivePath = null, string targetTpkName = null,
        int targetTpkOrdinal = -1, int targetTextureOrdinal = -1, string targetTextureName = null,
        uint targetBinKey = 0, string targetCompression = null)
    {
        if (fngOffset < 0) throw new ArgumentOutOfRangeException(nameof(fngOffset));
        var bytes = ValidatePng(pngData);
        var (width, height) = ReadPngSize(bytes);
        return new ContainerTextureOverride(fngOffset, requestId, requestName, width, height,
            NormalizeSourceName(sourceName, "replacement.png"), TextureInputKind.Png, "PNG", 1,
            bytes, Hash(bytes), targetArchivePath, targetTpkName, targetTpkOrdinal,
            targetTextureOrdinal, targetTextureName, targetBinKey, targetCompression);
    }

    /// <summary>
    /// Stores DDS bytes verbatim. When target metadata is supplied the DDS FourCC, dimensions and
    /// mip count are validated immediately; mismatches are rejected instead of being converted.
    /// </summary>
    public static ContainerTextureOverride CreateDds(long fngOffset, uint requestId,
        string requestName, string sourceName, byte[] ddsData, string targetArchivePath = null,
        string targetTpkName = null, int targetTpkOrdinal = -1, int targetTextureOrdinal = -1,
        string targetTextureName = null, uint targetBinKey = 0, string targetCompression = null,
        int targetWidth = 0, int targetHeight = 0, int targetMipCount = -1)
    {
        if (fngOffset < 0) throw new ArgumentOutOfRangeException(nameof(fngOffset));
        var bytes = ValidateDds(ddsData);
        var info = DdsTextureInfo.Parse(bytes, NormalizeSourceName(sourceName, "replacement.dds"));
        if (!string.IsNullOrWhiteSpace(targetCompression))
            info.ValidateAgainstTarget(targetCompression, targetWidth, targetHeight, targetMipCount,
                string.IsNullOrWhiteSpace(targetTextureName) ? requestName : targetTextureName);
        return new ContainerTextureOverride(fngOffset, requestId, requestName, info.Width,
            info.Height, NormalizeSourceName(sourceName, "replacement.dds"), TextureInputKind.Dds,
            info.Format, info.MipCount, bytes, Hash(bytes), targetArchivePath, targetTpkName,
            targetTpkOrdinal, targetTextureOrdinal, targetTextureName, targetBinKey,
            targetCompression);
    }

    public static ContainerTextureOverride Load(long fngOffset, uint requestId, string requestName,
        int width, int height, string sourceName, byte[] pngData, string expectedSha256,
        string targetArchivePath = null, string targetTpkName = null, int targetTpkOrdinal = -1,
        int targetTextureOrdinal = -1, string targetTextureName = null, uint targetBinKey = 0,
        string targetCompression = null) =>
        Load(fngOffset, requestId, requestName, width, height, sourceName, pngData, expectedSha256,
            TextureInputKind.Png, "PNG", 1, targetArchivePath, targetTpkName, targetTpkOrdinal,
            targetTextureOrdinal, targetTextureName, targetBinKey, targetCompression);

    public static ContainerTextureOverride Load(long fngOffset, uint requestId, string requestName,
        int width, int height, string sourceName, byte[] data, string expectedSha256,
        TextureInputKind inputKind, string format, int mipCount, string targetArchivePath = null,
        string targetTpkName = null, int targetTpkOrdinal = -1, int targetTextureOrdinal = -1,
        string targetTextureName = null, uint targetBinKey = 0, string targetCompression = null)
    {
        if (fngOffset < 0) throw new InvalidDataException("贴图覆盖的 FNG 偏移无效。");
        byte[] bytes;
        int actualWidth;
        int actualHeight;
        string actualFormat;
        int actualMipCount;
        if (inputKind == TextureInputKind.Dds)
        {
            bytes = ValidateDds(data);
            var info = DdsTextureInfo.Parse(bytes, sourceName);
            var normalized = DdsTextureRules.NormalizeFormat(format);
            if (!string.Equals(info.Format, normalized, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"贴图覆盖的 DDS 格式与工程记录不符：记录 {normalized}，文件 {info.Format}。");
            if (info.Width != width || info.Height != height)
                throw new InvalidDataException(
                    $"贴图覆盖的 DDS 尺寸与工程记录不符：记录 {width} × {height}，文件 {info.Width} × {info.Height}。");
            if (info.MipCount != mipCount)
                throw new InvalidDataException(
                    $"贴图覆盖的 DDS mip 数与工程记录不符：记录 {mipCount}，文件 {info.MipCount}。");
            actualWidth = info.Width;
            actualHeight = info.Height;
            actualFormat = normalized;
            actualMipCount = info.MipCount;
        }
        else
        {
            bytes = ValidatePng(data);
            (actualWidth, actualHeight) = ReadPngSize(bytes);
            if (actualWidth != width || actualHeight != height)
                throw new InvalidDataException("贴图覆盖的 PNG 尺寸与工程记录不符。");
            if (!string.IsNullOrWhiteSpace(format) &&
                !string.Equals(format, "PNG", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PNG 贴图覆盖的格式记录无效。");
            actualFormat = "PNG";
            actualMipCount = 1;
        }

        var sha256 = Hash(bytes);
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("贴图覆盖的 SHA256 与工程记录不符。");
        return new ContainerTextureOverride(fngOffset, requestId, requestName, actualWidth,
            actualHeight, NormalizeSourceName(sourceName, inputKind == TextureInputKind.Dds
                ? "replacement.dds" : "replacement.png"), inputKind, actualFormat, actualMipCount,
            bytes, sha256, targetArchivePath, targetTpkName, targetTpkOrdinal,
            targetTextureOrdinal, targetTextureName, targetBinKey, targetCompression);
    }

    /// <summary>Re-validates the stored payload before it is written into a project.</summary>
    public void ValidateStoredPayload()
    {
        if (Data == null || Data.Length == 0)
            throw new InvalidDataException("贴图覆盖为空。");
        if (IsDds)
        {
            var info = DdsTextureInfo.Parse(Data, SourceName);
            if (!string.Equals(info.Format, Format, StringComparison.OrdinalIgnoreCase) ||
                info.Width != Width || info.Height != Height || info.MipCount != MipCount)
                throw new InvalidDataException("贴图覆盖的 DDS 元数据与记录不一致。");
        }
        else
        {
            var bytes = ValidatePng(Data);
            var (width, height) = ReadPngSize(bytes);
            if (width != Width || height != Height || MipCount != 1 ||
                !string.Equals(Format, "PNG", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("贴图覆盖的 PNG 元数据与记录不一致。");
        }
        if (!string.Equals(Hash(Data), Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("贴图覆盖的 SHA256 与载荷不符。");
    }

    private static byte[] ValidatePng(byte[] pngData)
    {
        if (pngData == null || pngData.Length == 0)
            throw new InvalidDataException("贴图覆盖为空。");
        if (pngData.Length > MaxPngBytes)
            throw new InvalidDataException("PNG 贴图覆盖超过 32 MiB 上限。");
        return (byte[])pngData.Clone();
    }

    private static byte[] ValidateDds(byte[] ddsData)
    {
        if (ddsData == null || ddsData.Length == 0)
            throw new InvalidDataException("DDS 贴图覆盖为空。");
        if (ddsData.Length > MaxDdsBytes)
            throw new InvalidDataException("DDS 贴图覆盖超过 64 MiB 上限。");
        return (byte[])ddsData.Clone();
    }

    private static string NormalizeSourceName(string sourceName, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(sourceName) ? fallback : Path.GetFileName(sourceName);
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static (int Width, int Height) ReadPngSize(byte[] pngData)
    {
        if (pngData == null || pngData.Length < 24)
            throw new InvalidDataException("PNG 头不足 24 字节。");
        ReadOnlySpan<byte> signature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (!pngData.AsSpan(0, 8).SequenceEqual(signature))
            throw new InvalidDataException("不是有效的 PNG 文件。");
        if (pngData[12] != 'I' || pngData[13] != 'H' || pngData[14] != 'D' || pngData[15] != 'R')
            throw new InvalidDataException("PNG 缺少 IHDR 头。");
        var width = BinaryPrimitives.ReadInt32BigEndian(pngData.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(pngData.AsSpan(20, 4));
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new InvalidDataException($"PNG 尺寸超出支持范围：{width} × {height}。");
        return (width, height);
    }
}
