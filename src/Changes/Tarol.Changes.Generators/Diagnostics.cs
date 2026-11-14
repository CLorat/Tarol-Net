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

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "TCHG001" => NoTarget,
        "TCHG002" => NotPartial,
        "TCHG003" => Unsupported,
        "TCHG004" => ReadMisuse,
        _ => NotValidated,
    };
}
