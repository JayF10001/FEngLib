using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FEngLib.Packages;

/// <summary>Retains the source encoding for edits to existing fixed-size object fields and keys.
/// Structural edits use the normal writer. Unknown chunks, padding and unchanged tags are retained.</summary>
internal sealed class FngEncodingSnapshot
{
    private readonly byte[] original;
    private readonly byte[] baseline;
    private readonly Dictionary<string, Part> originalParts;
    private readonly Dictionary<string, Part> baselineParts;
    private record Part(int Offset, int Length, bool Editable);

    internal FngEncodingSnapshot(byte[] body, byte[] canonical)
    {
        original = new byte[body.Length + 8];
        BitConverter.GetBytes(0xE76E4546u).CopyTo(original, 0);
        BitConverter.GetBytes(body.Length).CopyTo(original, 4);
        body.CopyTo(original, 8);
        baseline = canonical;
        originalParts = Index(original);
        baselineParts = Index(baseline);
    }

    internal byte[] Restore(byte[] edited)
    {
        if (edited.SequenceEqual(baseline)) return (byte[])original.Clone();
        var current = Index(edited);
        if (current == null || originalParts == null || baselineParts == null ||
            !current.Keys.ToHashSet().SetEquals(baselineParts.Keys)) return null;
        var result = (byte[])original.Clone();
        foreach (var (key, before) in baselineParts)
        {
            var after = current[key];
            var oldBytes = baseline.AsSpan(before.Offset, before.Length);
            var newBytes = edited.AsSpan(after.Offset, after.Length);
            if (oldBytes.SequenceEqual(newBytes)) continue;
            if (!before.Editable || !originalParts.TryGetValue(key, out var source) ||
                source.Length != before.Length || after.Length != before.Length) return null;
            for (var i = 0; i < before.Length; i++)
            {
                if (oldBytes[i] == newBytes[i]) continue;
                // A normalized field cannot be translated back by guessing its source bytes.
                if (original[source.Offset + i] != oldBytes[i]) return null;
                result[source.Offset + i] = newBytes[i];
            }
        }
        return result;
    }

    private static Dictionary<string, Part> Index(byte[] bytes)
    {
        var result = new Dictionary<string, Part>();
        try { Walk(0, bytes.Length, "", false); return result; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException) { return null; }

        void Walk(int start, int end, string path, bool tags)
        {
            var counts = new Dictionary<uint, int>();
            while (start < end)
            {
                var header = tags ? 4 : 8;
                if (end - start < header) throw new InvalidDataException();
                uint id = tags ? BitConverter.ToUInt16(bytes, start) : BitConverter.ToUInt32(bytes, start);
                var length = tags ? BitConverter.ToUInt16(bytes, start + 2) : checked((int)BitConverter.ToUInt32(bytes, start + 4));
                var next = checked(start + header + length);
                if (next > end) throw new InvalidDataException();
                counts.TryGetValue(id, out var ordinal); counts[id] = ordinal + 1;
                var key = path + "/" + id.ToString("X8") + "[" + ordinal + "]";
                var payload = start + header;
                if (!tags && (id is 0xE76E4546 or 0xCC6A624F or 0xEA624F46 or 0xCC736552))
                    Walk(payload, next, key, false);
                else if (!tags && (id is 0x446A624F or 0x70726353))
                    Walk(payload, next, key, true);
                else result.Add(key, new Part(payload, length, tags && (id is 0x4153 or 0x504F or 0x644B)));
                start = next;
            }
        }
    }
}
