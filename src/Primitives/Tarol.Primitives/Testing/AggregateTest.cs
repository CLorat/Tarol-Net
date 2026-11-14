using Tarol.Results;

namespace Tarol.Primitives;

/// <summary>
/// Given/when/then for behaviour tests, without a host, a store or a clock:
/// <code>
/// var outcome = AggregateTest.Given(Gallery.Open("Tate"))
///     .And(g => g.Tag("modern"))
///     .When(g => g.Tag("modern"));
/// outcome.Result.ShouldBe(Error.Conflict("Gallery.AlreadyTagged"));
/// outcome.Events.ShouldBeEmpty();
/// </code>
/// "Given" is built by running behaviours, so the test never writes routing data by hand; every given step is committed
/// and replayed through <c>Rehydrate</c>, so a test also proves that replay rebuilds what the behaviours built.
/// </summary>
public static class AggregateTest
{
    /// <summary>Starts a test from an aggregate built by behaviours (for example its factory).</summary>
    /// <typeparam name="TSelf">The aggregate type.</typeparam>
    /// <param name="aggregate">The aggregate; its uncommitted events become the given history.</param>
    /// <returns>The test.</returns>
    /// <exception cref="InvalidOperationException">An invariant fails on the given state.</exception>
    public static AggregateTest<TSelf> Given<TSelf>(TSelf aggregate)
        where TSelf : AggregateRoot<TSelf>, IAggregateRoot<TSelf>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return new AggregateTest<TSelf>(aggregate);
    }
}

/// <summary>A behaviour test in progress: the given history and the aggregate rebuilt from it.</summary>
/// <typeparam name="TSelf">The aggregate type.</typeparam>
public sealed class AggregateTest<TSelf>
    where TSelf : AggregateRoot<TSelf>, IAggregateRoot<TSelf>
{
    private readonly List<StreamEvent> _history = [];

    internal AggregateTest(TSelf aggregate)
    {
        Aggregate = aggregate;
        Commit("Given");
    }

    /// <summary>The aggregate, rebuilt by replay from the given history; it has no uncommitted events.</summary>
    public TSelf Aggregate { get; private set; }

    /// <summary>The given history so far.</summary>
    public IReadOnlyList<StreamEvent> History => _history;

    /// <summary>Adds a given step: runs a behaviour that must succeed, then commits and replays.</summary>
    /// <param name="behaviour">The behaviour.</param>
    /// <returns>This test.</returns>
    /// <exception cref="InvalidOperationException">The behaviour or an invariant fails.</exception>
    public AggregateTest<TSelf> And(Func<TSelf, Result> behaviour)
    {
        ArgumentNullException.ThrowIfNull(behaviour);
        var result = behaviour(Aggregate);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"A given step failed: {result}.");
        Commit("A given step");
        return this;
    }

    /// <summary>Runs the behaviour under test, then the invariants (as the store would).</summary>
    /// <param name="behaviour">The behaviour.</param>
    /// <returns>The outcome: the result and the raised events.</returns>
    public AggregateOutcome<TSelf> When(Func<TSelf, Result> behaviour)
    {
        ArgumentNullException.ThrowIfNull(behaviour);
        var result = behaviour(Aggregate);
        return Outcome(result);
    }

    /// <summary>Runs a behaviour that returns a value (for example a created child), then the invariants.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="behaviour">The behaviour.</param>
    /// <returns>The outcome; <see cref="AggregateOutcome{TSelf}.Result"/> is the behaviour's result without its value.</returns>
    public AggregateOutcome<TSelf> When<T>(Func<TSelf, Result<T>> behaviour)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(behaviour);
        return Outcome(behaviour(Aggregate).ToResult());
    }

    private AggregateOutcome<TSelf> Outcome(Result result)
    {
        if (result.IsSuccess)
            result = AggregateChanges.Prepare(Aggregate);
        var raised = Aggregate.UncommittedChanges.ToArray();
        DomainEvent[] events = result.IsSuccess ? [.. raised.Select(static e => e.Event)] : [];
        return new AggregateOutcome<TSelf>(result, events, Aggregate, [.. _history, .. result.IsSuccess ? raised : []]);
    }

    private void Commit(string step)
    {
        var ready = AggregateChanges.Prepare(Aggregate);
        if (!ready.IsSuccess)
            throw new InvalidOperationException($"{step} breaks an invariant: {ready}.");
        _history.AddRange(Aggregate.UncommittedChanges);
        Aggregate = TSelf.Rehydrate(Aggregate.Id, [.. _history]);
    }
}

/// <summary>What a behaviour did: its result (or the failing invariant) and the events it raised.</summary>
/// <typeparam name="TSelf">The aggregate type.</typeparam>
/// <param name="Result">The behaviour's result; when it succeeded, the invariants' result.</param>
/// <param name="Events">The raised event payloads, in order; empty on a failure (the instance would be discarded).</param>
/// <param name="Aggregate">The live aggregate after the behaviour.</param>
/// <param name="History">The given history plus, on success, the raised events.</param>
public sealed record AggregateOutcome<TSelf>(Result Result, IReadOnlyList<DomainEvent> Events, TSelf Aggregate, IReadOnlyList<StreamEvent> History)
    where TSelf : AggregateRoot<TSelf>, IAggregateRoot<TSelf>
{
    /// <summary>Rebuilds the aggregate by replaying <see cref="History"/>, to compare replayed state with <see cref="Aggregate"/>.</summary>
    /// <returns>The replayed aggregate.</returns>
    public TSelf Replay() => TSelf.Rehydrate(Aggregate.Id, [.. History]);
}
