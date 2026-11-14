using System.Collections.Generic;
using System.Linq;

namespace Tarol.Primitives.Generators;

/// <summary>
/// The lifecycle part of the emitter: the live flag and <c>ApplyLive</c>, the step checks inside the dispatch cases (only
/// for events whose <c>On</c> handler assigns a lifecycle field, L-6), the creation checks (L-4) and the field accessors.
/// </summary>
internal sealed partial class Emitter
{
    private const string Violation = "global::Tarol.Primitives.LifecycleViolation";

    /// <summary>Whether any type of the aggregate has a lifecycle field, so the live hook is generated.</summary>
    private bool HasLifecycles => Owners().Any(static t => t.CanEmit && t.LifecycleFields.Count > 0);

    private string LifeAccessor(TypeModel type, LifecycleField field)
        => type.FullName == RootName ? $"__Life_{field.Member}" : $"__Life{_index[type.FullName]}_{field.Member}";

    /// <summary>The handler of <paramref name="type"/> that runs for <paramref name="e"/>: the most-derived matching one, as in <c>__Apply</c>.</summary>
    private HandlerModel? EffectiveHandler(TypeModel type, TypeModel e)
        => type.Handlers
            .Where(h => _events.ContainsKey(h.Event) && Applies(h.Event, e))
            .OrderByDescending(h => _events[h.Event].Bases.Count)
            .ThenBy(static h => h.Event, System.StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// The concrete events of <paramref name="target"/> of the given kinds whose own handler assigns a lifecycle field, with
    /// the fields they can change; an event whose base is in the list is left out (the base's case already holds it).
    /// </summary>
    private (List<TypeModel> Events, List<LifecycleField> Fields) StepEvents(TypeModel target, params EventKind[] kinds)
    {
        if (target.LifecycleFields.Count == 0)
            return ([], []);
        var events = new List<TypeModel>();
        var members = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var e in _model.Events.Where(e => !e.IsAbstract && e.Target == target.FullName && kinds.Contains(e.EventKind)))
        {
            if (EffectiveHandler(target, e) is not { Assigns.Count: > 0 } handler)
                continue;
            events.Add(e);
            foreach (var member in handler.Assigns)
                members.Add(member);
        }

        events.RemoveAll(e => events.Any(b => e.Bases.Contains(b.FullName)));
        return (events, target.LifecycleFields.Where(f => members.Contains(f.Member)).ToList());
    }

    /// <summary>Reads each field before the apply: <c>var __from0 = __Life…(owner);</c></summary>
    private void EmitCapture(TypeModel type, List<LifecycleField> fields, string owner)
    {
        for (var n = 0; n < fields.Count; n++)
            Line($"var __from{n} = {LifeAccessor(type, fields[n])}({owner});");
    }

    /// <summary>After the apply, on the live path only: reports a step that is not an edge.</summary>
    private void EmitStepCheck(TypeModel type, List<LifecycleField> fields, string owner, string @event)
    {
        // The first event of a new root is its creation: checked against the initial state in ApplyLive, not as a step.
        Line(type.FullName == RootName ? "if (__live && Version != 0)" : "if (__live)");
        Open();
        for (var n = 0; n < fields.Count; n++)
        {
            var f = fields[n];
            Line($"var __to{n} = {LifeAccessor(type, f)}({owner});");
            Line($"if (__to{n} != __from{n} && !{f.Class}.IsEdge(__from{n}, __to{n}))");
            Line($"    ForbiddenStep(new {Violation}({Literal(type.Name + "." + f.Member)}, __from{n}.ToString(), __to{n}.ToString(), {@event}.GetType().Name));");
        }

        Close();
    }

