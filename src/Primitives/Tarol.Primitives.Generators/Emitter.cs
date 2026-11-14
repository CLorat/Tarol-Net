using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tarol.Primitives.Generators;

/// <summary>Plans and writes the generated code of one aggregate: the root's dispatch, maps and Rehydrate, and each entity's partial.</summary>
internal sealed partial class Emitter
{
    private const string P = "global::Tarol.Primitives.";
    private const string Id = "global::Tarol.StronglyTypedId.Id";
    private const string Result = "global::Tarol.Results.Result";
    private const string Accessor = "[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.";

    private readonly AggregateModel _model;
    private readonly TypeModel _root;
    private readonly Dictionary<string, TypeModel> _entities = new(System.StringComparer.Ordinal);
    private readonly Dictionary<string, int> _index = new(System.StringComparer.Ordinal);
    private readonly Dictionary<string, TypeModel> _events = new(System.StringComparer.Ordinal);
    private readonly Dictionary<string, List<(TypeModel Owner, ContainerMember Member)>> _owners = new(System.StringComparer.Ordinal);
    private readonly List<DiagnosticInfo> _diagnostics = [];
    private readonly StringBuilder _sb = new();
    private int _indent;

    public Emitter(AggregateModel model)
    {
        _model = model;
        _root = model.Root;
        var i = 0;
        foreach (var entity in model.Entities)
        {
            _entities[entity.FullName] = entity;
            _index[entity.FullName] = i++;
        }

        foreach (var e in model.Events)
            _events[e.FullName] = e;

        foreach (var owner in Owners())
            foreach (var member in owner.Members)
            {
                if (!_owners.TryGetValue(member.Child, out var list))
                    _owners[member.Child] = list = [];
                list.Add((owner, member));
            }
    }

    public IReadOnlyList<DiagnosticInfo> Diagnostics => _diagnostics;

    private IEnumerable<TypeModel> Owners() => new[] { _root }.Concat(_model.Entities);

    private string RootName => _root.FullName;

    // ───────────────────────────── plan and diagnostics ─────────────────────────────

    public void Check()
    {
        foreach (var owner in Owners())
        {
            foreach (var group in owner.Members.GroupBy(static m => m.Child).Where(static g => g.Count() > 1))
                _diagnostics.Add(DiagnosticInfo.Create(Generators.Diagnostics.AmbiguousOwner, group.Skip(1).First().Location, Short(group.Key),
                    $"'{owner.Name}' holds it in more than one EntitySet/EntityRef ({string.Join(", ", group.Select(static m => m.Name))})"));

            foreach (var member in owner.Members.Where(m => !_entities.ContainsKey(m.Child)))
                _diagnostics.Add(DiagnosticInfo.Create(Generators.Diagnostics.ForeignChild, member.Location, owner.Name, Short(member.Child), _root.Name));
        }

        foreach (var entity in _model.Entities)
            if (!_owners.ContainsKey(entity.FullName))
                _diagnostics.Add(DiagnosticInfo.Create(Generators.Diagnostics.NoOwner, entity.Location, entity.Name, _root.Name));

        foreach (var e in _model.Events)
        {
            if (e.EventKind == EventKind.Created && OwnerTypes(e.Target!).Count > 1)
                _diagnostics.Add(DiagnosticInfo.Create(Generators.Diagnostics.AmbiguousOwner, e.Location, Short(e.Target!),
                    $"it has several owner types ({string.Join(", ", OwnerTypes(e.Target!).Select(static o => o.Name))}); derive '{e.Name}' from CreatedIn<TParent> instead of Created"));

            if (e.EventKind == EventKind.CreatedIn && !OwnerTypes(e.Target!).Any(o => o.FullName == e.Parent))
                _diagnostics.Add(DiagnosticInfo.Create(Generators.Diagnostics.NotAnOwner, e.Location, e.Name, Short(e.Target!), Short(e.Parent!)));

            if (!e.IsAbstract && e.EventKind is EventKind.RootEvent or EventKind.EntityEvent && TypeOf(e.Target!) is { } target
                && !target.Handlers.Any(h => Applies(h.Event, e)))
                _diagnostics.Add(DiagnosticInfo.Create(Generators.Diagnostics.MissingHandler, e.Location, e.Name, target.Name));
        }

        foreach (var type in Owners())
            foreach (var handler in type.Handlers)
            {
                if (!_events.TryGetValue(handler.Event, out var e))
                    _diagnostics.Add(DiagnosticInfo.Create(Generators.Diagnostics.HandlerNeverCalled, handler.Location, Short(handler.Event), type.Name,
                        $"'{Short(handler.Event)}' is not an event of aggregate '{_root.Name}'"));
                else if (e.Target != type.FullName && !IsAncestor(type.FullName, e.Target!))
                    _diagnostics.Add(DiagnosticInfo.Create(Generators.Diagnostics.HandlerNeverCalled, handler.Location, Short(handler.Event), type.Name,
                        $"the event targets '{Short(e.Target!)}', and '{type.Name}' is neither that type nor one of its owners"));
            }

        foreach (var group in _model.Events.Where(static e => !e.IsAbstract).GroupBy(static e => e.Alias).Where(static g => g.Count() > 1))
        {
            var first = group.First();
            foreach (var other in group.Skip(1))
                _diagnostics.Add(DiagnosticInfo.Create(Generators.Diagnostics.DuplicateAlias, other.Location, first.Name, other.Name, _root.Name, group.Key));
        }
    }

