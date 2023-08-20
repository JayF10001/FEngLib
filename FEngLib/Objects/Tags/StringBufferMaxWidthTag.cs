using System.IO;

namespace FEngLib.Objects.Tags;

public class StringBufferMaxWidthTag : ObjectTag
{
    public StringBufferMaxWidthTag(IObject<ObjectData> frontendObject) : base(frontendObject)
    {
    }

	public override object Clone()
	{
		var result = new StringBufferMaxWidthTag(null);

		result.InternalClone(this);

		result.MaxWidth = this.MaxWidth;

		return result;
	}

	public uint MaxWidth { get; set; }

    public override void Read(BinaryReader br,
        ushort id,
        ushort length)
    {
        MaxWidth = br.ReadUInt32();
    }
}
