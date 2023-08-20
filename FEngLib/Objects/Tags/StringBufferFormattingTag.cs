using System.IO;
using FEngLib.Utils;

namespace FEngLib.Objects.Tags;

public class StringBufferFormattingTag : ObjectTag
{
    public StringBufferFormattingTag(IObject<ObjectData> frontendObject) : base(frontendObject)
    {
    }

	public override object Clone()
	{
		var result = new StringBufferFormattingTag(null);

		result.InternalClone(this);

		result.Formatting = this.Formatting;

		return result;
	}

	public TextFormat Formatting { get; set; }

    public override void Read(BinaryReader br,
        ushort id,
        ushort length)
    {
        Formatting = br.ReadEnum<TextFormat>();
    }
}
