using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Tarol.Queries.Generators;

/// <summary>One When: the event (fully qualified), whether it takes the RecordedEvent too, and its inheritance depth.</summary>
internal sealed record WhenModel(string Event, bool WithRecorded, int Depth);

/// <summary>One typed column: member, column name, ColumnType member, stored CLR type (the element type of an array column), getter expression over 'r'.</summary>
internal sealed record ColumnModel(string Member, string Column, string Type, string ValueType, string Getter, bool Nullable, bool Sortable, bool Array = false);

/// <summary>What the source is emitted from: no locations, no symbols, no bodies.</summary>
internal sealed record ProjectionModel(
    string? Namespace,
    EquatableArray<string> ContainingTypes,
    string Name,
    string FullName,
    string ProjectionName,
    int Version,
    string Aggregate,
    EquatableArray<WhenModel> Creators,
    EquatableArray<WhenModel> Updaters,
    EquatableArray<ColumnModel> Columns)
{
    /// <summary>The row has <c>static TRow Empty(RecordedEvent r)</c>: the start of an instance <c>When</c> on a new stream (TN-54).</summary>
    public bool HasEmpty { get; init; }

    /// <summary><c>[Projection(…, PerEntity = true)]</c>: one row per entity (TN-54, P-1).</summary>
    public bool PerEntity { get; init; }

    public string HintName => (Namespace is null ? "" : Namespace + ".") + string.Join(".", ContainingTypes.Select(static c => c.Substring(c.LastIndexOf(' ') + 1))) + (ContainingTypes.IsEmpty ? "" : ".") + Name + ".Projection.g.cs";
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
internal sealed record Discovered(ProjectionModel? Model, EquatableArray<DiagnosticInfo> Diagnostics);
