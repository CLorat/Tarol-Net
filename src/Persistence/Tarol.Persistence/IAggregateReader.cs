using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence;

/// <summary>
/// Reads aggregates, scoped to the tenant of the <see cref="OperationContext"/>. The change pipeline loads a change's
/// target through it, and special changes (<c>ILoads&lt;T&gt;</c>) and read-only <c>[Read]</c> members use it
/// (docs/architecture/changes.md §3.1).
/// </summary>
public interface IAggregateReader
{
    /// <summary>Loads the aggregate with <paramref name="id"/> in the context's tenant.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="id">The stream id.</param>
    /// <param name="context">The operation; its tenant scopes the read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The aggregate with no uncommitted changes, or <c>None</c> when the stream does not exist in this tenant.</returns>
    ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>;

    /// <summary>Finds the id of the aggregate whose unique <paramref name="lookup"/> has <paramref name="key"/>.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="lookup">The lookup's name, e.g. <c>"lot"</c>.</param>
    /// <param name="key">The key to find.</param>
    /// <param name="context">The operation; its tenant scopes the lookup.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The id, or <c>None</c>.</returns>
    /// <exception cref="InvalidOperationException">No lookup of that name is defined for <typeparamref name="T"/>.</exception>
    ValueTask<Option<Id<T>>> Find<T>(string lookup, string key, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>;
}
