using System.IO;
using FEngLib.Objects;

namespace FEngLib.Scripts.Tags;

public class ScriptChainTag : ScriptTag
{
    public ScriptChainTag(IObject<ObjectData> frontendObject, ScriptProcessingContext scriptProcessingContext) : base(
        frontendObject, scriptProcessingContext)
    {
    }

	public override object Clone()
	{
		var result = new ScriptChainTag(null, null);

		result.InternalClone(this);

		result.Id = this.Id;

		return result;
	}

	public uint Id { get; set; }

    public override void Read(BinaryReader br,
        ushort id,
        ushort length)
    {
        Id = br.ReadUInt32();
    }
}
