using System.Text.Json;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Sagas;

/// <summary>
/// Runs changes and their reactions for real, in memory: an <see cref="InMemoryAggregateStore"/> that writes outbox rows,
/// the <see cref="ChangePipeline"/>, a <see cref="ReactionWorker"/> and a clock that stands still until you move it. A test
/// sees the whole chain event → reaction → change → its reactions, timeouts when it advances the clock, and dead letters.
/// <code>
/// var test = ReactionTest.At(start, GeneratedReactions.Set).Given(auction);
/// await test.When(new SettleAuction(auction.Id));
/// await test.Drain();                                // the payout and the saga start
/// await test.Later(TimeSpan.FromDays(3)).Drain();     // the timeout and its compensation
/// </code>
/// </summary>
public sealed class ReactionTest
{
    private readonly TestClock _clock;
    private readonly ReactionWorker _worker;
    private readonly HashSet<Guid> _given = [];
    private readonly ReactionOptions _options;
    private Actor _actor = Actor.User("test-user");
    private Option<Id<ITenant>> _tenant;

    private ReactionTest(DateTimeOffset now, ReactionSet reactions, JsonSerializerOptions? stateOptions, DeliveryPolicy? policy)
    {
        _clock = new TestClock(now);
        Store = new InMemoryAggregateStore(_clock, new InMemoryStoreOptions { StateOptions = stateOptions, Reactions = reactions });
        Pipeline = new ChangePipeline(Store, _clock);
        _options = new ReactionOptions { Policy = policy ?? new DeliveryPolicy() };
        _worker = new ReactionWorker(Store.Outbox, reactions, Pipeline, _clock, _options);
    }

    /// <summary>The store (its <see cref="InMemoryAggregateStore.Recorded"/> holds every event with its context).</summary>
    public InMemoryAggregateStore Store { get; }

    /// <summary>The store's outbox.</summary>
    public IOutbox Outbox => Store.Outbox;

    /// <summary>The pipeline the changes and the reactions run through.</summary>
    public ChangePipeline Pipeline { get; }

    /// <summary>The standing clock of the store, the pipeline and the worker.</summary>
    public TimeProvider Clock => _clock;

    /// <summary>Starts a test at <paramref name="now"/> with the application's reactions.</summary>
    /// <param name="now">The clock's time.</param>
    /// <param name="reactions">The reactions (<c>GeneratedReactions.Set</c>).</param>
    /// <param name="stateOptions">JSON options of the stored state (as for <see cref="ChangeTest"/>); <see langword="null"/> = replay.</param>
    /// <param name="policy">The delivery policy (e.g. fewer attempts); default <see cref="DeliveryPolicy"/>.</param>
    /// <returns>The test.</returns>
    public static ReactionTest At(DateTimeOffset now, ReactionSet reactions, JsonSerializerOptions? stateOptions = null, DeliveryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(reactions);
        return new(now, reactions, stateOptions, policy);
    }

    /// <summary>Commits <paramref name="aggregate"/>'s uncommitted events as history; their reactions do <b>not</b> run.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="aggregate">A new or changed aggregate.</param>
    /// <returns>The test.</returns>
    /// <exception cref="InvalidOperationException">The aggregate breaks an invariant, or the store refused it.</exception>
    public ReactionTest Given<T>(T aggregate)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (AggregateChanges.Prepare(aggregate).TryGetValue(out Violation? broken))
            throw new InvalidOperationException($"The given {typeof(T).Name} breaks an invariant: {broken}.");
        var before = Store.Recorded.Count;
        var expected = aggregate.Version - aggregate.UncommittedChanges.Length;
        var appended = Store.Append(aggregate, expected, Context(), Option.None, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (appended.TryGetValue(out Violation? refused))
            throw new InvalidOperationException($"The store refused the given {typeof(T).Name}: {refused}.");
        AggregateChanges.Committed(aggregate);
        foreach (var recorded in Store.Recorded.Skip(before))
            _ = _given.Add(recorded.EventId);
        return this;
    }

