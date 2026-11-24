using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Tarol.Persistence;
using Tarol.Results;

namespace Tarol.Queries;

/// <summary>An equality filter on a typed column: <c>column = value</c>, or <c>column IS NULL</c> for a <see langword="null"/> value.</summary>
/// <param name="Column">The column name (<see cref="ProjectionColumn{TRow}.Name"/>).</param>
/// <param name="Value">The value (an enum, a time, …: converted to the stored type).</param>
public readonly record struct ColumnFilter(string Column, object? Value);

/// <summary>
/// A set filter (TN-6): <c>column IN (values)</c>, or, for an array column (<see cref="ProjectionColumn{TRow}.IsArray"/>), the
/// array shares a value with the set (<c>column &amp;&amp; values</c>). <see cref="IdColumn"/> names the row id. An empty set
/// matches no row: a scope with nothing to see lists an empty page, never an unfiltered one.
/// </summary>
/// <param name="Column">The column name (<see cref="ProjectionColumn{TRow}.Name"/>, or the member name), or <see cref="IdColumn"/>.</param>
/// <param name="Values">The values (converted to the stored type; no <see langword="null"/>s).</param>
public sealed record SetFilter(string Column, IReadOnlyCollection<object> Values)
{
    /// <summary>The name of the row id in a set filter: <c>id</c>.</summary>
    public const string IdColumn = "id";

    /// <summary>A set filter over typed values.</summary>
    /// <typeparam name="T">The value type (a <see cref="Guid"/>, a <see cref="string"/>, an enum, …).</typeparam>
    /// <param name="column">The column or member name, or <see cref="IdColumn"/>.</param>
    /// <param name="values">The values.</param>
    /// <returns>The filter.</returns>
    public static SetFilter Of<T>(string column, IEnumerable<T> values)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(values);
        return new SetFilter(column, [.. values.Select(static v => (object)v)]);
    }
}

/// <summary>
/// What an <c>IListScope&lt;TQuery&gt;</c> answers for the caller (TN-6): the filters added to every list request, after the
/// request's own. <see cref="All"/> adds none, <see cref="None"/> lets the caller see no row (an empty page), and
/// <see cref="In{T}"/> restricts a column (or the row id) to a set of values, e.g. the children a parent may see.
/// </summary>
public sealed class ListScope
{
    private ListScope(IReadOnlyList<ColumnFilter> filters, IReadOnlyList<SetFilter> sets) => (Filters, Sets) = (filters, sets);

    /// <summary>No restriction: the caller sees every row the request selects.</summary>
    public static ListScope All { get; } = new([], []);

    /// <summary>The caller sees no row: every page is empty (a set filter on the id with an empty set).</summary>
    public static ListScope None { get; } = new([], [new SetFilter(SetFilter.IdColumn, [])]);

    /// <summary>Restricts a column to a set: <c>column IN (values)</c>, or overlap for an array column. An empty set is <see cref="None"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="column">The column or member name (<c>nameof(Row.Children)</c>), or <see cref="SetFilter.IdColumn"/> for the row id.</param>
    /// <param name="values">The values.</param>
    /// <returns>The scope.</returns>
    public static ListScope In<T>(string column, IEnumerable<T> values)
        where T : notnull
        => All.AndIn(column, values);

    /// <summary>Restricts a column to one value: <c>column = value</c> (<c>IS NULL</c> for <see langword="null"/>).</summary>
    /// <param name="column">The column or member name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The scope.</returns>
    public static ListScope Where(string column, object? value) => All.AndWhere(column, value);

    /// <summary>This scope and a set restriction (both must hold).</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="column">The column or member name, or <see cref="SetFilter.IdColumn"/>.</param>
    /// <param name="values">The values.</param>
    /// <returns>The scope.</returns>
    public ListScope AndIn<T>(string column, IEnumerable<T> values)
        where T : notnull
        => new(Filters, [.. Sets, SetFilter.Of(column, values)]);