    private static string Short(string fullName) => fullName.Split('.').Last();

    private TypeModel? TypeOf(string fullName) => fullName == RootName ? _root : _entities.TryGetValue(fullName, out var e) ? e : null;

    private List<TypeModel> OwnerTypes(string child)
        => _owners.TryGetValue(child, out var list) ? list.Select(static o => o.Owner).Distinct().ToList() : [];

    /// <summary>Whether <paramref name="ancestor"/> owns <paramref name="type"/>, directly or deeper. The root owns everything.</summary>
    private bool IsAncestor(string ancestor, string type, int depth = 0)
    {
        if (ancestor == RootName)
            return true;
        if (depth > 32)
            return false;
        return OwnerTypes(type).Any(o => o.FullName == ancestor || (o.FullName != RootName && IsAncestor(ancestor, o.FullName, depth + 1)));
    }

    /// <summary>Whether a handler for <paramref name="handlerEvent"/> runs for event <paramref name="e"/> (same type or a base).</summary>
    private static bool Applies(string handlerEvent, TypeModel e) => handlerEvent == e.FullName || e.Bases.Any(b => b == handlerEvent);

    /// <summary>Handlers of a type for events that target another type (ancestor handlers).</summary>
    private bool HasForeignHandlers(TypeModel type)
        => type.Handlers.Any(h => _events.TryGetValue(h.Event, out var e) && e.Target != type.FullName && (e.Target == null || IsAncestor(type.FullName, e.Target)));

    private readonly Dictionary<string, bool> _needsUp = new(System.StringComparer.Ordinal);

    /// <summary>Whether an event of <paramref name="entity"/> has to bubble up: some owner above it has ancestor handlers.</summary>
    private bool NeedsUp(string entity, int depth = 0)
    {
        if (_needsUp.TryGetValue(entity, out var known))
            return known;
        if (depth > 32)
            return false;
        var result = OwnerTypes(entity).Any(o => HasForeignHandlers(o) || (o.FullName != RootName && NeedsUp(o.FullName, depth + 1)));
        _needsUp[entity] = result;
        return result;
    }

    // ───────────────────────────── emission ─────────────────────────────

    public string? Emit()
    {
        if (!_root.CanEmit)
            return null;

        Line("// <auto-generated/>");
        Line("#nullable enable");
        Line();
        EmitRoot();
        foreach (var entity in _model.Entities.Where(static e => e.CanEmit))
            EmitEntity(entity);
        EmitFileAccessors();
        return _sb.ToString();
    }

