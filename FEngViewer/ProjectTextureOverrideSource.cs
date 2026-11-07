using System;
using System.Collections.Generic;
using System.Linq;
using FEngLib.Assets;

namespace FEngViewer;

/// <summary>Read-only resolver source for PNG overrides stored in the current .fengproj.</summary>
internal sealed class ProjectTextureOverrideSource : ITextureSource
{
    private readonly IReadOnlyDictionary<uint, ContainerTextureOverride> _overrides;

    public ProjectTextureOverrideSource(long fngOffset, IEnumerable<ContainerTextureOverride> overrides)
    {
        _overrides = (overrides ?? Array.Empty<ContainerTextureOverride>())
            .Where(texture => texture.FngOffset == fngOffset)
            .ToDictionary(texture => texture.RequestId);
    }

    public string Name => "工程贴图覆盖";
    public int Count => _overrides.Count;

    public void Preload()
    {
    }

    public TextureLookupResult TryResolve(string textureName, uint resourceId)
    {
        if (!_overrides.TryGetValue(resourceId, out var texture)) return null;
        var name = TextureResolver.NormalizeRequestName(textureName);
        var stored = TextureResolver.NormalizeRequestName(texture.RequestName);
        if (stored.Length > 0 && name.Length > 0 &&
            !string.Equals(name, stored, StringComparison.OrdinalIgnoreCase)) return null;
        return new TextureLookupResult
        {
            Status = TextureLookupStatus.Found,
            RequestedName = name,
            RequestedId = resourceId,
            PngData = texture.PngData,
            Source = "工程覆盖 · " + texture.SourceName,
            SourceTexture = texture.RequestName,
            IsProjectOverride = true,
            Width = texture.Width,
            Height = texture.Height,
            Compression = "PNG",
        };
    }
}