    /// <summary>This scope and an equality restriction (both must hold).</summary>
    /// <param name="column">The column or member name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The scope.</returns>
    public ListScope AndWhere(string column, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return new([.. Filters, new ColumnFilter(column, value)], Sets);
    }

    /// <summary>The equality filters.</summary>
    public IReadOnlyList<ColumnFilter> Filters { get; }

    /// <summary>The set filters.</summary>
    public IReadOnlyList<SetFilter> Sets { get; }

    /// <summary>Whether the scope adds no filter (<see cref="All"/>).</summary>
    public bool IsAll => Filters.Count == 0 && Sets.Count == 0;
}

/// <summary>
/// A list query over one projection (QR-4): always one tenant, equality filters on typed columns, one sort (a
/// <see cref="SortAttribute"/> column, or the row id) with the id as tie-break, and either a cursor (<see cref="After"/>,
/// the default way to page) or a bounded offset. Nothing in it becomes SQL text: columns are checked against the
/// projection's column map and values are parameters.
/// </summary>
/// <param name="Tenant">The tenant (<see cref="Guid.Empty"/> for none). Set it from the caller, never from the request.</param>
public sealed record ListQuery(Guid Tenant)
{
    /// <summary>The largest page (200).</summary>
    public const int MaxLimit = 200;

    /// <summary>The deepest offset (1,000); deeper pages need the cursor (offset 3.2 s at depth 900,000, cursor 0.6 ms).</summary>
    public const int MaxOffset = 1000;

    /// <summary>The default page size (50).</summary>
    public const int DefaultLimit = 50;

    /// <summary>The filters (all must hold).</summary>
    public IReadOnlyList<ColumnFilter> Filters { get; init; } = [];

    /// <summary>The set filters (all must hold, with <see cref="Filters"/>); a list scope's (TN-6). An empty set matches no row.</summary>
    public IReadOnlyList<SetFilter> Sets { get; init; } = [];

    /// <summary>This query restricted by <paramref name="scope"/>: its filters and sets are added to the query's own.</summary>
    /// <param name="scope">The caller's scope.</param>
    /// <returns>The query.</returns>
    public ListQuery Within(ListScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.IsAll ? this : this with { Filters = [.. Filters, .. scope.Filters], Sets = [.. Sets, .. scope.Sets] };
    }

    /// <summary>The sort column (a <see cref="SortAttribute"/> member's column), or <see langword="null"/> for the row id.</summary>
    public string? Sort { get; init; }

    /// <summary>Whether the sort descends (the id tie-break with it).</summary>
    public bool Descending { get; init; }

    /// <summary>The cursor of the previous page (<see cref="Page{T}.Next"/>); bound to the sort and the filters.</summary>
    public string? After { get; init; }

    /// <summary>Rows to skip (at most <see cref="MaxOffset"/>); ignored with <see cref="After"/>. Repeats a row when one is inserted before the page.</summary>
    public int? Offset { get; init; }

    /// <summary>The page size, 1 … <see cref="MaxLimit"/> (default <see cref="DefaultLimit"/>).</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>Whether to count every row of the filters (<see cref="Page{T}.Total"/>; 62 ms at 1e6 rows: opt-in).</summary>
    public bool Count { get; init; }
}

/// <summary>A page of a list query.</summary>
/// <typeparam name="T">The row record.</typeparam>
/// <param name="Items">The rows, in the query's order.</param>
/// <param name="Next">The cursor of the next page; <see langword="null"/> on the last page.</param>
/// <param name="ETag">A weak ETag: a hash of the page's (id, version) pairs (equal while the page's rows are unchanged).</param>
/// <param name="Position">The projection's checkpoint when the page was read (the <c>Tarol-Position</c> of the answer).</param>
/// <param name="Total">The number of rows of the filters, when <see cref="ListQuery.Count"/> asked for it.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? Next, string ETag, GlobalPosition Position, long? Total = null);