    private void EmitRoot()
    {
        var interfaces = new List<string> { $"{P}IAggregateRoot<{RootName}>" };
        interfaces.AddRange(_model.Entities.Select(e => $"{P}IRegistry<{e.FullName}>"));

        OpenType(_root, interfaces);

        foreach (var entity in _model.Entities)
        {
            var i = _index[entity.FullName];
            Line($"private readonly global::System.Collections.Generic.Dictionary<{Id}<{entity.FullName}>, {entity.FullName}> __map{i} = new();");
            Line($"bool {P}IRegistry<{entity.FullName}>.TryFind({Id}<{entity.FullName}> id, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out {entity.FullName} entity) => __map{i}.TryGetValue(id, out entity);");
            Line();
        }

        if (!_root.HasIdConstructor)
        {
            Line($"private {_root.Name}({Id}<{RootName}> id) : base(id) {{ }}");
            Line();
        }

        Line($"/// <summary>Rebuilds the aggregate by replaying <paramref name=\"history\"/> (gapless, from version 1). No invariant or rule runs.</summary>");
        Line($"/// <param name=\"id\">The aggregate's id.</param>");
        Line($"/// <param name=\"history\">The stored events, in order.</param>");
        Line($"/// <returns>The aggregate, with no uncommitted changes.</returns>");
        Line($"public static {RootName} Rehydrate({Id}<{RootName}> id, global::System.ReadOnlySpan<{P}StreamEvent> history)");
        Open();
        Line($"var aggregate = new {RootName}(id);");
        Line($"__TarolRoot<{RootName}>.ReplayHistory(aggregate, history);");
        Line("return aggregate;");
        Close();
        Line();

        EmitEventTypeOf();
        EmitDispatch();
        EmitApply(_root);
        EmitHelpers();
        EmitInvariants();
        EmitState();
        EmitRootAccessors();
        CloseType(_root);
    }

    private void EmitEventTypeOf()
    {
        Line($"/// <summary>The stored alias and schema version of an event of this aggregate.</summary>");
        Line($"/// <param name=\"domainEvent\">The event.</param>");
        Line($"/// <returns>Its event type.</returns>");
        Line($"public static {P}EventType EventTypeOf({P}DomainEvent domainEvent) => domainEvent switch");
        Open();
        // most-derived first: a non-sealed event must not hide the events derived from it
        foreach (var e in _model.Events.Where(static e => !e.IsAbstract).OrderByDescending(static e => e.Bases.Count).ThenBy(static e => e.FullName, System.StringComparer.Ordinal))
            Line($"{e.FullName} => new({Literal(e.Alias)}, {e.AliasVersion}),");
        Line($"_ => {P}ThrowHelper.UnknownEvent<{P}EventType>(domainEvent?.GetType().Name ?? \"null\", {Literal(_root.Name)}),");
        _indent--;
        Line("};");
        Line();
    }

    private void EmitDispatch()
    {
        Line($"/// <inheritdoc />");
        Line($"protected override void Dispatch(in {P}StreamEvent stream)");
        Open();
        Line("switch (stream.Event)");
        Open();

        foreach (var entity in _model.Entities)
        {
            if (!entity.CanEmit || !_owners.ContainsKey(entity.FullName))
                continue;
            var owners = OwnerTypes(entity.FullName);
            var x = entity.FullName;
            var i = _index[x];
            var bases = $"{P}Entity<{RootName}, {x}>";

            if (owners.Count > 1)
                foreach (var owner in owners)
                    EmitCreate(entity, owner, $"{bases}.CreatedIn<{owner.FullName}>");
            else
                EmitCreate(entity, owners[0], $"{bases}.Created");

            Line($"case {bases}.Deleted:");
            Open();
            Line($"if (!__map{i}.TryGetValue(({Id}<{x}>)stream.EntityId, out var entity))");
            Line($"    {P}ThrowHelper.UnknownEntity({Literal(entity.Name)}, stream.EntityId);");
            ApplyAndBubble(entity);
            Line($"__Unlink{i}(entity);");
            Line($"__Remove{i}(entity);");
            Line("return;");
            Close();

            Line($"case {bases}.Event:");
            Open();
            Line($"if (!__map{i}.TryGetValue(({Id}<{x}>)stream.EntityId, out var entity))");
            Line($"    {P}ThrowHelper.UnknownEntity({Literal(entity.Name)}, stream.EntityId);");
            ApplyAndBubble(entity);
            Line("return;");
            Close();
        }

        Line($"case {P}AggregateRoot<{RootName}>.Event:");
        Open();
        Line($"if (stream.EntityId != Id.Value)");
        Line($"    {P}ThrowHelper.ForeignEvent({Literal(_root.Name)}, Id.Value, stream.EntityId);");
        if (HasApply(_root))
            Line("__Apply(stream.Event);");
        Line("return;");
        Close();

        Line("default:");
        Line($"    {P}ThrowHelper.UnknownEvent(stream.Event?.GetType().Name ?? \"null\", {Literal(_root.Name)});");
        Line("    return;");
        Close();
        Close();
        Line();
    }

