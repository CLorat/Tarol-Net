using System.Globalization;

namespace Tarol.Queries;

/// <summary>The storage type of a typed column (PostgreSQL name in brackets).</summary>
public enum ColumnType
{
    /// <summary><see cref="Guid"/> (<c>uuid</c>).</summary>
    Uuid,

    /// <summary><see cref="string"/> (<c>text COLLATE "C"</c>: ordinal, as in memory).</summary>
    Text,

    /// <summary><see cref="bool"/> (<c>boolean</c>).</summary>
    Boolean,

    /// <summary><see cref="short"/> (<c>smallint</c>).</summary>
    Int2,

    /// <summary><see cref="int"/> and enums (<c>integer</c>; an enum is stored as its number).</summary>
    Int4,

    /// <summary><see cref="long"/> and enums over <see cref="long"/> or <see cref="uint"/> (<c>bigint</c>).</summary>
    BigInt,

    /// <summary><see cref="decimal"/> (<c>numeric</c>).</summary>
    Numeric,

    /// <summary><see cref="double"/> (<c>double precision</c>).</summary>
    Float8,

    /// <summary><see cref="DateTimeOffset"/> (<c>timestamptz</c>): stored in UTC, to the microsecond.</summary>
    TimestampTz,

    /// <summary><see cref="DateOnly"/> (<c>date</c>): a calendar day, no time and no zone (TN-7).</summary>
    Date,

    /// <summary><see cref="TimeOnly"/> (<c>time</c>): a time of day, no zone, to the microsecond (TN-7).</summary>
    Time,
}

/// <summary>
/// A typed column of a projection's read table: a scalar member of the row, written by the host next to the row's
/// <c>jsonb</c> (PostgreSQL cannot generate a <c>timestamptz</c> column from <c>jsonb</c>, queries.md §5 item 2). The
/// generator creates one <see cref="ProjectionColumn{TRow, TValue}"/> per column.
/// </summary>
/// <typeparam name="TRow">The row record.</typeparam>
public abstract class ProjectionColumn<TRow>
{
    private protected ProjectionColumn(string name, string member, ColumnType type, bool nullable, bool sortable, bool isArray = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        Name = name;
        Member = member;
        Type = type;
        Nullable = nullable;
        Sortable = sortable;
        IsArray = isArray;
    }

    /// <summary>The column name: the member's name in snake case (<c>EndsAt</c> → <c>ends_at</c>).</summary>
    public string Name { get; }

    /// <summary>The row member it is read from.</summary>
    public string Member { get; }

    /// <summary>The storage type.</summary>
    public ColumnType Type { get; }

    /// <summary>Whether the column holds <c>null</c>s.</summary>
    public bool Nullable { get; }

    /// <summary>Whether the member carries <see cref="SortAttribute"/> (an index per sort).</summary>
    public bool Sortable { get; }

    /// <summary>
    /// Whether the column holds an array of <see cref="Type"/> (a <c>Guid[]</c> member: <c>uuid[]</c>). An array column takes
    /// set filters only (<see cref="SetFilter"/>: the array shares a value with the set); it cannot be an equality filter or a sort.
    /// </summary>
    public bool IsArray { get; }

    /// <summary>
    /// Whether <paramref name="row"/> passes a set filter: its value is one of <paramref name="values"/>, or, for an
    /// <see cref="IsArray"/> column, one of its elements is (<c>column = ANY(values)</c>, <c>column &amp;&amp; values</c>).
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="values">The stored values (<see cref="ColumnValues.Normalize"/>d).</param>
    /// <returns>Whether it matches; a <see langword="null"/> value never does.</returns>
    public bool Matches(TRow row, IReadOnlySet<object> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var value = Value(row);
        if (!IsArray)
            return value is not null && values.Contains(value);
        if (value is System.Collections.IEnumerable items)
            foreach (var item in items)
                if (item is not null && values.Contains(item))
                    return true;
        return false;
    }

    /// <summary>The column's value of <paramref name="row"/>, as stored (an enum as its number, a time in UTC to the microsecond).</summary>
    /// <param name="row">The row.</param>
    /// <returns>The value, boxed.</returns>
    public abstract object? Value(TRow row);