/// <summary>
/// The opaque keyset cursor (base64url): the sort, a hash of the filters, the last row's sort value and id. A cursor of
/// another sort or other filters does not decode (HTTP 400, never a wrong page). Public so a hand-written query can use it.
/// </summary>
public static class Cursor
{
    private const ulong Seed = 14695981039346656037UL;

    /// <summary>Encodes a cursor.</summary>
    /// <param name="sort">The sort (column and direction, e.g. <c>ends_at</c> or <c>-highest_bid</c>).</param>
    /// <param name="filters">The hash of the filters (<c>Cursor.Hash</c>).</param>
    /// <param name="value">The last row's sort value (<see cref="ColumnValues.Format"/>; empty for an id sort).</param>
    /// <param name="id">The last row's id.</param>
    /// <returns>The cursor.</returns>
    public static string Encode(string sort, ulong filters, string value, Guid id)
    {
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(value);
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"1|{sort}|{filters:x16}|{id:N}|{value}"));
    }

    /// <summary>Decodes a cursor made for <paramref name="sort"/> and <paramref name="filters"/>.</summary>
    /// <param name="text">The cursor.</param>
    /// <param name="sort">The query's sort.</param>
    /// <param name="filters">The hash of the query's filters.</param>
    /// <param name="value">The sort value.</param>
    /// <param name="id">The id.</param>
    /// <returns>Whether the cursor is valid for this sort and these filters.</returns>
    public static bool TryDecode(string text, string sort, ulong filters, out string value, out Guid id)
    {
        ArgumentNullException.ThrowIfNull(sort);
        (value, id) = ("", Guid.Empty);
        if (string.IsNullOrEmpty(text) || text.Length > 2048)
            return false;
        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            return false;
        }
        string decoded;
        try
        {
            decoded = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        var parts = decoded.Split('|', 5);
        if (parts.Length != 5 || parts[0] != "1" || parts[1] != sort
            || parts[2] != filters.ToString("x16", CultureInfo.InvariantCulture)
            || !Guid.TryParseExact(parts[3], "N", out id))
            return false;
        value = parts[4];
        return true;
    }

    /// <summary>A stable hash (FNV-1a 64) of filters: column names and stored values, in column order.</summary>
    /// <param name="filters">(column, type, stored value) of each filter.</param>
    /// <returns>The hash.</returns>
    public static ulong Hash(IEnumerable<(string Column, ColumnType Type, object? Value)> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var h = Seed;
        foreach (var (column, type, value) in filters.OrderBy(static f => f.Column, StringComparer.Ordinal))
        {
            h = Fnv(Encoding.UTF8.GetBytes(column), h);
            h = Fnv(value is null ? "\u0001"u8 : Encoding.UTF8.GetBytes("=" + ColumnValues.Format(type, value)), h);
            h = Fnv("|"u8, h);
        }
        return h;
    }

    /// <summary>
    /// A stable hash of filters and set filters. Without sets it equals the hash of the filters alone,
    /// so the cursors of a query without sets stay valid; each set is hashed by its column and its values in stored order.
    /// </summary>
    /// <param name="filters">(column, type, stored value) of each filter.</param>
    /// <param name="sets">(column, type, distinct stored values in order) of each set filter.</param>
    /// <returns>The hash.</returns>
    public static ulong Hash(IEnumerable<(string Column, ColumnType Type, object? Value)> filters,
        IEnumerable<(string Column, ColumnType Type, IReadOnlyList<object> Values)> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var h = Hash(filters);
        var parts = new List<string>();
        foreach (var (column, type, values) in sets)
        {
            var part = new StringBuilder(column).Append(" in");
            foreach (var value in values)
            {
                var text = ColumnValues.Format(type, value);
                part.Append(CultureInfo.InvariantCulture, $" {text.Length}:").Append(text);
            }
            parts.Add(part.ToString());
        }
        parts.Sort(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            h = Fnv("&"u8, h);
            h = Fnv(Encoding.UTF8.GetBytes(part), h);
        }
        return h;
    }

    /// <summary>The weak ETag of a page: a hash of its (id, version) pairs, in order.</summary>
    /// <param name="rows">The page's ids and versions.</param>
    /// <returns><c>W/"&lt;16 hex digits&gt;"</c>.</returns>
    public static string ETag(IEnumerable<(Guid Id, long Version)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var h = Seed;
        Span<byte> b = stackalloc byte[24];
        foreach (var (id, version) in rows)
        {
            id.TryWriteBytes(b);
            BitConverter.TryWriteBytes(b[16..], version);
            h = Fnv(b, h);
        }
        return $"W/\"{h:x16}\"";
    }

    /// <summary>
    /// The weak ETag of a page: a hash of its (id, version) pairs, in order, its <c>next</c> cursor and its total. A row appended
    /// behind a full last page leaves the rows alone but turns <c>next</c> from <see langword="null"/> into a cursor, so the tag
    /// must cover it (BC-6); the total likewise.
    /// </summary>
    /// <param name="rows">The page's ids and versions.</param>
    /// <param name="next">The page's next cursor; <see langword="null"/> on the last page.</param>
    /// <param name="total">The count, if asked.</param>
    /// <returns><c>W/"&lt;16 hex digits&gt;"</c>.</returns>
    public static string ETag(IEnumerable<(Guid Id, long Version)> rows, string? next, long? total)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var h = Seed;
        Span<byte> b = stackalloc byte[24];
        foreach (var (id, version) in rows)
        {
            id.TryWriteBytes(b);
            BitConverter.TryWriteBytes(b[16..], version);
            h = Fnv(b, h);
        }
        h = Fnv(next is null ? "\u0001"u8 : Encoding.UTF8.GetBytes("n" + next), h);
        h = Fnv(total is { } t ? Encoding.UTF8.GetBytes("t" + t.ToString(CultureInfo.InvariantCulture)) : "\u0002"u8, h);
        return $"W/\"{h:x16}\"";
    }

    private static ulong Fnv(ReadOnlySpan<byte> data, ulong h)
    {
        foreach (var b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }
}

