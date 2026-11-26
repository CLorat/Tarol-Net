using System.Data.Common;
using System.Text.Json;
using Npgsql;

namespace Tarol.Queries.Npgsql;

/// <summary>
/// The read table of an <see cref="ISqlProjection{TRow}"/>'s rows on PostgreSQL (TN-54, owner decision P-2): the same table, columns
/// and <c>jsonb</c> row as a row projection's, so the list and row endpoints read it. Call <see cref="Prepare"/> from
/// <see cref="ISqlProjection.Prepare"/> and <see cref="Write"/>/<see cref="Remove"/> from <see cref="ISqlProjection.Apply"/>: every statement
/// runs in the batch transaction, so the rows commit with the checkpoint. Thread-safe.
/// </summary>
/// <typeparam name="TRow">The <c>[Projection(…, Sql = true)]</c> row record; its type must be in the application's <c>JsonSerializerContext</c>.</typeparam>
public sealed class NpgsqlRows<TRow>
    where TRow : class, IProjection<TRow>
{
    private readonly NpgsqlReadModelStore<TRow> _statements;

    /// <summary>Creates the rows.</summary>
    /// <param name="json">The application's JSON options (the ones <c>UseNpgsqlReadModels</c> reads rows with).</param>
    /// <param name="schema">The read models' schema (default <c>tarol</c>).</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TRow"/> is not in the JSON options' resolver, or is not an SQL projection's row.</exception>
    public NpgsqlRows(JsonSerializerOptions json, string schema = NpgsqlReadModelSchema.DefaultSchema)
    {
        if (!TRow.Sql)
            throw new InvalidOperationException($"{typeof(TRow).Name} is a row projection's row; NpgsqlRows writes the rows of [Projection(…, Sql = true)].");
        _statements = new NpgsqlReadModelStore<TRow>(json, schema);
    }

    /// <summary>The qualified table name (<c>tarol.rm_billing_month_v1</c>), for the projection's own SQL.</summary>
    public string Table => _statements.Table;

    /// <summary>Creates the read table and its indexes (idempotent), and adds a column a newer row shape has.</summary>
    /// <param name="connection">The connection <see cref="ISqlProjection.Prepare"/> got.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    public ValueTask Prepare(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        => _statements.PrepareIn(Npgsql(connection), Npgsql(transaction), cancellationToken);

    /// <summary>
    /// Writes rows as given (insert or replace by tenant and id; no version guard: the projection decides). The version is what a
    /// row query answers as its <c>ETag</c>; the feed position of the event it was computed from serves.
    /// </summary>
    /// <param name="rows">The rows.</param>
    /// <param name="connection">The connection <see cref="ISqlProjection.Apply"/> got.</param>
    /// <param name="transaction">The batch transaction.</param>
    /// <param name="cancellationToken">Cancels the batch.</param>
    /// <returns>The rows written.</returns>
    public ValueTask<int> Write(IReadOnlyList<Versioned<TRow>> rows, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return _statements.WriteIn(rows, Npgsql(connection), Npgsql(transaction), cancellationToken);
    }

    /// <summary>Removes rows by tenant and id.</summary>
    /// <param name="keys">The rows.</param>
    /// <param name="connection">The connection <see cref="ISqlProjection.Apply"/> got.</param>
    /// <param name="transaction">The batch transaction.</param>
    /// <param name="cancellationToken">Cancels the batch.</param>
    /// <returns>The rows removed.</returns>
    public ValueTask<int> Remove(IReadOnlyCollection<RowKey> keys, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return _statements.RemoveIn(keys, Npgsql(connection), Npgsql(transaction), cancellationToken);
    }

    private static NpgsqlConnection Npgsql(DbConnection connection)
        => connection as NpgsqlConnection ?? throw new ArgumentException("An SQL projection on PostgreSQL gets an NpgsqlConnection.", nameof(connection));

    private static NpgsqlTransaction Npgsql(DbTransaction transaction)
        => transaction as NpgsqlTransaction ?? throw new ArgumentException("An SQL projection on PostgreSQL gets an NpgsqlTransaction.", nameof(transaction));
}
