using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using FEngLib.Objects;
using FEngRender.Utils;
using SixLabors.Fonts;

namespace FEngRender.Data;

/// <summary>Shared FE-space geometry for selection, overlays and the existing GL renderer.</summary>
public static class RenderGeometry
{
    public static bool TryParentPlaneInverse(RenderTreeNode node,out Matrix4x4 inverse)
    {
        var m=node.ParentTransform;
        var z=node.EvaluatedData.Position.Z+node.EvaluatedData.Pivot.Z;
        var planar=new Matrix4x4(m.M11,m.M12,0,0,m.M21,m.M22,0,0,0,0,1,0,
            m.M41+z*m.M31,m.M42+z*m.M32,0,1);
        return Matrix4x4.Invert(planar,out inverse);
    }
    public static void Prepare(RenderTree tree, IObject<ObjectData> preview = null, uint? script = null, int time = 0,
        IReadOnlyDictionary<IObject<ObjectData>,ScriptPreviewFrame> previewFrames = null)
    {
        if (tree == null) return;
        void Walk(IEnumerable<RenderTreeNode> nodes, Matrix4x4 parent, RenderTreeNode parentNode)
        {
            foreach (var node in nodes)
            {
                node.Update(new RenderContext(parent, parentNode, true, preview, script, time, previewFrames), 0);
                if (node is RenderTreeGroup group) Walk(group, node.Transform, node);
            }
        }
        Walk(tree, Matrix4x4.Identity, null);
    }

    public static PointF[] Polygon(RenderTreeNode node)
    {
        if (node is RenderTreeGroup group)
        {
            var all = RenderTree.GetAllTreeNodesForRendering(group).SelectMany(Polygon).ToArray();
            if (all.Length != 0) return Corners(Bounding(all));
            var anchor = Vector3.Transform(Vector3.Zero, node.Transform);
            return Corners(new RectangleF(anchor.X - 5, anchor.Y - 5, 10, 10));
        }
        if(node.GetObject().Type is not (ObjectType.Image or ObjectType.ColoredImage or ObjectType.MultiImage or ObjectType.SimpleImage or ObjectType.String))
            return Array.Empty<PointF>();
        var local = new RectangleF(-0.5f, -0.5f, 1, 1);
        if (node.GetObject() is Text text)
        {
            var measure = TextHelpers.MeasureText(text.Value ?? "", new RendererOptions(TextHelpers.GetFont(18))
            { WrappingWidth = text.MaxWidth });
            local = new RectangleF(TextHelpers.CalculateXOffset((uint)text.Formatting, measure.Width),
                TextHelpers.CalculateYOffset((uint)text.Formatting, measure.Height), Math.Max(1, measure.Width), Math.Max(1, measure.Height));
        }
        return Corners(local).Select(p =>
        {
            var v = Vector3.Transform(new Vector3(p.X, p.Y, node.GetObject() is Text?0:1), node.Transform);
            return new PointF(v.X, v.Y);
        }).ToArray();
    }
    public static RectangleF Bounds(RenderTreeNode node) => Bounding(Polygon(node));
    public static RectangleF Bounding(PointF[] points) => points.Length == 0 ? RectangleF.Empty :
        RectangleF.FromLTRB(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    public static PointF[] Corners(RectangleF r) => new[]
    { new PointF(r.Left,r.Top), new PointF(r.Right,r.Top), new PointF(r.Right,r.Bottom), new PointF(r.Left,r.Bottom) };
    public static bool Contains(RenderTreeNode node, PointF point)
    {
        var p = Polygon(node);
        if (p.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y))) return false;
        var inside = false;
        for (int i=0,j=p.Length-1; i<p.Length; j=i++)
            if ((p[i].Y > point.Y) != (p[j].Y > point.Y) &&
                point.X < (p[j].X-p[i].X)*(point.Y-p[i].Y)/(p[j].Y-p[i].Y)+p[i].X) inside=!inside;
        return inside;
    }
}
