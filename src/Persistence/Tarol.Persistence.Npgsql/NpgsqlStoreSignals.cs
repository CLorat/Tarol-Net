using Microsoft.Extensions.DependencyInjection;
using Tarol.Options;

namespace Tarol.Persistence.Npgsql;

/// <summary>
/// The snapshot signals of the PostgreSQL store (step 15, O-1): index lookups and small counts, read by <c>Tarol.Operations</c>
/// once per poll, never per scrape (operations.md §2: a round costs 1.2–2.1 ms at any size).
/// </summary>
internal static class NpgsqlStoreSignals
{
    public static IReadOnlyList<OperationsSignal> All { get; } =
    [
        // reltuples: an estimate (−1 before the first ANALYZE, shown as 0); a size, not an alert signal
        new(OperationsSignals.Meter, OperationsSignals.ReactionsBacklog, "{row}", "Outbox rows, estimated (pg_class.reltuples).",
            static (provider, ct) => Scalar(provider, s => $"SELECT greatest(reltuples, 0)::float8 FROM pg_class WHERE oid = '{s}.outbox'::regclass", ct)),
        new(OperationsSignals.Meter, OperationsSignals.ReactionsOldestDue, "s", "Age of the oldest due reaction row; 0 when none is due.",
            static async (provider, ct) => [new(Age(provider, await Store(provider).Outbox.OldestDue(ct).ConfigureAwait(false)))]),
        new(OperationsSignals.Meter, OperationsSignals.ReactionsDead, "{row}", "Dead reaction rows.",
            static (provider, ct) => Scalar(provider, s => $"SELECT count(*)::float8 FROM {s}.outbox_dead", ct)),
        new(OperationsSignals.Meter, OperationsSignals.JobsOverdue, "s", "How late the most overdue job cursor is; 0 when none.",
            static async (provider, ct) => [new(Age(provider, await Store(provider).Jobs.Overdue(ct).ConfigureAwait(false)))]),
        new(OperationsSignals.Meter, OperationsSignals.JobsDead, "{occurrence}", "Dead job occurrences.",
            static (provider, ct) => Scalar(provider, s => $"SELECT count(*)::float8 FROM {s}.jobs_dead", ct)),
    ];

    private static NpgsqlAggregateStore Store(IServiceProvider provider) => provider.GetRequiredService<NpgsqlAggregateStore>();

    private static double Age(IServiceProvider provider, Option<DateTimeOffset> since)
        => since.TryGetValue(out DateTimeOffset at) ? Math.Max(0, (provider.GetRequiredService<TimeProvider>().GetUtcNow() - at).TotalSeconds) : 0;

    private static async Task<IReadOnlyList<SignalValue>> Scalar(IServiceProvider provider, Func<string, string> sql, CancellationToken cancellationToken)
    {
        var store = Store(provider);
        await using var command = store.DataSource.CreateCommand(sql(store.Schema));
        return [new(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is double value ? value : 0)];
    }
}
