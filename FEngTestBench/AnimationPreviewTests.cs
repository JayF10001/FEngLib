using System;
using System.IO;
using System.Linq;
using System.Numerics;
using FEngLib;
using FEngLib.Editing;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngLib.Scripts;
using FEngLib.Structures;
using FEngLib.Utils;
using FEngRender.Data;

namespace FEngTestBench;

internal static class AnimationPreviewTests
{
    public static int Run()
    {
        int checks=0;
        void Check(bool condition,string message){if(!condition)throw new InvalidDataException(message);checks++;}
        var package=new Package{Name="Preview",Filename="preview"};
        IObject<ObjectData> New(string name,IObject<ObjectData> parent=null)=>FrontendObjectFactory.Create(package,ObjectType.SimpleImage,name,parent);
        Script Script(IObject<ObjectData> obj,string name,uint length=1000)
        {var script=obj.CreateScript();script.Name=name;script.Id=Hashing.BinHash(name);script.Length=length;return script;}
        Vector3Track Moving(Vector3 start,Vector3 end)
        {
            var track=new Vector3Track{BaseKey=start,Length=1000,InterpType=TrackInterpolationMethod.Linear};
            track.DeltaKeys.AddLast(new TrackNode<Vector3>{Time=0,Val=Vector3.Zero});
            track.DeltaKeys.AddLast(new TrackNode<Vector3>{Time=1000,Val=end-start});return track;
        }
        var group=FrontendObjectFactory.Create(package,ObjectType.Group,"PARENT");
        ObjectPropertyEditing.SetPosition(group,Vector3.Zero);
        var groupScript=Script(group,"PULSE");groupScript.GetTracks().Position=Moving(Vector3.Zero,new Vector3(100,0,0));
        var first=New("FIRST",group);ObjectPropertyEditing.SetPosition(first,Vector3.Zero);
        Script(first,"CUSTOM");Script(first,"HIDE");
        var pulse=Script(first,"PULSE");pulse.GetTracks().Position=Moving(Vector3.Zero,new Vector3(0,100,0));
        pulse.ChainedId=Hashing.BinHash("HIDE");
        var second=New("SECOND");var blink=Script(second,"BLINK",500);blink.GetTracks().Position=Moving(Vector3.Zero,new Vector3(40,0,0));
        var staticOnly=New("STATIC_ONLY");
        var bad=New("REJECTED");Script(bad,"HIDE");Script(bad,"INACTIVE");Script(bad,"FLASH",0);
        var transparent=Script(bad,"PULSE");transparent.GetTracks().Color=new ColorTrack{BaseKey=new Color4(255,255,255,0)};
        var offscreen=Script(bad,"BLINK");offscreen.GetTracks().Position=new Vector3Track{BaseKey=new Vector3(5000,0,0)};
        var nonfinite=Script(bad,"LOOP");nonfinite.GetTracks().Size=new Vector3Track{BaseKey=new Vector3(float.NaN,1,1)};
        var unsupported=Script(bad,"ON");unsupported.GetTracks().Position=Moving(Vector3.Zero,Vector3.One);unsupported.GetTracks().Position.InterpAction=9;
        var hashed=New("HASHED");var hashedScript=Script(hashed,"SHOW");hashedScript.Name=null;
        var unknown=New("UNKNOWN");var unknownScript=Script(unknown,"UNKNOWN_SCRIPT");unknownScript.Name=null;
        var fallback=New("FALLBACK");var custom=Script(fallback,"CUSTOM_ANIMATION");
        byte[] Write(){using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);new FrontendChunkWriter(package).Write(writer);return stream.ToArray();}
        var before=Write();
        var history=new PackageEditHistory(package);
        Check(!history.Commit("未编辑"),"Unchanged invalid source track was treated as an edit.");
        var preview=new PackageAnimationPreview(package);var frames=preview.Sample(500);
        Check(preview.ObjectCount==5,"Unexpected automatic preview object count.");
        Check(frames[first].ScriptId==pulse.Id,"Preferred script did not replace ordinary candidate.");
        Check(!frames.ContainsKey(bad),"Hidden, transparent, offscreen, invalid or unsupported script selected.");
        Check(!frames.ContainsKey(staticOnly),"INIT-only object should remain static.");
        Check(frames[hashed].ScriptId==hashedScript.Id,"Known hashed SHOW script was not recognized.");
        Check(!frames.ContainsKey(unknown),"Unknown unnamed script must not be guessed.");
        Check(frames[fallback].ScriptId==custom.Id,"Valid ordinary display fallback was lost.");
        Check(preview.Sample(1250)[first].Time==1000,"Cycle rest does not hold final frame.");
        Check(preview.Sample(1500)[first].Time==50,"Cycle does not restart after the 450ms rest.");
        Check(preview.Sample(1500)[second].Time==500,"Objects do not use independent cycle lengths.");
        Check(preview.Sample(-10)[first].Time==0,"Negative preview time was not clamped.");
        Check(preview.Sample(29999).Count==5 && preview.Sample(30000).Count==0,"30-second preview boundary is wrong.");
        var tree=RenderTree.Create(package);
        var nodes=RenderTree.GetAllTreeNodesForRendering(tree).ToDictionary(n=>n.GetObject());
        RenderGeometry.Prepare(tree,previewFrames:frames);
        Check(Math.Abs(nodes[first].EvaluatedData.Position.Y-50)<0.001,"Child animation did not sample its own track.");
        Check(Math.Abs(nodes[first].ParentTransform.M41-50)<0.001,"Animated parent transform did not propagate to child.");
        Check(Math.Abs(nodes[second].EvaluatedData.Position.X-20)<0.001,"Second object did not sample independently.");
        Check(nodes[staticOnly].EvaluatedData.Position==staticOnly.Data.Position,"Unanimated object did not retain INIT/static state.");
        RenderGeometry.Prepare(tree,previewFrames:preview.Sample(1300));
        Check(nodes[first].EvaluatedData.Position.Y==100,"Display preview incorrectly followed a HIDE chain.");
        RenderGeometry.Prepare(tree,previewFrames:preview.Sample(30000));
        Check(nodes[first].EvaluatedData.Position==first.Data.Position && nodes[first].ParentTransform.M41==0,"Preview end did not restore INIT hierarchy.");
        Check(!history.Commit("预览") && !history.IsDirty,"Sampling changed the package object graph or history.");
        Check(Write().SequenceEqual(before),"Sampling changed serialized FNG contents.");
        return checks;
    }
}
