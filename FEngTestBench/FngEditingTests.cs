using System;
using System.IO;
using System.Linq;
using System.Numerics;
using FEngLib;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngLib.Scripts;
using FEngLib.Structures;
using FEngLib.Utils;

namespace FEngTestBench;

/// <summary>Generated fixtures for source encoding and animation-origin edits.</summary>
internal static class FngEditingTests
{
    internal static int Run(string directory)
    {
        var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidDataException(message); checks++; }
        try
        {
            Directory.CreateDirectory(directory);
            var package = new Package { Name = "Edit.fng", Filename = "f" };
            var group = new Group(new ObjectData
            {
                Color = new Color4(255, 255, 255, 255), Position = new Vector3(262, -175, -57),
                Rotation = Quaternion.Identity, Size = Vector3.One
            })
            {
                Guid = 1234, Name = "HEAT_X_ZOOM", NameHash = Hashing.BinHash("HEAT_X_ZOOM"), NameStoredAsHash = true
            };
            package.Objects.Add(group);
            foreach (var name in new[] { "INIT", "POS1", "POS2" })
            {
                var script = group.CreateScript(); script.Id = Hashing.BinHash(name);
                script.Tracks.Position = new Vector3Track
                { BaseKey = group.Data.Position, InterpType = TrackInterpolationMethod.Linear, Length = 24, InterpAction = 3 };
                script.Tracks.Position.DeltaKeys.AddLast(new TrackNode<Vector3> { Time = 8, Val = new Vector3(12, 34, 56) });
                script.Tracks.Color = new ColorTrack
                { BaseKey = new Color4(255, 255, 255, 0), InterpType = TrackInterpolationMethod.Linear };
            }
            var original = Write(package);
            var padding = 16 + BitConverter.ToInt32(original, 12) - 1;
            original[padding] = 0x7F; // Noncanonical padding must survive a fixed-field edit.
            var loaded = Read(original);
            Check(loaded.Objects.Single().NameStoredAsHash, "Hash-only name provenance was lost.");
            Check(Write(loaded).SequenceEqual(original), "No-edit save normalized original storage.");
            var obj = loaded.Objects.Single();
            obj.Name = "Resolved display name with many characters";
            Check(Write(loaded).SequenceEqual(original), "A resolved display name expanded a hash-only tag.");
            ObjectPropertyEditing.SetPosition(obj, new Vector3(162, -175, -57));
            ObjectPropertyEditing.SetColor(obj, new Color4(255, 0, 255, 255));
            foreach (var script in obj.GetScripts())
            {
                var track = script.GetTracks().Position;
                Check(track.BaseKey == new Vector3(162, -175, -57), "Animation base key did not follow the object.");
                Check(track.DeltaKeys.Single().Val == new Vector3(12, 34, 56), "Relative animation changed.");
                Check(track.InterpType == TrackInterpolationMethod.Linear && track.Length == 24 && track.InterpAction == 3,
                    "Track encoding or interpolation settings changed.");
                Check(script.GetTracks().Color.BaseKey.Alpha == 0 && script.GetTracks().Color.BaseKey.Green == 0,
                    "Tinting changed a transparent animation origin.");
            }
            var edited = Write(loaded);
            Check(edited.Length == original.Length && edited[padding] == 0x7F, "Fixed-field edit changed size or padding.");
            var roundtrip = Read(edited);
            Check(roundtrip.Objects.Single().Data.Position.X == 162 && roundtrip.Objects.Single().Data.Color.Green == 0,
                "Edited base properties did not roundtrip.");
            Check(Write(roundtrip).SequenceEqual(edited), "Repeated save changed the original encoding.");
            loaded.Filename += "-a-structural-change";
            var rebuilt = Write(loaded);
            Check(rebuilt.Length > edited.Length && Read(rebuilt).Filename == loaded.Filename,
                "Structural edits failed to use the regular writer.");
            Check(Read(rebuilt).Objects.Single().NameStoredAsHash, "Structural rebuild expanded hash-only names.");
            File.WriteAllBytes(Path.Combine(directory, "editing.original.feng"), original);
            File.WriteAllBytes(Path.Combine(directory, "editing.changed.feng"), edited);
            Console.WriteLine($"Editing tests passed: {checks}"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static byte[] Write(Package package)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        new FrontendChunkWriter(package).Write(writer); return stream.ToArray();
    }
    private static Package Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes.Skip(8).ToArray()); using var reader = new BinaryReader(stream);
        return new FrontendPackageLoader(new HashResolver()).Load(reader);
    }
}
