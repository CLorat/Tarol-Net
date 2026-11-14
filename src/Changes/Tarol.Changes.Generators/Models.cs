using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Tarol.Changes.Generators;

internal enum ChangeKind
{
    Change,
    Creation,
    Loads,
}

/// <summary>A [Read] member: its name, the aggregate type (fully qualified) and its simple name.</summary>
internal sealed record ReadModel(string Member, string Aggregate, string AggregateName);

/// <summary>What the source is emitted from: no locations, no symbols, no bodies.</summary>
internal sealed record ChangeModel(
    string? Namespace,
    EquatableArray<string> ContainingTypes,
    string Declaration,
    string Name,
    string FullName,
    string Aggregate,
    ChangeKind Kind,
    string? TargetMember,
    EquatableArray<ReadModel> Reads,
    int Depth)
{
    public bool NeedsPartial => TargetMember is not null || !Reads.IsEmpty;
}

/// <summary>What the dispatcher needs of a change: its name and inheritance depth (so a target rename does not touch it).</summary>
internal sealed record DispatchEntry(string FullName, int Depth);

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

/// <summary>The transform's result: the model to emit from (if any) and the diagnostics with their locations.</summary>
internal sealed record Discovered(ChangeModel? Model, EquatableArray<DiagnosticInfo> Diagnostics)
{
    public static readonly Discovered None = new(null, new EquatableArray<DiagnosticInfo>(ImmutableArray<DiagnosticInfo>.Empty));
}
