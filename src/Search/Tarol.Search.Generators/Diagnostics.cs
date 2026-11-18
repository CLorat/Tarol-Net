using Microsoft.CodeAnalysis;

namespace Tarol.Search.Generators;

internal static class Diagnostics
{
    private const string Category = "Tarol.Search";

    /// <summary>TSRCH001: [Searchable] on a class the generator cannot implement ISearchable on.</summary>
    public static readonly DiagnosticDescriptor Declaration = new(
        "TSRCH001", "Unsupported searchable declaration",
        "[Searchable] on '{0}': {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A searchable aggregate is a non-generic partial class deriving from AggregateRoot<T> (every containing type partial too).");

    /// <summary>TSRCH002: a search member whose type has no text.</summary>
    public static readonly DiagnosticDescriptor MemberType = new(
        "TSRCH002", "Search member has an unsupported type",
        "'{0}.{1}': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[Searchable] and [SearchTitle] members are strings, enums, value types or records with ToString, or a collection of strings; [SearchAudience] is a string, a Guid or a collection of them; [SearchLanguage] a string.");

    /// <summary>TSRCH003: personal data searchable as text.</summary>
    public static readonly DiagnosticDescriptor Personal = new(
        "TSRCH003", "Personal data is searchable only as an exact token",
        "'{0}.{1}' is [PersonalData]: mark it [Searchable(Exact = true)], or do not make it searchable",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Personal data leaves the process sealed (DP-2); in the search index it is an HMAC token of the whole normalised value under the tenant's lookup key, never text (GS-8). A [SearchTitle] cannot be personal data.");

    /// <summary>TSRCH004: two titles, or an audience or language outside the aggregate.</summary>
    public static readonly DiagnosticDescriptor Placement = new(
        "TSRCH004", "Search member in the wrong place",
        "'{0}.{1}': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "One [SearchTitle] per aggregate or entity; [SearchAudience] and [SearchLanguage] on the aggregate only (entities inherit them), at most one each.");

    /// <summary>TSRCH005: a searchable aggregate without anything to find.</summary>
    public static readonly DiagnosticDescriptor Empty = new(
        "TSRCH005", "Searchable aggregate has no searchable member",
        "'{0}' is [Searchable] but neither it nor its entities have a [Searchable] or [SearchTitle] member",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Mark the members whose text should be found: [Searchable(SearchWeight.A)] on titles and names, [Searchable] on descriptions.");

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "TSRCH001" => Declaration,
        "TSRCH002" => MemberType,
        "TSRCH003" => Personal,
        "TSRCH004" => Placement,
        _ => Empty,
    };
}
