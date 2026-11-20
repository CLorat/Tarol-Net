using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// <para>
/// Operations (step 15, O-1, O-6): every run is one span <c>change &lt;Name&gt;</c> of the <see cref="ActivitySourceName"/>
/// source (a child of the HTTP request, reaction or job), one count of <c>tarol.changes.runs</c> and one
/// <c>tarol.changes.duration</c> (seconds) on the meter <see cref="MeterName"/>, tagged <c>tarol.change</c> (the change's type
/// name) and <c>tarol.outcome</c>: <c>committed</c>, <c>conflict</c>, <c>invalid</c>, <c>denied</c>, <c>not_found</c> or
/// <c>error</c>. Logs: 7001 an error result, 7002 a conflict (Debug), 7003 an exception.
/// </para>
/// </remarks>
public sealed partial class ChangePipeline
{
    /// <summary>The name of the pipeline's <see cref="ActivitySource"/>.</summary>
    public const string ActivitySourceName = "Tarol.Changes";

    /// <summary>The name of the pipeline's meter.</summary>
    public const string MeterName = "Tarol.Changes";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter SharedMeter = new(MeterName);
    private readonly IAggregateStore _store;
    private readonly TimeProvider _time;
    private readonly IChangeAuthorization? _permission;
    private readonly ILogger _log;
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _duration;

    /// <summary>Creates the pipeline.</summary>
    /// <param name="store">The aggregate store.</param>
    /// <param name="time">The clock that supplies <see cref="ChangeContext.Now"/>.</param>
    /// <param name="permission">The resource permission, asked for every run that carries a principal; <see langword="null"/>
    /// refuses those runs (fail closed) and leaves internal runs unchecked.</param>
    /// <param name="logger">Logs failed runs (7001–7003); <see langword="null"/> = none.</param>
    /// <param name="meters">Creates the meter (the host's <see cref="IMeterFactory"/>); <see langword="null"/> = a shared meter.</param>
    public ChangePipeline(IAggregateStore store, TimeProvider time, IChangeAuthorization? permission = null, ILogger<ChangePipeline>? logger = null,
        IMeterFactory? meters = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
        _permission = permission;
        _log = logger ?? (ILogger)NullLogger.Instance;
        var meter = meters?.Create(MeterName) ?? SharedMeter;
        _runs = meter.CreateCounter<long>("tarol.changes.runs", "{change}", "Pipeline runs by change and outcome.");
        _duration = meter.CreateHistogram<double>("tarol.changes.duration", "s", "Duration of a pipeline run, commit included.");
    }

    /// <summary>The outcome tag of a run's result (<c>tarol.outcome</c>).</summary>
    /// <param name="result">The result.</param>
    /// <returns><c>committed</c>, <c>conflict</c>, <c>invalid</c>, <c>denied</c>, <c>not_found</c> or <c>error</c>.</returns>
    internal static string OutcomeOf(Result<ChangeOutcome> result) => !result.TryGetValue(out Violation? violation) ? "committed" : violation switch
    {
        Invalid => "invalid",
        Error { Type: ErrorType.UnprocessableEntity } => "invalid",
        Error { Type: ErrorType.Conflict } => "conflict",
        Error { Type: ErrorType.Forbidden or ErrorType.Unauthorized } => "denied",
        Error { Type: ErrorType.NotFound or ErrorType.Gone } => "not_found",
        _ => "error",
    };

