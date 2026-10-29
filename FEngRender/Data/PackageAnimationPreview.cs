using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngLib.Scripts;
using FEngLib.Utils;
using FEngRender.Scripts;

namespace FEngRender.Data;

public readonly record struct ScriptPreviewFrame(uint ScriptId, int Time);

/// <summary>A display preview, not a simulation of the game's message dispatch. Each object gets
/// one existing display script; sampling never changes package objects, flags, scripts or tracks.</summary>
public sealed class PackageAnimationPreview
{
    public const int DurationMilliseconds = 30000;
    public const int LoopRestMilliseconds = 450;
    private static readonly string[] Preferred =
        { "PULSE", "BLINK", "FLASH", "ANIMATE", "HOVER", "ACTIVE", "SHOW", "CURRENT", "NORMAL", "STATIC", "COMPARE", "IDLE", "LOOP", "ON" };
    private static readonly string[] Rejected =
        { "HIDE", "LEAVE", "CLOSE", "EXIT", "OUT", "DIM", "INACTIVE", "PRESSED", "GREY", "OFF" };
    private static readonly Dictionary<uint,string> KnownNames = Preferred.Concat(Rejected).Append("INIT")
        .ToDictionary(Hashing.BinHash, name=>name);
    private readonly Dictionary<IObject<ObjectData>,Script> scripts = new(ReferenceEqualityComparer.Instance);

    public PackageAnimationPreview(Package package)
    {
        if(package==null)throw new ArgumentNullException(nameof(package));
        foreach(var obj in package.Objects)
        {
            Script best=null;
            foreach(var script in obj.GetScripts())
            {
                var name=script.Name;
                if(string.IsNullOrEmpty(name))KnownNames.TryGetValue(script.Id,out name);
                if(string.IsNullOrEmpty(name) || script.Id==0x1744B3 ||
                    name.Equals("INIT",StringComparison.OrdinalIgnoreCase) || script.Length==0)continue;
                name=name.ToUpperInvariant();
                if(Rejected.Any(name.Contains) || !IsDisplaySafe(obj,script,name))continue;
                best ??= script;
                if(Preferred.Any(name.Contains)) {best=script;break;}
            }
            if(best!=null)scripts.Add(obj,best);
        }
    }
    public int ObjectCount => scripts.Count;
    public IReadOnlyDictionary<IObject<ObjectData>,ScriptPreviewFrame> Sample(int elapsedMilliseconds)
    {
        var frames=new Dictionary<IObject<ObjectData>,ScriptPreviewFrame>(ReferenceEqualityComparer.Instance);
        if(elapsedMilliseconds>=DurationMilliseconds)return frames;
        foreach(var pair in scripts)
        {
            var length=(long)Math.Min(pair.Value.Length,int.MaxValue);
            var time=Math.Max(0,elapsedMilliseconds)%(length+LoopRestMilliseconds);
            frames.Add(pair.Key,new ScriptPreviewFrame(pair.Value.Id,(int)Math.Min(time,length)));
        }
        return frames;
    }
    private static bool IsDisplaySafe(IObject<ObjectData> obj,Script script,string name)
    {
        var tracks=script.GetTracks();
        if(tracks==null)return false;
        try
        {
            var length=(int)Math.Min(script.Length,int.MaxValue);
            // Use the real interpolator. Check the endpoints too so malformed common tracks
            // cannot inject non-finite transforms at the start or end of a display cycle.
            foreach(var t in new[]{0,length/2,length})
            {
                var p=tracks.Position==null?obj.Data.Position:TrackInterpolation.Interpolate(tracks.Position,t);
                var size=tracks.Size==null?obj.Data.Size:TrackInterpolation.Interpolate(tracks.Size,t);
                var rotation=tracks.Rotation==null?obj.Data.Rotation:TrackInterpolation.Interpolate(tracks.Rotation,t);
                var pivot=tracks.Pivot==null?obj.Data.Pivot:TrackInterpolation.Interpolate(tracks.Pivot,t);
                if(!Finite(p) || !Finite(size) || !Finite(pivot) || !float.IsFinite(rotation.LengthSquared()))return false;
                if(t==length/2)
                {
                    if(tracks.Color!=null && TrackInterpolation.Interpolate(tracks.Color,t).Alpha<12)return false;
                    if(tracks.Position!=null && name!="SHOW" && (Math.Abs(p.X)>1600 || Math.Abs(p.Y)>1200))return false;
                }
            }
            return true;
        }
        catch(Exception e) when(e is InvalidOperationException or ArgumentException or NullReferenceException)
        {return false;}
    }
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
}
