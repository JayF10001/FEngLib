using System.IO;

namespace FEngLib.Objects.Tags;

public class StringBufferLengthTag : ObjectTag
{
    public StringBufferLengthTag(IObject<ObjectData> frontendObject) : base(frontendObject)
    {
    }

	public override object Clone()
	{
		var result = new StringBufferLengthTag(null);

		result.InternalClone(this);

		result.BufferLength = this.BufferLength;

		return result;
	}

	public uint BufferLength { get; set; }

    public override void Read(BinaryReader br,
        ushort id,
        ushort length)
    {
        BufferLength = br.ReadUInt32();
    }
}
