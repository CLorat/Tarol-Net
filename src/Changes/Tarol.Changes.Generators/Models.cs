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
/// <summary>
/// One read before Execute: <see cref="Key"/> in <c>Reads</c>; <see cref="IdExpression"/> computes the id from <c>this</c> and
/// <c>context</c> (<see langword="null"/>: the tenant's one, TN-40); <see cref="Optional"/>: a miss is left out.
/// </summary>
internal sealed record ReadModel(string Key, string Aggregate, string AggregateName, string? IdExpression, bool Optional);

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

/// <summary>One [OnEvent&lt;T&gt;] factory: what the reaction set is emitted from (no locations).</summary>
internal sealed record ReactionModel(
    string Name,
    string Change,
    string Event,
    int EventDepth,
    string Factory,
    bool WithRecorded,
    string? DueAt) : System.IComparable<ReactionModel>
{
    public int CompareTo(ReactionModel? other)
    {
        if (other is null)
            return 1;
        var byName = string.CompareOrdinal(Name, other.Name);
        return byName != 0 ? byName : string.CompareOrdinal(Event, other.Event);
    }
}

/// <summary>Where a reaction is declared, for TCHG007 only (kept apart from the emitted models).</summary>
internal sealed record ReactionSite(string Name, string Change, string Event, LocationInfo? Location);

/// <summary>The transform's result for one factory.</summary>
internal sealed record DiscoveredReaction(ReactionModel? Model, ReactionSite? Site, EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>One [Scheduled] factory: what the job set is emitted from (no locations).</summary>
internal sealed record JobModel(string Name, string Change, string Factory, string Cron, string TimeZone, int Missed, bool PerTenant)
    : System.IComparable<JobModel>
{
    public int CompareTo(JobModel? other) => other is null ? 1 : string.CompareOrdinal(Name, other.Name);
}

/// <summary>Where a job is declared, for TCHG011 only.</summary>
internal sealed record JobSite(string Name, string Change, LocationInfo? Location);

/// <summary>The transform's result for one [Scheduled] factory.</summary>
internal sealed record DiscoveredJob(JobModel? Model, JobSite? Site, EquatableArray<DiagnosticInfo> Diagnostics);
