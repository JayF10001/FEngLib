using System;

namespace FEngLib.Scripts;

public class ScriptProcessingContext : ICloneable
{
	private Script m_script;

    public ScriptProcessingContext(Script script)
    {
		m_script = script;
    }

	public object Clone()
	{
		return new ScriptProcessingContext(null)
		{
			m_script = this.m_script?.Clone() as Script,
			CurrentTrack = this.CurrentTrack?.Clone() as Track
		};
	}

	public Script Script => m_script;

	public Track CurrentTrack { get; set; }
}