    /// <summary>The column's values of <paramref name="rows"/> as a typed array (<c>Guid[]</c>, <c>int?[]</c>, …), for a batch write.</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The array.</returns>
    public abstract Array Values(IReadOnlyList<TRow> rows);

    /// <summary>Compares the column's value of <paramref name="row"/> with a stored value (<see cref="ColumnValues.Normalize"/>d).</summary>
    /// <param name="row">The row.</param>
    /// <param name="value">The value.</param>
    /// <returns>Less than, equal to or greater than zero, as PostgreSQL orders them (nulls last).</returns>
    public abstract int CompareTo(TRow row, object? value);
}

/// <summary>A typed column whose stored value is a <typeparamref name="TValue"/>.</summary>
/// <typeparam name="TRow">The row record.</typeparam>
/// <typeparam name="TValue">The stored CLR type (<c>int</c> for an enum, <c>int?</c> for a nullable one).</typeparam>
public sealed class ProjectionColumn<TRow, TValue> : ProjectionColumn<TRow>
{
    private readonly Func<TRow, TValue> _get;

    /// <summary>Creates a column (the generated column map calls it).</summary>
    /// <param name="name">The column name.</param>
    /// <param name="member">The row member.</param>
    /// <param name="type">The storage type.</param>
    /// <param name="nullable">Whether the column holds nulls.</param>
    /// <param name="sortable">Whether the member is a sort.</param>
    /// <param name="get">Reads the stored value from a row.</param>
    public ProjectionColumn(string name, string member, ColumnType type, bool nullable, bool sortable, Func<TRow, TValue> get)
        : base(name, member, type, nullable, sortable)
    {
        ArgumentNullException.ThrowIfNull(get);
        _get = get;
    }

    /// <inheritdoc />
    public override object? Value(TRow row) => _get(row);

    /// <inheritdoc />
    public override Array Values(IReadOnlyList<TRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var values = new TValue[rows.Count];
        for (var i = 0; i < values.Length; i++)
            values[i] = _get(rows[i]);
        return values;
    }

    /// <inheritdoc />
    public override int CompareTo(TRow row, object? value) => ColumnValues.Compare(Type, _get(row), value);
}

/// <summary>
/// An array column (<see cref="ProjectionColumn{TRow}.IsArray"/>): a <c>Guid[]</c>, <c>string[]</c>, <c>int[]</c>, … member
/// (or an <c>IReadOnlyList&lt;T&gt;</c>), stored as a PostgreSQL array of <see cref="ProjectionColumn{TRow}.Type"/>. It takes set
/// filters only (the array shares a value with the set, <c>&amp;&amp;</c>), never an equality filter or a sort.
/// </summary>
/// <typeparam name="TRow">The row record.</typeparam>
/// <typeparam name="TElement">The stored element type (<c>int</c> for an enum).</typeparam>
public sealed class ProjectionArrayColumn<TRow, TElement> : ProjectionColumn<TRow>
{
    private readonly Func<TRow, TElement[]?> _get;

    /// <summary>Creates an array column (the generated column map calls it).</summary>
    /// <param name="name">The column name.</param>
    /// <param name="member">The row member.</param>
    /// <param name="type">The element's storage type.</param>
    /// <param name="nullable">Whether the column holds nulls (a <see langword="null"/> array; never matches a set).</param>
    /// <param name="get">Reads the stored elements from a row.</param>
    public ProjectionArrayColumn(string name, string member, ColumnType type, bool nullable, Func<TRow, TElement[]?> get)
        : base(name, member, type, nullable, sortable: false, isArray: true)
    {
        ArgumentNullException.ThrowIfNull(get);
        _get = get;
    }

    /// <inheritdoc />
    public override object? Value(TRow row) => _get(row);

