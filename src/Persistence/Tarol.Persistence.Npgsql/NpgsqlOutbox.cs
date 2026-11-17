using Npgsql;
using NpgsqlTypes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql;

/// <summary>
/// The outbox of an <see cref="NpgsqlAggregateStore"/> (P-1, P-10, S-5, S-8, S-9). Workers claim due <b>heads</b> with
/// <c>FOR UPDATE SKIP LOCKED</c>: a row is the head when it is the oldest live row of its (tenant, type, stream, reaction)
/// (kept in a <c>head</c> column, so a claim costs the same however long the backlog), or when its reaction is scheduled.
/// <see cref="Complete"/> and <see cref="Bury"/> promote the next row of the stream under a lock on the stream's row, so a
/// concurrent commit of that stream cannot leave it without a head. Dead letters move to <c>outbox_dead</c>.
/// <code>
/// foreach (var row in await store.Outbox.Claim(32, TimeSpan.FromMinutes(1), ct))
///     _ = done ? await store.Outbox.Complete(row, ct) : await store.Outbox.Retry(row, now + backoff, reason, ct);
/// </code>
/// </summary>
/// <remarks>
/// With <see cref="NpgsqlStoreOptions.Notify"/>, commits that write rows send a <c>NOTIFY</c> and <see cref="WaitForRows"/>
/// listens on one connection per outbox; it costs writers throughput (docs/migration/sagas.md §4.3), so it is opt-in.
/// Dispose the outbox to close that connection.
/// </remarks>
public sealed class NpgsqlOutbox : IOutbox, IDeadLetterSource, IDeadLetterRetention, IAsyncDisposable
{
    private readonly NpgsqlDataSource _db;
    private readonly EventCodec _codec;
    private readonly TimeProvider _time;
    private readonly IOutboxReactions? _reactions;
    private readonly bool _notify;
    private readonly Statements _sql;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _listener;

    internal NpgsqlOutbox(NpgsqlDataSource dataSource, EventCodec codec, TimeProvider time, string schema, IOutboxReactions? reactions, bool notify)
    {
        _db = dataSource;
        _codec = codec;
        _time = time;
        _reactions = reactions;
        _notify = notify;
        _sql = new Statements(schema);
        Channel = schema + "_outbox";
    }

