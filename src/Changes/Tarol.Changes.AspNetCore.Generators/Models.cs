using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Tarol.Changes.AspNetCore.Generators;

internal enum EntryKind { Change, Creation, View, List, Row }

/// <summary>An input member: C# name, type, explicit JSON name, route value kind (null = body), optional, default literal.</summary>
internal sealed record Member(string Name, string Type, string? JsonName, string? RouteName, string? RouteKind, bool Optional, string? DefaultLiteral)
{
    /// <summary>The members of a record this member holds (also as a list element), for <c>Required</c> in every depth (TN-23).</summary>
    public EquatableArray<RequiredModel> Nested { get; init; }

    /// <summary>Whether the member is a list of those records.</summary>
    public bool NestedList { get; init; }
}

/// <summary>A member of a nested input record: its name, JSON name, whether it may be missing, whether it is a list, its own members.</summary>
internal sealed record RequiredModel(string Name, string? JsonName, bool Optional, bool List, EquatableArray<RequiredModel> Members);

/// <summary>A filter of a list: the member, its non-nullable type, the column, how it is parsed ("enum", "string", "date", "time" or "parsable"),
/// where it comes from ("query", "route", "claim"), the claim type and the OpenAPI kind.</summary>
internal sealed record FilterModel(string Member, string Type, string Column, string Parse, string Source, string? Claim, string OpenApiKind, EquatableArray<string> Values);

/// <summary>A sort of a list: the enum member, the column (or "id") and the direction.</summary>
internal sealed record SortModel(string EnumMember, string Column, bool Descending);

/// <summary>What a list entry adds: the row, its filters and its sorts (with the default), whether a list scope is required (TN-6).</summary>
internal sealed record QueryModel(string Row, EquatableArray<FilterModel> Filters, string? SortMember, EquatableArray<SortModel> Sorts, string? DefaultSort,
    bool RequiresScope = false);

/// <summary>An [Endpoint] entry, symbol- and location-free (incrementality).</summary>
internal sealed record EntryModel(string Name, string FullName, EntryKind Kind, string Aggregate, string Verb, string Pattern, string? Policy,
    bool Anonymous, EquatableArray<Member> Members, LocationInfo? Location, QueryModel? Query = null)
{
    /// <summary>The problem codes known at compile time (BC-3: <c>x-tarol-errors</c>), sorted.</summary>
    public EquatableArray<string> Errors { get; init; }

    /// <summary>A change that may start its stream (TN-42): the document lists 201 next to 204.</summary>
    public bool MayCreate { get; init; }

    /// <summary>A view of an IStartable&lt;T&gt; aggregate (TN-42): a missing stream answers the started state.</summary>
    public bool Startable { get; init; }

    public bool HasBody => Kind is not (EntryKind.View or EntryKind.List) && Members.Count > 0 && System.Linq.Enumerable.Any(Members, m => m.RouteKind is null);

    // equality without the location, so a moved declaration regenerates nothing
    public bool Equals(EntryModel? other) => other is not null && Name == other.Name && FullName == other.FullName && Kind == other.Kind
        && Aggregate == other.Aggregate && Verb == other.Verb && Pattern == other.Pattern && Policy == other.Policy && Anonymous == other.Anonymous
        && Members.Equals(other.Members) && Equals(Query, other.Query) && Errors.Equals(other.Errors) && MayCreate == other.MayCreate && Startable == other.Startable;

    public override int GetHashCode() => (FullName, Verb, Pattern).GetHashCode();
}

/// <summary>A [Webhook] factory.</summary>
internal sealed record HookModel(string Sender, string Type, string? Version, string Payload, string Change, string Method);

/// <summary>A value object's schema for OpenAPI (from its single positional parameter and its rule attributes).</summary>
internal sealed record ValueObjectModel(string FullName, string JsonType, string? Format, int? MinLength, int? MaxLength, string? Minimum, string? Maximum,
    string? Pattern)
{
    /// <summary>The rules as <c>x-tarol-rules</c> (BC-4).</summary>
    public EquatableArray<RuleModel> Rules { get; init; }
}

/// <summary>One validation rule as the client checks it (BC-4): the attribute, the code it reports, its bounds or pattern.</summary>
internal sealed record RuleModel(string Rule, string Code, string? Min, string? Max, string? Pattern, string? Flags, string? Step = null);

/// <summary>The rules of one member (a constructor parameter) of an input, a multi-field or class value object (BC-4).</summary>
internal sealed record MemberRulesModel(string Type, string Member, string? JsonName, EquatableArray<RuleModel> Rules);

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

internal sealed record EntryResult(EntryModel? Model, EquatableArray<ValueObjectModel> ValueObjects, EquatableArray<DiagnosticInfo> Diagnostics)
{
    /// <summary>The member rules of the input and of the types its members reach (BC-4).</summary>
    public EquatableArray<MemberRulesModel> MemberRules { get; init; }
}

internal sealed record HookResult(HookModel? Model, DiagnosticInfo? Diagnostic);

/// <summary>A <c>[ContractType]</c> type (its fully qualified name) or a <c>[ContractConstant]</c> (name and C# literal) of the assembly (TN-35).</summary>
internal sealed record ContractModel(string? Type, string? Constant, string? Literal);
