using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Tarol.Persistence.Npgsql;

namespace Tarol.Messaging.Npgsql;

/// <summary>
/// The in-database transport (docs/architecture/messaging.md §6, T-1): a bus schema every system reaches. Publishing is one
/// statement that inserts the message, deduplicated by (event, contract, version), and one delivery per subscription of
/// its contract; consuming claims due deliveries with <c>FOR UPDATE SKIP LOCKED</c> in publish order; an acknowledgement
/// deletes the delivery, a retry makes it due later; both present the claim. The database stamps the publisher's role.
/// </summary>
/// <remarks>
/// <para>
/// A delivery that outlives <see cref="Subscription.Lease"/> (a crashed consumer) is due again and claimed by another
/// consumer; the late acknowledgement of the first finds no row with its claim and changes nothing.
/// </para>
/// <para>
/// Order: one subscription with concurrency 1 receives its messages in publish order; the claim is ordered <b>after</b>
/// <c>UPDATE … RETURNING</c>, which itself has none (docs/migration/messaging.md §5 item 6).
/// </para>
/// </remarks>
public sealed partial class NpgsqlBus : IMessageTransport, IAsyncDisposable
{
    private readonly NpgsqlDataSource _db;
    private readonly bool _ownsDataSource;
    private readonly TimeProvider _time;
    private readonly NpgsqlBusOptions _options;
    private readonly ILogger _log;
    private readonly string _publish;
    private readonly string _claim;
    private readonly string _complete;
    private readonly string _retry;
    private readonly string _release;
    private readonly string _channel;
    private volatile bool _schemaEnsured;

    /// <summary>Creates the bus on a data source the application owns.</summary>
    /// <param name="dataSource">The data source of the database that holds the bus schema.</param>
    /// <param name="time">The clock of due times.</param>
    /// <param name="options">Schema, NOTIFY, poll interval, roles; <see langword="null"/> = defaults.</param>
    /// <param name="logger">Logs listener and claim failures; <see langword="null"/> = none.</param>
    public NpgsqlBus(NpgsqlDataSource dataSource, TimeProvider time, NpgsqlBusOptions? options = null, ILogger? logger = null)
        : this(dataSource, ownsDataSource: false, time, options, logger)
    {
    }

    internal NpgsqlBus(NpgsqlDataSource dataSource, bool ownsDataSource, TimeProvider time, NpgsqlBusOptions? options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(time);
        options ??= new NpgsqlBusOptions();
        options.Validate();
        _db = dataSource;
        _ownsDataSource = ownsDataSource;
        _time = time;
        _options = options;
        _log = logger ?? NullLogger.Instance;
        var s = options.Schema;
        _channel = s + "_deliveries";
        _publish = $"""
            WITH m AS (
                INSERT INTO {s}.messages (id, type, version, source, headers, data, published_at) VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (id, type, version) DO NOTHING
                RETURNING id, type, version)
            INSERT INTO {s}.deliveries (subscription, message_id, type, version, due_at)
            SELECT sub.name, m.id, m.type, m.version, $7 FROM m JOIN {s}.subscriptions sub ON sub.topic = m.type
            ORDER BY sub.name
            ON CONFLICT (subscription, message_id, type, version) DO NOTHING
            """;
        _claim = $"""
            WITH claimed AS (
                UPDATE {s}.deliveries d SET attempts = d.attempts + 1, claim_id = $3, due_at = $4
                FROM (SELECT id FROM {s}.deliveries WHERE subscription = $1 AND due_at <= $5
                      ORDER BY due_at, id LIMIT $2 FOR UPDATE SKIP LOCKED) c, {s}.messages m
                WHERE d.id = c.id AND m.id = d.message_id AND m.type = d.type AND m.version = d.version
                RETURNING d.id, d.attempts, m.headers::text AS headers, m.data, m.publisher, m.type)
            SELECT id, attempts, headers, data, publisher, type FROM claimed ORDER BY id
            """;
        _complete = $"DELETE FROM {s}.deliveries WHERE id = $1 AND claim_id = $2";
        _retry = $"UPDATE {s}.deliveries SET due_at = $3, claim_id = NULL WHERE id = $1 AND claim_id = $2";
        _release = $"UPDATE {s}.deliveries SET due_at = $3, claim_id = NULL, attempts = greatest(attempts - 1, 0) WHERE id = $1 AND claim_id = $2";
    }

