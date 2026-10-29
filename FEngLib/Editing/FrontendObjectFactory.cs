using System;
using System.Linq;
using System.Numerics;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngLib.Structures;
using FEngLib.Utils;

namespace FEngLib.Editing;

public static class FrontendObjectFactory
{
    public static IObject<ObjectData> Create(Package package, ObjectType type, string name,
        IObject<ObjectData> parent = null, ResourceRequest resource = null)
    {
        name = (name ?? "").Trim().ToUpperInvariant();
        if (name.Length is < 1 or > 48 || name.Any(c=>c<=32 || c>=127 || c=='"'))
            throw new ArgumentException("名称须为 1～48 个不含空格、双引号的 ASCII 字符。");
        var hash=Hashing.BinHash(name);
        if(package.Objects.Any(o=>o.NameHash==hash || string.Equals(o.Name,name,StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("名称或哈希已被其他对象使用。");
        if(parent!=null && (parent is not Group || !package.Objects.Contains(parent)))
            throw new ArgumentException("父级必须是当前文档的组。");
        if(resource!=null && !package.ResourceRequests.Contains(resource)) throw new ArgumentException("资源不属于当前文档。");
        if(resource!=null && resource.Type!=(type==ObjectType.String?ResourceType.Font:ResourceType.Image) &&
            !(type==ObjectType.MultiImage && resource.Type==ResourceType.MultiImage))
            throw new ArgumentException("资源类型与对象不匹配。");
        var white=new Color4(255,255,255,255);
        IObject<ObjectData> obj=type switch
        {
            ObjectType.Group=>new Group(new ObjectData()),
            ObjectType.String=>new Text(new ObjectData()) {Value="TEXT",BufferLength=256},
            ObjectType.SimpleImage=>new SimpleImage(new ObjectData()),
            ObjectType.Image=>new Image(new ImageData {LowerRight=Vector2.One}),
            ObjectType.ColoredImage=>new ColoredImage(new ColoredImageData
                {LowerRight=Vector2.One,TopLeft=white,TopRight=white,BottomLeft=white,BottomRight=white}),
            ObjectType.MultiImage=>new MultiImage(new MultiImageData
                {LowerRight=Vector2.One,BottomRight1=Vector2.One,BottomRight2=Vector2.One,BottomRight3=Vector2.One}),
            _=>throw new ArgumentException("此类型不支持新建。")
        };
        obj.Data.Position=new Vector3(0,0,100); obj.Data.Size=type is ObjectType.Group or ObjectType.String?Vector3.One:new Vector3(64,64,1);
        obj.Data.Rotation=Quaternion.Identity; obj.Data.Color=white;
        obj.Name=name; obj.NameHash=hash; obj.Parent=parent; obj.ResourceRequest=resource;
        var guid=1u; while(package.Objects.Any(o=>o.Guid==guid)) guid=checked(guid+1);
        obj.Guid=guid; obj.Flags=ObjectFlags.AffectAllScripts;
        var init=obj.CreateScript(); init.Name="INIT"; init.Id=Hashing.BinHash("INIT");
        package.Objects.Add(obj); return obj;
    }
}
