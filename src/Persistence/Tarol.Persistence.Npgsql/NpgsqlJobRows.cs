using Npgsql;
using NpgsqlTypes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql;

/// <summary>
/// The job rows of an <see cref="NpgsqlAggregateStore"/> (<c>jobs</c>, <c>jobs_dead</c>; docs/architecture/jobs.md §5, J-4, J-7).
/// <see cref="Claim"/> takes <b>one</b> row with <c>FOR UPDATE SKIP LOCKED</c> (a pending re-drive first, then the oldest due
/// cursor) and sets a claim id and a lease; every later call is one statement guarded by that claim id, so a worker that lost
/// its lease changes nothing. <see cref="Sync"/> runs in one transaction under an advisory lock, so two starting hosts do not
/// interleave.
/// </summary>
/// <remarks>
/// A claim costs about 1 ms at any table size (the partial index <c>jobs_due</c>). Times are whole seconds, written in UTC:
/// <c>timestamptz</c> keeps microseconds, so a whole second reads back unchanged (jobs.md §5 item 1).
/// </remarks>
public sealed class NpgsqlJobRows : IJobRows, IDeadLetterSource, IDeadLetterRetention
{
    private readonly NpgsqlDataSource _db;
    private readonly TimeProvider _time;
    private readonly Statements _sql;

