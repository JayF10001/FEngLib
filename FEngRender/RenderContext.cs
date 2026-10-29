using System.Numerics;
using FEngRender.Data;
using FEngLib.Objects;

namespace FEngRender;

public record RenderContext(Matrix4x4 ViewMatrix, RenderTreeNode Parent,
    bool Layout = false, IObject<ObjectData> PreviewObject = null, uint? PreviewScript = null, int Time = 0)
{
    public RenderContext(Matrix4x4 viewMatrix,RenderTreeNode parent):this(viewMatrix,parent,false,null,null,0) { }
    public void Deconstruct(out Matrix4x4 viewMatrix,out RenderTreeNode parent)
    {viewMatrix=ViewMatrix;parent=Parent;}
}
