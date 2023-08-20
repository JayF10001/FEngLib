using System.IO;

namespace FEngLib.Objects.Tags;

public class StringBufferLabelHashTag : ObjectTag
{
    public StringBufferLabelHashTag(IObject<ObjectData> frontendObject) : base(frontendObject)
    {
    }

	public override object Clone()
	{
		var result = new StringBufferLabelHashTag(null);

		result.InternalClone(this);

		result.Hash = this.Hash;

		return result;
	}

	public uint Hash { get; set; }

    public override void Read(BinaryReader br,
        ushort id,
        ushort length)
    {
        Hash = br.ReadUInt32();
    }
}
