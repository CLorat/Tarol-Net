using Microsoft.Extensions.DependencyInjection;

namespace Tarol.Persistence;

/// <summary>The snapshot signals of the in-memory store (step 15): the same names as the PostgreSQL store's, read from memory.</summary>
internal static class InMemorySignals
{
    public static IReadOnlyList<OperationsSignal> All { get; } =
    [
        new(OperationsSignals.Meter, OperationsSignals.ReactionsBacklog, "{row}", "Outbox rows (claimed or not).",
            static async (provider, ct) => [new(await Store(provider).Outbox.Pending(ct).ConfigureAwait(false))]),
        new(OperationsSignals.Meter, OperationsSignals.ReactionsOldestDue, "s", "Age of the oldest due reaction row; 0 when none is due.",
            static async (provider, ct) => [new(Age(provider, await Store(provider).Outbox.OldestDue(ct).ConfigureAwait(false)))]),
        new(OperationsSignals.Meter, OperationsSignals.ReactionsDead, "{row}", "Dead reaction rows.",
            static (provider, _) => Task.FromResult<IReadOnlyList<SignalValue>>([new(((InMemoryOutbox)Store(provider).Outbox).DeadCount)])),
        new(OperationsSignals.Meter, OperationsSignals.JobsOverdue, "s", "How late the most overdue job cursor is; 0 when none.",
            static async (provider, ct) => [new(Age(provider, await Store(provider).Jobs.Overdue(ct).ConfigureAwait(false)))]),
        new(OperationsSignals.Meter, OperationsSignals.JobsDead, "{occurrence}", "Dead job occurrences.",
            static (provider, _) => Task.FromResult<IReadOnlyList<SignalValue>>([new(((InMemoryJobRows)Store(provider).Jobs).DeadCount)])),
    ];

    private static InMemoryAggregateStore Store(IServiceProvider provider) => provider.GetRequiredService<InMemoryAggregateStore>();

    private static double Age(IServiceProvider provider, Options.Option<DateTimeOffset> since)
        => since.TryGetValue(out DateTimeOffset at) ? Math.Max(0, (provider.GetRequiredService<TimeProvider>().GetUtcNow() - at).TotalSeconds) : 0;
}

/// <summary>An outbox without its own source: pages and acts through the port, unaudited (the caller logs).</summary>
internal sealed class OutboxDeadLetterSource(IOutbox outbox) : IDeadLetterSource
{
    public string Kind => DeadLetterKinds.Reactions;

    public async Task<DeadLetterPage> List(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = await outbox.DeadLetters(query, cancellationToken).ConfigureAwait(false);
        return new DeadLetterPage([.. rows.Select(static d => d.ToItem())], rows.Count == query.Limit ? rows[^1].Id : null);
    }

    public Task<bool> Redrive(long id, string actor, CancellationToken cancellationToken) => outbox.Redrive(id, cancellationToken);

    public Task<bool> Discard(long id, string actor, CancellationToken cancellationToken) => outbox.Discard(id, cancellationToken);
}

/// <summary>Job rows without their own source: pages and acts through the port, unaudited (the caller logs).</summary>
internal sealed class JobDeadLetterSource(IJobRows jobs) : IDeadLetterSource
{
    public string Kind => DeadLetterKinds.Jobs;

    public async Task<DeadLetterPage> List(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = await jobs.DeadJobs(query, cancellationToken).ConfigureAwait(false);
        return new DeadLetterPage([.. rows.Select(static d => d.ToItem())], rows.Count == query.Limit ? rows[^1].Id : null);
    }

    public Task<bool> Redrive(long id, string actor, CancellationToken cancellationToken) => jobs.Redrive(id, cancellationToken);

    public Task<bool> Discard(long id, string actor, CancellationToken cancellationToken) => jobs.Discard(id, cancellationToken);
}
