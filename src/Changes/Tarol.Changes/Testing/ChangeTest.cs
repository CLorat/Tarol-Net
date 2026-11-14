using System.Text.Json;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes;

/// <summary>
/// Runs changes through the real <see cref="ChangePipeline"/> on an <see cref="InMemoryAggregateStore"/> with a clock that
/// stands still until moved: given aggregates, at a time, as an actor, when a change runs, then its result, the events it
/// committed and their recorded context.
/// </summary>
/// <example>
/// <code>
/// var test = ChangeTest.At(endsAt.AddDays(1)).Given(auction);
/// var then = await test.When(new SettleAuction(auction.Id), Actor.System("Jobs.AuctionSettlement"));
/// then.Result.IsSuccess.ShouldBeTrue();
/// then.Events.ShouldContain(e => e is Bid.Won);
/// then.Recorded[0].Context.Actor.Kind.ShouldBe(ActorKind.System);
/// </code>
/// </example>
public sealed class ChangeTest
{
    private readonly TestClock _clock;
    private Actor _actor = Actor.User("test-user");
    private Option<Id<ITenant>> _tenant;

    private ChangeTest(DateTimeOffset now, JsonSerializerOptions? stateOptions, IChangeAuthorization? authorization)
    {
        _clock = new TestClock(now);
        Store = new InMemoryAggregateStore(_clock, stateOptions);
        Pipeline = new ChangePipeline(Store, _clock, authorization);
    }

    /// <summary>The store the changes run against (inspect <see cref="InMemoryAggregateStore.Recorded"/>, define lookups).</summary>
    public InMemoryAggregateStore Store { get; }

    /// <summary>The pipeline.</summary>
    public ChangePipeline Pipeline { get; }

    /// <summary>The clock (stands still; move it with <see cref="At(DateTimeOffset)"/> or <see cref="Later"/>).</summary>
    public TimeProvider Clock => _clock;

    /// <summary>A test whose clock reads <paramref name="now"/>.</summary>
    /// <param name="now">The time the pipeline passes as <see cref="ChangeContext.Now"/>.</param>
    /// <param name="stateOptions">JSON options for the stored state (so loads read it like production); <see langword="null"/> = replay.</param>
    /// <param name="authorization">A resource permission to run (step 10); <see langword="null"/> = none.</param>
    /// <returns>The test.</returns>
    public static ChangeTest At(DateTimeOffset now, JsonSerializerOptions? stateOptions = null, IChangeAuthorization? authorization = null)
        => new(now, stateOptions, authorization);

    /// <summary>Commits <paramref name="aggregate"/>'s uncommitted events (e.g. fresh from its factory) as history.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="aggregate">The aggregate.</param>
    /// <returns>This test.</returns>
    /// <exception cref="InvalidOperationException">Its invariants fail, or the store refuses it.</exception>
    public ChangeTest Given<T>(T aggregate)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (AggregateChanges.Prepare(aggregate).TryGetValue(out Violation? broken))
            throw new InvalidOperationException($"The given {typeof(T).Name} breaks an invariant: {broken}.");
        var expected = aggregate.Version - aggregate.UncommittedChanges.Length;
        var appended = Store.Append(aggregate, expected, Context(), Option.None, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (appended.TryGetValue(out Violation? refused))
            throw new InvalidOperationException($"The store refused the given {typeof(T).Name}: {refused}.");
        AggregateChanges.Committed(aggregate);
        return this;
    }

    /// <summary>Moves the clock to <paramref name="now"/>.</summary>
    /// <param name="now">The new time.</param>
    /// <returns>This test.</returns>
    public ChangeTest At(DateTimeOffset now)
    {
        _clock.Now = now;
        return this;
    }

    /// <summary>Moves the clock forward.</summary>
    /// <param name="delta">How far.</param>
    /// <returns>This test.</returns>
    public ChangeTest Later(TimeSpan delta)
    {
        _clock.Now += delta;
        return this;
    }

    /// <summary>The actor of the following changes (default <c>Actor.User("test-user")</c>).</summary>
    /// <param name="actor">The actor.</param>
    /// <returns>This test.</returns>
    public ChangeTest As(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        _actor = actor;
        return this;
    }

    /// <summary>The tenant of the given aggregates and the following changes (default none).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>This test.</returns>
    public ChangeTest InTenant(Option<Id<ITenant>> tenant)
    {
        _tenant = tenant;
        return this;
    }

    /// <summary>Runs a change; <paramref name="actor"/> overrides the test's actor for this run.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="change">The change.</param>
    /// <param name="actor">The actor, or <see langword="null"/> for the test's.</param>
    /// <param name="options">Key, expected version, principal.</param>
    /// <returns>What happened.</returns>
    public async Task<ChangeTestOutcome> When<T>(IChange<T> change, Actor? actor = null, ChangeOptions options = default)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var before = Store.Recorded.Count;
        var result = await Pipeline.Run(change, Context(actor), options, CancellationToken.None);
        return Outcome(result, before);
    }

    /// <summary>Runs a creation; <paramref name="actor"/> overrides the test's actor for this run.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="creation">The creation.</param>
    /// <param name="actor">The actor, or <see langword="null"/> for the test's.</param>
    /// <param name="options">Key and principal.</param>
    /// <returns>What happened.</returns>
    public async Task<ChangeTestOutcome> When<T>(ICreation<T> creation, Actor? actor = null, ChangeOptions options = default)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var before = Store.Recorded.Count;
        var result = await Pipeline.Run(creation, Context(actor), options, CancellationToken.None);
        return Outcome(result, before);
    }

    /// <summary>Loads an aggregate as it is stored now.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="id">Its id.</param>
    /// <returns>The aggregate.</returns>
    /// <exception cref="InvalidOperationException">It does not exist in the test's tenant.</exception>
    public T Load<T>(Id<T> id)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Store.Load(id, Context(), CancellationToken.None).AsTask().GetAwaiter().GetResult().TryGetValue(out T? loaded)
            ? loaded
            : throw new InvalidOperationException($"No {typeof(T).Name} {id} in the test's tenant.");

    /// <summary>The operation context the next change runs with.</summary>
    /// <param name="actor">The actor, or <see langword="null"/> for the test's.</param>
    /// <returns>A new context (new correlation id) at the test's clock.</returns>
    public OperationContext Context(Actor? actor = null) => OperationContext.Start(actor ?? _actor, _tenant, _clock);

    private ChangeTestOutcome Outcome(Result<ChangeOutcome> result, int before)
    {
        var recorded = Store.Recorded.Skip(before).ToArray();
        return new ChangeTestOutcome(result, [.. recorded.Select(static r => r.Event.Event)], recorded);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>What a change did in a <see cref="ChangeTest"/>.</summary>
/// <param name="Result">The pipeline's answer.</param>
/// <param name="Events">The events this change committed (empty on failure).</param>
/// <param name="Recorded">The same events as the store recorded them: version, event id, time, operation context.</param>
public sealed record ChangeTestOutcome(Result<ChangeOutcome> Result, IReadOnlyList<DomainEvent> Events, IReadOnlyList<RecordedEvent> Recorded);
