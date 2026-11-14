using Tarol.Kernel;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes;

/// <summary>What a change gets besides its aggregate: the operation's context and the time the pipeline read once.</summary>
public readonly record struct ChangeContext(OperationContext Operation, DateTimeOffset Now);

/// <summary>A change on an existing aggregate: the input record names its target and calls one behaviour method.</summary>
public interface IChange<TAggregate>
    where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
{
    /// <summary>The aggregate this change targets (generated when the record has exactly one <c>Id&lt;TAggregate&gt;</c> member).</summary>
    Id<TAggregate> Target { get; }

    /// <summary>Calls the behaviour method. Returns its <see cref="Result"/>; never touches the store.</summary>
    Result Execute(TAggregate aggregate, ChangeContext context);
}

/// <summary>A change that creates an aggregate through its static factory.</summary>
public interface ICreation<TAggregate>
    where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
{
    /// <summary>The id of the new stream (chosen by the caller, so a retried creation is idempotent).</summary>
    Id<TAggregate> Target { get; }

    /// <summary>Calls the static factory.</summary>
    Result<TAggregate> Execute(ChangeContext context);
}

/// <summary>What a successful change reports: the stream, its new version and how many events were committed.</summary>
public readonly record struct ChangeOutcome(Guid StreamId, long Version, int Events, bool Replayed = false);
