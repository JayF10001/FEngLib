using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FEngLib.Packages;

namespace FEngLib.Assets;

public enum TextureLookupStatus
{
    Found,
    NoRequest,
    WrongType,
    NotFound,
    Ambiguous,
    Error,
}

public sealed class TextureCandidate
{
    public string Source { get; init; }
    public string ArchivePath { get; init; }
    public string TpkName { get; init; }
    public int TpkOrdinal { get; init; }
    public long? TpkBlockOffset { get; init; }
    public int TextureOrdinal { get; init; }
    public string TextureName { get; init; }
    public uint BinKey { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Compression { get; init; }
    public BinaryTextureInfo Entry { get; init; }
}

public sealed class TextureLookupResult
{
    public TextureLookupStatus Status { get; init; }
    public string RequestedName { get; init; }
    public uint RequestedId { get; init; }
    public byte[] PngData { get; init; }
    public string Source { get; init; }
    public string SourceTexture { get; init; }
    public string Message { get; init; }
    public IReadOnlyList<TextureCandidate> Candidates { get; init; } = Array.Empty<TextureCandidate>();

    /// <summary>The exact TPK entry when an archive source produced this result.</summary>
    public BinaryTextureInfo Entry { get; init; }

    /// <summary>Number of exact archive entries with this name; the first one is the render hit.</summary>
    public int MatchingEntryCount { get; init; } = 1;

    /// <summary>Extra identity note, e.g. duplicate exact names resolved by archive order.</summary>
    public string Note { get; init; }

    /// <summary>True when a .fengproj texture override produced this result.</summary>
    public bool IsProjectOverride { get; init; }

    /// <summary>Dimensions/format for non-TPK sources (project overrides).</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    public string Compression { get; init; }

    public bool IsFound => Status == TextureLookupStatus.Found && PngData is { Length: > 0 };
    public bool IsArchiveEntry => Entry != null;
}


public interface ITextureSource
{
    string Name { get; }

    /// <summary>Loads any external index. Called once before the first resolve, or explicitly.</summary>
    void Preload();

    TextureLookupResult TryResolve(string textureName, uint resourceId);
}

public interface ITextureProvider
{
    TextureLookupResult Resolve(ResourceRequest request);
}

/// <summary>
/// Ordered texture lookup: loose PNG overrides first, then configured game archives. This keeps the
/// existing "textures beside the FNG" workflow as an override while making game packages usable
/// without preparing a loose PNG for every resource.
/// </summary>
public sealed class TextureResolver : ITextureProvider
{
    private readonly List<ITextureSource> _sources = new();

    public TextureResolver(IEnumerable<ITextureSource> sources)
    {
        if (sources != null) _sources.AddRange(sources.Where(s => s != null));
    }

    public IReadOnlyList<ITextureSource> Sources => _sources;
    public LoosePngTextureSource Loose { get; private set; }
    private ITextureSource _projectOverride;

    public void SetTextureDirectory(string directory)
    {
        if (Loose == null)
        {
            Loose = new LoosePngTextureSource(directory);
            _sources.Insert(_projectOverride == null ? 0 : 1, Loose);
        }
        else
        {
            Loose.SetDirectory(directory);
        }
    }

    /// <summary>Project-level PNG overrides stay above loose PNGs, the current container and archives.</summary>
    public void SetProjectOverrideSource(ITextureSource source)
    {
        if (_projectOverride != null) _sources.Remove(_projectOverride);
        _projectOverride = source;
        if (source != null) _sources.Insert(0, source);
    }

    public void Preload()
    {
        foreach (var source in _sources)
        {
            try { source.Preload(); }
            catch (Exception) { /* a broken source must not hide the others */ }
        }
    }

    public TextureLookupResult Resolve(ResourceRequest request)
    {
        if (request == null)
            return new TextureLookupResult { Status = TextureLookupStatus.NoRequest };

        if (request.Type is not (ResourceType.Image or ResourceType.MultiImage))
            return new TextureLookupResult
            {
                Status = TextureLookupStatus.WrongType,
                RequestedName = request.Name,
                RequestedId = request.ID,
                Message = $"资源类型 {request.Type} 不是图片",
            };

        var name = NormalizeRequestName(request.Name);
        TextureLookupResult ambiguous = null;
        TextureLookupResult error = null;
        foreach (var source in _sources)
        {
            TextureLookupResult result;
            try { result = source.TryResolve(name, request.ID); }
            catch (Exception ex)
            {
                error ??= new TextureLookupResult
                {
                    Status = TextureLookupStatus.Error,
                    RequestedName = name,
                    RequestedId = request.ID,
                    Message = ex.Message,
                };
                continue;
            }

            if (result == null) continue;
            if (result.IsFound) return result;
            if (result.Status == TextureLookupStatus.Ambiguous && ambiguous == null)
                ambiguous = result;
            else if (result.Status == TextureLookupStatus.Error && error == null)
                error = result;
        }

        if (ambiguous != null) return ambiguous;
        if (error != null) return error;
        return new TextureLookupResult
        {
            Status = TextureLookupStatus.NotFound,
            RequestedName = name,
            RequestedId = request.ID,
            Message = "没有找到匹配的 PNG 或原档案贴图",
        };
    }

    public static string NormalizeRequestName(string path) =>
        Path.GetFileNameWithoutExtension((path ?? "").Replace('\\', '/')).Trim();
}