    /// <summary>Sets the clock.</summary>
    /// <param name="now">The new time.</param>
    /// <returns>The test.</returns>
    public ReactionTest At(DateTimeOffset now)
    {
        _clock.Now = now;
        return this;
    }

    /// <summary>Moves the clock forward: scheduled reactions and retries become due.</summary>
    /// <param name="delta">How far.</param>
    /// <returns>The test.</returns>
    public ReactionTest Later(TimeSpan delta)
    {
        _clock.Now += delta;
        return this;
    }

    /// <summary>The actor of the next changes (default <c>Actor.User("test-user")</c>).</summary>
    /// <param name="actor">The actor.</param>
    /// <returns>The test.</returns>
    public ReactionTest As(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        _actor = actor;
        return this;
    }

    /// <summary>The tenant of the next changes (default none).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The test.</returns>
    public ReactionTest InTenant(Option<Id<ITenant>> tenant)
    {
        _tenant = tenant;
        return this;
    }

    /// <summary>Runs a change (its reactions wait for <see cref="Drain"/>).</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="change">The change.</param>
    /// <param name="actor">The actor; default the test's.</param>
    /// <returns>The outcome and what it committed.</returns>
    public async Task<ChangeTestOutcome> When<T>(IChange<T> change, Actor? actor = null)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var before = Store.Recorded.Count;
        var result = await Pipeline.Run(change, Context(actor), default, CancellationToken.None).ConfigureAwait(false);
        return Outcome(result, before);
    }

    /// <summary>Runs a creation (its reactions wait for <see cref="Drain"/>).</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="creation">The creation.</param>
    /// <param name="actor">The actor; default the test's.</param>
    /// <returns>The outcome and what it committed.</returns>
    public async Task<ChangeTestOutcome> When<T>(ICreation<T> creation, Actor? actor = null)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var before = Store.Recorded.Count;
        var result = await Pipeline.Run(creation, Context(actor), default, CancellationToken.None).ConfigureAwait(false);
        return Outcome(result, before);
    }

    /// <summary>
    /// Runs every due row, and the rows their changes write, until nothing is due at the current time. Retries are due
    /// after their backoff: move the clock with <see cref="Later"/> and drain again.
    /// </summary>
    /// <returns>Every attempt, in order.</returns>
    /// <exception cref="InvalidOperationException">The reactions keep writing rows (more than 10,000 attempts in one drain).</exception>
    public async Task<IReadOnlyList<ReactionAttempt>> Drain()
    {
        var attempts = new List<ReactionAttempt>();
        while (true)
        {
            var rows = await Outbox.Claim(_options.Batch, _options.Lease, CancellationToken.None).ConfigureAwait(false);
            if (rows.Count == 0)
                return attempts;
            foreach (var row in rows)
            {
                if (_given.Contains(row.Event.Event.EventId))
                    _ = await Outbox.Complete(row, CancellationToken.None).ConfigureAwait(false);   // history: no reaction
                else
                    attempts.Add(await _worker.Handle(row, CancellationToken.None).ConfigureAwait(false));
            }
            if (attempts.Count > 10_000)
                throw new InvalidOperationException("The reactions keep producing rows (more than 10,000 attempts in one Drain): a reaction loop?");
        }
    }

    /// <summary>The dead letters.</summary>
    /// <returns>The dead letters, oldest first.</returns>
    public Task<IReadOnlyList<DeadLetter>> DeadLetters() => Outbox.DeadLetters(1000, CancellationToken.None);

    /// <summary>Loads an aggregate in the test's tenant.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="id">Its id.</param>
    /// <returns>The aggregate.</returns>
    /// <exception cref="InvalidOperationException">It does not exist.</exception>
    public T Load<T>(Id<T> id)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Store.Load(id, Context(), CancellationToken.None).AsTask().GetAwaiter().GetResult().TryGetValue(out T? loaded)
            ? loaded
            : throw new InvalidOperationException($"No {typeof(T).Name} {id} in the test's tenant.");

    /// <summary>A new operation of the test's actor and tenant.</summary>
    /// <param name="actor">Another actor.</param>
    /// <returns>The context.</returns>
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