    /// <summary>After a creation event, on the live path only: every lifecycle field must hold its initial state (L-4).</summary>
    private void EmitCreationCheck(TypeModel type, string owner, string condition)
    {
        if (type.LifecycleFields.Count == 0)
            return;
        Line($"if ({condition})");
        Open();
        var n = 0;
        foreach (var f in type.LifecycleFields)
        {
            Line($"var __state{n} = {LifeAccessor(type, f)}({owner});");
            Line($"if (__state{n} != {f.Initial})");
            Line($"    ForbiddenStep(new {Violation}({Literal(type.Name + "." + f.Member)}, {Violation}.New, __state{n}.ToString(), stream.Event.GetType().Name));");
            n++;
        }

        Close();
    }

    /// <summary>The case of the root events whose handlers assign a root lifecycle field, before <c>case AggregateRoot&lt;R&gt;.Event</c>.</summary>
    private void EmitRootStepCase()
    {
        var (events, fields) = StepEvents(_root, EventKind.RootEvent);
        if (events.Count == 0)
            return;
        Line($"case {string.Join(" or ", events.Select(static e => e.FullName))}:");
        Open();
        Line($"if (stream.EntityId != Id.Value)");
        Line($"    {P}ThrowHelper.ForeignEvent({Literal(_root.Name)}, Id.Value, stream.EntityId);");
        EmitCapture(_root, fields, "this");
        Line("__Apply(stream.Event);");
        EmitStepCheck(_root, fields, "this", "stream.Event");
        Line("return;");
        Close();
    }

    /// <summary>The case of an entity's events (or deletions) whose handlers assign one of its lifecycle fields.</summary>
    private void EmitEntityStepCase(TypeModel entity, bool deleted)
    {
        var (events, fields) = StepEvents(entity, deleted ? EventKind.Deleted : EventKind.EntityEvent);
        if (events.Count == 0)
            return;
        var x = entity.FullName;
        var i = _index[x];
        Line($"case {string.Join(" or ", events.Select(static e => e.FullName))}:");
        Open();
        Line($"if (!__map{i}.TryGetValue(({Id}<{x}>)stream.EntityId, out var entity))");
        Line($"    {P}ThrowHelper.UnknownEntity({Literal(entity.Name)}, stream.EntityId);");
        EmitCapture(entity, fields, "entity");
        ApplyAndBubble(entity);
        EmitStepCheck(entity, fields, "entity", "stream.Event");
        if (deleted)
        {
            Line($"__Unlink{i}(entity);");
            Line($"__Remove{i}(entity);");
        }

        Line("return;");
        Close();
    }

    /// <summary>The lifecycle fields of an owner that its handlers for descendants' events assign (checked while bubbling).</summary>
    private List<LifecycleField> AncestorStepFields(TypeModel owner)
    {
        var members = owner.Handlers
            .Where(h => h.Assigns.Count > 0 && _events.TryGetValue(h.Event, out var e) && e.Target != owner.FullName)
            .SelectMany(static h => h.Assigns);
        var set = new HashSet<string>(members, System.StringComparer.Ordinal);
        return owner.LifecycleFields.Where(f => set.Contains(f.Member)).ToList();
    }

    /// <summary>The live flag and the hook that sets it, plus the root's creation check (L-4).</summary>
    private void EmitApplyLive()
    {
        if (!HasLifecycles)
            return;
        Line("#pragma warning disable CS0414 // read by the step checks, which an aggregate whose handlers assign no lifecycle field does not have");
        Line("private bool __live;");
        Line("#pragma warning restore CS0414");
        Line();
        Line("/// <inheritdoc />");
        Line($"protected override void ApplyLive(in {P}StreamEvent stream)");
        Open();
        Line("__live = true;");
        Line("try");
        Open();
        Line("ApplyRaised(in stream);");
        Close();
        Line("finally");
        Open();
        Line("__live = false;");
        Close();
        EmitCreationCheck(_root, "this", "stream.Version == 1");
        Close();
        Line();
    }

    private void EmitLifecycleAccessors(TypeModel type)
    {
        foreach (var f in type.LifecycleFields)
            Line($"{Accessor}Field, Name = {Literal(f.Field)})] private static extern ref {f.Enum} {LifeAccessor(type, f)}({type.FullName} owner);");
    }
}
