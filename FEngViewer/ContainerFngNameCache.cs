using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FEngViewer;

/// <summary>
/// Best-effort cache of compressed FNG names, keyed by the source container SHA256. It stores only
/// offset -> name pairs so opening the same container again can fill the workspace captions
/// without decompressing every FNG entry again.
/// </summary>
internal static class ContainerFngNameCache
{
    public static Dictionary<long, string> Load(string sourceSha256)
    {
        var result = new Dictionary<long, string>();
        var path = PathFor(sourceSha256);
        if (path == null || !File.Exists(path)) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("SourceSha256", out var sha) ||
                !string.Equals(sha.GetString(), sourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("Names", out var names) ||
                names.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var property in names.EnumerateObject())
            {
                if (!long.TryParse(property.Name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset))
                    continue;
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                var name = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(name)) result[offset] = name;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken cache must never stop the container from opening.
        }
        return result;
    }

    public static void Save(string sourceSha256, IReadOnlyDictionary<long, string> names)
    {
        var path = PathFor(sourceSha256);
        if (path == null || names == null || names.Count == 0) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var payload = new
            {
                SourceSha256 = sourceSha256,
                Names = names.ToDictionary(
                    pair => pair.Key.ToString("X8", CultureInfo.InvariantCulture),
                    pair => pair.Value,
                    StringComparer.Ordinal)
            };
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Cache writes are best-effort.
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string PathFor(string sourceSha256)
    {
        if (string.IsNullOrWhiteSpace(sourceSha256) || sourceSha256.Length != 64) return null;
        foreach (var c in sourceSha256)
            if (!Uri.IsHexDigit(c)) return null;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FEngViewer", "fng-names", sourceSha256.ToUpperInvariant() + ".json");
    }
}
