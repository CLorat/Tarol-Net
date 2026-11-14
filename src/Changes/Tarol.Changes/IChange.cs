using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes;

/// <summary>
/// A change of an existing aggregate: the input record names its target and calls one behaviour method of it.
/// The pipeline loads the target (by <see cref="Target"/>), so the change never loads anything itself.
/// </summary>
/// <typeparam name="T">The aggregate the change targets.</typeparam>
/// <example>
/// <code>
/// public sealed partial record CloseAuction(Id&lt;Auction&gt; Auction) : IChange&lt;Auction&gt;, IValidatable
/// {
///     public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
/// }
/// </code>
/// </example>
public interface IChange<T>
    where T : AggregateRoot<T>, IAggregateRoot<T>
{
    /// <summary>The aggregate to load. Generated when the record has exactly one <c>Id&lt;T&gt;</c> member (TCHG001 otherwise).</summary>
    Id<T> Target { get; }

    /// <summary>Calls one behaviour method on the loaded aggregate and returns its result. Never touches the store.</summary>
    /// <param name="aggregate">The loaded target.</param>
    /// <param name="context">The operation, the time read once, and the <c>[Read]</c> aggregates.</param>
    /// <returns>The behaviour's result; a failure discards the aggregate.</returns>
    Result Execute(T aggregate, ChangeContext context);
}

/// <summary>A change that creates an aggregate through its static factory (the factory chooses the id, C-10).</summary>
/// <typeparam name="T">The aggregate the change creates.</typeparam>
public interface ICreation<T>
    where T : AggregateRoot<T>, IAggregateRoot<T>
{
    /// <summary>Calls the static factory.</summary>
    /// <param name="context">The operation, the time read once, and the <c>[Read]</c> aggregates.</param>
    /// <returns>The new aggregate with its uncommitted events, or why it cannot be created.</returns>
    Result<T> Execute(ChangeContext context);
}

/// <summary>
/// A special change that loads its target itself: by another key (<see cref="IAggregateReader.Find{T}"/>),
/// conditionally, or load-or-create. Everything else stays the pipeline's: validation first, the permission,
/// <see cref="IChange{T}.Execute"/>, the invariants, the append and the discard.
/// </summary>
/// <typeparam name="T">The aggregate the change targets.</typeparam>
public interface ILoads<T> : IChange<T>
    where T : AggregateRoot<T>, IAggregateRoot<T>
{
    /// <inheritdoc />
    Id<T> IChange<T>.Target => Id<T>.Empty;

    /// <summary>Returns the target: loaded, or new with uncommitted events (appended with expected version 0).</summary>
    /// <param name="reader">The store, tenant-scoped through <paramref name="context"/>.</param>
    /// <param name="context">The operation and the time read once.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The target, or the failure to report (usually <c>Error.NotFound</c>).</returns>
    ValueTask<Result<T>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken);
}
