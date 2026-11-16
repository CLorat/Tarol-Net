using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Npgsql;
using NpgsqlTypes;
using Tarol.Persistence;
using Tarol.Results;

namespace Tarol.Queries.Npgsql;

/// <summary>
/// The read table of one projection version on PostgreSQL (QR-2, QR-3): <c>&lt;schema&gt;.rm_&lt;name&gt;_v&lt;n&gt;</c> with
/// typed columns written by the host next to the row as <c>jsonb</c>, and its checkpoint row in <c>&lt;schema&gt;.projections</c>.
/// A write moves the checkpoint first, guarded by the position the batch read from (the row lock serializes two runners:
/// the second finds 0 rows, rolls back and re-reads), then upserts the rows whose version is above the stored one; one
/// transaction.
/// </summary>
/// <typeparam name="TRow">The row record; its type must be in the application's <c>JsonSerializerContext</c>.</typeparam>
public sealed class NpgsqlReadModelStore<TRow> : IReadModelStore<TRow>
    where TRow : class, IProjection<TRow>
{
    private readonly NpgsqlDataSource _db;
    private readonly JsonTypeInfo<TRow> _json;
    private readonly IReadOnlyList<ProjectionColumn<TRow>> _columns;
    private readonly string _schema;
    private readonly string _upsert;
    private readonly string _advance;
    private readonly string _checkpoint;
    private readonly string _load;
    private readonly string? _channel;

    /// <summary>Creates the store.</summary>
    /// <param name="dataSource">The data source (arrays enabled).</param>
    /// <param name="json">The application's JSON options: the row is stored and returned as JSON through them.</param>
    /// <param name="schema">The schema (default <c>tarol</c>, the store's).</param>
    /// <param name="pushCheckpoints">
    /// Whether <see cref="Write"/> sends the checkpoint <c>NOTIFY &lt;schema&gt;_checkpoint</c> in the batch transaction
    /// (<see cref="NpgsqlReadModelOptions.PushCheckpoints"/>; read by <see cref="NpgsqlProjectionPositions"/>).
    /// </param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TRow"/> is not in the JSON options' resolver.</exception>
    public NpgsqlReadModelStore(NpgsqlDataSource dataSource, JsonSerializerOptions json, string schema = NpgsqlReadModelSchema.DefaultSchema,
        bool pushCheckpoints = false)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(json);
        NpgsqlReadModelSchema.Validate(schema);
        _db = dataSource;
        _json = TypeInfo(json);
        _columns = TRow.Columns;
        _schema = schema;
        _channel = pushCheckpoints ? CheckpointSql.Channel(schema) : null;
        Table = $"{schema}.{Projections.TableOf<TRow>()}";

        var names = new StringBuilder("tenant, id, version");
        var arrays = new StringBuilder("$1::uuid[], $2::uuid[], $3::bigint[]");
        var updates = new StringBuilder("version = excluded.version");
        var p = 3;
        foreach (var column in _columns)
        {
            names.Append(", ").Append(column.Name);
            arrays.Append(CultureInfo.InvariantCulture, $", ${++p}::{NpgsqlReadModelSchema.ArrayType(column.Type)}");
            updates.Append(CultureInfo.InvariantCulture, $", {column.Name} = excluded.{column.Name}");
        }
        names.Append(", doc");
        arrays.Append(CultureInfo.InvariantCulture, $", ${++p}::jsonb[]");
        updates.Append(", doc = excluded.doc");
        _upsert = $"""
            INSERT INTO {Table} AS t ({names})
            SELECT * FROM unnest({arrays})
            ON CONFLICT (tenant, id) DO UPDATE SET {updates}
            WHERE t.version < excluded.version
            """;
        _advance = $"""
            UPDATE {schema}.projections SET tx = $3::text::xid8, pos = $4, state = $5, updated_at = now()
            WHERE name = $1 AND version = $2 AND tx = $6::text::xid8 AND pos = $7
            """;
        _checkpoint = $"SELECT tx::text, pos, state FROM {schema}.projections WHERE name = $1 AND version = $2";
        _load = $"SELECT tenant, id, version, doc::text FROM {Table} WHERE (tenant, id) IN (SELECT * FROM unnest($1::uuid[], $2::uuid[]))";
    }

    /// <summary>The error code of <see cref="Search"/> before any host prepared the table (<see cref="ErrorType.Unavailable"/>: HTTP 503).</summary>
    public const string NotPreparedCode = "Projection.NotPrepared";

    /// <summary>The qualified table name (<c>tarol.rm_auction_list_v1</c>).</summary>
    public string Table { get; }

    /// <summary>Whether <see cref="Write"/> sends the checkpoint <c>NOTIFY</c>.</summary>
    public bool PushesCheckpoints => _channel is not null;

    /// <inheritdoc />
    public async ValueTask Prepare(CancellationToken cancellationToken)
    {
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                // parallel CREATE … IF NOT EXISTS race on the catalog: one preparer at a time per schema
                await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext($1))", connection, transaction))
                {
                    lockCommand.Parameters.Add(new NpgsqlParameter { Value = "tarol.read-models:" + _schema });
                    await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using (var create = new NpgsqlCommand(NpgsqlReadModelSchema.Script<TRow>(_schema), connection, transaction))
                    await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_checkpoint);
        command.Parameters.Add(new NpgsqlParameter { Value = TRow.Name });
        command.Parameters.Add(new NpgsqlParameter { Value = TRow.Version });
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? CheckpointSql.Read(reader, 0) : default;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return default;                                         // no worker has prepared the projection yet: building, at the start
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<RowKey, Versioned<TRow>>> Load(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var found = new Dictionary<RowKey, Versioned<TRow>>(keys.Count);
        if (keys.Count == 0)
            return found;
        await using var command = _db.CreateCommand(_load);
        command.Parameters.Add(new NpgsqlParameter { Value = keys.Select(static k => k.Tenant).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = keys.Select(static k => k.Id).ToArray() });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new RowKey(reader.GetGuid(0), reader.GetGuid(1));
            found[key] = new Versioned<TRow>(key, reader.GetInt64(2), Deserialize(reader.GetString(3)));
        }
        return found;
    }

    /// <inheritdoc />
    public async ValueTask<bool> Write(ProjectionBatch<TRow> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                // first: the guarded checkpoint move; its row lock makes a concurrent runner wait, then find 0 rows
                await using (var advance = new NpgsqlCommand(_advance, connection, transaction))
                {
                    CheckpointSql.Add(advance.Parameters, TRow.Name, TRow.Version, batch.From, batch.To, batch.State);
                    if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return false;
                    }
                }
                // then the notice: PostgreSQL delivers it after the commit, so a listener never sees a checkpoint before its rows
                if (_channel is not null)
                    await CheckpointSql.Notify(connection, transaction, _channel, TRow.Name, TRow.Version, batch.To, batch.State, cancellationToken).ConfigureAwait(false);
                if (batch.Rows.Count > 0)
                {
                    await using var upsert = new NpgsqlCommand(_upsert, connection, transaction);
                    AddRows(upsert.Parameters, batch.Rows);
                    await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<Versioned<TRow>?> Find(RowKey key, CancellationToken cancellationToken)
    {
        var rows = await Load([key], cancellationToken).ConfigureAwait(false);
        return rows.TryGetValue(key, out var row) ? row : null;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Page<TRow>>> Search(ListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var planned = ListQueryPlan<TRow>.Create(query);
        if (!planned.TryGetValue(out ListQueryPlan<TRow>? plan))
            return planned.TryGetValue(out Violation? invalid) ? invalid : Error.Internal("Query.Plan");

        // one round trip: the checkpoint first (so the page's position never claims more than the rows show), the rows, the count
        var batch = new NpgsqlBatch();
        await using (batch.ConfigureAwait(false))
        {
            var checkpoint = new NpgsqlBatchCommand(_checkpoint);
            checkpoint.Parameters.Add(new NpgsqlParameter { Value = TRow.Name });
            checkpoint.Parameters.Add(new NpgsqlParameter { Value = TRow.Version });
            batch.BatchCommands.Add(checkpoint);

            var rows = new NpgsqlBatchCommand();
            var where = Where(plan, rows.Parameters);
            var sql = new StringBuilder($"SELECT id, version, doc::text FROM {Table} WHERE {where}");
            var direction = query.Descending ? " DESC" : "";
            if (plan.HasCursor)
            {
                rows.Parameters.Add(new NpgsqlParameter { Value = plan.CursorId });
                var idParameter = rows.Parameters.Count;
                var op = query.Descending ? "<" : ">";
                if (plan.Sort is { } sorted)
                {
                    rows.Parameters.Add(new NpgsqlParameter { Value = plan.CursorValue ?? DBNull.Value });
                    sql.Append(CultureInfo.InvariantCulture, $" AND ({sorted.Name}, id) {op} (${rows.Parameters.Count}, ${idParameter})");
                }
                else
                {
                    sql.Append(CultureInfo.InvariantCulture, $" AND id {op} ${idParameter}");
                }
            }
            sql.Append(" ORDER BY ");
            if (plan.Sort is { } sort)
                sql.Append(sort.Name).Append(direction).Append(", ");
            sql.Append("id").Append(direction);
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {plan.Limit + 1}");
            if (plan.Offset > 0)
                sql.Append(CultureInfo.InvariantCulture, $" OFFSET {plan.Offset}");
            rows.CommandText = sql.ToString();
            batch.BatchCommands.Add(rows);

            if (query.Count)
            {
                var count = new NpgsqlBatchCommand();
                count.CommandText = $"SELECT count(*) FROM {Table} WHERE {Where(plan, count.Parameters)}";
                batch.BatchCommands.Add(count);
            }

            var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                batch.Connection = connection;
                NpgsqlDataReader reader;
                try
                {
                    reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
                {
                    return Error.Unavailable(NotPreparedCode, $"The read table {Table} does not exist yet: no projection host has prepared it.");
                }
                await using var disposeReader = reader.ConfigureAwait(false);
                var position = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? CheckpointSql.Read(reader, 0).Position : GlobalPosition.Start;
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                var fetched = new List<Versioned<TRow>>(plan.Limit + 1);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var key = new RowKey(query.Tenant, reader.GetGuid(0));
                    fetched.Add(new Versioned<TRow>(key, reader.GetInt64(1), Deserialize(reader.GetString(2))));
                }
                long? total = null;
                if (query.Count && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)
                                && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    total = reader.GetInt64(0);
                return plan.ToPage(fetched, position, total);
            }
        }
    }

    private static string Where(ListQueryPlan<TRow> plan, NpgsqlParameterCollection parameters)
    {
        parameters.Add(new NpgsqlParameter { Value = plan.Query.Tenant });
        var where = new StringBuilder("tenant = $1");
        foreach (var (column, value) in plan.Filters)
        {
            if (value is null)
            {
                where.Append(CultureInfo.InvariantCulture, $" AND {column.Name} IS NULL");
                continue;
            }
            parameters.Add(new NpgsqlParameter { Value = value });
            where.Append(CultureInfo.InvariantCulture, $" AND {column.Name} = ${parameters.Count}");
        }
        return where.ToString();
    }

    private void AddRows(NpgsqlParameterCollection parameters, IReadOnlyList<Versioned<TRow>> rows)
    {
        var values = new TRow[rows.Count];
        var tenants = new Guid[rows.Count];
        var ids = new Guid[rows.Count];
        var versions = new long[rows.Count];
        var docs = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            values[i] = rows[i].Row;
            tenants[i] = rows[i].Key.Tenant;
            ids[i] = rows[i].Key.Id;
            versions[i] = rows[i].Version;
            docs[i] = JsonSerializer.Serialize(rows[i].Row, _json);
        }
        parameters.Add(new NpgsqlParameter { Value = tenants });
        parameters.Add(new NpgsqlParameter { Value = ids });
        parameters.Add(new NpgsqlParameter { Value = versions });
        foreach (var column in _columns)
            parameters.Add(new NpgsqlParameter { Value = column.Values(values), NpgsqlDbType = NpgsqlDbType.Array | DbType(column.Type) });
        parameters.Add(new NpgsqlParameter { Value = docs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
    }

    private static NpgsqlDbType DbType(ColumnType type) => type switch
    {
        ColumnType.Uuid => NpgsqlDbType.Uuid,
        ColumnType.Text => NpgsqlDbType.Text,
        ColumnType.Boolean => NpgsqlDbType.Boolean,
        ColumnType.Int2 => NpgsqlDbType.Smallint,
        ColumnType.Int4 => NpgsqlDbType.Integer,
        ColumnType.BigInt => NpgsqlDbType.Bigint,
        ColumnType.Numeric => NpgsqlDbType.Numeric,
        ColumnType.Float8 => NpgsqlDbType.Double,
        ColumnType.TimestampTz => NpgsqlDbType.TimestampTz,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private TRow Deserialize(string doc)
        => JsonSerializer.Deserialize(doc, _json) ?? throw new InvalidOperationException($"A row of {Table} holds JSON null.");

    private static JsonTypeInfo<TRow> TypeInfo(JsonSerializerOptions json)
    {
        try
        {
            return json.GetTypeInfo<TRow>();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The row {typeof(TRow).Name} of projection '{TRow.Name}' is not in the application's JsonSerializerContext; add [JsonSerializable(typeof({typeof(TRow).Name}))].", e);
        }
    }
}

/// <summary>The checkpoint row's parameters and columns, shared by row and SQL projections.</summary>
internal static class CheckpointSql
{
    internal static void Add(NpgsqlParameterCollection parameters, string name, int version, GlobalPosition from, GlobalPosition to, ProjectionState state)
    {
        parameters.Add(new NpgsqlParameter { Value = name });
        parameters.Add(new NpgsqlParameter { Value = version });
        parameters.Add(new NpgsqlParameter { Value = to.Transaction.ToString(CultureInfo.InvariantCulture) });
        parameters.Add(new NpgsqlParameter { Value = to.Position });
        parameters.Add(new NpgsqlParameter { Value = state == ProjectionState.Current ? "current" : "building" });
        parameters.Add(new NpgsqlParameter { Value = from.Transaction.ToString(CultureInfo.InvariantCulture) });
        parameters.Add(new NpgsqlParameter { Value = from.Position });
    }

    internal static string Channel(string schema)
    {
        var channel = schema + NpgsqlProjectionPositions.ChannelSuffix;
        NpgsqlReadModelSchema.Validate(channel);
        return channel;
    }

    internal static async Task Notify(NpgsqlConnection connection, NpgsqlTransaction transaction, string channel, string name, int version,
        GlobalPosition to, ProjectionState state, CancellationToken cancellationToken)
    {
        await using var notify = new NpgsqlCommand("SELECT pg_notify($1, $2)", connection, transaction);
        notify.Parameters.Add(new NpgsqlParameter { Value = channel });
        notify.Parameters.Add(new NpgsqlParameter { Value = CheckpointNotice.Format(name, version, to, state) });
        await notify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static ProjectionCheckpoint Read(NpgsqlDataReader reader, int first)
        => new(new GlobalPosition(ulong.Parse(reader.GetString(first), CultureInfo.InvariantCulture), reader.GetInt64(first + 1)),
            reader.GetString(first + 2) == "current" ? ProjectionState.Current : ProjectionState.Building);
}
