using System;
using System.Numerics;
using FEngLib.Structures;

namespace FEngLib.Objects;

/// <summary>Moves existing animation origins with an object's edited base properties.</summary>
public static class ObjectPropertyEditing
{
    public static void SetPosition(IObject<ObjectData> obj, Vector3 value)
    {
        var delta = value - obj.Data.Position;
        foreach (var script in obj.GetScripts())
            if (script.GetTracks().Position is { } track) track.BaseKey += delta;
        obj.Data.Position = value;
    }

    public static void SetColor(IObject<ObjectData> obj, Color4 value)
    {
        var old = obj.Data.Color;
        foreach (var script in obj.GetScripts())
            if (script.GetTracks().Color is { } track)
            {
                var c = track.BaseKey;
                track.BaseKey = new Color4(Channel(c.Blue, old.Blue, value.Blue),
                    Channel(c.Green, old.Green, value.Green), Channel(c.Red, old.Red, value.Red),
                    Channel(c.Alpha, old.Alpha, value.Alpha));
            }
        obj.Data.Color = value;
    }

    // Preserve existing fades (including fully transparent keys) when tinting an object.
    private static int Channel(int key, int before, int after) => before == after ? key :
        before == 0 ? after : (int)Math.Clamp((long)key * after / before, int.MinValue, int.MaxValue);
}
