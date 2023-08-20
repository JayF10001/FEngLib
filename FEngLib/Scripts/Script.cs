using FEngLib.Objects;
using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace FEngLib.Scripts;

public class ScriptTracks : ICloneable
{
	protected void InternalClone(ScriptTracks @object)
	{
		this.Color = @object.Color?.Clone() as ColorTrack;
		this.Pivot = @object.Pivot?.Clone() as Vector3Track;
		this.Position = @object.Position?.Clone() as Vector3Track;
		this.Rotation = @object.Rotation?.Clone() as QuaternionTrack;
		this.Size = @object.Size?.Clone() as Vector3Track;
	}

	public virtual object Clone()
	{
		var result = new ScriptTracks();

		result.InternalClone(this);

		return result;
	}

	public ColorTrack Color { get; set; }
	public Vector3Track Pivot { get; set; }
	public Vector3Track Position { get; set; }
	public QuaternionTrack Rotation { get; set; }
	public Vector3Track Size { get; set; }
}

public abstract class Script : ICloneable
{
	protected Script()
	{
		// Tracks = new List<Track>();
		Events = new List<Event>();
	}
	public abstract object Clone();

	protected void InternalClone(Script script)
	{
		this.Name = script.Name;
		this.Id = script.Id;
		this.ChainedId = script.ChainedId;
		this.Length = script.Length;
		this.Flags = script.Flags;

		foreach (var @event in script.Events)
		{
			this.Events.Add(@event?.Clone() as Event);
		}
	}

	public string Name { get; set; }
	public uint Id { get; set; }
	public uint? ChainedId { get; set; }
	public uint Length { get; set; }
	public uint Flags { get; set; }

	// public List<Track> Tracks { get; }
	public List<Event> Events { get; }

	public abstract ScriptTracks GetTracks();
}

public abstract class Script<TTracks> : Script where TTracks : ScriptTracks, new()
{
	protected Script()
	{
		Tracks = new TTracks();
	}

	protected void InternalClone(Script<TTracks> script)
	{
		base.InternalClone(script);

		this.Tracks = script.Tracks?.Clone() as TTracks;
	}

	public TTracks Tracks { get; protected set; }

	public override ScriptTracks GetTracks()
	{
		return Tracks;
	}
}
