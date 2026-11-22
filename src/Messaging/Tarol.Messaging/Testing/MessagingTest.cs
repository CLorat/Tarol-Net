using System.Collections.Concurrent;
using System.Text.Json;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging;

/// <summary>What one system of a <see cref="MessagingTest"/> has: its rows, publications, effects and consumers.</summary>
public sealed class MessagingSystemOptions
{
    internal Dictionary<string, Action<SourceTrust>?> Trusted { get; } = new(StringComparer.Ordinal);

    /// <summary>The reactions (<c>GeneratedReactions.Set</c>); default none.</summary>
    public ReactionSet Reactions { get; set; } = ReactionSet.Empty;

    /// <summary>The publications (<c>GeneratedPublications.Set</c>); default none.</summary>
    public PublicationSet Publications { get; set; } = PublicationSet.Empty;

    /// <summary>The consumers (<c>GeneratedConsumers.Set</c>); default none.</summary>
    public ConsumerSet? Consumers { get; set; }

    /// <summary>The effects (<see cref="EffectSet.Add{TEvent}"/> with test doubles); default none.</summary>
    public EffectSet Effects { get; set; } = new();

    /// <summary>Attempts and backoff of rows and deliveries (default <see cref="DeliveryPolicy"/>).</summary>
    public DeliveryPolicy Policy { get; set; } = new();

    /// <summary>
    /// Wraps the transport this system publishes through, e.g. a decorator that fails after publishing (a crash between
    /// publish and complete); <see langword="null"/> = the shared transport as is.
    /// </summary>
    public Func<IMessageTransport, IMessageTransport>? PublishThrough { get; set; }

    /// <summary>JSON options of the stored state; <see langword="null"/> = replay.</summary>
    public JsonSerializerOptions? StateOptions { get; set; }

    /// <summary>Accepts messages of <paramref name="source"/>'s contracts (M-13).</summary>
    /// <param name="source">The source system.</param>
    /// <param name="configure">Which tenants it may name.</param>
    /// <returns>This.</returns>
    public MessagingSystemOptions Trust(string source, Action<SourceTrust>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Trusted[source] = configure;
        return this;
    }
}

/// <summary>One step of <see cref="MessagingTest.Drain"/>: an outbox row (a reaction, publication or effect) or a delivery.</summary>
/// <param name="Application">The system it ran in.</param>
/// <param name="Row">The outbox row's attempt, or <see langword="null"/>.</param>
/// <param name="Delivery">The delivery's attempt, or <see langword="null"/>.</param>
public sealed record MessagingStep(string Application, ReactionAttempt? Row, ConsumerAttempt? Delivery);

/// <summary>
/// Several systems in one test, for real and in memory (docs/architecture/messaging.md §8): each with its own
/// <see cref="InMemoryAggregateStore"/>, change pipeline, reaction worker (reactions, publications, effects) and consumers,
/// all on one <see cref="InMemoryTransport"/> and one clock that stands still until you move it. <see cref="Drain"/> runs
/// outbox rows → transport → consumers → their rows … until nothing is due.
/// <code>
/// var test = MessagingTest.At(start)
///     .System("artflow", AppJson.Default.Options, o => { o.Publications = ArtFlow.GeneratedPublications.Set; o.Consumers = ArtFlow.GeneratedConsumers.Set; o.Trust("billing"); })
///     .System("billing", BillingJson.Default.Options, o => { o.Publications = Billing.GeneratedPublications.Set; o.Consumers = Billing.GeneratedConsumers.Set; o.Trust("artflow"); });
/// await test["artflow"].Given(auction).When(new SettleAuction(auction.Id, "alice", 150m));
/// await test.Drain();                                          // settled → published → invoice issued in billing
/// </code>
/// </summary>
public sealed class MessagingTest
{
    private readonly TestClock _clock;
    private readonly List<MessagingSystem> _systems = [];

