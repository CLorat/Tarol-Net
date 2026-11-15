using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Tarol.Changes.AspNetCore.Generators;

internal enum EntryKind { Change, Creation, View }

/// <summary>An input member: C# name, type, explicit JSON name, route value kind (null = body), optional, default literal.</summary>
internal sealed record Member(string Name, string Type, string? JsonName, string? RouteName, string? RouteKind, bool Optional, string? DefaultLiteral);

/// <summary>An [Endpoint] entry, symbol- and location-free (incrementality).</summary>
internal sealed record EntryModel(string Name, string FullName, EntryKind Kind, string Aggregate, string Verb, string Pattern, string? Policy,
    bool Anonymous, EquatableArray<Member> Members, LocationInfo? Location)
{
    public bool HasBody => Kind != EntryKind.View && Members.Count > 0 && System.Linq.Enumerable.Any(Members, m => m.RouteKind is null);

    // equality without the location, so a moved declaration regenerates nothing
    public bool Equals(EntryModel? other) => other is not null && Name == other.Name && FullName == other.FullName && Kind == other.Kind
        && Aggregate == other.Aggregate && Verb == other.Verb && Pattern == other.Pattern && Policy == other.Policy && Anonymous == other.Anonymous
        && Members.Equals(other.Members);

    public override int GetHashCode() => (FullName, Verb, Pattern).GetHashCode();
}

/// <summary>A [Webhook] factory.</summary>
internal sealed record HookModel(string Sender, string Type, string? Version, string Payload, string Change, string Method);

/// <summary>A value object's schema for OpenAPI (from its single positional parameter and its rule attributes).</summary>
internal sealed record ValueObjectModel(string FullName, string JsonType, string? Format, int? MinLength, int? MaxLength, string? Minimum, string? Maximum,
    string? Pattern);

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

internal sealed record EntryResult(EntryModel? Model, EquatableArray<ValueObjectModel> ValueObjects, EquatableArray<DiagnosticInfo> Diagnostics);

internal sealed record HookResult(HookModel? Model, DiagnosticInfo? Diagnostic);