    /// <inheritdoc />
    /// <remarks>An array of arrays (<c>Guid[]?[]</c>).</remarks>
    public override Array Values(IReadOnlyList<TRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var values = new TElement[]?[rows.Count];
        for (var i = 0; i < values.Length; i++)
            values[i] = _get(rows[i]);
        return values;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: an array column is neither an equality filter nor a sort.</exception>
    public override int CompareTo(TRow row, object? value)
        => throw new NotSupportedException($"The array column '{Name}' takes set filters only.");
}

/// <summary>Conversions of column values: normalizing a filter value, the cursor's text form, the PostgreSQL order.</summary>
public static class ColumnValues
{
    /// <summary>A time as a <see cref="ColumnType.TimestampTz"/> column stores it: UTC, truncated to the microsecond.</summary>
    /// <param name="value">The time.</param>
    /// <returns>The stored time.</returns>
    public static DateTimeOffset Timestamp(DateTimeOffset value)
    {
        var utc = value.UtcTicks;
        return new DateTimeOffset(utc - (utc % 10), TimeSpan.Zero);
    }

    /// <summary>A time of day as a <see cref="ColumnType.Time"/> column stores it: truncated to the microsecond.</summary>
    /// <param name="value">The time of day.</param>
    /// <returns>The stored time of day.</returns>
    public static TimeOnly Time(TimeOnly value) => new(value.Ticks - (value.Ticks % 10));

    /// <summary>A nullable time of day as stored.</summary>
    /// <param name="value">The time of day, or <see langword="null"/>.</param>
    /// <returns>The stored time of day.</returns>
    public static TimeOnly? Time(TimeOnly? value) => value is { } v ? Time(v) : null;

    /// <summary>A nullable time as stored.</summary>
    /// <param name="value">The time, or <see langword="null"/>.</param>
    /// <returns>The stored time.</returns>
    public static DateTimeOffset? Timestamp(DateTimeOffset? value) => value is { } v ? Timestamp(v) : null;

    /// <summary>
    /// A filter value converted to the column's stored type: an enum to its number, a time (or time of day) to microseconds, an
    /// <c>int</c> to a <c>long</c> for a <see cref="ColumnType.BigInt"/> column. <see langword="null"/> stays <see langword="null"/>.
    /// </summary>
    /// <param name="type">The column's type.</param>
    /// <param name="value">The value.</param>
    /// <returns>The stored value.</returns>
    /// <exception cref="ArgumentException">The value does not fit the column.</exception>
    public static object? Normalize(ColumnType type, object? value)
    {
        if (value is null)
            return null;
        try
        {
            return type switch
            {
                ColumnType.Uuid => (Guid)value,
                ColumnType.Text => (string)value,
                ColumnType.Boolean => (bool)value,
                ColumnType.Int2 => value is Enum e ? Convert.ToInt16(e, CultureInfo.InvariantCulture) : Convert.ToInt16(value, CultureInfo.InvariantCulture),
                ColumnType.Int4 => value is Enum e ? Convert.ToInt32(e, CultureInfo.InvariantCulture) : Convert.ToInt32(value, CultureInfo.InvariantCulture),
                ColumnType.BigInt => value is Enum e ? Convert.ToInt64(e, CultureInfo.InvariantCulture) : Convert.ToInt64(value, CultureInfo.InvariantCulture),
                ColumnType.Numeric => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                ColumnType.Float8 => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                ColumnType.TimestampTz => Timestamp((DateTimeOffset)value),
                ColumnType.Date => (DateOnly)value,
                ColumnType.Time => Time((TimeOnly)value),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
            };
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            throw new ArgumentException($"The value {value} ({value.GetType().Name}) does not fit a {type} column.", nameof(value), e);
        }
    }

    /// <summary>
    /// The invariant text of a stored value (the cursor carries it): a time as UTC ticks, a uuid without hyphens, a day as its
    /// day number, a time of day as its ticks.
    /// </summary>
    /// <param name="type">The column's type.</param>
    /// <param name="value">The stored value.</param>
    /// <returns>The text; empty for <see langword="null"/>.</returns>
    public static string Format(ColumnType type, object? value) => value switch
    {
        null => "",
        Guid g => g.ToString("N"),
        string s => s,
        bool b => b ? "1" : "0",
        DateTimeOffset t => t.UtcTicks.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.DayNumber.ToString(CultureInfo.InvariantCulture),
        TimeOnly t => t.Ticks.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"A {type} column cannot hold {value.GetType().Name}.", nameof(value)),
    };

