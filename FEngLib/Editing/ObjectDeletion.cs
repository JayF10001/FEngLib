using System;
using System.Collections.Generic;
using System.Linq;
using FEngLib.Messaging;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngLib.Scripts;

namespace FEngLib.Editing;

public sealed class ObjectReferenceReport
{
    public int MessageTargetCount { get; internal set; }
    public int MessageResponseCount { get; internal set; }
    public int ScriptEventCount { get; internal set; }
    public int Total => MessageTargetCount + MessageResponseCount + ScriptEventCount;
}

/// <summary>
/// Deletes an object subtree and keeps message/script references consistent. The game dereferences
/// MessageTargets and script event targets; removing only Package.Objects leaves dangling GUIDs.
/// </summary>
public static class ObjectDeletion
{
    public static IReadOnlyList<IObject<ObjectData>> CollectSubtree(Package package, IObject<ObjectData> root)
    {
        if (package == null) throw new ArgumentNullException(nameof(package));
        if (root == null) throw new ArgumentNullException(nameof(root));
        if (!package.Objects.Contains(root))
            throw new InvalidOperationException("对象不属于当前包。");

        var result = new List<IObject<ObjectData>> { root };
        result.AddRange(package.Objects.Where(o => IsDescendant(o, root)));
        return result;
    }

    public static ObjectReferenceReport Scan(Package package, IObject<ObjectData> root)
    {
        var guids = CollectSubtree(package, root).Select(o => o.Guid).ToHashSet();
        var report = new ObjectReferenceReport();
        foreach (var list in package.MessageTargetLists)
            report.MessageTargetCount += list.Targets.Count(guids.Contains);
        foreach (var obj in package.Objects)
        {
            foreach (var response in obj.MessageResponses)
                report.MessageResponseCount += response.Responses.Count(r => guids.Contains(r.Target));
            foreach (var script in obj.GetScripts())
                report.ScriptEventCount += script.Events.Count(e => guids.Contains(e.Target));
        }
        return report;
    }

    /// <summary>
    /// Removes the subtree and cleans message references. Script event targets are only removed when
    /// explicitly allowed; otherwise a dangling script event aborts the delete.
    /// </summary>
    public static ObjectReferenceReport Delete(Package package, IObject<ObjectData> root,
        bool removeScriptEvents = false)
    {
        var subtree = CollectSubtree(package, root);
        var guids = subtree.Select(o => o.Guid).ToHashSet();
        var report = Scan(package, root);

        if (report.ScriptEventCount > 0 && !removeScriptEvents)
            throw new InvalidOperationException(
                $"有 {report.ScriptEventCount} 个脚本事件仍指向要删除的对象；确认移除这些事件后才能删除。");

        foreach (var list in package.MessageTargetLists)
            list.Targets.RemoveAll(guids.Contains);

        foreach (var obj in package.Objects)
        {
            foreach (var response in obj.MessageResponses)
                response.Responses.RemoveAll(r => guids.Contains(r.Target));

            if (!removeScriptEvents) continue;
            foreach (var script in obj.GetScripts())
                script.Events.RemoveAll(e => guids.Contains(e.Target));
        }

        foreach (var obj in subtree)
            package.Objects.Remove(obj);
        return report;
    }

    private static bool IsDescendant(IObject<ObjectData> candidate, IObject<ObjectData> ancestor)
    {
        for (var parent = candidate.Parent; parent != null; parent = parent.Parent)
            if (ReferenceEquals(parent, ancestor)) return true;
        return false;
    }
}
