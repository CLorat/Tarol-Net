using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Tarol.Primitives.Generators;

internal enum TypeKind
{
    Root,
    Entity,
    Event,
}

internal enum EventKind
{
    None,
    RootEvent,
    EntityEvent,
    Created,
    CreatedIn,
    Deleted,
}

/// <summary>A containing type to re-open around generated code (<c>partial class Outer { … }</c>).</summary>
internal sealed record ContainerModel(string Keyword, string Name);

/// <summary>An <c>EntitySet&lt;T&gt;</c> or <c>EntityRef&lt;T&gt;</c> member of a root or entity.</summary>
internal sealed record ContainerMember(string Name, string Child, bool IsRef, bool IsField, LocationInfo? Location);

/// <summary>An <c>On(TEvent)</c> handler.</summary>
internal sealed record HandlerModel(string Event, LocationInfo? Location);

/// <summary>
/// One root, entity or event, as strings and value types only (no symbols, no <see cref="Location"/>), so the per-type
/// transform is cached; grouped per aggregate by <see cref="RootKey"/>.
/// </summary>
internal sealed record TypeModel(
    TypeKind Kind,
    string RootKey,
    string FullName,
    string Name,
    string HintName,
    string? Namespace,
    EquatableArray<ContainerModel> Containers,
    bool CanEmit,
    // root and entity
    EquatableArray<ContainerMember> Members,
    EquatableArray<HandlerModel> Handlers,
    EquatableArray<string> Invariants,
    bool HasIdConstructor,
    // event
    EventKind EventKind,
    string? Target,
    string? Parent,
    bool IsAbstract,
    EquatableArray<string> Bases,
    string Alias,
    int AliasVersion,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public TypeModel WithoutLocations() => this with
    {
        Location = null,
        Diagnostics = default,
        Members = new EquatableArray<ContainerMember>(Members.Select(static m => m with { Location = null }).ToImmutableArray()),
        Handlers = new EquatableArray<HandlerModel>(Handlers.Select(static h => h with { Location = null }).ToImmutableArray()),
    };
}

/// <summary>Every type of one aggregate, sorted by full name (equatable, so the aggregate is regenerated only when it changes).</summary>
internal sealed record AggregateModel(TypeModel Root, EquatableArray<TypeModel> Entities, EquatableArray<TypeModel> Events)
{
    public AggregateModel WithoutLocations() => new(
        Root.WithoutLocations(),
        new EquatableArray<TypeModel>(Entities.Select(static e => e.WithoutLocations()).ToImmutableArray()),
        new EquatableArray<TypeModel>(Events.Select(static e => e.WithoutLocations()).ToImmutableArray()));
}

/// <summary>An equatable diagnostic for the pipeline.</summary>
internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Arguments)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments)
        => new(descriptor.Id, location, new EquatableArray<string>(arguments.ToImmutableArray()));

    public Diagnostic ToDiagnostic()
    {
        var args = new object[Arguments.Count];
        var i = 0;
        foreach (var argument in Arguments)
            args[i++] = argument;
        return Diagnostic.Create(Diagnostics.ById(Id), Location?.ToLocation(), args);
    }
}
