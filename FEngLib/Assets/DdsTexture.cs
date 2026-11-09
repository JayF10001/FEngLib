using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FEngLib.Assets;

/// <summary>
/// Strict, read-only DDS header description for the texture override path. The parser only accepts
/// a 2D DXT1/DXT3/DXT5 surface whose mip chain exactly fills the payload. It never generates,
/// crops or discards mip levels; any mismatch is reported to the caller.
/// </summary>
public sealed class DdsTextureInfo
{
    public const int HeaderLength = 128;
    public const int MaxDimension = 16384;
    public const string Dxt1 = "DXT1";
    public const string Dxt3 = "DXT3";
    public const string Dxt5 = "DXT5";

    private const uint Magic = 0x20534444; // "DDS "
    private const uint HeaderSize = 124;
    private const uint DdsdHeight = 0x2;
    private const uint DdsdWidth = 0x4;
    private const uint DdsdPixelFormat = 0x1000;
    private const uint DdpfFourCc = 0x4;
    private const uint Caps2Cubemap = 0x200;
    private const uint Caps2CubemapFaces = 0xFE00;
    private const uint Caps2Volume = 0x200000;

    private DdsTextureInfo(string format, int width, int height, int mipCount, int payloadLength)
    {
        Format = format;
        Width = width;
        Height = height;
        MipCount = mipCount;
        PayloadLength = payloadLength;
    }

    /// <summary>DDS FourCC, one of DXT1/DXT3/DXT5.</summary>
    public string Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int MipCount { get; }
    public int PayloadLength { get; }

    public static bool LooksLikeDds(byte[] data) =>
        data is { Length: >= 4 } &&
        data[0] == (byte)'D' && data[1] == (byte)'D' && data[2] == (byte)'S' && data[3] == (byte)' ';