    private void EmitCreate(TypeModel entity, TypeModel owner, string caseType)
    {
        var x = entity.FullName;
        var i = _index[x];
        var member = _owners[x].First(o => o.Owner.FullName == owner.FullName).Member;
        Line($"case {caseType}:");
        Open();
        Line($"var id = ({Id}<{x}>)stream.EntityId;");
        Line($"if (__map{i}.ContainsKey(id))");
        Line($"    {P}ThrowHelper.DuplicateEntity({Literal(entity.Name)}, stream.EntityId);");
        string ownerExpr;
        if (owner.FullName == RootName)
        {
            Line("if (stream.ParentId != Id.Value)");
            Line($"    {P}ThrowHelper.OrphanChild({Literal(entity.Name)}, {Literal(owner.Name)}, stream.ParentId);");
            ownerExpr = "this";
        }
        else
        {
            var o = _index[owner.FullName];
            Line($"if (!__map{o}.TryGetValue(({Id}<{owner.FullName}>)stream.ParentId, out var owner))");
            Line($"    {P}ThrowHelper.OrphanChild({Literal(entity.Name)}, {Literal(owner.Name)}, stream.ParentId);");
            ownerExpr = "owner";
        }

        var container = Container(owner, member, ownerExpr);
        if (member.IsRef)
        {
            Line($"if ({container}.HasValue)");
            Line($"    {P}ThrowHelper.SlotOccupied({Literal(entity.Name)}, {Literal(owner.Name)});");
        }

        Line($"var entity = __New{i}(id, this);");
        Line($"__map{i}.Add(id, entity);");
        Line(member.IsRef ? $"__TarolRef<{x}>.Set({container}, entity);" : $"__TarolSet<{x}>.Add({container}, entity);");
        Line($"__Parent{i}(entity) = {ownerExpr};");
        ApplyAndBubble(entity);
        Line("return;");
        Close();
    }

    private void ApplyAndBubble(TypeModel entity)
    {
        var i = _index[entity.FullName];
        if (HasApply(entity))
            Line($"__Apply{i}(entity, stream.Event);");
        if (NeedsUp(entity.FullName))
            Line($"__Up{i}(entity, stream.Event);");
    }

    /// <summary>The expression for an owner's container member (direct on the root; through an accessor on an entity).</summary>
    private string Container(TypeModel owner, ContainerMember member, string ownerExpr)
        => owner.FullName == RootName
            ? $"this.{member.Name}"
            : $"__Member{_index[owner.FullName]}_{member.Name}({ownerExpr})";

    private void EmitApply(TypeModel type)
    {
        if (!HasApply(type))
            return;

        // Most-derived event types first, so a handler for a base event never hides one for a derived event.
        var handlers = type.Handlers
            .Select(h => (h.Event, Depth: _events.TryGetValue(h.Event, out var e) ? e.Bases.Count : -1))
            .Where(static h => h.Depth >= 0)
            .Distinct()
            .OrderByDescending(static h => h.Depth)
            .ThenBy(static h => h.Event, System.StringComparer.Ordinal)
            .ToList();

        Line($"private void __Apply({P}DomainEvent e)");
        Open();
        Line("switch (e)");
        Open();
        var n = 0;
        foreach (var (evt, _) in handlers)
            Line($"case {evt} e{n}: On(e{n++}); return;");
        Close();
        Close();
        Line();
    }

    /// <summary>Whether the type gets an apply switch: it declares at least one handler for an event of this aggregate.</summary>
    private bool HasApply(TypeModel type) => type.Handlers.Any(h => _events.ContainsKey(h.Event));