    private async ValueTask<Result<ChangeOutcome>> Observed(string name, Func<ValueTask<Result<ChangeOutcome>>> run, CancellationToken cancellationToken)
    {
        using var activity = Source.HasListeners() ? Source.StartActivity("change " + name, ActivityKind.Internal) : null;
        activity?.SetTag("tarol.change", name);
        var started = Stopwatch.GetTimestamp();
        var outcome = "error";
        try
        {
            var result = await run().ConfigureAwait(false);
            outcome = OutcomeOf(result);
            if (result.TryGetValue(out Violation? violation) && violation is Error error)
            {
                if (outcome == "conflict")
                    Conflict(_log, name, error.Code);
                else if (outcome == "error")
                    Failed(_log, name, error.Type, error.Code, error.Detail);
            }
            if (outcome != "committed")
                activity?.SetStatus(outcome == "error" ? ActivityStatusCode.Error : ActivityStatusCode.Unset);
            return result;
        }
        catch (Exception e)
        {
            outcome = "error";
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            if (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                Threw(_log, name, e);
            throw;
        }
        finally
        {
            var tags = new TagList { { "tarol.change", name }, { "tarol.outcome", outcome } };
            activity?.SetTag("tarol.outcome", outcome);
            _runs.Add(1, tags);
            _duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "Change {Change} failed: {Type} {Code} {Detail}")]
    private static partial void Failed(ILogger logger, string change, ErrorType type, string code, string? detail);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Debug, Message = "Change {Change} lost a race: {Code}")]
    private static partial void Conflict(ILogger logger, string change, string code);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Error, Message = "Change {Change} threw")]
    private static partial void Threw(ILogger logger, string change, Exception exception);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Warning, Message = "Idempotency key {Key} of {Scope} was presented again for another request")]
    private static partial void KeyReused(ILogger logger, string scope, string key);

    [LoggerMessage(EventId = 7005, Level = LogLevel.Warning, Message = "Idempotency key {Key} of {Scope} is held by a concurrent request")]
    private static partial void DuplicateKey(ILogger logger, string scope, string key);

    /// <summary>Runs a change of an existing aggregate.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="change">The input.</param>
    /// <param name="context">The operation: actor, tenant, correlation, causation. Built at the edge, never ambient.</param>
    /// <param name="options">Idempotency key, expected version, principal.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome, or the first failure.</returns>
    public ValueTask<Result<ChangeOutcome>> Run<T>(IChange<T> change, OperationContext context, ChangeOptions options = default,
        CancellationToken cancellationToken = default)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(context);
        return Observed(change.GetType().Name, () => RunChange(change, context, options, cancellationToken), cancellationToken);
    }

    private async ValueTask<Result<ChangeOutcome>> RunChange<T>(IChange<T> change, OperationContext context, ChangeOptions options, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        if (change is IValidatable validatable && Invalidity(validatable) is { } invalid)
            return invalid;                                                                     // 400, before any read
        if (await Seen(options, cancellationToken) is { } replayed)
            return replayed;                                                                    // the first outcome, or KeyReused

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
    public ValueTask<Result<ChangeOutcome>> Run<T>(ICreation<T> creation, OperationContext context, ChangeOptions options = default,
        CancellationToken cancellationToken = default)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(context);
        return Observed(creation.GetType().Name, () => RunCreation(creation, context, options, cancellationToken), cancellationToken);
    }

    private async ValueTask<Result<ChangeOutcome>> RunCreation<T>(ICreation<T> creation, OperationContext context, ChangeOptions options,
        CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
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
        {
            // a concurrent run under the same key (a client retrying after a timeout) may have committed first: the version
            // guard or the key's row refuses this one, and the stored outcome is the answer, not a 409
            if (options.Key.IsSome && conflict is Error { Type: ErrorType.Conflict, Code: StoreErrors.VersionConflictCode or StoreErrors.DuplicateKeyCode }
                && await Seen(options, cancellationToken) is { } replayed)
                return replayed;
            if (conflict is Error { Code: StoreErrors.DuplicateKeyCode } && options.Key.TryGetValue(out IdempotencyKey duplicate))
                DuplicateKey(_log, duplicate.Scope, duplicate.Key);
            return conflict;
        }
        var position = appended.TryGetValue(out GlobalPosition at) ? at : GlobalPosition.Start;
        AggregateChanges.Committed(aggregate);
        return new ChangeOutcome(aggregate.Id.Value, aggregate.Version, events, Position: position);
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

    private async ValueTask<Result<ChangeOutcome>?> Seen(ChangeOptions options, CancellationToken cancellationToken)
    {
        if (!options.Key.TryGetValue(out IdempotencyKey key))
            return null;
        if (!(await _store.Seen(key, cancellationToken)).TryGetValue(out StoredOutcome stored))
            return null;
        if (stored.Fingerprint.TryGetValue(out string? first) && key.Fingerprint.TryGetValue(out string? now) && !string.Equals(first, now, StringComparison.Ordinal))
        {
            KeyReused(_log, key.Scope, key.Key);                                                // the scope stays in the log (BC-17)
            return StoreErrors.KeyReused(key);                                                  // R-9: another request under the key
        }
        return new ChangeOutcome(stored.StreamId, stored.Version, stored.Events, Replayed: true);
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
