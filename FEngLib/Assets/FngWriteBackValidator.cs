using System;
using System.Collections.Generic;
using System.IO;
using FEngLib.Chunks;
using FEngLib.Packages;

namespace FEngLib.Assets;

/// <summary>Validate the complete replacement, rather than merely recognizing its package header.</summary>
internal static class FngWriteBackValidator
{
    public static void Validate(Stream source, long offset, long storedLength, string expectedName)
    {
        if (storedLength < 16 || storedLength > int.MaxValue || offset < 0 || offset > source.Length - storedLength)
            throw new InvalidDataException("FNG block lies outside its bounded input.");
        source.Position = offset;
        using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, true);
        if (reader.ReadUInt32() != FngContainer.FngChunkId || reader.ReadUInt32() != storedLength - 8 ||
            reader.ReadUInt32() != (uint)FrontendChunkType.PackageStart || reader.ReadUInt32() != storedLength - 16)
            throw new InvalidDataException("FNG/FEng lengths must cover exactly one complete package, without trailing data.");
        var body = reader.ReadBytes(checked((int)storedLength - 16));
        if (body.Length != storedLength - 16) throw new EndOfStreamException();
        using var bounded = new MemoryStream(body, writable: false);
        using var packageReader = new BinaryReader(bounded);
        try
        {
            CheckFrames(packageReader, bounded.Length, 0);
            bounded.Position = 0;
            var package = new FrontendPackageLoader(new HashResolver()).Load(packageReader);
            if (bounded.Position != bounded.Length || !string.Equals(package.Name, expectedName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The complete FNG model was not consumed or its package identity differs.");
        }
        catch (Exception error) when (error is ChunkReadingException or ArgumentException or
            IndexOutOfRangeException or KeyNotFoundException or OverflowException or NullReferenceException)
        {
            throw new InvalidDataException("FNG model validation failed: " + error.Message, error);
        }
    }

    private static void CheckFrames(BinaryReader reader, long end, int depth)
    {
        if (depth > 64) throw new InvalidDataException("FNG container nesting exceeds the validation limit.");
        var stream = reader.BaseStream;
        while (stream.Position < end)
        {
            if (end - stream.Position < 8) throw new InvalidDataException("Truncated inner FNG chunk header.");
            var type = (FrontendChunkType)reader.ReadUInt32();
            var length = reader.ReadInt32();
            if (length < 0 || length > end - stream.Position)
                throw new InvalidDataException("An inner FNG chunk exceeds its parent boundary.");
            var next = stream.Position + length;
            if (type is FrontendChunkType.ResourcesContainer or FrontendChunkType.ObjectContainer or
                FrontendChunkType.FrontendObjectContainer)
                CheckFrames(reader, next, depth + 1);
            stream.Position = next;
        }
    }
}
