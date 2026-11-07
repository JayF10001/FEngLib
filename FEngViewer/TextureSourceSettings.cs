using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FEngViewer;

/// <summary>Read-only game archives used by the texture resolver. Loose PNGs stay an override.</summary>
internal sealed class TextureSourceSettings
{
    public List<string> Archives { get; set; } = new();
}

internal static class TextureSourceStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FEngViewer", "asset-sources.json");

    public static TextureSourceSettings Load(string path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return new TextureSourceSettings();
            var settings = JsonSerializer.Deserialize<TextureSourceSettings>(File.ReadAllText(file))
                           ?? new TextureSourceSettings();
            settings.Archives = Normalize(settings.Archives);
            return settings;
        }
        catch (Exception)
        {
            return new TextureSourceSettings();
        }
    }

    public static bool Save(TextureSourceSettings settings, string path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            settings ??= new TextureSourceSettings();
            settings.Archives = Normalize(settings.Archives);
            File.WriteAllText(file, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static List<string> Normalize(IEnumerable<string> archives)
    {
        var result = new List<string>();
        foreach (var archive in archives ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(archive)) continue;
            string full;
            try { full = Path.GetFullPath(archive); }
            catch (Exception) { continue; }
            if (result.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(full);
        }
        return result;
    }
}
