using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Validations;

namespace Tarol.Changes;

/// <summary>
/// The one pipeline every entry of a change runs through (docs/architecture/changes.md §2):
/// <c>Validated()</c> → idempotency key seen? → <c>[Read]</c> aggregates → load (automatic by <c>Target</c>, or the change's
/// own <see cref="ILoads{T}.Load"/>) → permission (only when a principal is passed) → expected version → <c>Execute</c> with
/// the time read once → <c>Prepare</c> → append with the expected version and the key → <c>Committed</c>.
/// </summary>
/// <remarks>
/// Any failure (a guard, an invariant, a forbidden lifecycle step, a store conflict) returns the <see cref="Violation"/>
/// and drops the loaded instance; an exception propagates and drops it too. A version conflict is returned, never
/// retried (C-7): the caller decides.
/// <para>
/// The permission runs only when <see cref="ChangeOptions.Principal"/> is set (rest.md §3, R-6): HTTP entries always pass
/// one, internal entries (jobs, reactions, sagas, tests) pass none, because the intent was authorized where it entered. It
/// runs before the expected version is compared, so a caller who may not change the aggregate never learns its version
/// (R-2). A principal without an <see cref="IChangeAuthorization"/> fails closed with <c>Error.Forbidden("Auth.NotConfigured")</c>.
/// </para>
/// </remarks>
public sealed class ChangePipeline
{
    private readonly IAggregateStore _store;
    private readonly TimeProvider _time;
    private readonly IChangeAuthorization? _permission;

    /// <summary>Creates the pipeline.</summary>
    /// <param name="store">The aggregate store.</param>
    /// <param name="time">The clock that supplies <see cref="ChangeContext.Now"/>.</param>
    /// <param name="permission">The resource permission, asked for every run that carries a principal; <see langword="null"/>
    /// refuses those runs (fail closed) and leaves internal runs unchecked.</param>
    public ChangePipeline(IAggregateStore store, TimeProvider time, IChangeAuthorization? permission = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
        _permission = permission;
    }

    /// <summary>Runs a change of an existing aggregate.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="change">The input.</param>
    /// <param name="context">The operation: actor, tenant, correlation, causation. Built at the edge, never ambient.</param>
    /// <param name="options">Idempotency key, expected version, principal.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome, or the first failure.</returns>
    public async ValueTask<Result<ChangeOutcome>> Run<T>(IChange<T> change, OperationContext context, ChangeOptions options = default,
        CancellationToken cancellationToken = default)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(context);
        if (change is IValidatable validatable && Invalidity(validatable) is { } invalid)
            return invalid;                                                                     // 400, before any read
        if (await Seen(options, cancellationToken) is { } replayed)
            return replayed;

        var (missing, loadedReads) = await LoadReads(change, context, cancellationToken);
        if (missing is not null)
            return missing;
        var changeContext = new ChangeContext(context, _time.GetUtcNow(), loadedReads);

        T? aggregate;
        if (change is ILoads<T> loads)
        {
            var own = await loads.Load(_store, changeContext, cancellationToken);
            if (own.TryGetValue(out Violation? refused))
                return refused;
            aggregate = own.GetValueOrThrow();
            ArgumentNullException.ThrowIfNull(aggregate, nameof(ILoads<>.Load));
        }
        else if (!(await _store.Load(change.Target, context, cancellationToken)).TryGetValue(out aggregate))
        {
            return Error.NotFound($"{typeof(T).Name}.NotFound", change.Target.ToString());       // also another tenant's stream
        }

        if (await Permission(change, aggregate, changeContext, options, cancellationToken) is { } denied)
            return denied;                                                                      // before the version: R-2
        var expected = aggregate.Version - aggregate.UncommittedChanges.Length;                // 0 for a load-or-create
        if (options.ExpectedVersion.TryGetValue(out long seenVersion) && seenVersion != expected)
            return StoreErrors.VersionConflict(seenVersion, expected);

