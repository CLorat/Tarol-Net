using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Queries.Npgsql;

namespace Tarol.Audit.Npgsql;

/// <summary>
/// The change log in PostgreSQL (TN-64, AU-1 option B): per projection version <c>audit_rows_v{n}</c> (one row per event and field) and
/// <c>audit_values_v{n}</c> (the last value per tenant, target and field), written with the checkpoint (<c>projections</c>, name
/// <c>tarol-audit</c>) in one transaction; reads and denials in <c>audit_access</c>, outside the versions. Values are text (JSON),
/// personal ones sealed. Indexes serve the list newest first by tenant, target and actor.
/// </summary>
public sealed partial class NpgsqlAuditStore : IAuditStore
{
    private const string Columns = "id, tenant, at, kind, actor_kind, actor_id, on_behalf_of, stream_type, target, stream, event, field, before, after, captured_at, device, view, code";
    private readonly NpgsqlDataSource _db;
    private readonly HashSet<int> _versions = [];
    private readonly Lock _gate = new();

    /// <summary>Creates the store.</summary>
    /// <param name="dataSource">The database (the event store's).</param>
    /// <param name="schema">The schema (default <c>tarol</c>): a lowercase identifier.</param>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public NpgsqlAuditStore(NpgsqlDataSource dataSource, string schema = NpgsqlReadModelSchema.DefaultSchema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(schema);
        if (!Identifier().IsMatch(schema))
            throw new ArgumentException($"The schema '{schema}' must be a lowercase identifier (letters, digits, underscores).", nameof(schema));
        _db = dataSource;
        Schema = schema;
    }

    /// <summary>The schema.</summary>
    public string Schema { get; }

    /// <summary>The tables of <paramref name="version"/> and the access table (idempotent), for a migration tool.</summary>
    /// <param name="version">The projection version.</param>
    /// <returns>The SQL.</returns>
    public string Script(int version)
    {
        var v = version.ToString(CultureInfo.InvariantCulture);
        var s = Schema;
        return NpgsqlReadModelSchema.ProjectionsTable(s) + $"""
            INSERT INTO {s}.projections (name, version) VALUES ('{AuditHost.ProjectionName}', {v}) ON CONFLICT DO NOTHING;
            CREATE TABLE IF NOT EXISTS {s}.audit_rows_v{v} ({Table()});
            CREATE INDEX IF NOT EXISTS audit_rows_v{v}_tenant_at ON {s}.audit_rows_v{v} (tenant, at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS audit_rows_v{v}_target ON {s}.audit_rows_v{v} (tenant, target, at DESC);
            CREATE INDEX IF NOT EXISTS audit_rows_v{v}_actor ON {s}.audit_rows_v{v} (tenant, actor_id, at DESC);
            CREATE INDEX IF NOT EXISTS audit_rows_v{v}_purge ON {s}.audit_rows_v{v} (at);
            CREATE TABLE IF NOT EXISTS {s}.audit_values_v{v} (
                tenant uuid NOT NULL,
                target uuid NOT NULL,
                field  text NOT NULL,
                value  text NULL,                          -- JSON; a personal value sealed
                PRIMARY KEY (tenant, target, field));
            CREATE TABLE IF NOT EXISTS {s}.audit_access ({Table()});
            CREATE INDEX IF NOT EXISTS audit_access_tenant_at ON {s}.audit_access (tenant, at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS audit_access_purge ON {s}.audit_access (at);
            """;
    }

    private static string Table() => """
        id           uuid        PRIMARY KEY,           -- derived from the event and the field: a rebuild writes the same rows
        tenant       uuid        NOT NULL,
        at           timestamptz NOT NULL,
        kind         text        NOT NULL,              -- Created, Changed, Removed, Read, Denied
        actor_kind   text        NOT NULL,
        actor_id     text        NOT NULL,
        on_behalf_of text        NULL,
        stream_type  text        NOT NULL,
        target       uuid        NOT NULL,
        stream       uuid        NOT NULL,
        event        text        NULL,
        field        text        NULL,
        before       text        NULL,                  -- JSON; a personal value sealed (null once its subject is erased)
        after        text        NULL,
        captured_at  timestamptz NULL,
        device       text        NULL,
        view         text        NULL,
        code         text        NULL
        """;

    /// <inheritdoc />
    public async ValueTask Prepare(int version, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(Script(version));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
            _versions.Add(version);
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint> Checkpoint(int version, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand($"SELECT tx::text, pos, state FROM {Schema}.projections WHERE name = '{AuditHost.ProjectionName}' AND version = $1");
        command.Parameters.Add(new() { Value = version });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProjectionCheckpoint(new GlobalPosition(ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt64(1)),
                reader.GetString(2) == "current" ? ProjectionState.Current : ProjectionState.Building)
            : default;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<AuditValueKey, string?>> Values(int version, IReadOnlyCollection<(Guid Tenant, Guid Target)> objects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var found = new Dictionary<AuditValueKey, string?>();
        if (objects.Count == 0)
            return found;
        await using var command = _db.CreateCommand($"""
            SELECT v.tenant, v.target, v.field, v.value FROM {Schema}.audit_values_v{version.ToString(CultureInfo.InvariantCulture)} v
            JOIN unnest($1::uuid[], $2::uuid[]) AS o(tenant, target) ON v.tenant = o.tenant AND v.target = o.target
            """);
        command.Parameters.Add(new() { Value = objects.Select(static o => o.Tenant).ToArray() });
        command.Parameters.Add(new() { Value = objects.Select(static o => o.Target).ToArray() });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            found[new AuditValueKey(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2))] = reader.IsDBNull(3) ? null : reader.GetString(3);
        return found;
    }

    /// <inheritdoc />
    public async ValueTask<bool> Write(int version, AuditBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var v = version.ToString(CultureInfo.InvariantCulture);
        await using var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var move = new NpgsqlCommand($"""
            UPDATE {Schema}.projections SET tx = $4::text::xid8, pos = $5, state = $6, updated_at = now()
            WHERE name = '{AuditHost.ProjectionName}' AND version = $1 AND tx = $2::text::xid8 AND pos = $3
            """, connection, transaction))
        {
            move.Parameters.Add(new() { Value = version });
            move.Parameters.Add(new() { Value = batch.From.Transaction.ToString(CultureInfo.InvariantCulture) });
            move.Parameters.Add(new() { Value = batch.From.Position });
            move.Parameters.Add(new() { Value = batch.Through.Transaction.ToString(CultureInfo.InvariantCulture) });
            move.Parameters.Add(new() { Value = batch.Through.Position });
            move.Parameters.Add(new() { Value = batch.State == ProjectionState.Current ? "current" : "building" });
            if (await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                return false;                                                                   // another runner moved it: the batch is refused
        }
        if (batch.Removed.Count > 0)
        {
            await using var remove = new NpgsqlCommand($"""
                DELETE FROM {Schema}.audit_values_v{v} v USING unnest($1::uuid[], $2::uuid[]) AS o(tenant, target)
                WHERE v.tenant = o.tenant AND v.target = o.target
                """, connection, transaction);
            remove.Parameters.Add(new() { Value = batch.Removed.Select(static o => o.Tenant).ToArray() });
            remove.Parameters.Add(new() { Value = batch.Removed.Select(static o => o.Target).ToArray() });
            await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (batch.Values.Count > 0)
        {
            await using var upsert = new NpgsqlCommand($"""
                INSERT INTO {Schema}.audit_values_v{v} (tenant, target, field, value)
                SELECT * FROM unnest($1::uuid[], $2::uuid[], $3::text[], $4::text[])
                ON CONFLICT (tenant, target, field) DO UPDATE SET value = EXCLUDED.value
                """, connection, transaction);
            var values = batch.Values.ToList();
            upsert.Parameters.Add(new() { Value = values.Select(static x => x.Key.Tenant).ToArray() });
            upsert.Parameters.Add(new() { Value = values.Select(static x => x.Key.Target).ToArray() });
            upsert.Parameters.Add(new() { Value = values.Select(static x => x.Key.Field).ToArray() });
            upsert.Parameters.Add(new() { Value = values.Select(static x => x.Value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (batch.Entries.Count > 0)
            await Insert(connection, transaction, $"audit_rows_v{v}", batch.Entries, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask Record(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await Insert(connection, null, "audit_access", [entry], cancellationToken).ConfigureAwait(false);
    }

    private async Task Insert(NpgsqlConnection connection, NpgsqlTransaction? transaction, string table, IReadOnlyList<AuditEntry> rows, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {Schema}.{table} ({Columns})
            SELECT * FROM unnest($1::uuid[], $2::uuid[], $3::timestamptz[], $4::text[], $5::text[], $6::text[], $7::text[], $8::text[], $9::uuid[],
                                 $10::uuid[], $11::text[], $12::text[], $13::text[], $14::text[], $15::timestamptz[], $16::text[], $17::text[], $18::text[])
            ON CONFLICT (id) DO NOTHING
            """, connection, transaction);
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Id).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Tenant).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.At.ToUniversalTime()).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Kind.ToString()).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Actor.Kind.ToString()).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Actor.Id).ToArray() });
        Text(insert, rows.Select(static r => r.Actor.OnBehalfOf.TryGetValue(out string? s) ? s : null));
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.StreamType).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Target).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Stream).ToArray() });
        Text(insert, rows.Select(static r => r.Event));
        Text(insert, rows.Select(static r => r.Field));
        Text(insert, rows.Select(static r => r.Before));
        Text(insert, rows.Select(static r => r.After));
        insert.Parameters.Add(new()
        {
            Value = rows.Select(static r => r.CapturedAt is { } at ? (object)at.ToUniversalTime() : DBNull.Value).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
        });
        Text(insert, rows.Select(static r => r.Device));
        Text(insert, rows.Select(static r => r.View));
        Text(insert, rows.Select(static r => r.Code));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        static void Text(NpgsqlCommand command, IEnumerable<string?> values)
            => command.Parameters.Add(new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AuditEntry>> Query(int version, Guid tenant, AuditQuery query, (DateTimeOffset At, Guid Id)? after, int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var command = _db.CreateCommand();
        command.Parameters.Add(new() { Value = tenant });
        var where = new StringBuilder("tenant = $1");
        void Add(string condition, object value)
        {
            command.Parameters.Add(new() { Value = value });
            where.Append(CultureInfo.InvariantCulture, $" AND {condition.Replace("@", "$" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)}");
        }
        if (query.Target is { } target)
            Add("target = @", target);
        if (query.Actor is { } actor)
            Add("actor_id = @", actor);
        if (query.StreamType is { } streamType)
            Add("stream_type = @", streamType);
        if (query.Field is { } field)
            Add("field = @", field);
        if (query.Kind is { } kind)
            Add("kind = @", kind.ToString());
        if (query.From is { } from)
            Add("at >= @", from.ToUniversalTime());
        if (query.To is { } to)
            Add("at <= @", to.ToUniversalTime());
        if (after is { } a)
        {
            command.Parameters.Add(new() { Value = a.At.ToUniversalTime() });
            command.Parameters.Add(new() { Value = a.Id });
            var n = command.Parameters.Count;
            where.Append(CultureInfo.InvariantCulture, $" AND (at, id) < (${n - 1}, ${n})");
        }
        command.Parameters.Add(new() { Value = take });
        var limit = "$" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        var rows = $"audit_rows_v{version.ToString(CultureInfo.InvariantCulture)}";
        command.CommandText = $"""
            SELECT {Columns} FROM (
                (SELECT {Columns} FROM {Schema}.{rows} WHERE {where} ORDER BY at DESC, id DESC LIMIT {limit})
                UNION ALL
                (SELECT {Columns} FROM {Schema}.audit_access WHERE {where} ORDER BY at DESC, id DESC LIMIT {limit})) r
            ORDER BY at DESC, id DESC LIMIT {limit}
            """;
        var found = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            var actorKind = Enum.Parse<ActorKind>(reader.GetString(4));
            found.Add(new AuditEntry(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2), Enum.Parse<AuditKind>(reader.GetString(3)),
                new Actor(actorKind, reader.GetString(5), Text(6) is { } onBehalfOf ? onBehalfOf : Option.None), reader.GetString(7), reader.GetGuid(8), Text(11), Text(12), Text(13))
            {
                Stream = reader.GetGuid(9),
                Event = Text(10),
                CapturedAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
                Device = Text(15),
                View = Text(16),
                Code = Text(17),
            });
        }
        return found;
    }

    /// <inheritdoc />
    public async ValueTask<int> ForgetTenant(Guid tenant, CancellationToken cancellationToken)
    {
        var gone = 0;
        foreach (var table in Tables(values: true))
        {
            await using var command = _db.CreateCommand($"DELETE FROM {Schema}.{table} WHERE tenant = $1");
            command.Parameters.Add(new() { Value = tenant });
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (!table.StartsWith("audit_values", StringComparison.Ordinal))
                gone += deleted;
        }
        return gone;
    }

    /// <inheritdoc />
    public async ValueTask<int> Purge(DateTimeOffset olderThan, int batch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        var gone = 0;
        foreach (var table in Tables(values: false))
        {
            if (gone >= batch)
                break;
            await using var command = _db.CreateCommand($"DELETE FROM {Schema}.{table} WHERE id IN (SELECT id FROM {Schema}.{table} WHERE at < $1 ORDER BY at LIMIT $2)");
            command.Parameters.Add(new() { Value = olderThan.ToUniversalTime() });
            command.Parameters.Add(new() { Value = batch - gone });
            gone += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return gone;
    }

    // the access table and the tables of the versions this process prepared
    private List<string> Tables(bool values)
    {
        int[] versions;
        lock (_gate)
            versions = [.. _versions];
        var tables = new List<string> { "audit_access" };
        foreach (var v in versions)
        {
            tables.Add($"audit_rows_v{v.ToString(CultureInfo.InvariantCulture)}");
            if (values)
                tables.Add($"audit_values_v{v.ToString(CultureInfo.InvariantCulture)}");
        }
        return tables;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