    /// <summary>
    /// Validates a DDS byte array and returns its exact 2D DXT description. The data length must be
    /// the full header plus the complete mip chain for the declared format.
    /// </summary>
    public static DdsTextureInfo Parse(byte[] data, string sourceName = null)
    {
        if (data == null || data.Length < HeaderLength)
            throw new InvalidDataException($"不是有效的 DDS 文件：{Describe(sourceName)}不足 128 字节。");
        var span = data.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
            throw new InvalidDataException($"不是有效的 DDS 文件：{Describe(sourceName)}缺少 DDS 标识。");
        if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4)) != HeaderSize)
            throw new InvalidDataException($"DDS 头大小无效：{Describe(sourceName)}期望 124。");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
        if ((flags & (DdsdHeight | DdsdWidth | DdsdPixelFormat)) !=
            (DdsdHeight | DdsdWidth | DdsdPixelFormat))
            throw new InvalidDataException($"DDS 头缺少 2D 贴图标志：{Describe(sourceName)}。");

        var height = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(16, 4));
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new InvalidDataException(
                $"DDS 尺寸超出支持范围：{Describe(sourceName)}{width} × {height}。");

        var depth = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4));
        var mipCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(28, 4));
        if (mipCount == 0) mipCount = 1;
        var maxMipCount = MaximumMipCount(width, height);
        if (mipCount < 1 || mipCount > maxMipCount)
            throw new InvalidDataException(
                $"DDS mip 数无效：{Describe(sourceName)}{mipCount}（{width} × {height} 最多 {maxMipCount} 级）。");

        var caps2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(112, 4));
        if ((caps2 & Caps2Cubemap) != 0 || (caps2 & Caps2CubemapFaces) != 0)
            throw new InvalidDataException($"不支持 DDS cubemap：{Describe(sourceName)}。");
        if ((caps2 & Caps2Volume) != 0 || depth > 1)
            throw new InvalidDataException($"不支持 DDS volume 贴图：{Describe(sourceName)}。");

        if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(76, 4)) != 32)
            throw new InvalidDataException($"DDS 像素格式大小无效：{Describe(sourceName)}期望 32。");
        var pixelFlags = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(80, 4));
        if ((pixelFlags & DdpfFourCc) == 0)
            throw new InvalidDataException(
                $"只接受 FourCC 压缩的 2D DXT1/DXT3/DXT5：{Describe(sourceName)}。");
        var fourCc = Encoding.ASCII.GetString(data, 84, 4);
        var format = fourCc switch
        {
            Dxt1 => Dxt1,
            Dxt3 => Dxt3,
            Dxt5 => Dxt5,
            "DX10" => throw new InvalidDataException($"不支持 DX10 扩展头的 DDS：{Describe(sourceName)}。"),
            _ => throw new InvalidDataException(
                $"不支持 DDS FourCC “{fourCc}”：{Describe(sourceName)}只接受 DXT1/DXT3/DXT5。"),
        };

        var expectedLength = ExpectedPayloadLength(width, height, mipCount, format);
        var actualLength = data.Length - HeaderLength;
        if (actualLength != expectedLength)
            throw new InvalidDataException(
                $"DDS 像素数据长度不符：{Describe(sourceName)}期望 {expectedLength:N0} 字节" +
                $"（{format}，{mipCount} mip），实际 {actualLength:N0} 字节；不会自动生成、裁剪或丢弃 mip。");
        return new DdsTextureInfo(format, width, height, mipCount, actualLength);
    }

    /// <summary>
    /// Compares this file with the target TPK entry. A zero target mip count is normalized to one,
    /// matching both the DDS header convention and Binary's own generator.
    /// </summary>
    public void ValidateAgainstTarget(string targetCompression, int targetWidth, int targetHeight,
        int targetMipCount, string targetName = null)
    {
        var label = string.IsNullOrWhiteSpace(targetName) ? "" : targetName + "：";
        var expectedFormat = DdsTextureRules.FormatForCompression(targetCompression);
        if (expectedFormat == null)
            throw new NotSupportedException(
                $"{label}目标格式 {targetCompression ?? "(未知)"} 暂不支持 DDS 写回。");
        if (!string.Equals(Format, expectedFormat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"{label}DDS 格式不一致：目标 {expectedFormat}，文件 {Format}。");
        if ((targetWidth > 0 && Width != targetWidth) ||
            (targetHeight > 0 && Height != targetHeight))
            throw new InvalidDataException(
                $"{label}DDS 尺寸不一致：目标 {targetWidth} × {targetHeight}，文件 {Width} × {Height}。");
        if (targetMipCount >= 0)
        {
            var expectedMips = targetMipCount <= 0 ? 1 : targetMipCount;
            if (MipCount != expectedMips)
                throw new InvalidDataException(
                    $"{label}DDS mip 数不一致：目标 {expectedMips}，文件 {MipCount}；不会自动生成或丢弃 mip。");
        }
    }

    public static int ExpectedPayloadLength(int width, int height, int mipCount, string format)
    {
        var blockBytes = (format ?? "").ToUpperInvariant() switch
        {
            Dxt1 => 8,
            Dxt3 => 16,
            Dxt5 => 16,
            _ => throw new NotSupportedException("不支持 DDS 格式：" + format),
        };
        long total = 0;
        var levelWidth = width;
        var levelHeight = height;
        for (var level = 0; level < mipCount; level++)
        {
            long blocksX = (levelWidth + 3) / 4;
            long blocksY = (levelHeight + 3) / 4;
            total += blocksX * blocksY * blockBytes;
            if (total > int.MaxValue)
                throw new InvalidDataException("DDS mip 链超出支持大小。");
            levelWidth = Math.Max(1, levelWidth / 2);
            levelHeight = Math.Max(1, levelHeight / 2);
        }
        return (int)total;
    }

    public static int MaximumMipCount(int width, int height)
    {
        var count = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            count++;
        }
        return count;
    }

    private static string Describe(string sourceName) =>
        string.IsNullOrWhiteSpace(sourceName) ? "" : sourceName + " ";
}

/// <summary>Maps between TPK compression names and DDS FourCC codes.</summary>
public static class DdsTextureRules
{
    public static string FormatForCompression(string compression) =>
        (compression ?? "").ToUpperInvariant() switch
        {
            "TEXCOMP_DXTC1" => DdsTextureInfo.Dxt1,
            "TEXCOMP_DXTC3" => DdsTextureInfo.Dxt3,
            "TEXCOMP_DXTC5" => DdsTextureInfo.Dxt5,
            _ => null,
        };

    public static string CompressionForFormat(string format) =>
        (format ?? "").ToUpperInvariant() switch
        {
            DdsTextureInfo.Dxt1 => "TEXCOMP_DXTC1",
            DdsTextureInfo.Dxt3 => "TEXCOMP_DXTC3",
            DdsTextureInfo.Dxt5 => "TEXCOMP_DXTC5",
            _ => null,
        };

    public static string NormalizeFormat(string format)
    {
        var normalized = (format ?? "").Trim().ToUpperInvariant();
        return normalized switch
        {
            DdsTextureInfo.Dxt1 or DdsTextureInfo.Dxt3 or DdsTextureInfo.Dxt5 => normalized,
            _ => throw new InvalidDataException("DDS 格式必须是 DXT1、DXT3 或 DXT5：" + (format ?? "(空)")),
        };
    }

    public static bool IsSupportedCompression(string compression) =>
        FormatForCompression(compression) != null;
}