        var decided = change.Execute(aggregate, changeContext);
        if (decided.TryGetValue(out Violation? refusedByGuard))
            return refusedByGuard;
        return await Commit(aggregate, expected, context, options, cancellationToken);
    }

    /// <summary>Runs a change that creates an aggregate.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="creation">The input.</param>
    /// <param name="context">The operation.</param>
    /// <param name="options">Idempotency key and principal (an expected version must be 0 or absent).</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome (its <see cref="ChangeOutcome.StreamId"/> is the new id), or the first failure.</returns>
    public async ValueTask<Result<ChangeOutcome>> Run<T>(ICreation<T> creation, OperationContext context, ChangeOptions options = default,
        CancellationToken cancellationToken = default)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(context);
        if (creation is IValidatable validatable && Invalidity(validatable) is { } invalid)
            return invalid;
        if (await Seen(options, cancellationToken) is { } replayed)
            return replayed;

        var (missing, loadedReads) = await LoadReads(creation, context, cancellationToken);
        if (missing is not null)
            return missing;

        var changeContext = new ChangeContext(context, _time.GetUtcNow(), loadedReads);
        var created = creation.Execute(changeContext);
        if (created.TryGetValue(out Violation? refused))
            return refused;
        var aggregate = created.GetValueOrThrow();
        ArgumentNullException.ThrowIfNull(aggregate, nameof(ICreation<>.Execute));
        if (aggregate.Version != aggregate.UncommittedChanges.Length)
            throw new InvalidOperationException($"The factory of {typeof(T).Name} returned a loaded aggregate; a creation returns a new one.");
        if (await Permission(creation, aggregate, changeContext, options, cancellationToken) is { } denied)
            return denied;
        if (options.ExpectedVersion.TryGetValue(out long seenVersion) && seenVersion != 0)
            return StoreErrors.VersionConflict(seenVersion, 0);

        var committed = await Commit(aggregate, 0, context, options, cancellationToken);
        return StoreErrors.IsVersionConflict(committed.ToResult())
            ? Error.Conflict($"{typeof(T).Name}.AlreadyExists", aggregate.Id.ToString())
            : committed;
    }

    private async ValueTask<Result<ChangeOutcome>> Commit<T>(T aggregate, long expected, OperationContext context, ChangeOptions options,
        CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var ready = AggregateChanges.Prepare(aggregate);
        if (ready.TryGetValue(out Violation? broken))
            return broken;                                                                      // invariant / forbidden step
        var events = aggregate.UncommittedChanges.Length;
        if (events == 0)
            return new ChangeOutcome(aggregate.Id.Value, aggregate.Version, 0);
        var appended = await _store.Append(aggregate, expected, context, options.Key, cancellationToken);
        if (appended.TryGetValue(out Violation? conflict))
            return conflict;
        AggregateChanges.Committed(aggregate);
        return new ChangeOutcome(aggregate.Id.Value, aggregate.Version, events);
    }

    private async ValueTask<Violation?> Permission<T>(object change, T aggregate, ChangeContext context, ChangeOptions options,
        CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        if (options.Principal is not { } principal)
            return null;                                                                        // an internal entry (R-6)
        if (_permission is null)
            return Error.Forbidden(NotConfiguredCode, "a principal was passed, but the pipeline has no IChangeAuthorization");
        var allowed = await _permission.Check(change, aggregate, context, principal, cancellationToken);
        return allowed.TryGetValue(out Violation? denied) ? denied : null;
    }

    /// <summary>The code of the refusal when a principal is passed to a pipeline without an <see cref="IChangeAuthorization"/>.</summary>
    public const string NotConfiguredCode = "Auth.NotConfigured";

    private async ValueTask<ChangeOutcome?> Seen(ChangeOptions options, CancellationToken cancellationToken)
    {
        if (!options.Key.TryGetValue(out IdempotencyKey key))
            return null;
        return (await _store.Seen(key, cancellationToken)).TryGetValue(out StoredOutcome stored)
            ? new ChangeOutcome(stored.StreamId, stored.Version, stored.Events, Replayed: true)
            : null;
    }

    private async ValueTask<(Violation? Missing, Reads? Reads)> LoadReads(object change, OperationContext context, CancellationToken cancellationToken)
    {
        if (change is not IHasReads hasReads)
            return (null, null);
        var loaded = await hasReads.LoadReads(_store, context, cancellationToken);
        return loaded.TryGetValue(out Violation? missing) ? (missing, null) : (null, loaded.GetValueOrThrow());
    }

    private static Invalid? Invalidity(IValidatable input)
    {
        var errors = default(ValidationErrors);
        input.Validate(ref errors);
        return errors.ToInvalid();
    }
}
