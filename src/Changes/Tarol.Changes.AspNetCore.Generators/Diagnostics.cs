using Microsoft.CodeAnalysis;

namespace Tarol.Changes.AspNetCore.Generators;

internal static class Diagnostics
{
    private const string Category = "Tarol.Changes.AspNetCore";

    /// <summary>TWEB001: the route is not "VERB /pattern".</summary>
    public static readonly DiagnosticDescriptor BadRoute = new(
        "TWEB001", "Endpoint route is not a verb and a pattern",
        "[Endpoint(\"{0}\")] on '{1}': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The route of [Endpoint] is the verb (GET, POST, PUT, PATCH or DELETE), one space and a route pattern starting with '/'.");

    /// <summary>TWEB002: [Endpoint] on a type that is no entry.</summary>
    public static readonly DiagnosticDescriptor NotAnEntry = new(
        "TWEB002", "Endpoint type is not a change or a view",
        "'{0}' cannot be an HTTP entry: {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[Endpoint] goes on a change record (IChange<T>, ICreation<T>; POST, PUT, PATCH or DELETE) or on a view (IView<TAggregate, TSelf>; GET). The stored state of an aggregate is not an API (R-8).");

    /// <summary>TWEB003: a route value that cannot be bound.</summary>
    public static readonly DiagnosticDescriptor RouteValue = new(
        "TWEB003", "Route value cannot be bound",
        "'{0}': {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Every {value} of the pattern binds the input member of the same name (case-insensitive); route members are Id<T>, Guid, string, int or long. A view's route has exactly one value: the aggregate's id.");

    /// <summary>TWEB004: two entries with the same verb and pattern.</summary>
    public static readonly DiagnosticDescriptor Duplicate = new(
        "TWEB004", "Two entries share a route",
        "'{0}' and '{1}' both map {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Each verb and pattern belongs to one entry.");

    /// <summary>TWEB005: a [Webhook] factory that cannot be an entry.</summary>
    public static readonly DiagnosticDescriptor BadWebhook = new(
        "TWEB005", "Webhook factory is not a static From(payload)",
        "[Webhook(\"{0}\", \"{1}\")] on '{2}': {3}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[Webhook] goes on a public static method of a change record that takes the sender's payload record and returns the change; one factory per (sender, type, version).");

    /// <summary>TWEB006: an input without a constructor for its members.</summary>
    public static readonly DiagnosticDescriptor NoConstructor = new(
        "TWEB006", "Input has no public primary constructor",
        "'{0}' needs a public constructor taking its members (a positional record) so the generated binder can build it",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The generated binder reads route and body members and calls the input's constructor once, with all of them.");

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "TWEB001" => BadRoute,
        "TWEB002" => NotAnEntry,
        "TWEB003" => RouteValue,
        "TWEB004" => Duplicate,
        "TWEB005" => BadWebhook,
        _ => NoConstructor,
    };
}
