using FEngLib.Scripts;
using System;

namespace FEngLib.Messaging;

public class Response : ICloneable
{
	public object Clone()
	{
		return new Response()
		{
			Id = this.Id,
			IntParam = this.IntParam,
			StringParam = this.StringParam,
			Target = this.Target
		};
	}

	public uint Id { get; set; }
	public uint? IntParam { get; set; }
	public string StringParam { get; set; }
	public uint Target { get; set; }
}
