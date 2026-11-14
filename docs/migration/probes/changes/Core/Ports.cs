using System.Security.Claims;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes;

/// <summary>The read side of the store port, which special changes (<see cref="ILoads{TAggregate}"/>) and <c>[Read]</c> use.</summary>
public interface IAggregateReader
{
    ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>;

    /// <summary>The id of the <typeparamref name="T"/> with <paramref name="key"/> in a unique lookup (step 8: a column or an index on the stored state).</summary>
    ValueTask<Option<Id<T>>> Find<T>(string lookup, string key, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>;
}

/// <summary>The store port (step 8 implements it with Npgsql). Tenant-scoped: another tenant's stream is <c>None</c>.</summary>
public interface IAggregateStore : IAggregateReader
{

    /// <summary>
    /// Appends <see cref="AggregateRoot{TSelf}.UncommittedChanges"/> if the stream is still at <paramref name="expectedVersion"/>
    /// (0 = must not exist). With an idempotency key, the outcome is stored in the same transaction.
    /// Returns <c>Error.Conflict("Stream.VersionConflict")</c> when someone else committed first.
    /// </summary>
    ValueTask<Result> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key,
        CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>;

    /// <summary>The stored outcome of an earlier change with this key, if any.</summary>
    ValueTask<Option<ChangeOutcome>> Seen(IdempotencyKey key, CancellationToken cancellationToken);
}

/// <summary>Scope + key: (tenant, change type, key supplied by the channel).</summary>
public readonly record struct IdempotencyKey(Option<Id<ITenant>> Tenant, string Change, string Key);

/// <summary>Step 10's hook: the resource permission after load. Tarol.Security implements it with IAuthorizationService.</summary>
public interface IChangePermission
{
    ValueTask<Result> Check<TAggregate>(object change, TAggregate aggregate, OperationContext context,
        ClaimsPrincipal? principal, CancellationToken cancellationToken)
        where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>;
}

/// <summary>Step 7 default: internal callers only, nothing to check.</summary>
public sealed class NoPermissionChecks : IChangePermission
{
    public static NoPermissionChecks Instance { get; } = new();

    public ValueTask<Result> Check<TAggregate>(object change, TAggregate aggregate, OperationContext context,
        ClaimsPrincipal? principal, CancellationToken cancellationToken)
        where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
        => ValueTask.FromResult(Result.Success());
}
