using FEngLib.Packages;
using System;
using System.Collections.Generic;

namespace FEngLib.Messaging;

public class MessageResponse : ICloneable
{
    public MessageResponse()
    {
        Responses = new List<Response>();
    }

	public object Clone()
	{
		var result = new MessageResponse();

		result.Id = this.Id;

		foreach (var response in this.Responses)
		{
			result.Responses.Add(response?.Clone() as Response);
		}

		return result;
	}

	public uint Id { get; set; }
    public List<Response> Responses { get; set; }
}
