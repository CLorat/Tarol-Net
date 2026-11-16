using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;

namespace Tarol.Persistence;

/// <summary>
/// The aggregate store port: <see cref="IAggregateReader"/> plus an append with optimistic concurrency and idempotency.
/// <see cref="InMemoryAggregateStore"/> implements it for tests; <c>Tarol.Persistence.Npgsql</c> (step 8) for production.
/// </summary>
/// <remarks>
/// The contract every implementation keeps:
/// <list type="bullet">
/// <item>Tenant isolation: a stream is identified by (tenant, aggregate type, id); another tenant's stream does not exist.</item>
/// <item>The append is atomic: the events, the current state, the lookups and the idempotency key are committed together or not at all.</item>
/// <item>A version mismatch is a result, not an exception: <see cref="StoreErrors.VersionConflict"/>.</item>
/// <item>The append never changes the aggregate; the caller calls <see cref="AggregateChanges.Committed{TSelf}"/> after success.</item>
/// </list>
/// </remarks>
public interface IAggregateStore : IAggregateReader
{
    /// <summary>Appends the aggregate's uncommitted changes if its stream is still at <paramref name="expectedVersion"/>.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="aggregate">The aggregate, after <see cref="AggregateChanges.Prepare{TSelf}"/> succeeded.</param>
    /// <param name="expectedVersion">The stream's version when the aggregate was loaded; 0 = the stream must not exist.</param>
    /// <param name="context">The operation: tenant of the stream, and the actor, correlation and causation recorded per event.</param>
    /// <param name="key">An idempotency key to store with this commit (successes only).</param>
    /// <param name="cancellationToken">Cancels the append.</param>
    /// <returns>
    /// The commit's position in the <see cref="IEventFeed"/> (the position of its last event; <see cref="GlobalPosition.Start"/>
    /// when there was nothing to append), for read-your-writes; <see cref="StoreErrors.VersionConflict"/> when the stream moved on (or exists for an expected version of 0);
    /// <see cref="StoreErrors.DuplicateKey"/> when the key was stored meanwhile; <see cref="StoreErrors.LookupTaken"/> when a
    /// unique lookup key belongs to another aggregate.
    /// </returns>
    /// <exception cref="ArgumentException">The uncommitted changes do not start at <paramref name="expectedVersion"/> + 1.</exception>
    ValueTask<Result<GlobalPosition>> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key,
        CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>;

    /// <summary>The outcome stored with an earlier commit under <paramref name="key"/>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored outcome, or <c>None</c>.</returns>
    ValueTask<Option<StoredOutcome>> Seen(IdempotencyKey key, CancellationToken cancellationToken);
}
