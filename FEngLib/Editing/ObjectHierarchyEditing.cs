using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FEngLib.Objects;
using FEngLib.Packages;

namespace FEngLib.Editing;

/// <summary>
/// Moves an existing object between the FNG root and groups. The caller supplies the old and new
/// parent's world matrices from the render tree; a translation-only parent change is converted for
/// both the object's base position and every script position track.
/// </summary>
public static class ObjectHierarchyEditing
{
    private const float Tolerance = 0.00001f;

    public static void MoveToGroup(Package package, IObject<ObjectData> obj,
        IObject<ObjectData> newParent, Matrix4x4 oldParentWorld, Matrix4x4 newParentWorld)
    {
        if (package == null) throw new ArgumentNullException(nameof(package));
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        if (newParent != null && newParent is not Group)
            throw new ArgumentException("目标对象不是组。", nameof(newParent));
        if (!package.Objects.Contains(obj))
            throw new InvalidOperationException("对象不属于当前包。");
        if (newParent != null && !package.Objects.Contains(newParent))
            throw new InvalidOperationException("目标组不属于当前包。");
        if (newParent != null && (ReferenceEquals(newParent, obj) || IsDescendant(newParent, obj)))
            throw new InvalidOperationException("不能把对象移动到它自己的子树中。");

        var translationOnly = LinearPartsEqual(oldParentWorld, newParentWorld);
        if (!translationOnly)
            throw new NotSupportedException(
                "目标组的旋转或缩放与旧父级不同，当前只支持平移父级下的自动换算。请先把组合并到同级或手动检查动画轨道。");
        var shift = oldParentWorld.Translation - newParentWorld.Translation;

        // Moving the object and its complete subtree keeps group children after their parent.
        var moving = new List<IObject<ObjectData>> { obj };
        moving.AddRange(package.Objects.Where(o => IsDescendant(o, obj)));
        foreach (var item in moving) package.Objects.Remove(item);

        var insert = package.Objects.Count;
        if (newParent != null)
        {
            var parentIndex = package.Objects.IndexOf(newParent);
            if (parentIndex < 0) throw new InvalidOperationException("目标组已不在包中。");
            insert = parentIndex + 1;
            for (var i = package.Objects.Count - 1; i > parentIndex; i--)
                if (IsDescendant(package.Objects[i], newParent)) { insert = i + 1; break; }
        }
        package.Objects.InsertRange(insert, moving);
        obj.Parent = newParent;

        // Translation-only parent change: local positions move by the opposite parent translation.
        // Rotation, scale and pivot remain valid. Script delta keys are offsets from the base key.
        if (shift == Vector3.Zero) return;
        obj.Data.Position += shift;
        foreach (var script in obj.GetScripts())
            if (script.GetTracks().Position is { } track) track.BaseKey += shift;
    }

    private static bool IsDescendant(IObject<ObjectData> candidate, IObject<ObjectData> ancestor)
    {
        for (var parent = candidate.Parent; parent != null; parent = parent.Parent)
            if (ReferenceEquals(parent, ancestor)) return true;
        return false;
    }

    private static bool LinearPartsEqual(Matrix4x4 a, Matrix4x4 b) =>
        Near(a.M11, b.M11) && Near(a.M12, b.M12) && Near(a.M13, b.M13) &&
        Near(a.M21, b.M21) && Near(a.M22, b.M22) && Near(a.M23, b.M23) &&
        Near(a.M31, b.M31) && Near(a.M32, b.M32) && Near(a.M33, b.M33);

    private static bool Near(float a, float b) => Math.Abs(a - b) <= Tolerance;
}
