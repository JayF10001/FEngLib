namespace FEngLib.Objects;

public class Group : BaseObject
{
    public Group(ObjectData data) : base(data)
    {
        Type = ObjectType.Group;
    }

    public override object Clone()
    {
        var result = new Group(null);

        result.InternalClone(this);

        return result;
    }

    public override void InitializeData()
    {
        Data = new ObjectData();
    }
}