    /// <summary>The <c>LISTEN</c>/<c>NOTIFY</c> channel (<c>&lt;schema&gt;_outbox</c>).</summary>
    public string Channel { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxRow>> Claim(int max, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        var now = _time.GetUtcNow();
        await using var command = _db.CreateCommand(_sql.Claim);
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = max });
        command.Parameters.Add(new NpgsqlParameter { Value = now + lease });
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var rows = new List<OutboxRow>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var recorded = RecordedEvents.Read(reader, 7, _codec);
                rows.Add(new OutboxRow(reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), recorded.Event.Context.Tenant,
                    reader.GetInt32(3), reader.IsDBNull(4) ? Option.None : reader.GetString(4), recorded,
                    reader.IsDBNull(5) ? Option.None : reader.GetString(5))
                { DueAt = reader.GetFieldValue<DateTimeOffset>(6) });
            }
            return rows;
        }
    }

    /// <inheritdoc />
    public Task<bool> Complete(OutboxRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Locked(_sql.LockForRow, _sql.Complete, cancellationToken, row.Id, row.ClaimId);
    }

    /// <inheritdoc />
    public async Task<bool> Retry(OutboxRow row, DateTimeOffset retryAt, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(reason);
        await using var command = _db.CreateCommand(_sql.Retry);
        Add(command, row.Id, row.ClaimId, retryAt, reason);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public Task<bool> Bury(OutboxRow row, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(reason);
        return Locked(_sql.LockForRow, _sql.Bury, cancellationToken, row.Id, row.ClaimId, _time.GetUtcNow(), reason);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadLetter>> DeadLetters(int max, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        await using var command = _db.CreateCommand(_sql.DeadLetters);
        Add(command, max);
        return await ReadDead(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadLetter>> DeadLetters(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var command = _db.CreateCommand(_sql.DeadPage);
        Add(command, query.After, query.Limit);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = query.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)query.Name ?? DBNull.Value });
        return await ReadDead(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DeadLetter>> ReadDead(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var list = new List<DeadLetter>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var tenant = reader.GetGuid(2);
                list.Add(new DeadLetter(reader.GetInt64(0), reader.GetString(1), tenant == Guid.Empty ? Option.None : (Id<ITenant>)tenant,
                    reader.GetString(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetInt32(6),
                    reader.IsDBNull(7) ? Option.None : reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8)));
            }
            return list;
        }
    }

    /// <inheritdoc />
    public Task<bool> Discard(long id, CancellationToken cancellationToken) => Discard(id, null, cancellationToken);

    /// <inheritdoc />
    public async Task<Option<DateTimeOffset>> OldestDue(CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_sql.OldestDue);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = _time.GetUtcNow().UtcDateTime });
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(0)
                ? reader.GetFieldValue<DateTimeOffset>(0)
                : Option.None;
    }


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
    string IDeadLetterRetention.Kind => DeadLetterKinds.Reactions;

    /// <summary>The kind of these dead letters: <see cref="DeadLetterKinds.Reactions"/>.</summary>
    string IDeadLetterSource.Kind => DeadLetterKinds.Reactions;

    /// <inheritdoc />
    async Task<DeadLetterPage> IDeadLetterSource.List(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        var rows = await DeadLetters(query, cancellationToken).ConfigureAwait(false);
        return new DeadLetterPage([.. rows.Select(static d => d.ToItem())], rows.Count == query.Limit ? rows[^1].Id : null);
    }

    /// <inheritdoc />
    Task<bool> IDeadLetterSource.Redrive(long id, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return Redrive(id, actor, cancellationToken);
    }

    /// <inheritdoc />
    Task<bool> IDeadLetterSource.Discard(long id, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return Discard(id, actor, cancellationToken);
    }

    private async Task<bool> Discard(long id, string? actor, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(actor is null ? _sql.Discard : _sql.DiscardAudited);
        Add(command, id);
        if (actor is not null)
            Add(command, _time.GetUtcNow().UtcDateTime, actor);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is 1L;
    }

    /// <inheritdoc />
    public Task<bool> Redrive(long id, CancellationToken cancellationToken) => Redrive(id, null, cancellationToken);

    private async Task<bool> Redrive(long id, string? actor, CancellationToken cancellationToken)
    {
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                // exclusive on the stream row: serializes with a promotion (FOR SHARE) of the same stream
                string? reaction;
                await using (var lockStream = new NpgsqlCommand(_sql.LockForDead, connection, transaction))
                {
                    Add(lockStream, id);
                    reaction = await lockStream.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                }
                if (reaction is null)
                    return false;
                var now = _time.GetUtcNow();
                await using var redrive = new NpgsqlCommand(actor is null ? _sql.Redrive : _sql.RedriveAudited, connection, transaction);
                Add(redrive, id, now, _reactions?.IsScheduled(reaction) ?? false);
                if (actor is not null)
                    Add(redrive, actor);
                var moved = await redrive.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is 1L;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return moved;
            }
        }
    }

    /// <inheritdoc />
    public async Task<long> Pending(CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_sql.Pending);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long n ? n : 0;
    }

    /// <inheritdoc />
    public Task WaitForRows(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_notify)
            return Task.Delay(timeout, _time, cancellationToken);
        lock (_gate)
            _listener ??= Task.Run(Listen, CancellationToken.None);
        return Task.WhenAny(Volatile.Read(ref _written).Task, Task.Delay(timeout, _time, cancellationToken));
    }

    /// <summary>Stops the <c>LISTEN</c> connection, if one was opened.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listener is not null)
            await _listener.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var connection = await _db.OpenConnectionAsync(_stop.Token).ConfigureAwait(false);
                await using (connection.ConfigureAwait(false))
                {
                    connection.Notification += (_, _) => Signal();
                    await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                        await listen.ExecuteNonQueryAsync(_stop.Token).ConfigureAwait(false);
                    Signal();                                                       // rows may have arrived before LISTEN
                    while (!_stop.IsCancellationRequested)
                        await connection.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (NpgsqlException)
            {
                // the connection broke: workers keep polling with their timeout; reconnect after a pause
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _time, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Signal() => Interlocked.Exchange(ref _written, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Takes the stream row lock (statement 1), then runs <paramref name="change"/> (statement 2, a fresh snapshot).</summary>
    private async Task<bool> Locked(string lockSql, string change, CancellationToken cancellationToken, params object[] values)
    {
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await using (var lockStream = new NpgsqlCommand(lockSql, connection, transaction))
                {
                    Add(lockStream, values[0], values[1]);
                    if (await lockStream.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                        return false;                                               // gone, or claimed by someone else
                }
                await using var command = new NpgsqlCommand(change, connection, transaction);
                Add(command, values);
                var done = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long n && n == 1;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return done;
            }
        }
    }

    private static void Add(NpgsqlCommand command, params object[] values)
    {
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
    }

    private sealed class Statements(string schema)
    {
        private string Promote(string deleted) => $"""
            p AS (UPDATE {schema}.outbox o SET head = true FROM {deleted} d
                  WHERE o.id = (SELECT min(n.id) FROM {schema}.outbox n
                                WHERE n.tenant = d.tenant AND n.type = d.type AND n.stream_id = d.stream_id AND n.reaction = d.reaction
                                  AND NOT n.head AND n.id <> d.id)
                  RETURNING 1)
            """;

        public string Claim => $"""
            WITH c AS (
                SELECT o.id FROM {schema}.outbox o
                WHERE o.head AND o.due_at <= $1 AND (o.lease_until IS NULL OR o.lease_until <= $1)
                ORDER BY o.due_at, o.id
                LIMIT $2
                FOR UPDATE SKIP LOCKED),
            u AS (
                UPDATE {schema}.outbox o SET claim_id = gen_random_uuid(), lease_until = $3, attempts = o.attempts + 1
                FROM c WHERE o.id = c.id
                RETURNING o.id, o.claim_id, o.reaction, o.attempts, o.last_error, o.event_id, o.due_at)
            SELECT u.id, u.claim_id, u.reaction, u.attempts, u.last_error, e.traceparent, u.due_at, {RecordedEvents.Columns("e")}
            FROM u JOIN {schema}.events e ON e.event_id = u.event_id
            ORDER BY u.id
            """;

        // statement 1 of Complete and Bury: share-lock the stream row, so a commit of this stream (which updates that row)
        // either finished before the promotion's snapshot or waits until the promotion committed
        public string LockForRow => $"""
            SELECT 1 FROM {schema}.streams s JOIN {schema}.outbox o ON s.tenant = o.tenant AND s.type = o.type AND s.id = o.stream_id
            WHERE o.id = $1 AND o.claim_id = $2
            FOR SHARE OF s
            """;

        public string Complete => $"""
            WITH d AS (DELETE FROM {schema}.outbox WHERE id = $1 AND claim_id = $2 RETURNING id, tenant, type, stream_id, reaction),
            {Promote("d")}
            SELECT count(*) FROM d
            """;

        public string Retry => $"UPDATE {schema}.outbox SET due_at = $3, claim_id = NULL, lease_until = NULL, last_error = $4 WHERE id = $1 AND claim_id = $2";

        public string Bury => $"""
            WITH d AS (DELETE FROM {schema}.outbox WHERE id = $1 AND claim_id = $2
                       RETURNING id, tenant, reaction, type, stream_id, version, event_id, attempts),
            i AS (INSERT INTO {schema}.outbox_dead (id, tenant, reaction, type, stream_id, version, event_id, attempts, last_error, dead_at)
                  SELECT id, tenant, reaction, type, stream_id, version, event_id, attempts, $4, $3 FROM d),
            {Promote("d")}
            SELECT count(*) FROM d
            """;

        public string DeadLetters => $"""
            SELECT d.id, d.reaction, d.tenant, d.type, d.stream_id, d.event_id, d.attempts, d.last_error, d.dead_at
            FROM {schema}.outbox_dead d ORDER BY d.id LIMIT $1
            """;

        public string LockForDead => $"""
            SELECT d.reaction FROM {schema}.streams s JOIN {schema}.outbox_dead d ON s.tenant = d.tenant AND s.type = d.type AND s.id = d.stream_id
            WHERE d.id = $1
            FOR NO KEY UPDATE OF s
            """;

        private string RedriveOf(string audit) => $"""
            WITH d AS (DELETE FROM {schema}.outbox_dead WHERE id = $1 RETURNING id, tenant, reaction, type, stream_id, version, event_id),
            i AS (INSERT INTO {schema}.outbox (tenant, reaction, type, stream_id, version, event_id, due_at, head)
                  SELECT d.tenant, d.reaction, d.type, d.stream_id, d.version, d.event_id, $2,
                         $3 OR NOT EXISTS (SELECT 1 FROM {schema}.outbox p
                                           WHERE p.tenant = d.tenant AND p.type = d.type AND p.stream_id = d.stream_id AND p.reaction = d.reaction)
                  FROM d RETURNING 1){audit}
            SELECT count(*) FROM i
            """;

        public string Redrive => RedriveOf("");

        // the audit row in the same statement (O-3): DELETE … RETURNING feeds it
        public string RedriveAudited => RedriveOf(Audit("redrive", "$2", "$4"));

        private string Audit(string action, string at, string actor) => $"""
            ,
            a AS (INSERT INTO {schema}.operations_audit (at, actor, action, target, detail)
                  SELECT {at}, {actor}, '{action}', 'reactions:' || d.id,
                         jsonb_build_object('reaction', d.reaction, 'tenant', d.tenant, 'stream', d.type || '/' || d.stream_id, 'event', d.event_id) FROM d)
            """;

        private const string DeadColumns = "d.id, d.reaction, d.tenant, d.type, d.stream_id, d.event_id, d.attempts, d.last_error, d.dead_at";

        public string Purge => $"""
            WITH d AS (DELETE FROM {schema}.outbox_dead WHERE id IN (SELECT id FROM {schema}.outbox_dead WHERE dead_at < $1 ORDER BY dead_at LIMIT $2) RETURNING id),
            a AS (INSERT INTO {schema}.operations_audit (at, actor, action, target, detail)
                  SELECT $3, $4, 'purge', 'reactions', jsonb_build_object('rows', count(*), 'before', $1) FROM d HAVING count(*) > 0)
            SELECT count(*) FROM d
            """;

        public string DeadPage => $"""
            SELECT {DeadColumns} FROM {schema}.outbox_dead d
            WHERE d.id > $1 AND ($3::uuid IS NULL OR d.tenant = $3) AND ($4::text IS NULL OR d.reaction = $4)
            ORDER BY d.id LIMIT $2
            """;

        private string DiscardOf(string audit) => $"""
            WITH d AS (DELETE FROM {schema}.outbox_dead WHERE id = $1 RETURNING id, tenant, reaction, type, stream_id, event_id){audit}
            SELECT count(*) FROM d
            """;

        public string Discard => DiscardOf("");

        public string DiscardAudited => DiscardOf(Audit("discard", "$2", "$3"));

        // the oldest due head: an index lookup on outbox_heads (due_at, id) WHERE head
        public string OldestDue => $"SELECT min(due_at) FROM {schema}.outbox WHERE head AND due_at <= $1";

        public string Pending => $"SELECT count(*) FROM {schema}.outbox";
    }
}
