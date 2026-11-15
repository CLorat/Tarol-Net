using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Tarol.Messaging;

/// <summary>
/// The in-database transport (candidate T-1): a <c>bus</c> schema both systems reach. Publish = one statement that inserts
/// the message (deduplicated by (id, type, version)) and one delivery row per subscription of its topic; consume = claim
/// due deliveries with <c>FOR UPDATE SKIP LOCKED</c>, complete = delete, retry = due later. The publisher's role is stamped
/// by the database (<c>session_user</c>), and row-level security can bind a role to its contract prefix.
/// </summary>
public sealed class PgTransport(NpgsqlDataSource db, TimeProvider time, string schema = "bus") : IMessageTransport
{
    /// <summary>Poll interval when idle (and no NOTIFY arrived).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Send a NOTIFY with every publish and LISTEN while idle.</summary>
    public bool Notify { get; init; }
    /// <summary>
    /// The database roles of the publishing systems (role → source). Absent: every system connects as the same role, so the
    /// stamped role proves nothing and <see cref="Incoming.AuthenticatedAs"/> stays <see langword="null"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Roles { get; init; }
    /// <summary>Deliveries claimed per round.</summary>
    public int Batch { get; init; } = 32;

    /// <inheritdoc />
    public string Name => Notify ? "postgres+notify" : "postgres";

    /// <summary>The script (idempotent).</summary>
    public string Script => $"""
        CREATE SCHEMA IF NOT EXISTS {schema};
        CREATE TABLE IF NOT EXISTS {schema}.messages (
            id           uuid        NOT NULL,
            type         text        NOT NULL,
            version      integer     NOT NULL,
            source       text        NOT NULL,
            publisher    text        NOT NULL DEFAULT session_user,   -- authenticated by the database, not by the envelope
            headers      jsonb       NOT NULL,
            data         bytea       NOT NULL,
            published_at timestamptz NOT NULL,
            PRIMARY KEY (id, type, version));
        CREATE TABLE IF NOT EXISTS {schema}.subscriptions (
            topic text NOT NULL,
            name  text NOT NULL,
            PRIMARY KEY (topic, name));
        CREATE TABLE IF NOT EXISTS {schema}.deliveries (
            id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            subscription text        NOT NULL,
            message_id   uuid        NOT NULL,
            type         text        NOT NULL,
            version      integer     NOT NULL,
            due_at       timestamptz NOT NULL,
            attempts     integer     NOT NULL DEFAULT 0,
            claim_id     uuid        NULL,
            UNIQUE (subscription, message_id, type, version));
        CREATE INDEX IF NOT EXISTS deliveries_due ON {schema}.deliveries (subscription, due_at, id);
        """;

