using System;
using System.IO;
using System.Text.Json;

namespace FEngLib.Assets;

/// <summary>Publish a complete JSON file without ever deleting the last readable version first.</summary>
internal static class AtomicJsonFile
{
    public static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path); // a competing creation must fail, never overwrite
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { /* the previous published version is still authoritative */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    // Older builds deleted the published JSON before moving path.tmp. Recover only this known
    // legacy name, after verifying JSON syntax; ambiguous/invalid files must surface an error.
    public static void RecoverLegacyTemporary(string path)
    {
        if (File.Exists(path) || !File.Exists(path + ".tmp")) return;
        using (var json = JsonDocument.Parse(File.ReadAllText(path + ".tmp"))) { }
        File.Move(path + ".tmp", path);
    }
}
