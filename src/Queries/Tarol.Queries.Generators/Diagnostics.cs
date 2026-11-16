using Microsoft.CodeAnalysis;

namespace Tarol.Queries.Generators;

internal static class Diagnostics
{
    private const string Category = "Tarol.Queries";

    /// <summary>TQRY001: [Projection] on a type the generator cannot implement IProjection on.</summary>
    public static readonly DiagnosticDescriptor Declaration = new(
        "TQRY001", "Unsupported projection declaration",
        "[Projection] on '{0}': {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A projection is a non-generic partial record class (every containing type partial too) with a name of lowercase letters, digits and hyphens (at most 40, starting with a letter) and a Version of 1 or more.");

    /// <summary>TQRY002: no static When creating the row, or two When for one event.</summary>
    public static readonly DiagnosticDescriptor Creation = new(
        "TQRY002", "Projection has no creating When, or two When for one event",
        "'{0}': {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A static When(TEvent) or When(TEvent, RecordedEvent) returning the row creates a stream's row from its first relevant event; instance When methods return the next row. One static and one instance When at most per event type.");

    /// <summary>TQRY003: a When the switch cannot call.</summary>
    public static readonly DiagnosticDescriptor When = new(
        "TQRY003", "When is not an event handler of the projection",
        "'{0}.When({1})': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A When takes a concrete event of the projection's one aggregate (an AggregateRoot<T> or Entity<T, _> event), optionally followed by RecordedEvent, and returns the row record. All Whens of a projection read the same aggregate.");

    /// <summary>TQRY004: [Sort] on a member that is no sortable column.</summary>
    public static readonly DiagnosticDescriptor Sort = new(
        "TQRY004", "Sort member has no sortable column",
        "[Sort] on '{0}.{1}': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A sort is a non-nullable scalar member (Guid, string, bool, short, int, long, decimal, double, DateTimeOffset or an enum) whose column name is not tenant, id, version or doc.");

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "TQRY001" => Declaration,
        "TQRY002" => Creation,
        "TQRY003" => When,
        _ => Sort,
    };
}