    private void EmitHelpers()
    {
        foreach (var entity in _model.Entities.Where(e => e.CanEmit && _owners.ContainsKey(e.FullName)))
        {
            var x = entity.FullName;
            var i = _index[x];
            var owners = OwnerTypes(x);

            // bubble: owner's handlers, then the owner's owners, ending with the root
            if (NeedsUp(x))
            {
                Line($"private void __Up{i}({x} entity, {P}DomainEvent e)");
                Open();
                if (owners.Count == 1)
                {
                    EmitUpStep(owners[0], $"__Parent{i}(entity)");
                }
                else
                {
                    Line($"switch (__Parent{i}(entity))");
                    Open();
                    var n = 0;
                    foreach (var owner in owners)
                    {
                        Line($"case {owner.FullName} p{n}:");
                        _indent++;
                        EmitUpStep(owner, $"p{n}");
                        Line("break;");
                        _indent--;
                        n++;
                    }

                    Close();
                }

                Close();
                Line();
            }

            // unlink from the owner's container
            Line($"private void __Unlink{i}({x} entity)");
            Open();
            if (owners.Count == 1)
            {
                EmitUnlink(entity, owners[0], $"__Parent{i}(entity)");
            }
            else
            {
                Line($"switch (__Parent{i}(entity))");
                Open();
                var n = 0;
                foreach (var owner in owners)
                {
                    Line($"case {owner.FullName} p{n}:");
                    _indent++;
                    EmitUnlink(entity, owner, $"p{n}");
                    Line("break;");
                    _indent--;
                    n++;
                }

                Close();
            }

            Close();
            Line();
        }

        // cascade: remove an entity and everything below it from the maps (no On calls for descendants)
        foreach (var entity in _model.Entities.Where(static e => e.CanEmit))
        {
            var i = _index[entity.FullName];
            Line($"private void __Remove{i}({entity.FullName} entity)");
            Open();
            foreach (var member in entity.Members.Where(m => _entities.TryGetValue(m.Child, out var c) && c.CanEmit))
            {
                var c = _index[member.Child];
                var container = $"__Member{i}_{member.Name}(entity)";
                if (member.IsRef)
                {
                    Line($"if ({container}.Value is {{ }} child{c}_{member.Name})");
                    Line($"    __Remove{c}(child{c}_{member.Name});");
                    Line($"__TarolRef<{member.Child}>.Clear({container});");
                }
                else
                {
                    Line($"foreach (var child in {container})");
                    Line($"    __Remove{c}(child);");
                    Line($"__TarolSet<{member.Child}>.Clear({container});");
                }
            }

            Line($"__map{i}.Remove(entity.Id);");
            Close();
            Line();
        }
    }

    private void EmitUpStep(TypeModel owner, string ownerExpr)
    {
        if (owner.FullName == RootName)
        {
            if (HasForeignHandlers(_root))
                Line("__Apply(e);");
            return;
        }

        var o = _index[owner.FullName];
        if (HasForeignHandlers(owner))
            Line($"__Apply{o}({ownerExpr}, e);");
        if (NeedsUp(owner.FullName))
            Line($"__Up{o}({ownerExpr}, e);");
    }

    private void EmitUnlink(TypeModel entity, TypeModel owner, string ownerExpr)
    {
        var member = _owners[entity.FullName].First(o => o.Owner.FullName == owner.FullName).Member;
        var container = owner.FullName == RootName ? $"this.{member.Name}" : $"__Member{_index[owner.FullName]}_{member.Name}({ownerExpr})";
        Line(member.IsRef ? $"__TarolRef<{entity.FullName}>.Clear({container});" : $"__TarolSet<{entity.FullName}>.Remove({container}, entity);");
    }

    private void EmitInvariants()
    {
        var entitiesWithInvariants = _model.Entities.Where(static e => e.CanEmit && e.Invariants.Count > 0).ToList();
        if (_root.Invariants.Count == 0 && entitiesWithInvariants.Count == 0)
            return;

        Line($"/// <inheritdoc />");
        Line($"protected override {Result} CheckInvariants()");
        Open();
        Line($"{Result} result;");
        foreach (var invariant in _root.Invariants)
        {
            Line($"result = {invariant}();");
            Line("if (!result.IsSuccess) return result;");
        }

        foreach (var entity in entitiesWithInvariants)
        {
            var i = _index[entity.FullName];
            Line($"foreach (var entity in __map{i}.Values)");
            Open();
            Line($"result = __Invariants{i}(entity);");
            Line("if (!result.IsSuccess) return result;");
            Close();
        }

        Line($"return {Result}.Success();");
        Close();
        Line();
    }

    private void EmitRootAccessors()
    {
        foreach (var entity in _model.Entities.Where(static e => e.CanEmit))
        {
            var x = entity.FullName;
            var i = _index[x];
            Line($"{Accessor}Constructor)] private static extern {x} __New{i}({Id}<{x}> id, {RootName} root);");
            Line($"{Accessor}Field, Name = \"__Parent\")] private static extern ref {ParentType(entity)} __Parent{i}({x} entity);");
            if (HasApply(entity))
                Line($"{Accessor}Method, Name = \"__Apply\")] private static extern void __Apply{i}({x} entity, {P}DomainEvent e);");
            if (entity.Invariants.Count > 0)
                Line($"{Accessor}Method, Name = \"__CheckInvariants\")] private static extern {Result} __Invariants{i}({x} entity);");
            foreach (var member in entity.Members.Where(m => _entities.ContainsKey(m.Child)))
            {
                var type = member.IsRef ? $"{P}EntityRef<{member.Child}>" : $"{P}EntitySet<{member.Child}>";
                Line(member.IsField
                    ? $"{Accessor}Field, Name = {Literal(member.Name)})] private static extern ref {type} __Member{i}_{member.Name}({x} owner);"
                    : $"{Accessor}Method, Name = {Literal("get_" + member.Name)})] private static extern {type} __Member{i}_{member.Name}({x} owner);");
            }
        }
    }