/// <summary>
/// A <see cref="ListQuery"/> checked against <typeparamref name="TRow"/>'s columns: the resolved filters, sort and cursor.
/// For read store implementations (the in-memory store and <c>Tarol.Queries.Npgsql</c> share it, so both answer alike).
/// </summary>
/// <typeparam name="TRow">The row record.</typeparam>
public sealed class ListQueryPlan<TRow>
    where TRow : class, IProjection<TRow>
{
    private ListQueryPlan(ListQuery query, IReadOnlyList<(ProjectionColumn<TRow> Column, object? Value)> filters, IReadOnlyList<SetFilterPlan<TRow>> sets,
        ProjectionColumn<TRow>? sort, string sortKey, ulong filterHash, int limit)
    {
        Query = query;
        Filters = filters;
        Sets = sets;
        Sort = sort;
        SortKey = sortKey;
        FilterHash = filterHash;
        Limit = limit;
    }

    /// <summary>The query.</summary>
    public ListQuery Query { get; }

    /// <summary>The filters with their columns and stored values.</summary>
    public IReadOnlyList<(ProjectionColumn<TRow> Column, object? Value)> Filters { get; }

    /// <summary>The set filters with their columns and distinct stored values (TN-6).</summary>
    public IReadOnlyList<SetFilterPlan<TRow>> Sets { get; }

    /// <summary>The sort column, or <see langword="null"/> for the id.</summary>
    public ProjectionColumn<TRow>? Sort { get; }

    /// <summary>The sort as the cursor names it (<c>ends_at</c>, <c>-highest_bid</c>, <c>id</c>).</summary>
    public string SortKey { get; }

    /// <summary>The hash of the filters the cursor is bound to.</summary>
    public ulong FilterHash { get; }

    /// <summary>The page size.</summary>
    public int Limit { get; }

    /// <summary>Whether the query continues after a cursor.</summary>
    public bool HasCursor { get; private set; }

    /// <summary>The cursor's sort value (stored type; <see langword="null"/> for an id sort or a null value).</summary>
    public object? CursorValue { get; private set; }

    /// <summary>The cursor's id.</summary>
    public Guid CursorId { get; private set; }

    /// <summary>The offset to skip (0 with a cursor).</summary>
    public int Offset { get; private set; }

    /// <summary>Checks a query.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The plan, or <see cref="Invalid"/> (<c>limit</c>, <c>offset</c>, <c>after</c>) for values a caller sent.</returns>
    /// <exception cref="ArgumentException">
    /// A filter, a set filter or the sort names no column, the sort column is not a <see cref="SortAttribute"/>, an equality
    /// filter names an array column, or a set holds a <see langword="null"/> or a value that does not fit its column.
    /// </exception>
    public static Result<ListQueryPlan<TRow>> Create(ListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > ListQuery.MaxLimit)
            return Invalid.Field("limit", "Query.Limit", $"The limit must be between 1 and {ListQuery.MaxLimit}.");
        if (query.Offset is < 0 or > ListQuery.MaxOffset)
            return Invalid.Field("offset", "Query.Offset", $"The offset must be between 0 and {ListQuery.MaxOffset}; page deeper with the cursor ('after').");
        var columns = TRow.Columns;
        var filters = new List<(ProjectionColumn<TRow>, object?)>(query.Filters.Count);
        foreach (var filter in query.Filters)
        {
            var column = Column(columns, filter.Column)
                ?? throw new ArgumentException($"The projection '{TRow.Name}' has no column '{filter.Column}'.", nameof(query));
            if (column.IsArray)
                throw new ArgumentException($"The column '{column.Name}' of '{TRow.Name}' is an array: filter it with a set (SetFilter), not for equality.", nameof(query));
            filters.Add((column, ColumnValues.Normalize(column.Type, filter.Value)));
        }
        var sets = new List<SetFilterPlan<TRow>>(query.Sets.Count);
        foreach (var set in query.Sets)
        {
            ArgumentNullException.ThrowIfNull(set);
            ProjectionColumn<TRow>? column = null;
            if (set.Column != SetFilter.IdColumn)
                column = Column(columns, set.Column)
                    ?? throw new ArgumentException($"The projection '{TRow.Name}' has no column '{set.Column}'.", nameof(query));
            var type = column?.Type ?? ColumnType.Uuid;
            var values = new List<object>(set.Values.Count);
            foreach (var value in set.Values)
                values.Add(ColumnValues.Normalize(type, value
                    ?? throw new ArgumentException($"The set filter on '{set.Column}' holds a null; filter nulls with a ColumnFilter.", nameof(query)))!);
            values.Sort((a, b) => ColumnValues.Compare(type, a, b));
            var distinct = new List<object>(values.Count);
            foreach (var value in values)
                if (distinct.Count == 0 || ColumnValues.Compare(type, distinct[^1], value) != 0)
                    distinct.Add(value);
            sets.Add(new SetFilterPlan<TRow>(column, type, distinct));
        }
        ProjectionColumn<TRow>? sort = null;
        if (query.Sort is { } sortName && sortName != "id")
        {
            sort = Column(columns, sortName) ?? throw new ArgumentException($"The projection '{TRow.Name}' has no column '{sortName}'.", nameof(query));
            if (!sort.Sortable)
                throw new ArgumentException($"The column '{sortName}' of '{TRow.Name}' is not a [Sort] member.", nameof(query));
        }
        var sortKey = (query.Descending ? "-" : "") + (sort?.Name ?? "id");
        var hash = sets.Count == 0
            ? Cursor.Hash(filters.Select(static f => (f.Item1.Name, f.Item1.Type, f.Item2)))
            : Cursor.Hash(filters.Select(static f => (f.Item1.Name, f.Item1.Type, f.Item2)), sets.Select(static s => (s.Name, s.Type, s.Values)));
        var plan = new ListQueryPlan<TRow>(query, filters, sets, sort, sortKey, hash, query.Limit);
        if (query.After is { } after)
        {
            if (!Cursor.TryDecode(after, sortKey, hash, out var text, out var id))
                return Invalid.Field("after", "Query.Cursor", "The cursor is not valid for this sort and these filters; start again without 'after'.");
            object? value = null;
            if (sort is not null && text.Length > 0 && !ColumnValues.TryParse(sort.Type, text, out value))
                return Invalid.Field("after", "Query.Cursor", "The cursor is not valid for this sort and these filters; start again without 'after'.");
            plan.HasCursor = true;
            plan.CursorValue = value;
            plan.CursorId = id;
        }
        else
        {
            plan.Offset = query.Offset ?? 0;
        }
        return plan;
    }

    /// <summary>Builds the page from the rows fetched in order (at most <see cref="Limit"/> + 1: one more tells there is a next page).</summary>
    /// <param name="fetched">The rows.</param>
    /// <param name="position">The projection's checkpoint.</param>
    /// <param name="total">The count, if asked.</param>
    /// <returns>The page.</returns>
    public Page<TRow> ToPage(IReadOnlyList<Versioned<TRow>> fetched, GlobalPosition position, long? total)
    {
        ArgumentNullException.ThrowIfNull(fetched);
        var more = fetched.Count > Limit;
        var count = more ? Limit : fetched.Count;
        var items = new TRow[count];
        var pairs = new (Guid, long)[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = fetched[i].Row;
            pairs[i] = (fetched[i].Key.Id, fetched[i].Version);
        }
        string? next = null;
        if (more && count > 0)
        {
            var last = fetched[count - 1];
            next = Cursor.Encode(SortKey, FilterHash, Sort is null ? "" : ColumnValues.Format(Sort.Type, Sort.Value(last.Row)), last.Key.Id);
        }
        return new Page<TRow>(items, next, Cursor.ETag(pairs, next, total), position, total);
    }

    private static ProjectionColumn<TRow>? Column(IReadOnlyList<ProjectionColumn<TRow>> columns, string name)
    {
        foreach (var column in columns)
            if (column.Name == name)
                return column;
        foreach (var column in columns)
            if (column.Member == name)
                return column;
        return null;
    }
}

