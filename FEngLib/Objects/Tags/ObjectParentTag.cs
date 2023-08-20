using System.IO;

namespace FEngLib.Objects.Tags;

public class ObjectParentTag : ObjectTag
{
    public ObjectParentTag(IObject<ObjectData> frontendObject) : base(frontendObject)
    {
    }

	public override object Clone()
	{
		var result = new ObjectParentTag(null);

		result.InternalClone(this);

		result.ParentId = this.ParentId;

		return result;
	}

	public uint ParentId { get; set; }

    public override void Read(BinaryReader br,
        ushort id,
        ushort length)
    {
        ParentId = br.ReadUInt32();
    }
}
