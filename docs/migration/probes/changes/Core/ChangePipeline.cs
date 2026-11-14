using System.Security.Claims;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Validations;

namespace Tarol.Changes;

/// <summary>Per-call options a channel sets.</summary>
public readonly record struct ChangeOptions(
    Option<IdempotencyKey> Key = default,
    Option<long> ExpectedVersion = default,   // If-Match: no retry with a fresh load
    ClaimsPrincipal? Principal = null,
    int Attempts = 3);

/// <summary>
/// The one pipeline every entry runs through:
/// input → Validated() → (key seen?) → load → permission → behaviour(now) → Prepare → append(expected version) → Committed.
/// </summary>
public sealed class ChangePipeline(IAggregateStore store, TimeProvider time, IChangePermission permission)
{
    public ChangePipeline(IAggregateStore store, TimeProvider time) : this(store, time, NoPermissionChecks.Instance) { }

    public async ValueTask<Result<ChangeOutcome>> Run<TAggregate>(IChange<TAggregate> change, OperationContext context,
        ChangeOptions options = default, CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(context);
        if (change is IValidatable validatable && Validate(validatable) is { } invalid)
            return invalid;                                                          // 400, before any read
        if (options.Key.TryGetValue(out IdempotencyKey key) && (await store.Seen(key, cancellationToken)).TryGetValue(out ChangeOutcome seen))
            return seen with { Replayed = true };                                    // redelivery: same answer, no rerun

        var attempts = options.ExpectedVersion.IsSome ? 1 : Math.Max(1, options.Attempts == 0 ? 3 : options.Attempts);
        for (var attempt = 1; ; attempt++)
        {
            if (!(await store.Load(change.Target, context, cancellationToken)).TryGetValue(out TAggregate? aggregate))
                return Error.NotFound($"{typeof(TAggregate).Name}.NotFound");         // also another tenant's stream
            var expected = aggregate.Version;
            if (options.ExpectedVersion.TryGetValue(out long ifMatch) && ifMatch != expected)
                return Error.Conflict("Stream.VersionConflict", $"expected {ifMatch}, found {expected}");

            var allowed = await permission.Check(change, aggregate, context, options.Principal, cancellationToken);
            if (!allowed.IsSuccess)
                return Fail(allowed);

            var now = time.GetUtcNow();
            var decided = change.Execute(aggregate, new ChangeContext(context, now)); // a throw drops the instance with the frame
            if (!decided.IsSuccess)
                return Fail(decided);                                                // discard: the instance goes out of scope
            var ready = AggregateChanges.Prepare(aggregate);
            if (!ready.IsSuccess)
                return Fail(ready);                                                  // invariant / forbidden step: discard
            var events = aggregate.UncommittedChanges.Length;
            if (events == 0)
                return new ChangeOutcome(aggregate.Id.Value, aggregate.Version, 0);  // nothing to commit

            var appended = await store.Append(aggregate, expected, context, options.Key, cancellationToken);
            if (appended.IsSuccess)
            {
                AggregateChanges.Committed(aggregate);
                return new ChangeOutcome(aggregate.Id.Value, aggregate.Version, events);
            }
            if (!IsVersionConflict(appended) || attempt >= attempts)
                return Fail(appended);
            // someone else committed first: drop this instance, load fresh, decide again
        }
    }

    public async ValueTask<Result<ChangeOutcome>> Run<TAggregate>(ICreation<TAggregate> creation, OperationContext context,
        ChangeOptions options = default, CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(context);
        if (creation is IValidatable validatable && Validate(validatable) is { } invalid)
            return invalid;
        if (options.Key.TryGetValue(out IdempotencyKey key) && (await store.Seen(key, cancellationToken)).TryGetValue(out ChangeOutcome seen))
            return seen with { Replayed = true };

        var created = creation.Execute(new ChangeContext(context, time.GetUtcNow()));
        if (created.TryGetValue(out Violation? refused))
            return refused;
        created.TryGetValue(out TAggregate? aggregate);
        if (aggregate!.Id != creation.Target)
            return Error.Internal("Change.TargetMismatch", $"{typeof(TAggregate).Name} factory returned another id");
        var ready = AggregateChanges.Prepare(aggregate);
        if (!ready.IsSuccess)
            return Fail(ready);
        var events = aggregate!.UncommittedChanges.Length;
        var appended = await store.Append(aggregate, 0, context, options.Key, cancellationToken);
        if (!appended.IsSuccess)
            return IsVersionConflict(appended) ? Error.Conflict($"{typeof(TAggregate).Name}.AlreadyExists") : Fail(appended);
        AggregateChanges.Committed(aggregate);
        return new ChangeOutcome(aggregate.Id.Value, aggregate.Version, events);
    }

    private static Invalid? Validate(IValidatable input)
    {
        var errors = default(ValidationErrors);
        input.Validate(ref errors);
        return errors.ToInvalid();
    }

    private static Violation Fail(Result result) => result.TryGetValue(out Violation? violation) ? violation : Error.Uninitialized;

    private static bool IsVersionConflict(Result result)
        => result.TryGetValue(out Violation? violation) && violation is Error { Type: ErrorType.Conflict, Code: "Stream.VersionConflict" };
}
