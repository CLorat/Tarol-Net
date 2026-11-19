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

    /// <summary>TQRY005: a query record member that names no column, or a sort that names no [Sort] column.</summary>
    public static readonly DiagnosticDescriptor QueryMember = new(
        "TQRY005", "Query member names no column of the projection",
        "'{0}.{1}': {2}",
        "Tarol.Queries", DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A list query record (IQuery<TRow>) has nullable filter members named like a typed column of the row (equality; absent = no filter), at most one enum member named Sort whose members name [Sort] members of the row (or Id), route members and [FromClaim] members. Nothing else becomes part of the query.");

    /// <summary>TQRY006: a [FromClaim] member also named by the route.</summary>
    public static readonly DiagnosticDescriptor ClaimAlsoRouted = new(
        "TQRY006", "Claim-bound member is also a route value",
        "'{0}.{1}' is bound from the claim '{2}' and named by the route value '{{{3}}}'; the claim wins and the route value is ignored",
        "Tarol.Queries", DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "A [FromClaim] member is set from the caller's principal, never from the request (QR-6). Remove the route value, or the attribute.");

    /// <summary>TWEB007: an Option&lt;T&gt; member with a parameter default (CC-5).</summary>
    public static readonly DiagnosticDescriptor OptionDefault = new(
        "TWEB007", "Option<T> member with a default value",
        "'{0}.{1}' is an Option<T> with a default value; ASP.NET's OpenAPI document cannot describe it (it throws): drop the default, None is Option<T>'s default already",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Microsoft.AspNetCore.OpenApi reads a constructor parameter's default value through the JSON converter, which fails for Option<T> (CC-5, docs/migration/client-contract.md). An Option<T> member is None when the JSON omits it; the default adds nothing.");

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "TWEB007" => OptionDefault,
        "TQRY005" => QueryMember,
        "TQRY006" => ClaimAlsoRouted,
        "TWEB001" => BadRoute,
        "TWEB002" => NotAnEntry,
        "TWEB003" => RouteValue,
        "TWEB004" => Duplicate,
        "TWEB005" => BadWebhook,
        _ => NoConstructor,
    };
}
