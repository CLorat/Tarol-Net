using Microsoft.CodeAnalysis;

namespace Tarol.Changes.Generators;

internal static class Diagnostics
{
    private const string Category = "Tarol.Changes";

    /// <summary>TCHG001: a change without a single target id.</summary>
    public static readonly DiagnosticDescriptor NoTarget = new(
        "TCHG001", "Change has no single target id",
        "'{0}' needs exactly one Id<{1}> member to be its Target, found {2}; implement 'Id<{1}> IChange<{1}>.Target' yourself, or make it an ILoads<{1}> that loads its target",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The pipeline loads the aggregate a change names. The generator implements IChange<T>.Target from the record's only Id<T> member ([Read] members do not count).");

    /// <summary>TCHG002: a change (or a containing type) is not partial.</summary>
    public static readonly DiagnosticDescriptor NotPartial = new(
        "TCHG002", "Change must be partial",
        "'{0}' must be declared partial so that {1} can be generated",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The generator adds IChange<T>.Target and the [Read] loads as a partial declaration; every containing type must be partial too.");

    /// <summary>TCHG003: a shape the generator does not support.</summary>
    public static readonly DiagnosticDescriptor Unsupported = new(
        "TCHG003", "Unsupported change shape",
        "'{0}' is not supported as a change: {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A change is a non-generic, non-file-local type that implements exactly one of IChange<T>, ICreation<T> or ILoads<T>, for one aggregate.");

    /// <summary>TCHG004: [Read] on a member it cannot load.</summary>
    public static readonly DiagnosticDescriptor ReadMisuse = new(
        "TCHG004", "[Read] member cannot be loaded",
        "[Read] on '{0}' of '{1}': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[Read] marks an Id<X> member whose aggregate X the pipeline loads read-only before Execute. The change's own target is loaded anyway and is not a read.");

    /// <summary>TCHG005: value-object rules that would not run.</summary>
    public static readonly DiagnosticDescriptor NotValidated = new(
        "TCHG005", "Change input is not validated",
        "'{0}' has members with validation rules ({1}) but does not implement IValidatable, so the pipeline cannot check them before the load",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Inputs built from JSON, routes or messages use the value objects' trusting constructors. Declare the change IValidatable so Tarol.Validations generates Validate and the pipeline returns all field errors as Invalid before any read.");

    /// <summary>TCHG006: an [OnEvent&lt;T&gt;] factory of the wrong shape.</summary>
    public static readonly DiagnosticDescriptor ReactionShape = new(
        "TCHG006", "Unsupported reaction factory",
        "[OnEvent<{0}>] on '{1}': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A reaction factory is a static, non-private method of a change that returns that change and takes (TEvent) or (TEvent, RecordedEvent). It maps the committed event to the change's input and does nothing else.");

    /// <summary>TCHG007: two reactions with one name, or two factories of one change for one event.</summary>
    public static readonly DiagnosticDescriptor ReactionName = new(
        "TCHG007", "Duplicate reaction",
        "The reaction '{0}' is declared more than once ({1}); reaction names are stored in outbox rows and idempotency keys and must be unique",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A reaction's name is the change's type name, or the Name of its [OnEvent<T>]. Two changes cannot share a name, and one change has at most one factory per event type.");

    /// <summary>TCHG008: DueAt does not name a DateTimeOffset property of the event.</summary>
    public static readonly DiagnosticDescriptor ReactionDueAt = new(
        "TCHG008", "Reaction due time is not a DateTimeOffset of the event",
        "[OnEvent<{0}>(DueAt = \"{1}\")] on '{2}': {0} has no DateTimeOffset property '{1}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "DueAt names the event member that holds when the reaction is due (a saga timeout), e.g. DueAt = nameof(MyApp.Settlement.DeadlineSet.DueAt). Qualify the type when a member of the change has the same name.");

    /// <summary>TCHG009: a [Scheduled] factory of the wrong shape.</summary>
    public static readonly DiagnosticDescriptor JobShape = new(
        "TCHG009", "Unsupported scheduled factory",
        "[Scheduled] on '{0}': {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A scheduled factory is a static, non-private method of a change (IChange<T> or ICreation<T>) that returns that change and takes one JobOccurrence. It maps the occurrence to the change's input and does nothing else.");

    /// <summary>TCHG010: a cron literal the job host would refuse at startup.</summary>
    public static readonly DiagnosticDescriptor JobCron = new(
        "TCHG010", "Invalid cron schedule",
        "[Scheduled(\"{0}\")] on '{1}': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A schedule is 5-field Unix cron: minute hour day-of-month month day-of-week, with lists, ranges, steps and the names JAN-DEC, SUN-SAT. Seconds, L, W, # and ? are not supported; use Every(TimeSpan) for intervals.");

    /// <summary>TCHG011: two scheduled changes with one job name.</summary>
    public static readonly DiagnosticDescriptor JobName = new(
        "TCHG011", "Duplicate job",
        "The job '{0}' is declared more than once ({1}); job names are stored in job rows and idempotency keys and must be unique",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A job's name is the change's type name, or the Name of its [Scheduled]. Two changes cannot share a name.");

    public static readonly DiagnosticDescriptor NameAcrossAssemblies = new(
        "TCHG012", "Reaction or job name in two assemblies",
        "The {0} '{1}' is declared in '{2}'; names are stored in outbox and job rows, so ReactionSet.Combine/JobSet.Combine refuse to start. Give one of them a unique Name ([OnEvent<T>(Name = …)] or [Scheduled(Name = …)]) before it is deployed.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Reported by every assembly that references two or more assemblies declaring the same reaction or job name (typically the host). Renaming after a deployment breaks rows in flight; pin the old name with Name instead.");

    public static readonly DiagnosticDescriptor ReadsMisuse = new(
        "TCHG013", "[Reads<T>] cannot be loaded",
        "[Reads<{0}>] on '{1}': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[Reads<T>] loads T before Execute: the tenant's one when T is [PerTenant], otherwise the one with the Guid of the member SameIdAs names (an Id<X> or a Guid). The generator writes IHasReads; a change does not implement it by hand.");

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "TCHG013" => ReadsMisuse,
        "TCHG012" => NameAcrossAssemblies,
        "TCHG009" => JobShape,
        "TCHG010" => JobCron,
        "TCHG011" => JobName,
        "TCHG006" => ReactionShape,
        "TCHG007" => ReactionName,
        "TCHG008" => ReactionDueAt,
        "TCHG001" => NoTarget,
        "TCHG002" => NotPartial,
        "TCHG003" => Unsupported,
        "TCHG004" => ReadMisuse,
        _ => NotValidated,
    };
}
