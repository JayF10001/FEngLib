using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FEngLib.Assets;

/// <summary>
/// Read-only TPK source. It indexes texture names from one or more game archives and turns a match
/// into PNG bytes on demand; no texture is extracted to disk. Catalogs stay in memory so the
/// viewer can locate the exact entry and reuse the decoded PNG cache.
/// </summary>
public sealed class ArchiveTextureSource : ITextureSource
{
    private readonly List<string> _archives;
    private readonly List<BinaryTextureInfo> _textures = new();
    private readonly Dictionary<string, BinaryTextureArchive> _catalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<BinaryTextureInfo>> _exact =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _errors = new();
    private bool _loaded;

    public ArchiveTextureSource(IEnumerable<string> archives)
    {
        _archives = (archives ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string Name => "游戏档案 TPK";
    public IReadOnlyList<string> Archives => _archives;
    public IReadOnlyList<string> Errors => _errors;
    public int TextureCount => _textures.Count;
    public IReadOnlyList<BinaryTextureInfo> Textures => _textures;

    /// <summary>Loaded catalogs, keyed by the full archive path.</summary>
    public IReadOnlyDictionary<string, BinaryTextureArchive> Catalogs => _catalogs;

    /// <summary>Returns the catalog for an archive path, or null when it was not loaded.</summary>
    public BinaryTextureArchive GetCatalog(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath)) return null;
        try { archivePath = Path.GetFullPath(archivePath); }
        catch (Exception) { return null; }
        return _catalogs.TryGetValue(archivePath, out var catalog) ? catalog : null;
    }

    public void Preload()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var archive in _archives)
        {
            try
            {
                var catalog = BinaryTextureArchive.Load(archive);
                _catalogs[archive] = catalog;
                foreach (var texture in catalog.Textures)
                {
                    if (string.IsNullOrWhiteSpace(texture.Name)) continue;
                    _textures.Add(texture);
                    if (!_exact.TryGetValue(texture.Name, out var list))
                        _exact[texture.Name] = list = new List<BinaryTextureInfo>();
                    list.Add(texture);
                }
            }
            catch (Exception ex)
            {
                _errors.Add($"{Path.GetFileName(archive)}: {ex.Message}");
            }
        }
    }

    public TextureLookupResult TryResolve(string textureName, uint resourceId)
    {
        Preload();
        var name = TextureResolver.NormalizeRequestName(textureName);
        if (name.Length == 0) return null;

        if (_exact.TryGetValue(name, out var exact) && exact.Count > 0)
        {
            var candidates = exact.Count > 1 ? exact.Select(ToCandidate).ToList() : null;
            return FromTexture(name, resourceId, exact[0], exact.Count, candidates);
        }

        if (name.Length >= 20)
        {
            var candidates = _textures
                .Where(t => t.Name.Length >= 20 && name.StartsWith(t.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 1)
                return FromTexture(name, resourceId, candidates[0]);
            if (candidates.Count > 1)
                return new TextureLookupResult
                {
                    Status = TextureLookupStatus.Ambiguous,
                    RequestedName = name,
                    RequestedId = resourceId,
                    Message = $"名称被 TPK 头截断，有 {candidates.Count} 个候选",
                    Candidates = candidates.Select(ToCandidate).ToList(),
                };
        }

        if (_errors.Count > 0 && _textures.Count == 0)
            return new TextureLookupResult
            {
                Status = TextureLookupStatus.Error,
                RequestedName = name,
                RequestedId = resourceId,
                Message = string.Join("; ", _errors),
            };
        return null;
    }

    private TextureLookupResult FromTexture(string name, uint resourceId, BinaryTextureInfo texture,
        int matchingEntryCount = 1, IReadOnlyList<TextureCandidate> candidates = null)
    {
        var note = matchingEntryCount > 1
            ? $"同名条目 {matchingEntryCount} 个，按档案顺序命中第 1 个"
            : null;
        try
        {
            return new TextureLookupResult
            {
                Status = TextureLookupStatus.Found,
                RequestedName = name,
                RequestedId = resourceId,
                PngData = texture.GetPng(),
                Source = $"{texture.ArchivePath} :: {texture.TpkName}",
                SourceTexture = texture.Name,
                Entry = texture,
                MatchingEntryCount = matchingEntryCount,
                Note = note,
                Candidates = candidates ?? Array.Empty<TextureCandidate>(),
            };
        }
        catch (Exception ex)
        {
            return new TextureLookupResult
            {
                Status = TextureLookupStatus.Error,
                RequestedName = name,
                RequestedId = resourceId,
                Message = ex.Message,
                Source = texture.ArchivePath,
                SourceTexture = texture.Name,
                Entry = texture,
                MatchingEntryCount = matchingEntryCount,
                Note = note,
                Candidates = candidates ?? Array.Empty<TextureCandidate>(),
            };
        }
    }

    private static TextureCandidate ToCandidate(BinaryTextureInfo texture) => new()
    {
        Source = $"{texture.ArchivePath} :: {texture.TpkName}",
        ArchivePath = texture.ArchivePath,
        TpkName = texture.TpkName,
        TpkOrdinal = texture.TpkOrdinal,
        TpkBlockOffset = texture.TpkBlockOffset,
        TextureOrdinal = texture.TextureOrdinal,
        TextureName = texture.Name,
        BinKey = texture.BinKey,
        Width = texture.Width,
        Height = texture.Height,
        Compression = texture.Compression,
        Entry = texture,
    };
}