    private MessagingTest(DateTimeOffset now)
    {
        _clock = new TestClock(now);
        Transport = new InMemoryTransport(_clock);
    }

    /// <summary>The transport all systems share (its <see cref="InMemoryTransport.Published"/> lists every message).</summary>
    public InMemoryTransport Transport { get; }

    /// <summary>The standing clock.</summary>
    public TimeProvider Clock => _clock;

    /// <summary>The systems, in the order they were added.</summary>
    public IReadOnlyList<MessagingSystem> Systems => _systems;

    /// <summary>A system by its application name.</summary>
    /// <param name="application">The application name.</param>
    /// <returns>The system.</returns>
    /// <exception cref="KeyNotFoundException">No system has that name.</exception>
    public MessagingSystem this[string application]
        => _systems.FirstOrDefault(s => s.Application == application) ?? throw new KeyNotFoundException($"No system '{application}' in this test.");

    /// <summary>Starts a test at <paramref name="now"/>.</summary>
    /// <param name="now">The clock's time.</param>
    /// <returns>The test.</returns>
    public static MessagingTest At(DateTimeOffset now) => new(now);

    /// <summary>Adds a system.</summary>
    /// <param name="application">Its application name (the prefix of its subscriptions).</param>
    /// <param name="json">Its JSON options (its source-generated context lists its contract records).</param>
    /// <param name="configure">Its reactions, publications, consumers, effects and trust.</param>
    /// <returns>The test.</returns>
    /// <exception cref="ArgumentException">A system with that name exists.</exception>
    public MessagingTest System(string application, JsonSerializerOptions json, Action<MessagingSystemOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(application);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(configure);
        if (_systems.Any(s => s.Application == application))
            throw new ArgumentException($"The system '{application}' is added already.", nameof(application));
        var options = new MessagingSystemOptions();
        configure(options);
        _systems.Add(new MessagingSystem(application, json, options, Transport, _clock));
        return this;
    }

    /// <summary>Moves the clock forward: retries become due.</summary>
    /// <param name="delta">How far.</param>
    /// <returns>The test.</returns>
    public MessagingTest Later(TimeSpan delta)
    {
        _clock.Now += delta;
        return this;
    }

