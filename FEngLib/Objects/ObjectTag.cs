using FEngLib.Tags;

namespace FEngLib.Objects;

public abstract class ObjectTag : Tag
{
	private IObject<ObjectData> m_frontendObject;

    protected ObjectTag(IObject<ObjectData> frontendObject)
    {
		m_frontendObject = frontendObject;
    }

	protected void InternalClone(ObjectTag tag)
	{
		this.m_frontendObject = tag.m_frontendObject?.Clone() as IObject<ObjectData>;
	}

	protected IObject<ObjectData> FrontendObject => m_frontendObject;
}
