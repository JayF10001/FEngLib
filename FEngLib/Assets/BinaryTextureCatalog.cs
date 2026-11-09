using System;
using System.Collections.Generic;

namespace FEngLib.Assets;

/// <summary>
/// Read-only view of one TPK texture. The Binary/Nikki object stays behind the adapter.
/// <para>
/// <see cref="TpkOrdinal"/> and <see cref="TextureOrdinal"/> are the catalog positions used to
/// tell same-named entries apart. <see cref="TpkBlockOffset"/> is only set when the container
/// index and the TPK catalog agree on the block count; offsets are logical offsets in the
/// (outer-decoded, when applicable) chunk stream.
/// </para>
/// </summary>
public sealed class BinaryTextureInfo
{
    internal object Instance { get; init; }
    internal object Owner { get; init; }

    public string ArchivePath { get; init; }
    public bool ArchiveHasOuterCompression { get; init; }
    public string TpkName { get; init; }
    public int TpkOrdinal { get; init; }
    public long? TpkBlockOffset { get; init; }
    public int TextureOrdinal { get; init; }
    public string Name { get; init; }
    public uint BinKey { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>TPK <c>Mipmaps</c> value; 1 means the base level only.</summary>
    public int MipCount { get; init; }
    public string Compression { get; init; }

    private byte[] _png;

    public byte[] GetPng() => _png ??= BinaryRuntime.ExportTexturePng(this);
}

public sealed class BinaryTextureArchive
{
    private BinaryTextureArchive(string path, bool hasOuterCompression, List<BinaryTextureInfo> textures)
    {
        Path = path;
        HasOuterCompression = hasOuterCompression;
        Textures = textures;
    }

    public string Path { get; }

    /// <summary>True when the whole archive is wrapped in JDLZ/HUFF/etc. and offsets are logical.</summary>
    public bool HasOuterCompression { get; }

    public IReadOnlyList<BinaryTextureInfo> Textures { get; }

    public static BinaryTextureArchive Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var textures = BinaryRuntime.LoadTextures(path, out var hasOuterCompression);
        return new BinaryTextureArchive(path, hasOuterCompression, textures);
    }
}
