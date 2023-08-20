using System.IO;
using System.Text;

namespace FEngLib.Objects.Tags;

public class StringBufferTextTag : ObjectTag
{
    public StringBufferTextTag(IObject<ObjectData> frontendObject) : base(frontendObject)
    {
    }

	public override object Clone()
	{
		var result = new StringBufferTextTag(null);

		result.InternalClone(this);

		result.Value = this.Value;

		return result;
	}

	public string Value { get; set; }

    public override void Read(BinaryReader br,
        ushort id,
        ushort length)
    {
        Value = Encoding.Unicode.GetString(br.ReadBytes(length)).Trim('\0');
    }
}
