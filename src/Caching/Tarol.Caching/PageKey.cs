using System.Globalization;
using System.Text;
using Tarol.Queries;

namespace Tarol.Caching;

/// <summary>
/// The key of a cached list page: the tenant first (a page of one tenant is never another's), the projection's name and
/// version, then the canonical query (<see cref="Query"/>).
/// </summary>
/// <param name="Tenant">The query's tenant (<see cref="Guid.Empty"/> for none).</param>
/// <param name="Projection">The projection's name.</param>
/// <param name="Version">The projection's version.</param>
/// <param name="Query">
/// The canonical query: the sort and direction, the filters sorted by column (a <c>[FromClaim]</c> value is a filter, so each
/// claim value has its own key), the cursor, the offset, the limit and the count flag. Every part is length-prefixed, so no
/// value can make two queries share a key.
/// </param>
public readonly record struct PageKey(Guid Tenant, string Projection, int Version, string Query)
{
    /// <summary>The key of a checked query.</summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <param name="plan">The query checked against the projection's columns (<see cref="ListQueryPlan{TRow}.Create"/>).</param>
    /// <returns>The key.</returns>
    public static PageKey Of<TRow>(ListQueryPlan<TRow> plan)
        where TRow : class, IProjection<TRow>
    {
        ArgumentNullException.ThrowIfNull(plan);
        var filters = new List<(string Column, string? Value)>(plan.Filters.Count);
        foreach (var (column, value) in plan.Filters)
            filters.Add((column.Name, value is null ? null : ColumnValues.Format(column.Type, value)));
        filters.Sort(static (a, b) =>
        {
            var byColumn = string.CompareOrdinal(a.Column, b.Column);
            return byColumn != 0 ? byColumn : string.CompareOrdinal(a.Value, b.Value);
        });
        var query = new StringBuilder(64);
        Part(query, "s", plan.SortKey);
        foreach (var (column, value) in filters)
        {
            Part(query, "f", column);
            Part(query, "=", value);
        }
        Part(query, "a", plan.HasCursor ? plan.Query.After : null);
        query.Append(CultureInfo.InvariantCulture, $"o{plan.Offset};l{plan.Limit};c{(plan.Query.Count ? 1 : 0)}");
        return new PageKey(plan.Query.Tenant, NameOf<TRow>(), VersionOf<TRow>(), query.ToString());
    }

    private static string NameOf<TRow>()
        where TRow : class, IProjection<TRow> => TRow.Name;

    private static int VersionOf<TRow>()
        where TRow : class, IProjection<TRow> => TRow.Version;

    // tag, then the length and the text ("-" for null): "f6:status=4:Open"
    private static void Part(StringBuilder key, string tag, string? text)
    {
        key.Append(tag);
        if (text is null)
            key.Append("-;");
        else
            key.Append(CultureInfo.InvariantCulture, $"{text.Length}:").Append(text).Append(';');
    }
}
