using Tarol.Kernel;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes;

/// <summary>What a change gets besides its aggregate: the operation's context, the time the pipeline read once, and the
/// read-only aggregates its <c>[Read]</c> members named (C-16).</summary>
public readonly record struct ChangeContext(OperationContext Operation, DateTimeOffset Now, Reads? Reads = null)
{
    /// <summary>The single read-only aggregate of type <typeparamref name="T"/> the pipeline loaded for a <c>[Read]</c> member.</summary>
    public T Read<T>() where T : class => (Reads ?? throw new InvalidOperationException("This change declares no [Read] members.")).Get<T>();
}

/// <summary>The read-only aggregates loaded for a change's <c>[Read]</c> members, by member name.</summary>
public sealed class Reads(IReadOnlyDictionary<string, object> byMember)
{
    public T Get<T>() where T : class
        => byMember.Values.OfType<T>().ToArray() is [var one] ? one
         : throw new InvalidOperationException($"Expected exactly one [Read] {typeof(T).Name}; use Get<T>(member).");

    public T Get<T>(string member) where T : class => (T)byMember[member];
}

/// <summary>On an <c>Id&lt;X&gt;</c> member of a change: load that aggregate read-only before <c>Execute</c> (C-16).
/// Missing → <c>Error.UnprocessableEntity("X.NotFound")</c> (422: the input refers to something that does not exist).</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, Inherited = false)]
public sealed class ReadAttribute : Attribute;

/// <summary>Generated for changes with <c>[Read]</c> members.</summary>
public interface IHasReads
{
    ValueTask<Result<Reads>> LoadReads(IAggregateReader reader, OperationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The escape hatch (C-16): a special change loads its target itself (another key, conditional, load-or-create).
/// The pipeline still validates first, checks the permission, runs <c>Execute</c>, <c>Prepare</c>, appends and discards.
/// </summary>
public interface ILoads<TAggregate> : IChange<TAggregate>
    where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
{
    Id<TAggregate> IChange<TAggregate>.Target => Id<TAggregate>.Empty;   // not used: Load decides

    /// <summary>Returns the target (loaded, or new with uncommitted events), or the failure to report.</summary>
    ValueTask<Result<TAggregate>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken);
}

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
