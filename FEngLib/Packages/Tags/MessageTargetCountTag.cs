using System.IO;
using FEngLib.Tags;

namespace FEngLib.Packages.Tags;

public class MessageTargetCountTag : Tag
{
    public override void Read(BinaryReader br, ushort id,
        ushort length)
    {
        br.ReadUInt32();
    }
}