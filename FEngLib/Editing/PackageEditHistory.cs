using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Numerics;
using FEngLib.Packages;

namespace FEngLib.Editing;

/// <summary>In-memory graph mementos: preserve identities, shared resources and the package's
/// private source encoding. No serialization, name resolution or disk writes during undo.</summary>
public sealed class PackageEditHistory
{
    private readonly Package package;
    private readonly List<(GraphState State, string Label)> states = new();
    private int cursor;
    private GraphState saved;
    public PackageEditHistory(Package package)
    {
        this.package = package ?? throw new ArgumentNullException(nameof(package));
        saved = GraphState.Capture(package);
        states.Add((saved, "打开文档"));
    }
    public bool CanUndo => cursor > 0;
    public bool CanRedo => cursor + 1 < states.Count;
    public bool IsDirty => !ReferenceEquals(states[cursor].State, saved);
    public string UndoLabel => CanUndo ? states[cursor].Label : "";
    public string RedoLabel => CanRedo ? states[cursor + 1].Label : "";
    public bool Commit(string label)
    {
        var next = GraphState.Capture(package);
        if (next.SameAs(states[cursor].State)) return false;
        states.RemoveRange(cursor + 1, states.Count - cursor - 1);
        states.Add((next, label)); cursor++;
        return true;
    }
    public void MarkSaved() { Commit("编辑属性"); saved = states[cursor].State; }
    public bool Undo() { if (!CanUndo) return false; states[--cursor].State.Restore(); return true; }
    public bool Redo() { if (!CanRedo) return false; states[++cursor].State.Restore(); return true; }
    public void CancelPending() => states[cursor].State.Restore();

    private sealed class GraphState
    {
        private record Member(object Owner, PropertyInfo Property, object Value);
        private record Sequence(object Owner, object[] Items, MethodInfo Clear, MethodInfo Add);
        private readonly List<Member> members = new();
        private readonly List<Sequence> sequences = new();
        public static GraphState Capture(object root)
        {
            var state = new GraphState();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Walk(object value)
            {
                if (value == null || value is string || value.GetType().IsValueType || !visited.Add(value)) return;
                if (value is IEnumerable enumerable && value.GetType().Namespace == "System.Collections.Generic")
                {
                    var type = value.GetType();
                    var element = type.GetGenericArguments().Single();
                    var items = enumerable.Cast<object>().ToArray();
                    var clear = type.GetMethod("Clear", Type.EmptyTypes);
                    var add = type.GetMethod(value is IList ? "Add" : "AddLast", new[] { element });
                    if (clear == null || add == null) throw new NotSupportedException("Unsupported editor collection: " + type);
                    state.sequences.Add(new Sequence(value, items, clear, add));
                    foreach (var item in items) Walk(item);
                    return;
                }
                if (value.GetType().Assembly != typeof(Package).Assembly) return;
                foreach (var p in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                    var child = p.GetValue(value);
                    if (p.SetMethod != null) state.members.Add(new Member(value, p, child));
                    Walk(child);
                }
            }
            Walk(root); return state;
        }
        public bool SameAs(GraphState other) => members.Count == other.members.Count &&
            sequences.Count == other.sequences.Count && members.Zip(other.members).All(pair =>
                ReferenceEquals(pair.First.Owner, pair.Second.Owner) && pair.First.Property == pair.Second.Property &&
                SameValue(pair.First.Value, pair.Second.Value)) && sequences.Zip(other.sequences).All(pair =>
                ReferenceEquals(pair.First.Owner, pair.Second.Owner) && pair.First.Items.SequenceEqual(pair.Second.Items));
        // Vector.Equals uses floating-point ==, so an unchanged NaN in an unsupported source
        // track otherwise looks like a new edit on every comparison. Compare components using
        // float.Equals, which treats the same non-finite source value as unchanged.
        private static bool SameValue(object a, object b) => (a,b) switch
        {
            (Vector2 x,Vector2 y)=>x.X.Equals(y.X)&&x.Y.Equals(y.Y),
            (Vector3 x,Vector3 y)=>x.X.Equals(y.X)&&x.Y.Equals(y.Y)&&x.Z.Equals(y.Z),
            (Quaternion x,Quaternion y)=>x.X.Equals(y.X)&&x.Y.Equals(y.Y)&&x.Z.Equals(y.Z)&&x.W.Equals(y.W),
            _=>Equals(a,b)
        };
        public void Restore()
        {
            foreach (var member in members) member.Property.SetValue(member.Owner, member.Value);
            foreach (var seq in sequences)
            {
                seq.Clear.Invoke(seq.Owner, null);
                foreach (var item in seq.Items) seq.Add.Invoke(seq.Owner, new[] { item });
            }
        }
    }
}