    private string ParentType(TypeModel entity)
    {
        var owners = OwnerTypes(entity.FullName);
        return owners.Count == 1 ? owners[0].FullName : "object";
    }

    private void EmitEntity(TypeModel entity)
    {
        var interfaces = OwnerTypes(entity.FullName).Select(o => $"{P}IOwnedBy<{o.FullName}>").ToList();
        OpenType(entity, interfaces);
        Line("#pragma warning disable CS0414 // read and written by the root's generated code through [UnsafeAccessor]");
        Line($"private {ParentType(entity)} __Parent = null!;");
        Line("#pragma warning restore CS0414");
        Line();
        if (!entity.HasIdConstructor)
        {
            Line($"private {entity.Name}({Id}<{entity.FullName}> id, {RootName} root) : base(id, root) {{ }}");
            Line();
        }

        EmitApply(entity);

        if (entity.Invariants.Count > 0)
        {
            Line($"private {Result} __CheckInvariants()");
            Open();
            Line($"{Result} result;");
            foreach (var invariant in entity.Invariants)
            {
                Line($"result = {invariant}();");
                Line("if (!result.IsSuccess) return result;");
            }

            Line($"return {Result}.Success();");
            Close();
        }

        CloseType(entity);
    }

    private void EmitFileAccessors()
    {
        Line($"file static class __TarolRoot<TSelf> where TSelf : {P}AggregateRoot<TSelf>");
        Open();
        Line($"{Accessor}Method, Name = \"ReplayHistory\")] public static extern void ReplayHistory({P}AggregateRoot<TSelf> aggregate, global::System.ReadOnlySpan<{P}StreamEvent> history);");
        if (!_root.LoadByReplay)
            Line($"{Accessor}Method, Name = \"RestoreVersion\")] public static extern void RestoreVersion({P}AggregateRoot<TSelf> aggregate, long version);");
        Close();
        Line();
        Line($"file static class __TarolSet<T> where T : {P}IEntity<T>");
        Open();
        Line($"{Accessor}Method, Name = \"AddInternal\")] public static extern void Add({P}EntitySet<T> set, T entity);");
        Line($"{Accessor}Method, Name = \"RemoveInternal\")] public static extern void Remove({P}EntitySet<T> set, T entity);");
        Line($"{Accessor}Method, Name = \"ClearInternal\")] public static extern void Clear({P}EntitySet<T> set);");
        Close();
        Line();
        Line($"file static class __TarolRef<T> where T : class, {P}IEntity<T>");
        Open();
        Line($"{Accessor}Method, Name = \"SetInternal\")] public static extern void Set({P}EntityRef<T> reference, T entity);");
        Line($"{Accessor}Method, Name = \"ClearInternal\")] public static extern void Clear({P}EntityRef<T> reference);");
        Close();
    }

    // ───────────────────────────── writer ─────────────────────────────

    private void OpenType(TypeModel type, List<string> interfaces)
    {
        if (type.Namespace is not null)
        {
            Line($"namespace {type.Namespace}");
            Open();
        }

        foreach (var container in type.Containers)
        {
            Line($"partial {container.Keyword} {container.Name}");
            Open();
        }

        Line(interfaces.Count == 0 ? $"partial class {type.Name}" : $"partial class {type.Name} : {string.Join(", ", interfaces)}");
        Open();
    }

    private void CloseType(TypeModel type)
    {
        Close();
        foreach (var _ in type.Containers)
            Close();
        if (type.Namespace is not null)
            Close();
        Line();
    }

    private static string Literal(string text) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(text, quote: true);

    private void Open()
    {
        Line("{");
        _indent++;
    }

    private void Close()
    {
        _indent--;
        Line("}");
    }

    private void Line(string text = "")
    {
        if (text.Length > 0)
            _sb.Append(' ', _indent * 4);
        _sb.Append(text).Append('\n');
    }
}
