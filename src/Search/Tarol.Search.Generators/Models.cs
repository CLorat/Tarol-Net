using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Tarol.Search.Generators;

/// <summary>How a member's value becomes text: a string as is, another value through ToString, a collection of strings joined.</summary>
internal enum ValueKind
{
    String,
    Formatted,
    NullableFormatted,
    Strings,
    Guid,
    Guids,
}

/// <summary>One searchable member: its name, kind, weight (0–3) or role.</summary>
internal sealed record MemberModel(string Name, ValueKind Kind, int Weight, bool Exact, bool Title);

/// <summary>An entity reached from the aggregate: its type suffix, label, the navigation from its parent, its members and children.</summary>
internal sealed record EntityModel(string Suffix, string Label, string Property, bool IsSet, EquatableArray<MemberModel> Members, EquatableArray<EntityModel> Children);

/// <summary>What the source is emitted from: names only, no symbols or locations.</summary>
internal sealed record SearchModel(
    string? Namespace,
    EquatableArray<string> ContainingTypes,
    string Name,
    string FullName,
    string Label,
    EquatableArray<MemberModel> Members,
    string? Audience,
    ValueKind AudienceKind,
    string? Language,
    EquatableArray<EntityModel> Entities,
    string Fingerprint)
{
    public string HintName => (Namespace is null ? "" : Namespace + ".") + string.Join(".", ContainingTypes.Select(static c => c.Substring(c.LastIndexOf(' ') + 1))) + (ContainingTypes.IsEmpty ? "" : ".") + Name + ".Search.g.cs";
}

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

/// <summary>The transform's result: the model to emit from (if any) and the diagnostics.</summary>
internal sealed record Discovered(SearchModel? Model, EquatableArray<DiagnosticInfo> Diagnostics);