    /// <summary>Parses <see cref="Format"/>'s text back to the stored value.</summary>
    /// <param name="type">The column's type.</param>
    /// <param name="text">The text.</param>
    /// <param name="value">The stored value.</param>
    /// <returns>Whether the text was valid.</returns>
    public static bool TryParse(ColumnType type, string text, out object? value)
    {
        ArgumentNullException.ThrowIfNull(text);
        var invariant = CultureInfo.InvariantCulture;
        value = null;
        switch (type)
        {
            case ColumnType.Uuid when Guid.TryParseExact(text, "N", out var g):
                value = g;
                return true;
            case ColumnType.Text:
                value = text;
                return true;
            case ColumnType.Boolean when text is "0" or "1":
                value = text == "1";
                return true;
            case ColumnType.Int2 when short.TryParse(text, NumberStyles.Integer, invariant, out var s):
                value = s;
                return true;
            case ColumnType.Int4 when int.TryParse(text, NumberStyles.Integer, invariant, out var i):
                value = i;
                return true;
            case ColumnType.BigInt when long.TryParse(text, NumberStyles.Integer, invariant, out var l):
                value = l;
                return true;
            case ColumnType.Numeric when decimal.TryParse(text, NumberStyles.Number, invariant, out var d):
                value = d;
                return true;
            case ColumnType.Float8 when double.TryParse(text, NumberStyles.Float, invariant, out var f):
                value = f;
                return true;
            case ColumnType.TimestampTz when long.TryParse(text, NumberStyles.Integer, invariant, out var t)
                                             && t >= DateTimeOffset.MinValue.UtcTicks && t <= DateTimeOffset.MaxValue.UtcTicks:
                value = new DateTimeOffset(t, TimeSpan.Zero);
                return true;
            case ColumnType.Date when int.TryParse(text, NumberStyles.Integer, invariant, out var day)
                                      && day >= DateOnly.MinValue.DayNumber && day <= DateOnly.MaxValue.DayNumber:
                value = DateOnly.FromDayNumber(day);
                return true;
            case ColumnType.Time when long.TryParse(text, NumberStyles.Integer, invariant, out var ticks)
                                      && ticks >= TimeOnly.MinValue.Ticks && ticks <= TimeOnly.MaxValue.Ticks:
                value = new TimeOnly(ticks);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Compares two stored values as PostgreSQL orders them: uuids by their bytes, text ordinally, nulls last.</summary>
    /// <param name="type">The column's type.</param>
    /// <param name="left">A stored value.</param>
    /// <param name="right">A stored value.</param>
    /// <returns>Less than, equal to or greater than zero.</returns>
    public static int Compare(ColumnType type, object? left, object? right)
    {
        if (left is null || right is null)
            return left is null ? (right is null ? 0 : 1) : -1;
        return type switch
        {
            ColumnType.Uuid => CompareIds((Guid)left, (Guid)right),
            ColumnType.Text => string.CompareOrdinal((string)left, (string)right),
            ColumnType.Boolean => ((bool)left).CompareTo((bool)right),
            ColumnType.Int2 => ((short)left).CompareTo((short)right),
            ColumnType.Int4 => ((int)left).CompareTo((int)right),
            ColumnType.BigInt => ((long)left).CompareTo((long)right),
            ColumnType.Numeric => ((decimal)left).CompareTo((decimal)right),
            ColumnType.Float8 => ((double)left).CompareTo((double)right),
            ColumnType.TimestampTz => ((DateTimeOffset)left).UtcTicks.CompareTo(((DateTimeOffset)right).UtcTicks),
            ColumnType.Date => ((DateOnly)left).CompareTo((DateOnly)right),
            ColumnType.Time => ((TimeOnly)left).CompareTo((TimeOnly)right),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
    }

    /// <summary>Compares two ids as PostgreSQL orders <c>uuid</c>s: by their 16 bytes in RFC 9562 order.</summary>
    /// <param name="left">An id.</param>
    /// <param name="right">An id.</param>
    /// <returns>Less than, equal to or greater than zero.</returns>
    public static int CompareIds(Guid left, Guid right)
    {
        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        left.TryWriteBytes(a, bigEndian: true, out _);
        right.TryWriteBytes(b, bigEndian: true, out _);
        return a.SequenceCompareTo(b);
    }
}