    internal NpgsqlJobRows(NpgsqlDataSource dataSource, TimeProvider time, string schema)
    {
        _db = dataSource;
        _time = time;
        _sql = new Statements(schema);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSyncChange>> Sync(IReadOnlyCollection<JobSchedule> jobs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var declared = new Dictionary<(Guid, string), JobSchedule>();
        foreach (var job in jobs)
        {
            ArgumentNullException.ThrowIfNull(job, nameof(jobs));
            ArgumentException.ThrowIfNullOrWhiteSpace(job.Name, nameof(jobs));
            ArgumentNullException.ThrowIfNull(job.Schedule, nameof(jobs));
            ThrowIfNotWholeSecond(job.First, nameof(jobs));
            if (!declared.TryAdd((TenantOf(job.Tenant), job.Name), job))
                throw new ArgumentException($"The job row ('{job.Name}', {job.Tenant}) is listed twice.", nameof(jobs));
        }

        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await using (var gate = new NpgsqlCommand(Statements.SyncLock, connection, transaction))
                {
                    gate.Parameters.Add(new NpgsqlParameter { Value = _sql.Schema + ".jobs" });
                    _ = await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                var stored = new Dictionary<(Guid, string), (string Schedule, bool Enabled)>();
                await using (var read = new NpgsqlCommand(_sql.SyncRead, connection, transaction))
                {
                    var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            stored[(reader.GetGuid(0), reader.GetString(1))] = (reader.GetString(2), reader.GetBoolean(3));
                }

                var changes = new List<JobSyncChange>();
                var inserts = new List<JobSchedule>();
                var restarts = new List<JobSchedule>();
                var flips = new List<((Guid Tenant, string Name) Key, bool Enabled)>();
                foreach (var (key, job) in declared)
                {
                    if (!stored.TryGetValue(key, out var row))
                    {
                        inserts.Add(job);
                        changes.Add(new(job.Name, job.Tenant, job.Enabled ? JobSyncKind.Added : JobSyncKind.Disabled));
                        continue;
                    }
                    if (!string.Equals(row.Schedule, job.Schedule, StringComparison.Ordinal))
                    {
                        restarts.Add(job);
                        if (job.Enabled)
                            changes.Add(new(job.Name, job.Tenant, JobSyncKind.Rescheduled));
                    }
                    else if (job.Enabled && !row.Enabled)
                    {
                        restarts.Add(job);
                        changes.Add(new(job.Name, job.Tenant, JobSyncKind.Enabled));
                    }
                    else if (!job.Enabled && row.Enabled)
                        flips.Add((key, false));
                    if (row.Enabled && !job.Enabled)
                        changes.Add(new(job.Name, job.Tenant, JobSyncKind.Disabled));
                }
                foreach (var (key, row) in stored)
                {
                    if (!row.Enabled || declared.ContainsKey(key))
                        continue;
                    flips.Add((key, false));
                    changes.Add(new(key.Item2, Tenant(key.Item1), JobSyncKind.Disabled));
                }

                if (inserts.Count > 0)
                    await Rows(_sql.SyncInsert, inserts, connection, transaction, cancellationToken).ConfigureAwait(false);
                if (restarts.Count > 0)
                    await Rows(_sql.SyncRestart, restarts, connection, transaction, cancellationToken).ConfigureAwait(false);
                if (flips.Count > 0)
                {
                    await using var disable = new NpgsqlCommand(_sql.SyncDisable, connection, transaction);
                    disable.Parameters.Add(new NpgsqlParameter { Value = flips.Select(f => f.Key.Tenant).ToArray() });
                    disable.Parameters.Add(new NpgsqlParameter { Value = flips.Select(f => f.Key.Name).ToArray() });
                    _ = await disable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return changes;
            }
        }

        static async Task Rows(string sql, List<JobSchedule> rows, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => TenantOf(r.Tenant)).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Name).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Schedule).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Enabled).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.First.ToUniversalTime()).ToArray() });
            _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<Option<JobRow>> Claim(TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        var now = _time.GetUtcNow().ToUniversalTime();
        await using var command = _db.CreateCommand(_sql.Claim);
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7(now) });
        command.Parameters.Add(new NpgsqlParameter { Value = now + lease });
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return Option.None;
            var nextAt = reader.GetFieldValue<DateTimeOffset>(3);
            Option<long> redrive = reader.IsDBNull(7) ? Option.None : reader.GetInt64(7);
            return new JobRow(Tenant(reader.GetGuid(0)), reader.GetString(1), reader.GetString(2), nextAt, reader.GetGuid(4), reader.GetInt32(5),
                reader.IsDBNull(6) ? Option.None : reader.GetString(6), redrive, reader.IsDBNull(8) ? nextAt : reader.GetFieldValue<DateTimeOffset>(8));
        }
    }

    /// <inheritdoc />
    public Task<bool> Move(JobRow row, DateTimeOffset cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ThrowIfNotWholeSecond(cursor, nameof(cursor));
        return Owned(_sql.Move, row, cancellationToken, cursor.ToUniversalTime());
    }

    /// <inheritdoc />
    public Task<bool> Advance(JobRow row, DateTimeOffset cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ThrowIfNotWholeSecond(cursor, nameof(cursor));
        return Owned(_sql.Advance, row, cancellationToken, cursor.ToUniversalTime(), Redrive(row));
    }

    /// <inheritdoc />
    public Task<bool> Extend(JobRow row, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        return Owned(_sql.Extend, row, cancellationToken, _time.GetUtcNow().ToUniversalTime() + lease);
    }

    /// <inheritdoc />
    public Task<bool> Retry(JobRow row, DateTimeOffset retryAt, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(reason);
        return Owned(_sql.Retry, row, cancellationToken, retryAt.ToUniversalTime(), reason);
    }

    /// <inheritdoc />
    public Task<bool> Bury(JobRow row, DateTimeOffset cursor, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(reason);
        ThrowIfNotWholeSecond(cursor, nameof(cursor));
        return Owned(_sql.Bury, row, cancellationToken, cursor.ToUniversalTime(), Redrive(row), row.Occurrence.ToUniversalTime(), row.Attempts,
            reason, _time.GetUtcNow().ToUniversalTime());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadJob>> DeadJobs(int max, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        await using var command = _db.CreateCommand(_sql.DeadJobs);
        command.Parameters.Add(new NpgsqlParameter { Value = max });
        return await ReadDead(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadJob>> DeadJobs(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var command = _db.CreateCommand(_sql.DeadPage);
        command.Parameters.Add(new NpgsqlParameter { Value = query.After });
        command.Parameters.Add(new NpgsqlParameter { Value = query.Limit });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = query.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)query.Name ?? DBNull.Value });
        return await ReadDead(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DeadJob>> ReadDead(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var list = new List<DeadJob>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(new DeadJob(reader.GetInt64(0), Tenant(reader.GetGuid(1)), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetInt32(4), reader.IsDBNull(5) ? Option.None : reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetBoolean(7)));
            return list;
        }
    }

    /// <inheritdoc />
    public Task<bool> Redrive(long id, CancellationToken cancellationToken) => Act(_sql.Redrive, id, null, cancellationToken);

    /// <inheritdoc />
    public Task<bool> Discard(long id, CancellationToken cancellationToken) => Act(_sql.Discard, id, null, cancellationToken);


    /// <inheritdoc />
    /// <remarks>Deletes the oldest first, on the <c>dead_at</c> index of schema version 2, with one audit row per call.</remarks>
    public async Task<int> Purge(DateTimeOffset olderThan, int max, string actor, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await using var command = _db.CreateCommand(_sql.Purge);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = olderThan.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter { Value = max });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = _time.GetUtcNow().UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter { Value = actor });
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long? ?? 0);
    }

    /// <inheritdoc />
    string IDeadLetterRetention.Kind => DeadLetterKinds.Jobs;

    /// <summary>The kind of these dead letters: <see cref="DeadLetterKinds.Jobs"/>.</summary>
    string IDeadLetterSource.Kind => DeadLetterKinds.Jobs;

    /// <inheritdoc />
    async Task<DeadLetterPage> IDeadLetterSource.List(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        var rows = await DeadJobs(query, cancellationToken).ConfigureAwait(false);
        return new DeadLetterPage([.. rows.Select(static d => d.ToItem())], rows.Count == query.Limit ? rows[^1].Id : null);
    }

    /// <inheritdoc />
    Task<bool> IDeadLetterSource.Redrive(long id, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return Act(_sql.RedriveAudited, id, actor, cancellationToken);
    }

    /// <inheritdoc />
    Task<bool> IDeadLetterSource.Discard(long id, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return Act(_sql.DiscardAudited, id, actor, cancellationToken);
    }

    private async Task<bool> Act(string sql, long id, string? actor, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        if (actor is not null)
        {
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = _time.GetUtcNow().UtcDateTime });
            command.Parameters.Add(new NpgsqlParameter { Value = actor });
        }
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is 1L;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobState>> Rows(CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_sql.Rows);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var list = new List<JobState>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(new JobState(Tenant(reader.GetGuid(0)), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                    reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? Option.None : reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetInt32(6), reader.IsDBNull(7) ? Option.None : reader.GetString(7)));
            return list;
        }
    }

    /// <inheritdoc />
    public async Task<Option<DateTimeOffset>> Overdue(CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_sql.Overdue);
        command.Parameters.Add(new NpgsqlParameter { Value = _time.GetUtcNow().ToUniversalTime() });
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(0)
                ? reader.GetFieldValue<DateTimeOffset>(0)
                : Option.None;
    }

    private async Task<bool> Owned(string sql, JobRow row, CancellationToken cancellationToken, params object[] values)
    {
        await using var command = _db.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = TenantOf(row.Tenant) });
        command.Parameters.Add(new NpgsqlParameter { Value = row.Name });
        command.Parameters.Add(new NpgsqlParameter { Value = row.ClaimId });
        foreach (var value in values)
            command.Parameters.Add(value as NpgsqlParameter ?? new NpgsqlParameter { Value = value });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long and 1;
    }

    private static NpgsqlParameter Redrive(JobRow row)
        => new() { NpgsqlDbType = NpgsqlDbType.Bigint, Value = row.Redrive.TryGetValue(out long id) ? id : DBNull.Value };

    private static void ThrowIfNotWholeSecond(DateTimeOffset value, string name)
    {
        if (value.UtcTicks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentException($"A job time is a whole second; {value:O} is not.", name);
    }

    private static Guid TenantOf(Option<Id<ITenant>> tenant) => tenant.TryGetValue(out Id<ITenant> id) ? id.Value : Guid.Empty;

    private static Option<Id<ITenant>> Tenant(Guid tenant) => tenant == Guid.Empty ? Option.None : (Id<ITenant>)tenant;

    /// <summary>The statements, built once per schema. Every claim-guarded statement returns the count of rows it owned.</summary>
    private sealed class Statements(string schema)
    {
        public string Schema => schema;

        public static string SyncLock => "SELECT pg_advisory_xact_lock(hashtext($1))";

        public string SyncRead => $"SELECT tenant, name, schedule, enabled FROM {schema}.jobs";

        public string SyncInsert => $"""
            INSERT INTO {schema}.jobs (tenant, name, schedule, enabled, next_at)
            SELECT * FROM unnest($1::uuid[], $2::text[], $3::text[], $4::boolean[], $5::timestamptz[])
            ON CONFLICT (tenant, name) DO NOTHING
            """;

        public string SyncRestart => $"""
            UPDATE {schema}.jobs j SET schedule = u.schedule, enabled = u.enabled, next_at = u.first,
                   claim_id = NULL, lease_until = NULL, attempts = 0, last_error = NULL
            FROM unnest($1::uuid[], $2::text[], $3::text[], $4::boolean[], $5::timestamptz[]) AS u(tenant, name, schedule, enabled, first)
            WHERE j.tenant = u.tenant AND j.name = u.name
            """;

        public string SyncDisable => $"""
            UPDATE {schema}.jobs j SET enabled = false
            FROM unnest($1::uuid[], $2::text[]) AS u(tenant, name)
            WHERE j.tenant = u.tenant AND j.name = u.name
            """;

        // A pending re-drive of a free row first (the partial index is empty almost always), else the oldest due cursor.
        public string Claim => $"""
            WITH r AS (
                SELECT j.tenant, j.name, d.id AS redrive, d.occurrence
                FROM {schema}.jobs_dead d JOIN {schema}.jobs j ON j.tenant = d.tenant AND j.name = d.name
                WHERE d.redrive AND j.enabled AND (j.lease_until IS NULL OR j.lease_until <= $1)
                ORDER BY d.id LIMIT 1
                FOR UPDATE OF j SKIP LOCKED),
            n AS (
                SELECT j.tenant, j.name, NULL::bigint AS redrive, NULL::timestamptz AS occurrence
                FROM {schema}.jobs j
                WHERE NOT EXISTS (SELECT 1 FROM r) AND j.enabled AND j.next_at <= $1 AND (j.lease_until IS NULL OR j.lease_until <= $1)
                ORDER BY j.next_at LIMIT 1
                FOR UPDATE SKIP LOCKED),
            c AS (SELECT * FROM r UNION ALL SELECT * FROM n LIMIT 1)
            UPDATE {schema}.jobs j SET claim_id = $2, lease_until = $3, attempts = j.attempts + 1
            FROM c WHERE j.tenant = c.tenant AND j.name = c.name
            RETURNING j.tenant, j.name, j.schedule, j.next_at, j.claim_id, j.attempts, j.last_error, c.redrive, c.occurrence
            """;

        private string Guarded(string set) => $"""
            WITH u AS (UPDATE {schema}.jobs SET {set} WHERE tenant = $1 AND name = $2 AND claim_id = $3 RETURNING 1)
            SELECT count(*) FROM u
            """;

        public string Move => Guarded("next_at = $4, attempts = 1, last_error = NULL");

        public string Extend => Guarded("lease_until = $4");

        public string Retry => Guarded("claim_id = NULL, lease_until = $4, last_error = $5");

        public string Advance => $"""
            WITH u AS (UPDATE {schema}.jobs SET next_at = $4, claim_id = NULL, lease_until = NULL, attempts = 0, last_error = NULL
                       WHERE tenant = $1 AND name = $2 AND claim_id = $3 RETURNING 1),
                 d AS (DELETE FROM {schema}.jobs_dead WHERE id = $5 AND EXISTS (SELECT 1 FROM u))
            SELECT count(*) FROM u
            """;

        // $5 the re-drive (or NULL), $6 the occurrence, $7 attempts, $8 reason, $9 now
        public string Bury => $"""
            WITH u AS (UPDATE {schema}.jobs SET next_at = $4, claim_id = NULL, lease_until = NULL, attempts = 0, last_error = NULL
                       WHERE tenant = $1 AND name = $2 AND claim_id = $3 RETURNING 1),
                 i AS (INSERT INTO {schema}.jobs_dead (tenant, name, occurrence, attempts, last_error, dead_at)
                       SELECT $1, $2, $6, $7, $8, $9 WHERE $5::bigint IS NULL AND EXISTS (SELECT 1 FROM u)),
                 r AS (UPDATE {schema}.jobs_dead SET attempts = $7, last_error = $8, dead_at = $9, redrive = false
                       WHERE id = $5 AND EXISTS (SELECT 1 FROM u))
            SELECT count(*) FROM u
            """;

        public string DeadJobs => $"""
            SELECT id, tenant, name, occurrence, attempts, last_error, dead_at, redrive FROM {schema}.jobs_dead ORDER BY id LIMIT $1
            """;

        private string Audited(string changed, string action) => $"""
            WITH d AS ({changed} RETURNING id, tenant, name, occurrence),
            a AS (INSERT INTO {schema}.operations_audit (at, actor, action, target, detail)
                  SELECT $2, $3, '{action}', 'jobs:' || d.id, jsonb_build_object('job', d.name, 'tenant', d.tenant, 'occurrence', d.occurrence) FROM d)
            SELECT count(*) FROM d
            """;

        private string RedriveRow => $"UPDATE {schema}.jobs_dead SET redrive = true WHERE id = $1 AND NOT redrive";

        private string DiscardRow => $"DELETE FROM {schema}.jobs_dead WHERE id = $1";

        public string Redrive => $"WITH d AS ({RedriveRow} RETURNING id) SELECT count(*) FROM d";

        public string Discard => $"WITH d AS ({DiscardRow} RETURNING id) SELECT count(*) FROM d";

        // the audit row in the same statement (O-3)
        public string RedriveAudited => Audited(RedriveRow, "redrive");

        public string DiscardAudited => Audited(DiscardRow, "discard");

        public string Purge => $"""
            WITH d AS (DELETE FROM {schema}.jobs_dead WHERE id IN (SELECT id FROM {schema}.jobs_dead WHERE dead_at < $1 ORDER BY dead_at LIMIT $2) RETURNING id),
            a AS (INSERT INTO {schema}.operations_audit (at, actor, action, target, detail)
                  SELECT $3, $4, 'purge', 'jobs', jsonb_build_object('rows', count(*), 'before', $1) FROM d HAVING count(*) > 0)
            SELECT count(*) FROM d
            """;

        public string DeadPage => $"""
            SELECT id, tenant, name, occurrence, attempts, last_error, dead_at, redrive FROM {schema}.jobs_dead
            WHERE id > $1 AND ($3::uuid IS NULL OR tenant = $3) AND ($4::text IS NULL OR name = $4)
            ORDER BY id LIMIT $2
            """;

        public string Rows => $"""
            SELECT tenant, name, schedule, enabled, next_at, lease_until, attempts, last_error FROM {schema}.jobs ORDER BY name, tenant
            """;

        public string Overdue => $"SELECT min(next_at) FROM {schema}.jobs WHERE enabled AND next_at <= $1";
    }
}
