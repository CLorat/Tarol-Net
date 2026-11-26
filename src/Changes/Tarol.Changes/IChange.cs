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

/// <summary>What <see cref="ILoads{T, TData}.Load"/> returns: the aggregate to change and the data the decision needs.</summary>
/// <typeparam name="T">The aggregate.</typeparam>
/// <typeparam name="TData">The data (the actor's role, facts of other aggregates, a rule profile).</typeparam>
/// <param name="Aggregate">The aggregate to change (loaded, or new for a load-or-create).</param>
/// <param name="Data">The data, passed to <see cref="ILoads{T, TData}.Execute(T, TData, ChangeContext)"/>.</param>
public readonly record struct Loaded<T, TData>(T Aggregate, TData Data);

/// <summary>
/// A change whose <c>Load</c> also returns data for the decision (TN-41, owner decision CR-2; Tatalu FB-F13.3-6, FB-F05.1-1,
/// FB-F06.3-3, FB-F06.4-4): the actor's domain role, facts read from other aggregates, a rule profile. No private field carries
/// them from <c>Load</c> to <c>Execute</c> any more. <c>Load</c> may read anything through the reader; <c>Execute</c> stays pure.
/// </summary>
/// <example>
/// <code>
/// public sealed partial record CheckIn(Guid Child, DateOnly Day) : ILoads&lt;AttendanceMonth, EntryRole&gt;
/// {
///     public async ValueTask&lt;Result&lt;Loaded&lt;AttendanceMonth, EntryRole&gt;&gt;&gt; Load(IAggregateReader reader, ChangeContext context, CancellationToken ct)
///         => new Loaded&lt;AttendanceMonth, EntryRole&gt;(await Month(reader, context, ct), await EntryRoles.Of(reader, context, ct));
///
///     public Result Execute(AttendanceMonth month, EntryRole role, ChangeContext context) => month.CheckIn(Child, role, context.Now);
/// }
/// </code>
/// </example>
/// <typeparam name="T">The aggregate.</typeparam>
/// <typeparam name="TData">The data.</typeparam>
public interface ILoads<T, TData> : ILoads<T>, ILoadsWithData<T>
    where T : AggregateRoot<T>, IAggregateRoot<T>
{
    /// <summary>Loads (or starts) the aggregate and reads the data the decision needs.</summary>
    /// <param name="reader">Loads aggregates of the operation's tenant.</param>
    /// <param name="context">The operation, time and reads.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The aggregate and the data, or a refusal (e.g. <c>X.NotFound</c>).</returns>
    new ValueTask<Result<Loaded<T, TData>>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken);

    /// <summary>Decides: calls one behaviour of <paramref name="aggregate"/> with <paramref name="data"/>.</summary>
    /// <param name="aggregate">The loaded aggregate.</param>
    /// <param name="data">What <see cref="Load"/> read.</param>
    /// <param name="context">The operation, time and reads.</param>
    /// <returns>Success or the refusal.</returns>
    Result Execute(T aggregate, TData data, ChangeContext context);

    async ValueTask<Result<T>> ILoads<T>.Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
    {
        var loaded = await Load(reader, context, cancellationToken).ConfigureAwait(false);
        return loaded.TryGetValue(out Violation? refused) ? refused : loaded.GetValueOrThrow().Aggregate;
    }

    Result IChange<T>.Execute(T aggregate, ChangeContext context)
        => throw new InvalidOperationException($"{GetType().Name} decides with data: run it through ChangePipeline, which calls Execute(aggregate, data, context).");

    async ValueTask<Result<Loaded<T, object?>>> ILoadsWithData<T>.LoadWithData(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
    {
        var loaded = await Load(reader, context, cancellationToken).ConfigureAwait(false);
        if (loaded.TryGetValue(out Violation? refused))
            return refused;
        var (aggregate, data) = loaded.GetValueOrThrow();
        return new Loaded<T, object?>(aggregate, data);
    }

    Result ILoadsWithData<T>.ExecuteWithData(T aggregate, object? data, ChangeContext context) => Execute(aggregate, (TData)data!, context);
}

/// <summary>The pipeline's view of an <see cref="ILoads{T, TData}"/> without its data type (implemented by the interface itself).</summary>
/// <typeparam name="T">The aggregate.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public interface ILoadsWithData<T>
    where T : AggregateRoot<T>, IAggregateRoot<T>
{
    /// <summary>Loads the aggregate and the boxed data.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="context">The context.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The aggregate and the data.</returns>
    ValueTask<Result<Loaded<T, object?>>> LoadWithData(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken);

    /// <summary>Decides with the boxed data.</summary>
    /// <param name="aggregate">The aggregate.</param>
    /// <param name="data">The data.</param>
    /// <param name="context">The context.</param>
    /// <returns>Success or the refusal.</returns>
    Result ExecuteWithData(T aggregate, object? data, ChangeContext context);
}

/// <summary>
/// An aggregate that a change may start when it does not exist yet (TN-42, Tatalu FB-F02.5-2, FB-F15.5-2: "load or start"
/// repeated in every change): the first change of an <see cref="IOrStart{T}"/> kind starts it with <see cref="Start"/>, and a
/// view of it before its first event shows the started state.
/// </summary>
/// <typeparam name="T">The aggregate itself.</typeparam>
public interface IStartable<T>
    where T : AggregateRoot<T>, IAggregateRoot<T>, IStartable<T>
{
    /// <summary>Starts the aggregate under <paramref name="id"/> (raises its first event; pure like a factory).</summary>
    /// <param name="id">The id the change targets (for a <c>[PerTenant]</c> aggregate the tenant's).</param>
    /// <param name="context">The operation and time.</param>
    /// <returns>The new aggregate with its uncommitted first event.</returns>
    static abstract T Start(Id<T> id, ChangeContext context);
}

/// <summary>
/// On a change: when its target does not exist, the pipeline starts it with <see cref="IStartable{T}.Start"/> instead of
/// answering <c>T.NotFound</c>, then runs the change on it (TN-42). The outcome is <see cref="ChangeOutcome.Created"/>, HTTP 201.
/// A concurrent start is a version conflict, which the pipeline retries.
/// </summary>
/// <typeparam name="T">The startable target.</typeparam>
public interface IOrStart<T> : IStartsTarget<T>
    where T : AggregateRoot<T>, IAggregateRoot<T>, IStartable<T>
{
    T IStartsTarget<T>.StartTarget(Id<T> id, ChangeContext context) => T.Start(id, context);
}

/// <summary>The pipeline's view of an <see cref="IOrStart{T}"/> (implemented by that interface).</summary>
/// <typeparam name="T">The target.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public interface IStartsTarget<T>
    where T : AggregateRoot<T>, IAggregateRoot<T>
{
    /// <summary>Starts the target.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="context">The context.</param>
    /// <returns>The started aggregate.</returns>
    T StartTarget(Id<T> id, ChangeContext context);
}