    /// <inheritdoc />
    public async Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        await using (var create = db.CreateCommand(Script))
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var s in subscriptions)
            foreach (var topic in s.Topics)
            {
                await using var add = db.CreateCommand($"INSERT INTO {schema}.subscriptions (topic, name) VALUES ($1, $2) ON CONFLICT (topic, name) DO NOTHING");
                add.Parameters.Add(new() { Value = topic });
                add.Parameters.Add(new() { Value = s.Name });
                await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
    }

    /// <inheritdoc />
    public async Task Publish(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var sql = $"""
            WITH m AS (
                INSERT INTO {schema}.messages (id, type, version, source, headers, data, published_at) VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (id, type, version) DO NOTHING
                RETURNING id, type, version)
            INSERT INTO {schema}.deliveries (subscription, message_id, type, version, due_at)
            SELECT s.name, m.id, m.type, m.version, $7 FROM m JOIN {schema}.subscriptions s ON s.topic = m.type
            ON CONFLICT (subscription, message_id, type, version) DO NOTHING
            """;
        await using var connection = await db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(connection);
        var insert = new NpgsqlBatchCommand(sql);
        insert.Parameters.Add(new() { Value = message.Id });
        insert.Parameters.Add(new() { Value = message.Type });
        insert.Parameters.Add(new() { Value = message.Version });
        insert.Parameters.Add(new() { Value = message.Source });
        insert.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(message.Headers().ToDictionary(), MessagingJson.Default.DictionaryStringString) });
        insert.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = message.Data.ToArray() });
        insert.Parameters.Add(new() { Value = time.GetUtcNow() });
        batch.BatchCommands.Add(insert);
        if (Notify)                                                    // one implicit transaction: the NOTIFY is sent at its commit
        {
            var notify = new NpgsqlBatchCommand($"SELECT pg_notify('{schema}_deliveries', $1)");
            notify.Parameters.Add(new() { Value = message.Type });
            batch.BatchCommands.Add(notify);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        using var signal = new SemaphoreSlim(0);
        var listener = Notify ? Listen(signal, cancellationToken) : Task.CompletedTask;
        var loops = Enumerable.Range(0, subscription.Concurrency).Select(_ => Loop(subscription, handler, signal, cancellationToken)).ToArray();
        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        try { await listener.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private async Task Loop(Subscription subscription, MessageHandler handler, SemaphoreSlim signal, CancellationToken cancellationToken)
    {
        // UPDATE … RETURNING has no order (the subquery's ORDER BY does not carry through): order the batch afterwards, by the
        // delivery id = the publish order of the subscription, or one stream's messages are handled out of order
        var claim = $"""
            WITH claimed AS (
                UPDATE {schema}.deliveries d SET attempts = d.attempts + 1, claim_id = $3, due_at = $4
                FROM (SELECT id FROM {schema}.deliveries WHERE subscription = $1 AND due_at <= $5
                      ORDER BY due_at, id LIMIT $2 FOR UPDATE SKIP LOCKED) c, {schema}.messages m
                WHERE d.id = c.id AND m.id = d.message_id AND m.type = d.type AND m.version = d.version
                RETURNING d.id, d.attempts, m.headers::text AS headers, m.data, m.publisher, m.type)
            SELECT * FROM claimed ORDER BY id
            """;
        while (!cancellationToken.IsCancellationRequested)
        {
            var rows = new List<(long Id, int Attempts, string Headers, byte[] Data, string Publisher, string Type)>();
            var claimId = Guid.CreateVersion7();
            var now = time.GetUtcNow();
            try
            {
                await using (var command = db.CreateCommand(claim))
                {
                    command.Parameters.Add(new() { Value = subscription.Name });
                    command.Parameters.Add(new() { Value = Batch });
                    command.Parameters.Add(new() { Value = claimId });
                    command.Parameters.Add(new() { Value = now + subscription.Lease });
                    command.Parameters.Add(new() { Value = now });
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        rows.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), (byte[])reader[3], reader.GetString(4), reader.GetString(5)));
                }
                if (rows.Count == 0)
                {
                    try
                    {
                        await signal.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
                }
                foreach (var row in rows)
                {
                    var headers = JsonSerializer.Deserialize(row.Headers, MessagingJson.Default.DictionaryStringString) ?? [];
                    var message = Message.From(h => headers.TryGetValue(h, out var v) ? v : null, row.Data);
                    var handled = await handler(new Incoming(message, row.Attempts, Roles is null ? null : Roles.TryGetValue(row.Publisher, out var source) ? source : "role:" + row.Publisher, row.Data, row.Type), cancellationToken).ConfigureAwait(false);
                    await using var settle = handled.Ack == Ack.Ack
                        ? db.CreateCommand($"DELETE FROM {schema}.deliveries WHERE id = $1 AND claim_id = $2")
                        : db.CreateCommand($"UPDATE {schema}.deliveries SET due_at = $3, claim_id = NULL WHERE id = $1 AND claim_id = $2");
                    settle.Parameters.Add(new() { Value = row.Id });
                    settle.Parameters.Add(new() { Value = claimId });
                    if (handled.Ack == Ack.Retry)
                        settle.Parameters.Add(new() { Value = time.GetUtcNow() + handled.RetryAfter });
                    await settle.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task Listen(SemaphoreSlim signal, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        connection.Notification += (_, _) => signal.Release();
        await using (var listen = new NpgsqlCommand($"LISTEN {schema}_deliveries", connection))
            await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
            await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Dead letters in the consumer's database (DL-1).</summary>
public sealed class NpgsqlDeadLetters(NpgsqlDataSource db, TimeProvider time, string schema) : IMessageDeadLetters
{
    /// <summary>Creates the table.</summary>
    public async Task Ensure(CancellationToken cancellationToken)
    {
        await using var command = db.CreateCommand($"""
            CREATE TABLE IF NOT EXISTS {schema}.messages_dead (
                id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                subscription text        NOT NULL,
                topic        text        NOT NULL,
                message_id   uuid        NULL,
                attempts     integer     NOT NULL,
                reason       text        NOT NULL,
                headers      jsonb       NOT NULL,
                data         bytea       NOT NULL,
                dead_at      timestamptz NOT NULL)
            """);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Add(string subscription, Incoming incoming, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        await using var command = db.CreateCommand($"INSERT INTO {schema}.messages_dead (subscription, topic, message_id, attempts, reason, headers, data, dead_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)");
        command.Parameters.Add(new() { Value = subscription });
        command.Parameters.Add(new() { Value = incoming.Topic });
        command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = incoming.Message is { } m ? m.Id : DBNull.Value });
        command.Parameters.Add(new() { Value = incoming.Attempt });
        command.Parameters.Add(new() { Value = reason });
        command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(incoming.Message is null ? [] : incoming.Message.Headers().ToDictionary(), MessagingJson.Default.DictionaryStringString) });
        command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = incoming.Raw.ToArray() });
        command.Parameters.Add(new() { Value = time.GetUtcNow() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadMessage>> List(int max, CancellationToken cancellationToken)
    {
        await using var command = db.CreateCommand($"SELECT id, subscription, topic, message_id, attempts, reason, headers::text, data, dead_at FROM {schema}.messages_dead ORDER BY id LIMIT $1");
        command.Parameters.Add(new() { Value = max });
        return await Read(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DeadMessage?> Take(long id, CancellationToken cancellationToken)
    {
        await using var command = db.CreateCommand($"DELETE FROM {schema}.messages_dead WHERE id = $1 RETURNING id, subscription, topic, message_id, attempts, reason, headers::text, data, dead_at");
        command.Parameters.Add(new() { Value = id });
        return (await Read(command, cancellationToken).ConfigureAwait(false)) is [var one] ? one : null;
    }

    private static async Task<List<DeadMessage>> Read(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var list = new List<DeadMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(new DeadMessage(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetInt32(4), reader.GetString(5), Encoding.UTF8.GetBytes(reader.GetString(6)), (byte[])reader[7], reader.GetFieldValue<DateTimeOffset>(8)));
        return list;
    }
}
