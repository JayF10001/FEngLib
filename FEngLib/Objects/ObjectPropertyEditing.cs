using System;
using System.Numerics;
using FEngLib.Structures;
using FEngLib.Scripts;
using System.Linq;

namespace FEngLib.Objects;

/// <summary>Moves existing animation origins with an object's edited base properties.</summary>
public static class ObjectPropertyEditing
{
    public static void SetPosition(IObject<ObjectData> obj, Vector3 value)
    {
        RequireFinite(value);
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

    public static void SetPivot(IObject<ObjectData> obj, Vector3 value)
    {
        RequireFinite(value);
        var delta=value-obj.Data.Pivot;
        foreach(var script in obj.GetScripts())
            if(script.GetTracks().Pivot is { } track) track.BaseKey+=delta;
        obj.Data.Pivot=value;
    }

    public static void Scale(IObject<ObjectData> obj, Vector3 factor)
    {
        if (!float.IsFinite(factor.X) || !float.IsFinite(factor.Y) || !float.IsFinite(factor.Z))
            throw new ArgumentOutOfRangeException(nameof(factor));
        // Scaling about the existing pivot can be represented exactly for a constant pivot.
        // Do not approximate a moving pivot by silently resampling/replacing its animation.
        if (obj.GetScripts().Any(s => s.GetTracks().Pivot?.DeltaKeys.Any(k=>k.Val!=Vector3.Zero)==true))
            throw new InvalidOperationException("对象含动画旋转中心，无法保留原轨道并绕中心缩放；请在高级页检查轨道。");
        var pivot=obj.Data.Pivot;
        var position=obj.Data.Position;
        obj.Data.Position += pivot*(Vector3.One-factor);
        obj.Data.Pivot *= factor;
        obj.Data.Size *= factor;
        foreach (var script in obj.GetScripts())
        {
            var tracks=script.GetTracks();
            var scriptPivot=tracks.Pivot?.BaseKey??pivot;
            var correction=scriptPivot*(Vector3.One-factor);
            if(tracks.Position!=null) tracks.Position.BaseKey+=correction;
            else if(correction!=pivot*(Vector3.One-factor))
                tracks.Position=new Vector3Track{BaseKey=position+correction};
            if(tracks.Pivot!=null)tracks.Pivot.BaseKey*=factor;
            if (script.GetTracks().Size is { } track)
            {
                track.BaseKey *= factor;
                foreach (var key in track.DeltaKeys) key.Val *= factor;
            }
        }
    }

    public static void Rotate(IObject<ObjectData> obj, Quaternion delta)
    {
        if (!float.IsFinite(delta.LengthSquared()) || delta.LengthSquared() < 0.000001f)
            throw new ArgumentOutOfRangeException(nameof(delta));
        delta = Quaternion.Normalize(delta);
        obj.Data.Rotation *= delta;
        foreach (var script in obj.GetScripts())
            if (script.GetTracks().Rotation is { } track) track.BaseKey *= delta;
    }

    public static void SetSize(IObject<ObjectData> obj, Vector3 value)
    {
        var old = obj.Data.Size;
        float Ratio(float a, float b) => Math.Abs(b) < 0.000001f ?
            a == b ? 1 : throw new InvalidOperationException("零尺寸轴无法按比例缩放，请在高级轨道中检查原值。") : a / b;
        Scale(obj, new Vector3(Ratio(value.X, old.X), Ratio(value.Y, old.Y), Ratio(value.Z, old.Z)));
    }

    public static void SetRotation(IObject<ObjectData> obj, Quaternion value)
    {
        if (obj.Data.Rotation.LengthSquared() < 0.000001f) throw new InvalidOperationException("原旋转四元数为零。");
        Rotate(obj, Quaternion.Inverse(obj.Data.Rotation) * value);
    }

    // Preserve existing fades (including fully transparent keys) when tinting an object.
    private static int Channel(int key, int before, int after) => before == after ? key :
        before == 0 ? after : (int)Math.Clamp((long)key * after / before, int.MinValue, int.MaxValue);

    private static void RequireFinite(Vector3 value)
    {if(!float.IsFinite(value.X)||!float.IsFinite(value.Y)||!float.IsFinite(value.Z))throw new ArgumentException("坐标不能使用 NaN 或无穷值。");}
}