    /// <summary>
    /// Runs every due outbox row of every system, then every due delivery, and again, until nothing is due at the current
    /// time. Retries are due after their backoff: move the clock with <see cref="Later"/> and drain again.
    /// </summary>
    /// <returns>The steps of this drain, in order.</returns>
    /// <exception cref="InvalidOperationException">More than 10,000 steps in one drain (a loop between the systems?).</exception>
    public async Task<IReadOnlyList<MessagingStep>> Drain()
    {
        var steps = new List<MessagingStep>();
        for (var busy = true; busy;)
        {
            busy = false;
            foreach (var system in _systems)
                for (IReadOnlyList<ReactionAttempt> round; (round = await system.Worker.RunOnce(CancellationToken.None).ConfigureAwait(false)).Count > 0;)
                {
                    busy = true;
                    foreach (var attempt in round)
                    {
                        system.Record(attempt);
                        steps.Add(new MessagingStep(system.Application, attempt, null));
                    }
                }
            foreach (var system in _systems)
                foreach (var subscription in system.Consumers.Subscriptions)
                {
                    var consumer = subscription.Name[(system.Application.Length + 1)..];
                    var before = system.Deliveries.Count;
                    var handled = await Transport.RunDue(subscription.Name, (incoming, ct) => system.Consumers.Handle(consumer, incoming, ct), CancellationToken.None)
                        .ConfigureAwait(false);
                    steps.AddRange(system.Deliveries.Skip(before).Select(d => new MessagingStep(system.Application, null, d)));
                    busy |= handled > 0;
                }
            if (steps.Count > 10_000)
                throw new InvalidOperationException("More than 10,000 steps in one Drain: do the systems publish to each other in a loop?");
        }
        return steps;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>One system of a <see cref="MessagingTest"/>.</summary>
public sealed class MessagingSystem
{
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, bool> _given = new();
    private readonly List<ReactionAttempt> _rows = [];
    private readonly List<ConsumerAttempt> _deliveries = [];
    private Actor _actor = Actor.User("test-user");
    private Option<Id<ITenant>> _tenant;

    internal MessagingSystem(string application, JsonSerializerOptions json, MessagingSystemOptions options, InMemoryTransport transport, TimeProvider clock)
    {
        _clock = clock;
        Application = application;
        Store = new InMemoryAggregateStore(clock, new InMemoryStoreOptions
        {
            StateOptions = options.StateOptions,
            Reactions = OutboxReactions.Combine(options.Reactions, options.Publications, options.Effects),
        });
        Pipeline = new ChangePipeline(Store, clock);
        Publisher = new MessagePublisher(options.Publications, options.PublishThrough?.Invoke(transport) ?? transport, json);
        var rows = OutboxRows.Combine(options.Reactions, Publisher, options.Effects);
        Worker = new ReactionWorker(Store.Outbox, new SkipGiven(rows, _given), Pipeline, clock, new ReactionOptions { Policy = options.Policy });
        DeadLetters = new InMemoryMessageDeadLetters(clock);
        var messaging = new MessagingOptions { Application = application, Policy = options.Policy };
        foreach (var (source, trust) in options.Trusted)
            messaging.Trust(source, trust);
        Consumers = new MessageConsumers(options.Consumers ?? new ConsumerSet([], static (_, _, _, _, _) => ValueTask.FromResult(ConsumerRun.Not(Mapped.UnknownContract))),
            Pipeline, DeadLetters, messaging, json)
        { Observed = RecordDelivery };
        transport.Prepare(options.Publications.Topics, Consumers.Subscriptions, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The application name.</summary>
    public string Application { get; }

    /// <summary>The store (its <see cref="InMemoryAggregateStore.Recorded"/> holds every event with its context).</summary>
    public InMemoryAggregateStore Store { get; }

    /// <summary>The store's outbox (reactions, publications and effects).</summary>
    public IOutbox Outbox => Store.Outbox;

    /// <summary>The pipeline.</summary>
    public ChangePipeline Pipeline { get; }

    /// <summary>The publisher of the system's publication rows.</summary>
    public MessagePublisher Publisher { get; }

    /// <summary>The system's consumers.</summary>
    public MessageConsumers Consumers { get; }

    /// <summary>The consumers' dead letters.</summary>
    public InMemoryMessageDeadLetters DeadLetters { get; }

    /// <summary>Every outbox row attempt of this system so far.</summary>
    public IReadOnlyList<ReactionAttempt> Rows
    {
        get
        {
            lock (_rows)
                return [.. _rows];
        }
    }

    /// <summary>Every delivery attempt of this system's consumers so far.</summary>
    public IReadOnlyList<ConsumerAttempt> Deliveries
    {
        get
        {
            lock (_deliveries)
                return [.. _deliveries];
        }
    }

    internal ReactionWorker Worker { get; }

    /// <summary>The actor of the next changes (default <c>Actor.User("test-user")</c>).</summary>
    /// <param name="actor">The actor.</param>
    /// <returns>The system.</returns>
    public MessagingSystem As(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        _actor = actor;
        return this;
    }

    /// <summary>The tenant of the next changes and of <see cref="Given"/> (default none).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The system.</returns>
    public MessagingSystem InTenant(Option<Id<ITenant>> tenant)
    {
        _tenant = tenant;
        return this;
    }

    /// <summary>Commits <paramref name="aggregate"/>'s uncommitted events as history: nothing reacts to them or publishes them.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="aggregate">A new or changed aggregate.</param>
    /// <returns>The system.</returns>
    /// <exception cref="InvalidOperationException">The aggregate breaks an invariant, or the store refused it.</exception>
    public MessagingSystem Given<T>(T aggregate)
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
            _given[recorded.EventId] = true;
        return this;
    }

    /// <summary>Runs a change (its rows wait for <see cref="MessagingTest.Drain"/>).</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="change">The change.</param>
    /// <param name="actor">The actor; default the system's.</param>
    /// <returns>The pipeline's answer.</returns>
    public Task<Result<ChangeOutcome>> When<T>(IChange<T> change, Actor? actor = null)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Pipeline.Run(change, Context(actor), default, CancellationToken.None).AsTask();

    /// <summary>Runs a creation (its rows wait for <see cref="MessagingTest.Drain"/>).</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="creation">The creation.</param>
    /// <param name="actor">The actor; default the system's.</param>
    /// <returns>The pipeline's answer.</returns>
    public Task<Result<ChangeOutcome>> When<T>(ICreation<T> creation, Actor? actor = null)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Pipeline.Run(creation, Context(actor), default, CancellationToken.None).AsTask();

    /// <summary>Loads an aggregate in the system's tenant.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="id">Its id.</param>
    /// <returns>The aggregate.</returns>
    /// <exception cref="InvalidOperationException">It does not exist.</exception>
    public T Load<T>(Id<T> id)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Store.Load(id, Context(), CancellationToken.None).AsTask().GetAwaiter().GetResult().TryGetValue(out T? loaded)
            ? loaded
            : throw new InvalidOperationException($"No {typeof(T).Name} {id} in {Application}'s tenant.");

    /// <summary>The outbox rows that were given up (reactions, publications, effects).</summary>
    /// <returns>The dead letters, oldest first.</returns>
    public Task<IReadOnlyList<DeadLetter>> OutboxDead() => Outbox.DeadLetters(1000, CancellationToken.None);

    /// <summary>A new operation of the system's actor and tenant.</summary>
    /// <param name="actor">Another actor.</param>
    /// <returns>The context.</returns>
    public OperationContext Context(Actor? actor = null) => OperationContext.Start(actor ?? _actor, _tenant, _clock);

    internal void Record(ReactionAttempt attempt)
    {
        lock (_rows)
            _rows.Add(attempt);
    }

    private void RecordDelivery(ConsumerAttempt attempt)
    {
        lock (_deliveries)
            _deliveries.Add(attempt);
    }

    /// <summary>History is not reacted to or published: its rows complete without running.</summary>
    private sealed class SkipGiven(IOutboxRowHandler rows, ConcurrentDictionary<Guid, bool> given) : IOutboxRowHandler
    {
        public IReadOnlyList<string> Names => rows.Names;

        public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent) => rows.ReactionsTo(domainEvent);

        public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction) => rows.DueAt(domainEvent, reaction);

        public bool IsScheduled(string reaction) => rows.IsScheduled(reaction);

        public string ActivityName(string reaction) => rows.ActivityName(reaction);

        public ValueTask<Result<ChangeOutcome>> Run(ChangePipeline pipeline, string reaction, RecordedEvent recorded, CancellationToken cancellationToken)
            => given.ContainsKey(recorded.EventId)
                ? ValueTask.FromResult<Result<ChangeOutcome>>(new ChangeOutcome(recorded.StreamId, recorded.Event.Version, 0))
                : rows.Run(pipeline, reaction, recorded, cancellationToken);

        public ValueTask<Result<ChangeOutcome>> RunPositioned(ChangePipeline pipeline, string reaction, PositionedEvent positioned, CancellationToken cancellationToken)
            => given.ContainsKey(positioned.Event.EventId)
                ? ValueTask.FromResult<Result<ChangeOutcome>>(new ChangeOutcome(positioned.Event.StreamId, positioned.Event.Event.Version, 0))
                : rows.RunPositioned(pipeline, reaction, positioned, cancellationToken);
    }
}
