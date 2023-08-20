using FEngLib.Objects;

namespace FEngLib.Scripts;

public abstract class ScriptTag : ObjectTag
{
	private ScriptProcessingContext m_scriptProcessingContext;

	protected ScriptTag(IObject<ObjectData> frontendObject, ScriptProcessingContext scriptProcessingContext) : base(
        frontendObject)
    {
		m_scriptProcessingContext = scriptProcessingContext;
    }

	protected void InternalClone(ScriptTag tag)
	{
		base.InternalClone(tag);

		this.m_scriptProcessingContext = tag.m_scriptProcessingContext?.Clone() as ScriptProcessingContext;
	}

    protected Script Script => ScriptProcessingContext.Script;
	protected ScriptProcessingContext ScriptProcessingContext => m_scriptProcessingContext;
}
