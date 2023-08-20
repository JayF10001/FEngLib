using System.IO;
using System.Numerics;
using System.Xml.Linq;
using FEngLib.Scripts;
using FEngLib.Utils;

namespace FEngLib.Objects;

public class MultiImageData : ImageData
{
    public Vector2 TopLeft1 { get; set; }
    public Vector2 TopLeft2 { get; set; }
    public Vector2 TopLeft3 { get; set; }
    public Vector2 BottomRight1 { get; set; }
    public Vector2 BottomRight2 { get; set; }
    public Vector2 BottomRight3 { get; set; }
    public Vector3 PivotRotation { get; set; }

	public override object Clone()
	{
		var result = new MultiImageData();

		result.InternalClone(this);

		result.TopLeft1 = this.TopLeft1;
		result.TopLeft2 = this.TopLeft2;
		result.TopLeft3 = this.TopLeft3;

		result.BottomRight1 = this.BottomRight1;
		result.BottomRight2 = this.BottomRight2;
		result.BottomRight3 = this.BottomRight3;

		result.PivotRotation = this.PivotRotation;

		return result;
	}

	public override void Read(BinaryReader br)
    {
        base.Read(br);

        TopLeft1 = br.ReadVector2();
        TopLeft2 = br.ReadVector2();
        TopLeft3 = br.ReadVector2();
        BottomRight1 = br.ReadVector2();
        BottomRight2 = br.ReadVector2();
        BottomRight3 = br.ReadVector2();
        PivotRotation = br.ReadVector3();
    }

    public override void Write(BinaryWriter bw)
    {
        base.Write(bw);

        bw.Write(TopLeft1);
        bw.Write(TopLeft2);
        bw.Write(TopLeft3);
        bw.Write(BottomRight1);
        bw.Write(BottomRight2);
        bw.Write(BottomRight3);
        bw.Write(PivotRotation);
    }
}

public class MultiImageScriptTracks : ImageScriptTracks
{
	public override object Clone()
	{
		var result = new MultiImageScriptTracks();

		result.InternalClone(this);

		result.TopLeft1 = this.TopLeft1?.Clone() as Vector2Track;
		result.TopLeft2 = this.TopLeft2?.Clone() as Vector2Track;
		result.TopLeft3 = this.TopLeft3?.Clone() as Vector2Track;

		result.BottomRight1 = this.BottomRight1?.Clone() as Vector2Track;
		result.BottomRight2 = this.BottomRight2?.Clone() as Vector2Track;
		result.BottomRight3 = this.BottomRight3?.Clone() as Vector2Track;

		result.PivotRotation = this.PivotRotation?.Clone() as Vector3Track;

		return result;
	}

	public Vector2Track TopLeft1 { get; set; }
    public Vector2Track TopLeft2 { get; set; }
    public Vector2Track TopLeft3 { get; set; }
    public Vector2Track BottomRight1 { get; set; }
    public Vector2Track BottomRight2 { get; set; }
    public Vector2Track BottomRight3 { get; set; }
    public Vector3Track PivotRotation { get; set; }
}

public class MultiImage : Image<MultiImageData, ImageScript<MultiImageScriptTracks>>
{
    public MultiImage(MultiImageData data) : base(data)
    {
        Type = ObjectType.MultiImage;
    }

	public override object Clone()
	{
		var result = new MultiImage(null);

		result.InternalClone(this);

		result.Texture1 = this.Texture1;
		result.TextureFlags1 = this.TextureFlags1;
		result.Texture2 = this.Texture2;
		result.TextureFlags2 = this.TextureFlags2;
		result.Texture3 = this.Texture3;
		result.TextureFlags3 = this.TextureFlags3;

		return result;
	}

	public uint Texture1 { get; set; }
    public uint TextureFlags1 { get; set; }
    public uint Texture2 { get; set; }
    public uint TextureFlags2 { get; set; }
    public uint Texture3 { get; set; }
    public uint TextureFlags3 { get; set; }

    public override void InitializeData()
    {
        Data = new MultiImageData();
    }
}
