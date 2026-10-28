using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace FEngLib.Assets;

internal static class BinaryRebuildValidation
{
    internal static List<string> OtherResources(string path, out bool jdlz)
    {
        var bytes = File.ReadAllBytes(path);
        jdlz = bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == 0x5A4C444A;
        if (jdlz) bytes = BinaryRuntime.Decompress(bytes);
        var result = new List<string>();
        for (var offset = 0; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 8) throw new InvalidDataException("容器块头被截断。");
            var id = BitConverter.ToUInt32(bytes, offset);
            var length = checked((int)BitConverter.ToUInt32(bytes, offset + 4) + 8);
            if (length > bytes.Length - offset) throw new InvalidDataException("容器块超出边界。");
            // TPK payloads are rebuilt by Nikki; their pixels and public properties are checked in the worker.
            // All remaining resource blocks (including unknown blocks) must remain byte-identical.
            if (id is not (0 or FngContainer.FngChunkId or FngContainer.CompressedFngChunkId or 0xB3300000 or 0x0003A100))
                result.Add(id.ToString("X8") + ":" + Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, length))));
            offset += length;
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    internal static void Verify(string path, ContainerWritePlan plan)
    {
        var container = FngContainer.Open(path);
        if (container.Entries.Count != plan.ExpectedFngs.Count || container.Entries.Any(e => !e.CanExtract) ||
            container.Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
            throw new InvalidDataException("重建后的 FNG 数量、唯一性或可读取性不符。");
        foreach (var entry in container.Entries)
        {
            if (!plan.ExpectedFngs.TryGetValue(entry.Name, out var hash))
                throw new InvalidDataException("重建后出现未知 FNG：" + entry.Name);
            var target = plan.Items.SingleOrDefault(i => string.Equals(i.PackageName, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (entry.Sha256 != (target?.ReplacementSha256 ?? hash))
                throw new InvalidDataException("重建后的 FNG 内容不符：" + entry.Name);
        }
        var resources = OtherResources(path, out var jdlz);
        if (jdlz != plan.PreserveJdlz || !resources.SequenceEqual(plan.ExpectedOtherResources))
            throw new InvalidDataException("重建后的压缩形式或非目标资源内容不符。");
    }
}
