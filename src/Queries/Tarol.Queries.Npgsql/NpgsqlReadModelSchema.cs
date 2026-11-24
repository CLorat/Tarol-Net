using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tarol.Queries.Npgsql;

/// <summary>
/// The SQL of the read tables, derived from a projection's generated column map (the generator emits the map, this class
/// the PostgreSQL text, once per projection version). Hand <see cref="Script{TRow}"/> to a migration tool, or let
/// <see cref="NpgsqlReadModelStore{TRow}.Prepare"/> run it.
/// </summary>
public static partial class NpgsqlReadModelSchema
{
    /// <summary>The default schema (the store's: <c>tarol</c>).</summary>
    public const string DefaultSchema = "tarol";

    /// <summary>The checkpoint table: one row per projection version.</summary>
    /// <param name="schema">The schema.</param>
    /// <returns>The SQL (idempotent).</returns>
    public static string ProjectionsTable(string schema = DefaultSchema)
    {
        Validate(schema);
        return $"""
            CREATE SCHEMA IF NOT EXISTS {schema};
            CREATE TABLE IF NOT EXISTS {schema}.projections (
                name        text        NOT NULL,
                version     integer     NOT NULL,
                tx          xid8        NOT NULL DEFAULT '0',          -- the checkpoint: (tx, pos) of the last event handled
                pos         bigint      NOT NULL DEFAULT 0,
                state       text        NOT NULL DEFAULT 'building',   -- 'building' until the head of the feed, then 'current'
                updated_at  timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (name, version));
            """;
    }

    /// <summary>
    /// The read table of <typeparamref name="TRow"/>'s version: (tenant, id, version, typed columns, doc), the key
    /// (tenant, id), an index (tenant, column, id) per sort; plus its checkpoint row.
    /// </summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <param name="schema">The schema.</param>
    /// <returns>The SQL (idempotent).</returns>
    public static string Script<TRow>(string schema = DefaultSchema)
        where TRow : class, IProjection<TRow>
        => TableScript<TRow>(schema) + IndexScript<TRow>(schema);

    /// <summary>The checkpoint table, the checkpoint row and the read table (without its indexes).</summary>
    internal static string TableScript<TRow>(string schema)
        where TRow : class, IProjection<TRow>
    {
        Validate(schema);
        var table = $"{schema}.{Projections.TableOf<TRow>()}";
        var sb = new StringBuilder(ProjectionsTable(schema));
        sb.Append(CultureInfo.InvariantCulture, $"INSERT INTO {schema}.projections (name, version) VALUES ('{TRow.Name}', {TRow.Version}) ON CONFLICT DO NOTHING;\n");
        sb.Append(CultureInfo.InvariantCulture, $"CREATE TABLE IF NOT EXISTS {table} (\n");
        sb.Append("    tenant      uuid        NOT NULL,\n    id          uuid        NOT NULL,\n    version     bigint      NOT NULL,\n");
        foreach (var column in TRow.Columns)
            sb.Append(CultureInfo.InvariantCulture, $"    {column.Name} {SqlType(column)}{(column.Nullable ? "" : " NOT NULL")},\n");
        sb.Append("    doc         jsonb       NOT NULL,\n    PRIMARY KEY (tenant, id));\n");
        return sb.ToString();
    }

    /// <summary>The read table's indexes: (tenant, column, id) per sort, a GIN index per array column (set filters).</summary>
    internal static string IndexScript<TRow>(string schema)
        where TRow : class, IProjection<TRow>
    {
        var table = $"{schema}.{Projections.TableOf<TRow>()}";
        var sb = new StringBuilder();
        var i = 0;
        foreach (var column in TRow.Columns)
        {
            i++;
            if (column.Sortable)
                sb.Append(CultureInfo.InvariantCulture, $"CREATE INDEX IF NOT EXISTS {Projections.TableOf<TRow>()}_s{i} ON {table} (tenant, {column.Name}, id);\n");
            if (column.IsArray)
                sb.Append(CultureInfo.InvariantCulture, $"CREATE INDEX IF NOT EXISTS {Projections.TableOf<TRow>()}_a{i} ON {table} USING gin ({column.Name});\n");
        }
        return sb.ToString();
    }

    /// <summary>The PostgreSQL type of a column (text with <c>COLLATE "C"</c>: ordinal, as the in-memory store orders it).</summary>
    /// <param name="type">The column type.</param>
    /// <returns>The SQL type.</returns>
    public static string SqlType(ColumnType type) => type switch
    {
        ColumnType.Uuid => "uuid",
        ColumnType.Text => "text COLLATE \"C\"",
        ColumnType.Boolean => "boolean",
        ColumnType.Int2 => "smallint",
        ColumnType.Int4 => "integer",
        ColumnType.BigInt => "bigint",
        ColumnType.Numeric => "numeric",
        ColumnType.Float8 => "double precision",
        ColumnType.TimestampTz => "timestamptz",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>The PostgreSQL type of a column: <see cref="SqlType(ColumnType)"/>, or its array for an array column (<c>uuid[]</c>, <c>text[] COLLATE "C"</c>).</summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The SQL type.</returns>
    public static string SqlType<TRow>(ProjectionColumn<TRow> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!column.IsArray)
            return SqlType(column.Type);
        return column.Type == ColumnType.Text ? "text[] COLLATE \"C\"" : SqlType(column.Type) + "[]";
    }

    /// <summary>The array type of a cast (<c>uuid[]</c>, <c>text[]</c>): no collation, which a cast does not take.</summary>
    internal static string CastArrayType(ColumnType type) => type == ColumnType.Text ? "text[]" : SqlType(type) + "[]";

    internal static string ArrayType(ColumnType type) => type switch
    {
        ColumnType.Text => "text[]",
        _ => SqlType(type) + "[]",
    };

    internal static void Validate(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!Identifier().IsMatch(schema))
            throw new ArgumentException($"'{schema}' is not a plain PostgreSQL identifier (lowercase letters, digits and _, at most 63 characters).", nameof(schema));
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Identifier();
}
