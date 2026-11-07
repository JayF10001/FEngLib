using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace FEngLib.Assets;

/// <summary>
/// One PNG texture override stored in a .fengproj. The override is scoped to one FNG block and one
/// resource request; it never touches the container or the game directory. The optional target
/// fields identify the underlying TPK entry for a later container-copy write-back.
/// </summary>
public sealed class ContainerTextureOverride
{
    public const int MaxPngBytes = 32 * 1024 * 1024;
    private const int MaxDimension = 16384;

    private ContainerTextureOverride(long fngOffset, uint requestId, string requestName, int width,
        int height, string sourceName, byte[] pngData, string sha256,
        string targetArchivePath, string targetTpkName, int targetTpkOrdinal,
        int targetTextureOrdinal, string targetTextureName, uint targetBinKey, string targetCompression)
    {
        FngOffset = fngOffset;
        RequestId = requestId;
        RequestName = requestName ?? "";
        Width = width;
        Height = height;
        SourceName = sourceName ?? "";
        PngData = pngData;
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
    public byte[] PngData { get; }
    public string Sha256 { get; }

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

    public ContainerTextureOverride WithTargetArchive(string targetArchivePath) =>
        new(FngOffset, RequestId, RequestName, Width, Height, SourceName, PngData, Sha256,
            targetArchivePath, TargetTpkName, TargetTpkOrdinal, TargetTextureOrdinal,
            TargetTextureName, TargetBinKey, TargetCompression);

    public static ContainerTextureOverride Create(long fngOffset, uint requestId, string requestName,
        string sourceName, byte[] pngData, string targetArchivePath = null, string targetTpkName = null,
        int targetTpkOrdinal = -1, int targetTextureOrdinal = -1, string targetTextureName = null,
        uint targetBinKey = 0, string targetCompression = null)
    {
        if (fngOffset < 0) throw new ArgumentOutOfRangeException(nameof(fngOffset));
        var bytes = ValidatePng(pngData);
        var (width, height) = ReadPngSize(bytes);
        return new ContainerTextureOverride(fngOffset, requestId, requestName, width, height,
            string.IsNullOrWhiteSpace(sourceName) ? "replacement.png" : Path.GetFileName(sourceName),
            bytes, Convert.ToHexString(SHA256.HashData(bytes)),
            targetArchivePath, targetTpkName, targetTpkOrdinal, targetTextureOrdinal,
            targetTextureName, targetBinKey, targetCompression);
    }

    public static ContainerTextureOverride Load(long fngOffset, uint requestId, string requestName,
        int width, int height, string sourceName, byte[] pngData, string expectedSha256,
        string targetArchivePath = null, string targetTpkName = null, int targetTpkOrdinal = -1,
        int targetTextureOrdinal = -1, string targetTextureName = null, uint targetBinKey = 0,
        string targetCompression = null)
    {
        if (fngOffset < 0) throw new InvalidDataException("贴图覆盖的 FNG 偏移无效。");
        var bytes = ValidatePng(pngData);
        var (actualWidth, actualHeight) = ReadPngSize(bytes);
        if (actualWidth != width || actualHeight != height)
            throw new InvalidDataException("贴图覆盖的 PNG 尺寸与工程记录不符。");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("贴图覆盖的 SHA256 与工程记录不符。");
        return new ContainerTextureOverride(fngOffset, requestId, requestName, actualWidth, actualHeight,
            sourceName, bytes, sha256, targetArchivePath, targetTpkName, targetTpkOrdinal,
            targetTextureOrdinal, targetTextureName, targetBinKey, targetCompression);
    }

    private static byte[] ValidatePng(byte[] pngData)
    {
        if (pngData == null || pngData.Length == 0)
            throw new InvalidDataException("贴图覆盖为空。");
        if (pngData.Length > MaxPngBytes)
            throw new InvalidDataException("贴图覆盖超过 32 MiB 上限。");
        return (byte[])pngData.Clone();
    }

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
