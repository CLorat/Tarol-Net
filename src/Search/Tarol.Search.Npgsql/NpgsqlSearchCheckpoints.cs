using System.Globalization;
using Npgsql;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Queries.Npgsql;

namespace Tarol.Search.Npgsql;

/// <summary>
/// The checkpoints of an engine's search index in PostgreSQL: the rows (<c>search</c>, n) of <c>&lt;schema&gt;.projections</c>,
/// where the lag gauge, the gate and <c>DropVersion</c> see them (GS-4, GS-11). Register with <c>UseNpgsqlSearchCheckpoints</c>.
/// </summary>
public sealed class NpgsqlSearchCheckpoints : ISearchCheckpoints
{
    private readonly NpgsqlDataSource _db;

    /// <summary>Creates the checkpoints.</summary>
    /// <param name="dataSource">The data source.</param>
    /// <param name="schema">The schema of <c>projections</c>.</param>
    public NpgsqlSearchCheckpoints(NpgsqlDataSource dataSource, string schema = NpgsqlReadModelSchema.DefaultSchema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        NpgsqlSearchSchema.Validate(schema);
        _db = dataSource;
        Schema = schema;
    }

    /// <summary>The schema.</summary>
    public string Schema { get; }

    /// <inheritdoc />
    public async ValueTask Prepare(int version, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(NpgsqlReadModelSchema.ProjectionsTable(Schema)
            + $"INSERT INTO {Schema}.projections (name, version) VALUES ('{SearchIndexDefinition.Name}', {version.ToString(CultureInfo.InvariantCulture)}) ON CONFLICT DO NOTHING;");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint> Checkpoint(int version, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand($"SELECT tx::text, pos, state FROM {Schema}.projections WHERE name = '{SearchIndexDefinition.Name}' AND version = $1");
        command.Parameters.Add(new() { Value = version });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader, 0) : default;
    }

    /// <inheritdoc />
    public async ValueTask<bool> Move(int version, GlobalPosition from, GlobalPosition through, ProjectionState state, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand($"""
            UPDATE {Schema}.projections SET tx = $4::text::xid8, pos = $5, state = $6, updated_at = now()
            WHERE name = '{SearchIndexDefinition.Name}' AND version = $1 AND tx = $2::text::xid8 AND pos = $3
            """);
        command.Parameters.Add(new() { Value = version });
        command.Parameters.Add(new() { Value = from.Transaction.ToString(CultureInfo.InvariantCulture) });
        command.Parameters.Add(new() { Value = from.Position });
        command.Parameters.Add(new() { Value = through.Transaction.ToString(CultureInfo.InvariantCulture) });
        command.Parameters.Add(new() { Value = through.Position });
        command.Parameters.Add(new() { Value = state == ProjectionState.Current ? "current" : "building" });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async ValueTask<(int Version, ProjectionCheckpoint Checkpoint)?> NewestCurrent(int version, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand($"""
            SELECT tx::text, pos, state, version FROM {Schema}.projections
            WHERE name = '{SearchIndexDefinition.Name}' AND version <= $1 AND state = 'current' ORDER BY version DESC LIMIT 1
            """);
        command.Parameters.Add(new() { Value = version });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (reader.GetInt32(3), Read(reader, 0)) : null;
    }

    private static ProjectionCheckpoint Read(NpgsqlDataReader reader, int at)
        => new(new GlobalPosition(ulong.Parse(reader.GetString(at), CultureInfo.InvariantCulture), reader.GetInt64(at + 1)),
            reader.GetString(at + 2) == "current" ? ProjectionState.Current : ProjectionState.Building);
}
