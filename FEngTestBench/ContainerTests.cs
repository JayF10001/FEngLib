using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FEngLib;
using FEngLib.Assets;
using FEngLib.Packages;

namespace FEngTestBench;

/// <summary>Generated fixtures only; no copyrighted game data is required.</summary>
internal static class ContainerTests
{
    public static int Run(string outputDirectory)
    {
        var root = Path.Combine(Path.GetFullPath(outputDirectory), "container-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        void Reject<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new Exception(message);
        }
        string Save(string name, byte[] bytes)
        {
            var path = Path.Combine(root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        try
        {
            var fng = MakeFng("Duplicate.fng");
            // A signature inside another resource must not be treated as a top-level FNG.
            var unknown = Block(0x12345678, fng);
            var data = unknown.Concat(fng).Concat(fng).Concat(Block(0, new byte[4])).ToArray();
            var source = Save("fixture.bun", data);
            var container = FngContainer.Open(source);
            Check(container.BlockCount == 4 && container.Entries.Count == 2, "Wrong top-level block traversal.");
            Check(container.Entries.All(e => e.CanExtract && e.Name == "Duplicate.fng"), "FNG headers not recognized.");
            Check(container.Entries[0].Offset == unknown.Length, "Offset must include the outer header.");
            var extracted = container.Extract(container.Entries, Path.Combine(root, "extracted"));
            Check(extracted.Count == 2 && extracted[0].OutputPath != extracted[1].OutputPath, "Duplicate names collide.");
            foreach (var result in extracted)
            {
                Check(File.ReadAllBytes(result.OutputPath).SequenceEqual(fng), "Extraction changed bytes.");
                var manifest = JsonSerializer.Deserialize<FngExtraction>(File.ReadAllText(result.OutputPath + ".source.json"));
                Check(manifest.SourcePath == source && manifest.SourceSha256 == container.SourceSha256 &&
                    manifest.Offset == result.Offset && manifest.ExtractedSha256 == result.ExtractedSha256,
                    "Missing extraction provenance.");
                using var payload = new MemoryStream(File.ReadAllBytes(result.OutputPath).Skip(16).ToArray());
                using var reader = new BinaryReader(payload);
                Check(new FrontendPackageLoader(new HashResolver()).Load(reader).Name == "Duplicate.fng",
                    "Extracted FNG cannot be read by the existing model loader.");
            }
            Reject<IOException>(() => container.Extract(container.Entries, Path.Combine(root, "extracted")), "Overwrote existing outputs.");
            Check(File.ReadAllBytes(source).SequenceEqual(data), "Extraction modified source.");
            Reject<InvalidOperationException>(() => container.Extract(new[] { container.Entries[0], container.Entries[0] }, root), "Accepted duplicate selection.");
            var other = FngContainer.Open(source);
            Reject<InvalidOperationException>(() => container.Extract(other.Entries, root), "Accepted foreign entries.");
            data[data.Length - 1] ^= 1;
            File.WriteAllBytes(source, data);
            Reject<InvalidDataException>(() => container.Extract(container.Entries, Path.Combine(root, "changed")), "Accepted changed source with same size.");
            Check(!Directory.Exists(Path.Combine(root, "changed")), "Changed source created output directory.");

            var compressedPayload = new byte[20];
            Encoding.ASCII.GetBytes("JDLZ").CopyTo(compressedPayload, 4);
            var compressed = FngContainer.Open(Save("compressed.bun", Block(FngContainer.CompressedFngChunkId, compressedPayload)));
            Check(compressed.Entries.Single().Storage == FngStorageKind.Compressed &&
                compressed.Entries[0].Diagnostic.Contains("JDLZ"), "Compressed FNG was hidden or mislabeled.");
            Reject<InvalidOperationException>(() => compressed.Extract(compressed.Entries, root), "Extracted compressed bytes as a regular FNG.");
            var nested = FngContainer.Open(Save("nested.bun", Block(FngContainer.FngChunkId,
                Block(FngContainer.CompressedFngChunkId, compressedPayload))));
            Check(nested.Entries.Single().Storage == FngStorageKind.Compressed &&
                nested.Entries[0].Diagnostic.Contains("JDLZ"), "Nested compressed wrapper was not detected.");
            var invalid = FngContainer.Open(Save("invalid-fng.bun", Block(FngContainer.FngChunkId, new byte[40])));
            Check(invalid.Entries.Single().Storage == FngStorageKind.Invalid, "Malformed FNG was accepted.");
            Check(FngContainer.Open(Save("empty.bun", Array.Empty<byte>())).BlockCount == 0, "Empty container failed.");
            Reject<InvalidDataException>(() => FngContainer.Open(Save("short-header.bun", new byte[7])), "Accepted truncated header.");
            Reject<InvalidDataException>(() => FngContainer.Open(Save("short-payload.bun", fng.Take(fng.Length - 1).ToArray())), "Accepted truncated payload.");
            Reject<InvalidDataException>(() => FngContainer.Open(Save("huge-length.bun", new byte[] { 3, 2, 3, 0, 255, 255, 255, 255 })), "Accepted out-of-bounds unsigned length.");
            Reject<NotSupportedException>(() => FngContainer.Open(Save("whole-compressed.lzc", Encoding.ASCII.GetBytes("RAWW123456789012"))), "Misread whole-file compression as chunks.");
            var traversal = FngContainer.Open(Save("unsafe-name.bun", MakeFng("../unsafe.fng")));
            var safeDirectory = Path.Combine(root, "safe-names");
            var safe = traversal.Extract(traversal.Entries, safeDirectory).Single();
            Check(Path.GetDirectoryName(safe.OutputPath) == safeDirectory, "FNG name escaped output folder.");
            Console.WriteLine($"PASS: {checks} container checks. Fixtures: {root}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL after {checks} checks: {error}\nFixtures: {root}");
            return 1;
        }
    }

    private static byte[] MakeFng(string name)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        new FrontendChunkWriter(new Package { Name = name, Filename = "fixture" }).Write(writer);
        return Block(FngContainer.FngChunkId, stream.ToArray());
    }

    private static byte[] Block(uint id, byte[] payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(id);
        writer.Write(payload.Length);
        writer.Write(payload);
        return stream.ToArray();
    }
}
