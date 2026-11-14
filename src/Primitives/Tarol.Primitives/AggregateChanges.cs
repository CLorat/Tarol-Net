using Tarol.Results;

namespace Tarol.Primitives;

/// <summary>
/// The hand-off between an aggregate and the store (or a test): check that the uncommitted events may be committed, then
/// acknowledge the commit. The aggregate's invariants run here and nowhere else, so replay never runs them.
/// <code>
/// var ready = AggregateChanges.Prepare(auction);   // invariants
/// if (!ready.IsSuccess) return ready;              // discard the instance
/// await store.Append(auction.Id, auction.UncommittedChanges, expectedVersion, ct);
/// AggregateChanges.Committed(auction);
/// </code>
/// </summary>
public static class AggregateChanges
{
    /// <summary>
    /// Runs the aggregate's invariants (root and every entity) on its current state. On a failure the instance must be
    /// discarded with its uncommitted events; there is no rollback.
    /// </summary>
    /// <typeparam name="TSelf">The aggregate type.</typeparam>
    /// <param name="aggregate">The aggregate.</param>
    /// <returns>Success, or the first failing invariant's failure (unchanged).</returns>
    /// <exception cref="InvalidOperationException">The aggregate is broken (an <c>On</c> handler threw).</exception>
    public static Result Prepare<TSelf>(AggregateRoot<TSelf> aggregate)
        where TSelf : AggregateRoot<TSelf>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return aggregate.Prepare();
    }

    /// <summary>Acknowledges that the uncommitted events were stored durably: clears them. Call it only after the commit.</summary>
    /// <typeparam name="TSelf">The aggregate type.</typeparam>
    /// <param name="aggregate">The aggregate.</param>
    public static void Committed<TSelf>(AggregateRoot<TSelf> aggregate)
        where TSelf : AggregateRoot<TSelf>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        aggregate.Committed();
    }
}