    /// <summary>The bus schema.</summary>
    public string Schema => _options.Schema;

    /// <summary>
    /// Creates or upgrades the bus schema (<see cref="NpgsqlBusSchema.Definition"/>) under a polled advisory lock, or only
    /// verifies it (<see cref="NpgsqlBusOptions.SchemaMode"/>; operations.md §4).
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">
    /// The schema is compatible only from a newer version than this package's; in <see cref="SchemaMode.VerifyOnly"/>, it is missing.
    /// </exception>
    public async Task EnsureSchema(CancellationToken cancellationToken)
    {
        _ = await EnsureSchema(_options.SchemaMode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates or upgrades the bus schema, or only verifies it, as <paramref name="mode"/> says.</summary>
    /// <param name="mode">Upgrade or verify.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The schema's version afterwards.</returns>
    /// <exception cref="InvalidOperationException">As <see cref="EnsureSchema(CancellationToken)"/>.</exception>
    public async Task<SchemaState> EnsureSchema(SchemaMode mode, CancellationToken cancellationToken)
    {
        var state = await new NpgsqlSchemaUpgrader(_db, NpgsqlBusSchema.Definition(Schema), _log) { LockTimeout = _options.SchemaLockTimeout }
            .Ensure(mode, cancellationToken).ConfigureAwait(false);
        _schemaEnsured = true;
        return state;
    }

    /// <inheritdoc />
    /// <remarks>Runs the schema script once (unless <see cref="NpgsqlBusOptions.EnsureSchema"/> is off), then adds the
    /// subscriptions' rows; topics need nothing on the bus (a message without a subscription is stored, not delivered).</remarks>
    public async Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (_options.EnsureSchema && !_schemaEnsured)
            await EnsureSchema(cancellationToken).ConfigureAwait(false);
        if (subscriptions.Count == 0)
            return;
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await using var batch = new NpgsqlBatch(connection);
            foreach (var subscription in subscriptions)
                foreach (var topic in subscription.Topics)
                {
                    var add = new NpgsqlBatchCommand($"INSERT INTO {Schema}.subscriptions (topic, name) VALUES ($1, $2) ON CONFLICT (topic, name) DO NOTHING");
                    add.Parameters.Add(new() { Value = topic });
                    add.Parameters.Add(new() { Value = subscription.Name });
                    batch.BatchCommands.Add(add);
                }
            if (batch.BatchCommands.Count > 0)
                await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One statement: the message (a republish of the same (event, contract, version) is dropped) and one delivery per
    /// subscription of its contract; with <see cref="NpgsqlBusOptions.Notify"/> a <c>NOTIFY</c> in the same implicit
    /// transaction. Durable when it returns. A role refused by row-level security fails with SQLSTATE 42501.
    /// </remarks>
    public async Task Publish(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await using var batch = new NpgsqlBatch(connection);
            var insert = new NpgsqlBatchCommand(_publish);
            insert.Parameters.Add(new() { Value = message.Id });
            insert.Parameters.Add(new() { Value = message.Type });
            insert.Parameters.Add(new() { Value = message.Version });
            insert.Parameters.Add(new() { Value = message.Source });
            insert.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(message.Headers().ToDictionary(), BusJson.Default.DictionaryStringString) });
            insert.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = message.Data.ToArray() });
            insert.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = _time.GetUtcNow() });
            batch.BatchCommands.Add(insert);
            if (_options.Notify)
            {
                var notify = new NpgsqlBatchCommand("SELECT pg_notify($1, $2)");
                notify.Parameters.Add(new() { Value = _channel });
                notify.Parameters.Add(new() { Value = message.Type });
                batch.BatchCommands.Add(notify);
            }
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="Subscription.Concurrency"/> claim loops (and a <c>LISTEN</c> connection with
    /// <see cref="NpgsqlBusOptions.Notify"/>). A claim that fails (the database is unreachable) ends the call with the
    /// exception; the host consumes again after its restart delay, and the deliveries wait in the table.
    /// </remarks>
    public async Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var signal = new SemaphoreSlim(0);
        var listener = _options.Notify ? Listen(signal, stop.Token) : Task.CompletedTask;
        var loops = Enumerable.Range(0, subscription.Concurrency).Select(_ => Loop(subscription, handler, signal, stop.Token)).ToArray();
        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);                       // a failed loop stops the others and the listener
            try
            {
                await listener.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Deletes messages published before <paramref name="olderThan"/> that no subscription still has to receive, in batches
    /// (retention). With a job host it runs as the system job <c>tarol.purge-messages</c> (<see cref="NpgsqlBusOptions.Retention"/>). Same shape as the store's key purge.
    /// </summary>
    /// <param name="olderThan">The cut-off.</param>
    /// <param name="batch">The most messages per statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many messages were deleted.</returns>
    public async Task<int> PurgeMessages(DateTimeOffset olderThan, int batch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        await using var command = _db.CreateCommand($"""
            DELETE FROM {Schema}.messages WHERE (id, type, version) IN (
                SELECT m.id, m.type, m.version FROM {Schema}.messages m
                WHERE m.published_at < $1
                  AND NOT EXISTS (SELECT 1 FROM {Schema}.deliveries d WHERE d.message_id = m.id AND d.type = m.type AND d.version = m.version)
                LIMIT $2)
            """);
        command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = olderThan });
        command.Parameters.Add(new() { Value = batch });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many deliveries wait for <paramref name="subscription"/> (due or not; tests and health checks).</summary>
    /// <param name="subscription">The subscription's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The count.</returns>
    public async Task<long> Pending(string subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        await using var command = _db.CreateCommand($"SELECT count(*) FROM {Schema}.deliveries WHERE subscription = $1");
        command.Parameters.Add(new() { Value = subscription });
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// The deliveries that wait for each subscription of <paramref name="application"/> (<c>&lt;application&gt;.*</c>, due or not):
    /// the snapshot signal <c>tarol.messages.backlog</c> (step 15). A count per subscription: it scans the waiting deliveries
    /// (acknowledged ones are deleted), once per poll, never per scrape.
    /// </summary>
    /// <param name="application">The application's name (the prefix of its subscriptions).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One entry per subscription with deliveries.</returns>
    public async Task<IReadOnlyList<(string Subscription, long Count)>> Backlog(string application, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(application);
        // starts_with, not a range: under a linguistic collation '.' and '/' do not sort by their code points
        await using var command = _db.CreateCommand(
            $"SELECT subscription, count(*) FROM {Schema}.deliveries WHERE starts_with(subscription, $1) GROUP BY subscription ORDER BY subscription");
        command.Parameters.Add(new() { Value = application + "." });
        var list = new List<(string, long)>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add((reader.GetString(0), reader.GetInt64(1)));
        return list;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
            await _db.DisposeAsync().ConfigureAwait(false);
    }

    private async Task Loop(Subscription subscription, MessageHandler handler, SemaphoreSlim signal, CancellationToken cancellationToken)
    {
        var rows = new List<Claimed>(_options.Batch);
        while (!cancellationToken.IsCancellationRequested)
        {
            rows.Clear();
            var claimId = Guid.CreateVersion7(_time.GetUtcNow());
            var now = _time.GetUtcNow();
            await using (var command = _db.CreateCommand(_claim))
            {
                command.Parameters.Add(new() { Value = subscription.Name });
                command.Parameters.Add(new() { Value = _options.Batch });
                command.Parameters.Add(new() { Value = claimId });
                command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = now + subscription.Lease });
                command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = now });
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    rows.Add(new Claimed(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetFieldValue<byte[]>(3), reader.GetString(4), reader.GetString(5)));
            }
            if (rows.Count == 0)
            {
                try
                {
                    _ = await signal.WaitAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            // the batch shares one lease: a row starts only while half of it is left, so the tail of a slow batch is never
            // run after the lease, alongside another instance that claimed it again; the rest goes back unrun and uncounted
            var startBefore = now + subscription.Lease / 2;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                cancellationToken.ThrowIfCancellationRequested();                     // the rest of the batch comes back after the lease
                if (_time.GetUtcNow() >= startBefore)
                {
                    await Release(subscription.Name, rows, i, claimId, now).ConfigureAwait(false);
                    break;
                }
                var headers = Headers(row.Headers);
                var message = headers is null ? null : Message.From(h => headers.TryGetValue(h, out var v) ? v : null, row.Data);
                var handled = await handler(new Incoming(message, row.Attempts, AuthenticatedAs(row.Publisher), row.Data, row.Type), cancellationToken)
                    .ConfigureAwait(false);
                await using var settle = _db.CreateCommand(handled.Ack == Ack.Ack ? _complete : _retry);
                settle.Parameters.Add(new() { Value = row.Id });
                settle.Parameters.Add(new() { Value = claimId });
                if (handled.Ack == Ack.Retry)
                    settle.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = _time.GetUtcNow() + handled.RetryAfter });
                // the handler has run: record its answer even while stopping, so a handled delivery is not delivered again
                if (await settle.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false) == 0)
                    LeaseLost(_log, subscription.Name, row.Id, row.Attempts);
            }
        }
    }

    private async Task Release(string subscription, List<Claimed> rows, int from, Guid claimId, DateTimeOffset dueAt)
    {
        BatchOutlived(_log, subscription, rows.Count - from, rows.Count);
        for (var i = from; i < rows.Count; i++)                                         // one by one: the slim data source has no arrays
        {
            await using var release = _db.CreateCommand(_release);
            release.Parameters.Add(new() { Value = rows[i].Id });
            release.Parameters.Add(new() { Value = claimId });
            release.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = dueAt });
            _ = await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private string? AuthenticatedAs(string publisher)
        => _options.Roles is not { } roles ? null
         : roles.TryGetValue(publisher, out var source) ? source
         : "role:" + publisher;

    private static Dictionary<string, string>? Headers(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, BusJson.Default.DictionaryStringString);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task Listen(SemaphoreSlim signal, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using (connection.ConfigureAwait(false))
                {
                    connection.Notification += (_, _) =>
                    {
                        if (signal.CurrentCount < 64)
                            signal.Release();
                    };
                    await using (var listen = new NpgsqlCommand($"LISTEN {_channel}", connection))
                        await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    while (!cancellationToken.IsCancellationRequested)
                        await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is NpgsqlException or System.IO.IOException or TimeoutException)
            {
                // the claim loops still poll; listen again after a pause
                ListenFailed(_log, e);
                try
                {
                    await Task.Delay(_options.PollInterval * 10, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private sealed record Claimed(long Id, int Attempts, string Headers, byte[] Data, string Publisher, string Type);

    [LoggerMessage(EventId = 11101, Level = LogLevel.Warning, Message = "Delivery {DeliveryId} of {Subscription} (attempt {Attempt}) outlived its lease; another consumer owns it")]
    private static partial void LeaseLost(ILogger logger, string subscription, long deliveryId, int attempt);

    [LoggerMessage(EventId = 11103, Level = LogLevel.Warning, Message = "A batch of {Subscription} outlived half its lease; {Count} of {Batch} deliveries were handed back unrun (lower NpgsqlBusOptions.Batch or raise the lease)")]
    private static partial void BatchOutlived(ILogger logger, string subscription, int count, int batch);

    [LoggerMessage(EventId = 11102, Level = LogLevel.Warning, Message = "The bus LISTEN connection failed; the consumers poll and listen again later")]
    private static partial void ListenFailed(ILogger logger, Exception exception);
}
