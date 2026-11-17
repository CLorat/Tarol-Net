using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Tarol.Persistence.Npgsql;

namespace Tarol.Messaging.Npgsql;

/// <summary>
/// The consumer's dead letters in its own database (M-9, DL-1): table <c>&lt;schema&gt;.messages_dead</c>, usually next to the
/// store's tables. Every transport buries into it the same way; <c>MessageConsumers.Redrive(id)</c> takes one out and runs it again.
/// </summary>
public sealed class NpgsqlMessageDeadLetters : IMessageDeadLetters
{
    private readonly NpgsqlDataSource _db;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly string _insert;
    private readonly string _list;
    private readonly string _take;

    /// <summary>Creates the store.</summary>
    /// <param name="dataSource">The consumer's database.</param>
    /// <param name="time">The clock of <see cref="DeadMessage.DeadAt"/>.</param>
    /// <param name="schema">The schema of the table (default <c>tarol</c>, the store's).</param>
    /// <param name="logger">Logs the schema upgrades (event ids 8001–8005); <see langword="null"/> = none.</param>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public NpgsqlMessageDeadLetters(NpgsqlDataSource dataSource, TimeProvider time, string schema = "tarol", ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(time);
        NpgsqlBusSchema.Validate(schema);
        _db = dataSource;
        _time = time;
        _log = logger;
        Schema = schema;
        const string Columns = "id, subscription, topic, message_id, attempts, reason, headers::text, data, dead_at";
        _insert = $"INSERT INTO {schema}.messages_dead (subscription, topic, message_id, attempts, reason, headers, data, dead_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)";
        _list = $"SELECT {Columns} FROM {schema}.messages_dead ORDER BY id LIMIT $1";
        _take = $"DELETE FROM {schema}.messages_dead WHERE id = $1 RETURNING {Columns}";
    }

    /// <summary>The schema of the table.</summary>
    public string Schema { get; }

    /// <summary>
    /// Creates or upgrades the table (<see cref="NpgsqlBusSchema.DeadLettersDefinition"/>, version table
    /// <c>messages_dead_version</c>) under a polled advisory lock (operations.md §4).
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">The table's schema is compatible only from a newer version than this package's.</exception>
    public async Task EnsureSchema(CancellationToken cancellationToken)
    {
        _ = await EnsureSchema(SchemaMode.Upgrade, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates or upgrades the table, or only verifies it, as <paramref name="mode"/> says.</summary>
    /// <param name="mode">Upgrade or verify.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version afterwards.</returns>
    /// <exception cref="InvalidOperationException">
    /// The schema is compatible only from a newer version; in <see cref="SchemaMode.VerifyOnly"/>, it is missing or older.
    /// </exception>
    public Task<SchemaState> EnsureSchema(SchemaMode mode, CancellationToken cancellationToken)
        => new NpgsqlSchemaUpgrader(_db, NpgsqlBusSchema.DeadLettersDefinition(Schema), _log).Ensure(mode, cancellationToken);

    /// <inheritdoc />
    public async Task Add(string subscription, Incoming incoming, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(reason);
        var headers = incoming.Message is { } message ? message.Headers().ToDictionary() : [];
        await using var command = _db.CreateCommand(_insert);
        command.Parameters.Add(new() { Value = subscription });
        command.Parameters.Add(new() { Value = incoming.Topic });
        command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = incoming.Message is { } m ? m.Id : DBNull.Value });
        command.Parameters.Add(new() { Value = incoming.Attempt });
        command.Parameters.Add(new() { Value = reason });
        command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(headers, BusJson.Default.DictionaryStringString) });
        command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = incoming.Raw.ToArray() });
        command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = _time.GetUtcNow() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadMessage>> List(int max, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        await using var command = _db.CreateCommand(_list);
        command.Parameters.Add(new() { Value = max });
        return await Read(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DeadMessage?> Take(long id, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_take);
        command.Parameters.Add(new() { Value = id });
        return await Read(command, cancellationToken).ConfigureAwait(false) is [var one] ? one : null;
    }

    private static async Task<List<DeadMessage>> Read(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var list = new List<DeadMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(new DeadMessage(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetInt32(4), reader.GetString(5), JsonSerializer.Deserialize(reader.GetString(6), BusJson.Default.DictionaryStringString) ?? [],
                reader.GetFieldValue<byte[]>(7), reader.GetFieldValue<DateTimeOffset>(8)));
        return list;
    }
}