/// <summary>A checked set filter of a <see cref="ListQueryPlan{TRow}"/>.</summary>
/// <typeparam name="TRow">The row record.</typeparam>
public sealed class SetFilterPlan<TRow>
    where TRow : class, IProjection<TRow>
{
    private HashSet<object>? _lookup;

    internal SetFilterPlan(ProjectionColumn<TRow>? column, ColumnType type, IReadOnlyList<object> values) => (Column, Type, Values) = (column, type, values);

    /// <summary>The column, or <see langword="null"/> for the row id.</summary>
    public ProjectionColumn<TRow>? Column { get; }

    /// <summary>The column name as SQL names it (<c>id</c> for the row id).</summary>
    public string Name => Column?.Name ?? SetFilter.IdColumn;

    /// <summary>The stored type of the values.</summary>
    public ColumnType Type { get; }

    /// <summary>The distinct stored values, in <see cref="ColumnValues.Compare"/> order; empty: no row matches.</summary>
    public IReadOnlyList<object> Values { get; }

    /// <summary>Whether a row passes the filter.</summary>
    /// <param name="row">The row with its key.</param>
    /// <returns>Whether it matches.</returns>
    public bool Matches(Versioned<TRow> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var lookup = _lookup ??= [.. Values];
        return Column is null ? lookup.Contains(row.Key.Id) : Column.Matches(row.Row, lookup);
    }
}
