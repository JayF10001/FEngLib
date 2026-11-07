using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FEngLib.Assets;

/// <summary>
/// Optional loose-PNG override. Names are normalized so common export suffixes such as
/// "NAME (1).png" and "NAME (0x1234ABCD).png" can still match their FNG resource.
/// </summary>
public sealed class LoosePngTextureSource : ITextureSource
{
    private static readonly Regex HashSuffix =
        new(@"\s*\(0x([0-9a-fA-F]{1,8})\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DupSuffix = new(@"\s*\(\d+\)\s*$", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _normalized = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _byHash = new();
    private string _directory;
    private bool _loaded;

    public LoosePngTextureSource(string directory)
    {
        SetDirectory(directory);
    }

    public string Name => "textures/ PNG";
    public string DirectoryPath => _directory;

    public void SetDirectory(string directory)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
        _exact.Clear();
        _normalized.Clear();
        _byHash.Clear();
        _loaded = false;
    }

    public void Preload()
    {
        if (_loaded) return;
        _loaded = true;
        if (string.IsNullOrWhiteSpace(_directory) || !Directory.Exists(_directory)) return;

        foreach (var file in Directory.EnumerateFiles(_directory, "*.png", SearchOption.TopDirectoryOnly))
        {
            var stem = Path.GetFileNameWithoutExtension(file) ?? "";
            if (stem.Length == 0) continue;
            _exact[stem] = file;
            var normalized = Normalize(stem);
            if (normalized.Length > 0) _normalized[normalized] = file;
            var hash = ParseHash(stem);
            if (hash.HasValue) _byHash[hash.Value] = file;
        }
    }

    public TextureLookupResult TryResolve(string textureName, uint resourceId)
    {
        Preload();
        var name = TextureResolver.NormalizeRequestName(textureName);
        if (name.Length == 0) return null;

        if (!_exact.TryGetValue(name, out var path) &&
            !_normalized.TryGetValue(Normalize(name), out path) &&
            !(resourceId != 0 && _byHash.TryGetValue(resourceId, out path)))
            return null;

        try
        {
            return new TextureLookupResult
            {
                Status = TextureLookupStatus.Found,
                RequestedName = name,
                RequestedId = resourceId,
                PngData = File.ReadAllBytes(path),
                Source = path,
                SourceTexture = Path.GetFileName(path),
            };
        }
        catch (Exception ex)
        {
            return new TextureLookupResult
            {
                Status = TextureLookupStatus.Error,
                RequestedName = name,
                RequestedId = resourceId,
                Source = path,
                Message = ex.Message,
            };
        }
    }

    public static string Normalize(string stem)
    {
        stem = HashSuffix.Replace(stem ?? "", "");
        stem = DupSuffix.Replace(stem, "");
        return stem.Trim();
    }

    private static uint? ParseHash(string stem)
    {
        var match = HashSuffix.Match(stem ?? "");
        if (!match.Success) return null;
        return uint.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
