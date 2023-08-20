using System.IO;
using FEngLib.Objects.Tags;
using FEngLib.Tags;

namespace FEngLib.Messaging.Tags;

public class MessageResponseCountTag : Tag
{
	public override object Clone()
	{
		return new MessageResponseCountTag();
	}

	public override void Read(BinaryReader br,
        ushort id,
        ushort length)
    {
        br.ReadUInt32();
    }
}
