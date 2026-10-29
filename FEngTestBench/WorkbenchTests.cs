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
using FEngRender.Data;
using FEngRender.Scripts;

namespace FEngTestBench;

internal static class WorkbenchTests
{
    public static int Run(string directory)
    {
        var checks=0;
        void Check(bool value,string message){if(!value)throw new InvalidDataException(message);checks++;}
        bool Near(Vector3 a,Vector3 b)=>Vector3.Distance(a,b)<0.001f;
        try
        {
            Directory.CreateDirectory(directory);
            var pkg=new Package{Name="Workbench.fng",Filename="workbench"};
            var resource=new ResourceRequest{Name="TEST_IMAGE",ID=123,Type=ResourceType.Image};pkg.ResourceRequests.Add(resource);
            var parent=FrontendObjectFactory.Create(pkg,ObjectType.Group,"ROOT");
            foreach(var kind in new[]{ObjectType.Image,ObjectType.String,ObjectType.SimpleImage,ObjectType.ColoredImage,ObjectType.MultiImage})
                FrontendObjectFactory.Create(pkg,kind,"NEW_"+kind,parent,kind==ObjectType.String || kind==ObjectType.SimpleImage?null:resource);
            Check(pkg.Objects.Select(o=>o.Guid).Distinct().Count()==6,"New object GUIDs collide.");
            try{FrontendObjectFactory.Create(pkg,ObjectType.Group,"root");throw new InvalidDataException("Duplicate accepted.");}catch(ArgumentException){checks++;}
            try{FrontendObjectFactory.Create(pkg,ObjectType.Group,"中文");throw new InvalidDataException("Unicode name accepted.");}catch(ArgumentException){checks++;}
            var bytes=Write(pkg);var roundtrip=Read(bytes);
            Check(roundtrip.Objects.Count==6 && roundtrip.Objects.All(o=>o.GetScripts().Single().Id==0x1744B3),"Six types did not roundtrip with INIT.");
            Check(roundtrip.Objects.Skip(1).All(o=>ReferenceEquals(o.Parent,roundtrip.Objects[0])),"Roundtrip parent links failed.");
            Check(roundtrip.Objects.Where(o=>o.ResourceRequest!=null).All(o=>ReferenceEquals(o.ResourceRequest,roundtrip.ResourceRequests[0])),"Resource sharing lost.");
            var imageFixture=roundtrip.Objects.OfType<FEngLib.Objects.Image>().First();
            ((ImageScriptTracks)imageFixture.GetScripts().Single().GetTracks()).UpperLeft=new Vector2Track{BaseKey=Vector2.One};
            var imageCopy=(IObject<ObjectData>)imageFixture.Clone();
            var uv=((ImageScriptTracks)imageFixture.GetScripts().Single().GetTracks()).UpperLeft;
            var copiedUv=((ImageScriptTracks)imageCopy.GetScripts().Single().GetTracks()).UpperLeft;
            copiedUv.BaseKey=Vector2.Zero;
            Check(uv.BaseKey==Vector2.One && !ReferenceEquals(uv,copiedUv),"Image copies share mutable UV tracks.");
            var original=Write(roundtrip);var history=new PackageEditHistory(roundtrip);
            var obj=roundtrip.Objects.OfType<FEngLib.Objects.Image>().First();
            var originalParent=obj.Parent;var originalResource=obj.ResourceRequest;
            var script=obj.GetScripts().Single();
            script.GetTracks().Position=new Vector3Track{BaseKey=new Vector3(10,20,0),Length=1000,InterpType=TrackInterpolationMethod.Linear};
            script.GetTracks().Position.DeltaKeys.AddLast(new TrackNode<Vector3>{Time=0,Val=Vector3.Zero});
            script.GetTracks().Position.DeltaKeys.AddLast(new TrackNode<Vector3>{Time=1000,Val=new Vector3(100,50,0)});
            script.GetTracks().Size=new Vector3Track{BaseKey=new Vector3(64,32,1),Length=1000,InterpType=TrackInterpolationMethod.Linear};
            script.GetTracks().Size.DeltaKeys.AddLast(new TrackNode<Vector3>{Time=0,Val=Vector3.Zero});
            script.GetTracks().Size.DeltaKeys.AddLast(new TrackNode<Vector3>{Time=1000,Val=new Vector3(16,8,0)});
            script.GetTracks().Rotation=new QuaternionTrack{BaseKey=Quaternion.Identity};
            script.Length=1000;script.Flags=1;history.Commit("添加轨道");
            var track=script.GetTracks().Position;var key=track.DeltaKeys.Last.Value;
            ObjectPropertyEditing.SetPosition(obj,obj.Data.Position+new Vector3(5,6,0));history.Commit("移动");
            history.Undo();Check(Near(track.BaseKey,new Vector3(10,20,0)) && ReferenceEquals(key,track.DeltaKeys.Last.Value),"Undo replaced keys or missed origin.");
            Check(ReferenceEquals(obj.Parent,originalParent) && ReferenceEquals(obj.ResourceRequest,originalResource),"Undo broke shared identities.");
            history.Redo();Check(Near(track.BaseKey,new Vector3(15,26,0)),"Redo origin failed.");history.MarkSaved();
            obj.Data.Color=new Color4(1,2,3,4);history.Commit("颜色");history.Undo();Check(!history.IsDirty,"Undo did not reach save checkpoint.");
            history.Redo();history.Undo();
            ObjectPropertyEditing.SetPosition(obj,obj.Data.Position+Vector3.One);history.Commit("分支");Check(!history.CanRedo,"Redo survived history fork.");
            roundtrip.Objects.Remove(obj);history.Commit("删除");history.Undo();
            Check(roundtrip.Objects.Contains(obj) && ReferenceEquals(obj.Parent,originalParent),"Structural undo lost object identity.");
            history.CancelPending();
            var times=new[]{0,250,500,750,1000};
            var before=times.Select(t=>TrackInterpolation.Interpolate(script.GetTracks().Size,t)).ToArray();
            ObjectPropertyEditing.Scale(obj,new Vector3(2,3,1));
            for(int i=0;i<times.Length;i++)Check(Near(TrackInterpolation.Interpolate(script.GetTracks().Size,times[i]),before[i]*new Vector3(2,3,1)),"Scale animation relationship changed.");
            Check(script.GetTracks().Size.Length==1000 && script.Length==1000 && script.Flags==1,"Scaling changed metadata.");
            history.CancelPending();
            var delta=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,0.4f);
            ObjectPropertyEditing.Rotate(obj,delta);
            Check(Quaternion.Dot(script.GetTracks().Rotation.BaseKey,delta)>0.9999f,"Rotation was not composed into base.");
            history.CancelPending();
            obj.Data.Pivot=new Vector3(3,4,0);obj.Data.Position=new Vector3(10,20,0);
            var pivotWorld=obj.Data.Position+obj.Data.Pivot;
            ObjectPropertyEditing.Scale(obj,new Vector3(2,3,1));
            Check(Near(obj.Data.Position+obj.Data.Pivot,pivotWorld),"Pivot moved while scaling.");history.CancelPending();
            var tree=RenderTree.Create(roundtrip);RenderGeometry.Prepare(tree);
            var node=RenderTree.GetAllTreeNodesForRendering(tree).First(n=>ReferenceEquals(n.GetObject(),obj));
            var initial=node.Transform;
            for(int i=0;i<8;i++)RenderGeometry.Prepare(tree);
            Check(node.Transform==initial,"INIT layout depends on frame count.");
            var polygon=RenderGeometry.Polygon(node);var center=new System.Drawing.PointF(polygon.Average(p=>p.X),polygon.Average(p=>p.Y));
            Check(RenderGeometry.Contains(node,center),"Transformed quad cannot be hit.");
            parent=roundtrip.Objects[0];parent.Data.Size=new Vector3(2,3,1);parent.Data.Rotation=delta;
            RenderGeometry.Prepare(tree);
            Check(Matrix4x4.Invert(node.ParentTransform,out var inv) && Near(Vector3.TransformNormal(Vector3.TransformNormal(new Vector3(4,6,0),node.ParentTransform),inv),new Vector3(4,6,0)),"Parent-space delta mapping failed.");
            parent.Data.Rotation=Quaternion.CreateFromYawPitchRoll(0,0.4f,0.2f);RenderGeometry.Prepare(tree);
            Check(RenderGeometry.TryParentPlaneInverse(node,out var planeInverse),"Projected parent plane cannot be inverted.");
            var localPoint=new Vector3(10,20,node.EvaluatedData.Position.Z);
            var projected=Vector3.Transform(localPoint,node.ParentTransform);
            var recovered=Vector3.Transform(new Vector3(projected.X,projected.Y,0),planeInverse);
            Check(Math.Abs(recovered.X-10)<0.001 && Math.Abs(recovered.Y-20)<0.001,"Projected plane inverse does not recover local XY.");
            script.GetTracks().Position.InterpAction=3;RenderGeometry.Prepare(tree);
            Check(node.PoseError!=null,"Unsupported layout track did not report an error.");history.CancelPending();
            while(history.CanUndo)history.Undo();
            Check(Write(roundtrip).SequenceEqual(original),"Undo to original lost binary source encoding.");
            var other=new PackageEditHistory(new Package{Name="Other",Filename="o"});Check(!other.CanUndo && !other.IsDirty,"Document histories share state.");
            File.WriteAllBytes(Path.Combine(directory,"six-object-types.feng"),bytes);
            checks+=AnimationPreviewTests.Run();
            Console.WriteLine($"Workbench tests passed: {checks}");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static byte[] Write(Package p){using var ms=new MemoryStream();using var bw=new BinaryWriter(ms);new FrontendChunkWriter(p).Write(bw);return ms.ToArray();}
    private static Package Read(byte[] bytes){using var ms=new MemoryStream(bytes.Skip(8).ToArray());using var br=new BinaryReader(ms);return new FrontendPackageLoader(new HashResolver()).Load(br);}
}
