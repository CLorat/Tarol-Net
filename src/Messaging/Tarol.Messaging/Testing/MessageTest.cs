using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging;

/// <summary>
/// Runs an application's consumers for real, in memory: an <see cref="InMemoryAggregateStore"/>, the change pipeline, an
/// <see cref="InMemoryTransport"/>, in-memory dead letters and a clock that stands still until you move it. A test sends
/// another system's messages and sees what the consumers committed, retried and buried.
/// <code>
/// var test = MessageTest.At(start, GeneratedConsumers.Set, AppJson.Default.Options, "artflow")
///     .Trust("billing", t => t.AnyTenant())
///     .Given(auction);
/// await test.Send(test.Message("billing.invoice-paid", 1, new InvoicePaidV1(invoice, auction.Id.Value, 150m), AppJson.Default.InvoicePaidV1));
/// var attempts = await test.Drain();                     // RecordPayment committed
/// await test.Later(TimeSpan.FromSeconds(2)).Drain();      // a retry, once its backoff passed
/// </code>
/// </summary>
public sealed class MessageTest
{
    private readonly TestClock _clock;
    private readonly List<ConsumerAttempt> _attempts = [];
    private Option<Id<ITenant>> _tenant;
    private Actor _actor = Actor.Service("test-sender");

    private MessageTest(DateTimeOffset now, ConsumerSet consumers, JsonSerializerOptions json, string application, JsonSerializerOptions? stateOptions,
        DeliveryPolicy? policy)
    {
        _clock = new TestClock(now);
        Store = new InMemoryAggregateStore(_clock, new InMemoryStoreOptions { StateOptions = stateOptions });
        Pipeline = new Changes.ChangePipeline(Store, _clock);
        Transport = new InMemoryTransport(_clock);
        DeadLetters = new InMemoryMessageDeadLetters(_clock);
        Options = new MessagingOptions { Application = application, Policy = policy ?? new DeliveryPolicy() };
        Consumers = new MessageConsumers(consumers, Pipeline, DeadLetters, Options, json) { Observed = Record };
        Transport.Prepare([], Consumers.Subscriptions, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The store (its <see cref="InMemoryAggregateStore.Recorded"/> holds every event with its context).</summary>
    public InMemoryAggregateStore Store { get; }

    /// <summary>The pipeline the consumers' changes run through.</summary>
    public Changes.ChangePipeline Pipeline { get; }

    /// <summary>The transport (its <see cref="InMemoryTransport.Published"/> lists what was sent).</summary>
    public InMemoryTransport Transport { get; }

    /// <summary>The consumers under test.</summary>
    public MessageConsumers Consumers { get; }

    /// <summary>Where given-up messages went.</summary>
    public InMemoryMessageDeadLetters DeadLetters { get; }

    /// <summary>Application name, trust and policy (change them before sending).</summary>
    public MessagingOptions Options { get; }

    /// <summary>The standing clock.</summary>
    public TimeProvider Clock => _clock;

    /// <summary>Every attempt so far, in order.</summary>
    public IReadOnlyList<ConsumerAttempt> Attempts => _attempts;

    /// <summary>Starts a test at <paramref name="now"/> with the application's consumers.</summary>
    /// <param name="now">The clock's time.</param>
    /// <param name="consumers">The consumers (<c>GeneratedConsumers.Set</c>).</param>
    /// <param name="json">The application's JSON options (its source-generated context lists the contract records).</param>
    /// <param name="application">This application's name (the prefix of its subscriptions).</param>
    /// <param name="stateOptions">JSON options of the stored state; <see langword="null"/> = replay.</param>
    /// <param name="policy">The delivery policy (e.g. fewer attempts); default <see cref="DeliveryPolicy"/>.</param>
    /// <returns>The test.</returns>
    public static MessageTest At(DateTimeOffset now, ConsumerSet consumers, JsonSerializerOptions json, string application = "test",
        JsonSerializerOptions? stateOptions = null, DeliveryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        ArgumentNullException.ThrowIfNull(json);
        return new(now, consumers, json, application, stateOptions, policy);
    }

    /// <summary>Accepts messages of <paramref name="source"/>'s contracts (M-13).</summary>
    /// <param name="source">The source system.</param>
    /// <param name="configure">Which tenants it may name.</param>
    /// <returns>The test.</returns>
    public MessageTest Trust(string source, Action<SourceTrust>? configure = null)
    {
        Options.Trust(source, configure);
        return this;
    }

    /// <summary>Commits <paramref name="aggregate"/>'s uncommitted events as history, in the test's tenant.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="aggregate">A new or changed aggregate.</param>
    /// <returns>The test.</returns>
    /// <exception cref="InvalidOperationException">The aggregate breaks an invariant, or the store refused it.</exception>
    public MessageTest Given<T>(T aggregate)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (AggregateChanges.Prepare(aggregate).TryGetValue(out Violation? broken))
            throw new InvalidOperationException($"The given {typeof(T).Name} breaks an invariant: {broken}.");
        var expected = aggregate.Version - aggregate.UncommittedChanges.Length;
        var appended = Store.Append(aggregate, expected, OperationContext.Start(Actor.User("test-user"), _tenant, _clock), Option.None, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        if (appended.TryGetValue(out Violation? refused))
            throw new InvalidOperationException($"The store refused the given {typeof(T).Name}: {refused}.");
        AggregateChanges.Committed(aggregate);
        return this;
    }

    /// <summary>The tenant of <see cref="Given"/> and of the messages <see cref="Message"/> builds (default none).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The test.</returns>
    public MessageTest InTenant(Option<Id<ITenant>> tenant)
    {
        _tenant = tenant;
        return this;
    }

    /// <summary>The publishing system's actor <see cref="Message"/> puts in <c>ce-actor</c> (default <c>Service:test-sender</c>).</summary>
    /// <param name="actor">The actor.</param>
    /// <returns>The test.</returns>
    public MessageTest From(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        _actor = actor;
        return this;
    }

    /// <summary>Moves the clock forward: retries become due.</summary>
    /// <param name="delta">How far.</param>
    /// <returns>The test.</returns>
    public MessageTest Later(TimeSpan delta)
    {
        _clock.Now += delta;
        return this;
    }

    /// <summary>
    /// A message of another system, as its publisher would send it: a new id, the source = the contract's prefix, the
    /// test's tenant and clock, a new correlation, the payload serialized through <paramref name="typeInfo"/>.
    /// </summary>
    /// <typeparam name="T">The publisher's record of the contract.</typeparam>
    /// <param name="contract">The contract.</param>
    /// <param name="version">Its version.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="typeInfo">Its source-generated metadata.</param>
    /// <returns>The message.</returns>
    public Message Message<T>(string contract, int version, T payload, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new Message
        {
            Id = Guid.CreateVersion7(_clock.GetUtcNow()),
            Source = Messaging.Message.SystemOf(contract),
            Type = contract,
            Version = version,
            Time = _clock.GetUtcNow(),
            Tenant = _tenant,
            Correlation = Id<ICorrelation>.New(_clock),
            Actor = _actor.OnBehalfOf.TryGetValue(out string? behalf) ? $"{_actor.Kind}:{_actor.Id}:{behalf}" : $"{_actor.Kind}:{_actor.Id}",
            Data = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo),
        };
    }

    /// <summary>Publishes <paramref name="message"/> to every subscription of its contract (delivered by <see cref="Drain"/>).</summary>
    /// <param name="message">The message.</param>
    /// <returns>A task that completes when it is queued.</returns>
    public Task Send(Message message) => Transport.Publish(message, CancellationToken.None);

    /// <summary>Delivers every due message to its consumers until nothing is due at the current time; retries are due after their backoff.</summary>
    /// <returns>The attempts of this drain, in order.</returns>
    /// <exception cref="InvalidOperationException">More than 10,000 deliveries in one drain.</exception>
    public async Task<IReadOnlyList<ConsumerAttempt>> Drain()
    {
        var start = _attempts.Count;
        var consumers = Consumers.Subscriptions.Select(static s => s.Name).ToArray();
        for (var handled = 1; handled > 0;)
        {
            handled = 0;
            foreach (var subscription in consumers)
            {
                var consumer = subscription[(Options.Application.Length + 1)..];
                handled += await Transport.RunDue(subscription, (incoming, ct) => Consumers.Handle(consumer, incoming, ct), CancellationToken.None).ConfigureAwait(false);
            }
            if (_attempts.Count - start > 10_000)
                throw new InvalidOperationException("More than 10,000 deliveries in one Drain.");
        }
        return [.. _attempts.Skip(start)];
    }

    /// <summary>The dead letters.</summary>
    /// <returns>The dead letters, oldest first.</returns>
    public Task<IReadOnlyList<DeadMessage>> DeadMessages() => DeadLetters.List(1000, CancellationToken.None);

    /// <summary>Re-drives a dead letter (<see cref="MessageConsumers.Redrive(long, CancellationToken)"/>).</summary>
    /// <param name="id">The dead letter's id.</param>
    /// <returns>What happened, or <see langword="null"/> when there is none.</returns>
    public Task<ConsumerAttempt?> Redrive(long id) => Consumers.Redrive(id, CancellationToken.None);

    /// <summary>Loads an aggregate in the test's tenant.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="id">Its id.</param>
    /// <returns>The aggregate.</returns>
    /// <exception cref="InvalidOperationException">It does not exist.</exception>
    public T Load<T>(Id<T> id)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Store.Load(id, OperationContext.Start(Actor.User("test-user"), _tenant, _clock), CancellationToken.None).AsTask().GetAwaiter().GetResult()
            .TryGetValue(out T? loaded)
            ? loaded
            : throw new InvalidOperationException($"No {typeof(T).Name} {id} in the test's tenant.");

    private void Record(ConsumerAttempt attempt)
    {
        lock (_attempts)
            _attempts.Add(attempt);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
