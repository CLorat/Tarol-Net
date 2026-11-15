using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Tarol.Messaging.Npgsql;

/// <summary>
/// The consumer's dead letters in its own database (M-9, DL-1): table <c>&lt;schema&gt;.messages_dead</c>, usually next to the
/// store's tables. Every transport buries into it the same way; <c>MessageConsumers.Redrive(id)</c> takes one out and runs it again.
/// </summary>
public sealed class NpgsqlMessageDeadLetters : IMessageDeadLetters
{
    private readonly NpgsqlDataSource _db;
    private readonly TimeProvider _time;
    private readonly string _insert;
    private readonly string _list;
    private readonly string _take;

    /// <summary>Creates the store.</summary>
    /// <param name="dataSource">The consumer's database.</param>
    /// <param name="time">The clock of <see cref="DeadMessage.DeadAt"/>.</param>
    /// <param name="schema">The schema of the table (default <c>tarol</c>, the store's).</param>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public NpgsqlMessageDeadLetters(NpgsqlDataSource dataSource, TimeProvider time, string schema = "tarol")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(time);
        NpgsqlBusSchema.Validate(schema);
        _db = dataSource;
        _time = time;
        Schema = schema;
        const string Columns = "id, subscription, topic, message_id, attempts, reason, headers::text, data, dead_at";
        _insert = $"INSERT INTO {schema}.messages_dead (subscription, topic, message_id, attempts, reason, headers, data, dead_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)";
        _list = $"SELECT {Columns} FROM {schema}.messages_dead ORDER BY id LIMIT $1";
        _take = $"DELETE FROM {schema}.messages_dead WHERE id = $1 RETURNING {Columns}";
    }

    /// <summary>The schema of the table.</summary>
    public string Schema { get; }

    /// <summary>Creates the table if it is missing (<see cref="NpgsqlBusSchema.DeadLettersScript"/>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    public async Task EnsureSchema(CancellationToken cancellationToken)
    {
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext($1))", connection, transaction))
                {
                    lockCommand.Parameters.Add(new() { Value = "tarol.messages_dead." + Schema });
                    await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using (var create = new NpgsqlCommand(NpgsqlBusSchema.DeadLettersScript(Schema), connection, transaction))
                    await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

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
