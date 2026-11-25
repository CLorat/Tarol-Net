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

    private readonly ChangeTestOptions _options;
    private readonly HashSet<Guid> _given = [];

    private ChangeTest(DateTimeOffset now, ChangeTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RunReactions && options.Reactions is null)
            throw new ArgumentException("RunReactions needs Reactions (the app's ReactionSet, e.g. GeneratedReactions.Set).", nameof(options));
        _options = options;
        _clock = new TestClock(now);
        Store = new InMemoryAggregateStore(_clock, new InMemoryStoreOptions
        {
            StateOptions = options.StateOptions,
            Codec = options.Codec,
            Keys = options.Codec is null ? null
                : new InMemoryKeyStore(new ConfiguredKeyEncryptionKey("change-test", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), _clock),
            Lookups = options.Lookups ?? new StoreLookups(),
            Reactions = options.Reactions,
        });
        Pipeline = new ChangePipeline(Store, _clock, options.Authorization);
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
        => new(now, new ChangeTestOptions { StateOptions = stateOptions, Authorization = authorization });

    /// <summary>
    /// A test like the application's store (round 3): with <see cref="ChangeTestOptions.Codec"/> events and state are encoded
    /// and personal data sealed as in production (Tatalu FB-F05.3-1: a sealing error surfaced only in the host tests), with
    /// <see cref="ChangeTestOptions.Lookups"/> the app's lookups (TN-15), with <see cref="ChangeTestOptions.RunReactions"/>
    /// the reactions a change causes (FB-F06.2-4).
    /// </summary>
    /// <param name="now">The time the pipeline passes as <see cref="ChangeContext.Now"/>.</param>
    /// <param name="options">Codec, lookups, reactions, authorization.</param>
    /// <returns>The test.</returns>
    public static ChangeTest At(DateTimeOffset now, ChangeTestOptions options) => new(now, options);

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
        foreach (var recorded in Store.Recorded.Skip(Store.Recorded.Count - aggregate.UncommittedChanges.Length))
            _given.Add(recorded.EventId);                                                  // history: its reactions do not run
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
        Check(options);
        var before = Store.Recorded.Count;
        var result = await Pipeline.Run(change, Context(actor), options, CancellationToken.None);
        return await Outcome(result, before);
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
        Check(options);
        var before = Store.Recorded.Count;
        var result = await Pipeline.Run(creation, Context(actor), options, CancellationToken.None);
        return await Outcome(result, before);
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

    /// <summary>
    /// The stored history of one aggregate: the events of stream type <typeparamref name="T"/> and id <paramref name="id"/> in
    /// the test's tenant, in order. Per-tenant singletons share their stream id (the tenant's), so
    /// <c>Store.Recorded.Where(r =&gt; r.StreamId == id)</c> mixes them (Tatalu FB-F06.3-2, FB-F01.1-7).
    /// </summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="id">Its id.</param>
    /// <returns>Its recorded events.</returns>
    public IReadOnlyList<RecordedEvent> History<T>(Id<T> id)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => [.. Store.ReadAll(GlobalPosition.Start, 1_000_000, CancellationToken.None).ToBlockingEnumerable()
            .Where(p => p.StreamType == T.StreamType && p.Event.StreamId == id.Value && p.Event.Context.Tenant == _tenant)
            .Select(static p => p.Event)];

    /// <summary>
    /// <see cref="AggregateTest.StoredStateMatchesReplay{TSelf}(TSelf, ReadOnlySpan{StreamEvent}, JsonSerializerOptions)"/> for a
    /// stored aggregate: loads it and replays <see cref="History{T}"/> (Tatalu FB-F01.1-7: the other overload refuses a loaded one).
    /// </summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="id">Its id.</param>
    /// <param name="options">The state's JSON options.</param>
    /// <returns>The instance read back from the state.</returns>
    /// <exception cref="InvalidOperationException">The states differ, or it does not exist.</exception>
    public T StoredStateMatchesReplay<T>(Id<T> id, JsonSerializerOptions options)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => AggregateTest.StoredStateMatchesReplay(Load(id), [.. History(id).Select(static r => r.Event)], options);

    /// <summary>The operation context the next change runs with.</summary>
    /// <param name="actor">The actor, or <see langword="null"/> for the test's.</param>
    /// <returns>A new context (new correlation id) at the test's clock.</returns>
    public OperationContext Context(Actor? actor = null) => OperationContext.Start(actor ?? _actor, _tenant, _clock);

    private void Check(ChangeOptions options)
    {
        // FB-F06.3-4: the pipeline fails closed (Auth.NotConfigured) for a principal without an authorization; in a test that
        // is a set-up mistake, so say so instead of answering 403
        if (options.Principal is not null && _options.Authorization is null)
            throw new InvalidOperationException("ChangeOptions.Principal is set, but this ChangeTest has no authorization: pass "
                + "ChangeTestOptions.Authorization (the app's IChangeAuthorization) to check permissions, or leave the principal out "
                + "to run without them. In production the pipeline refuses such a run with 403 Auth.NotConfigured.");
    }

    private async Task<ChangeTestOutcome> Outcome(Result<ChangeOutcome> result, int before)
    {
        var recorded = Store.Recorded.Skip(before).ToArray();
        var reactions = _options.RunReactions ? await Drain().ConfigureAwait(false) : [];
        return new ChangeTestOutcome(result, [.. recorded.Select(static r => r.Event.Event)], recorded) { Reactions = reactions };
    }

    /// <summary>Runs every due reaction row, and the rows their changes write, at the current time (no retries).</summary>
    private async Task<IReadOnlyList<ReactionRun>> Drain()
    {
        var runs = new List<ReactionRun>();
        while (true)
        {
            var rows = await Store.Outbox.Claim(100, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            if (rows.Count == 0)
                return runs;
            foreach (var row in rows)
            {
                if (!_given.Contains(row.Event.Event.EventId))
                {
                    var before = Store.Recorded.Count;
                    var result = await _options.Reactions!.Run(Pipeline, row.Reaction, row.Event.Event, CancellationToken.None).ConfigureAwait(false);
                    runs.Add(new ReactionRun(row.Reaction, row.Event.Event, result, [.. Store.Recorded.Skip(before)]));
                }
                _ = await Store.Outbox.Complete(row, CancellationToken.None).ConfigureAwait(false);
            }
            if (runs.Count > 10_000)
                throw new InvalidOperationException("The reactions keep producing rows (more than 10,000 runs after one change): a reaction loop?");
        }
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
public sealed record ChangeTestOutcome(Result<ChangeOutcome> Result, IReadOnlyList<DomainEvent> Events, IReadOnlyList<RecordedEvent> Recorded)
{
    /// <summary>The reactions the change caused, in order, with <see cref="ChangeTestOptions.RunReactions"/>; empty otherwise.</summary>
    public IReadOnlyList<ReactionRun> Reactions { get; init; } = [];
}

/// <summary>One reaction a <see cref="ChangeTest"/> ran (<see cref="ChangeTestOptions.RunReactions"/>).</summary>
/// <param name="Reaction">The reaction's name.</param>
/// <param name="Cause">The event it reacted to.</param>
/// <param name="Result">The pipeline's outcome (a refusal is not retried).</param>
/// <param name="Recorded">The events its change committed.</param>
public sealed record ReactionRun(string Reaction, RecordedEvent Cause, Result<ChangeOutcome> Result, IReadOnlyList<RecordedEvent> Recorded);

/// <summary>How a <see cref="ChangeTest"/> stores and runs (<see cref="ChangeTest.At(DateTimeOffset, ChangeTestOptions)"/>).</summary>
public sealed record ChangeTestOptions
{
    /// <summary>JSON options for the stored state without a codec (so loads read it like production); <see langword="null"/> = replay.</summary>
    public JsonSerializerOptions? StateOptions { get; init; }

    /// <summary>
    /// The application's codec (its aggregates added): every event and the state are encoded, and personal data sealed with
    /// the keys of an in-memory key store, as by the production store. A sealing error then fails the change in the unit
    /// test; <c>Store.AssertNoPlaintext</c> checks the stored bytes.
    /// </summary>
    public EventCodec? Codec { get; init; }

    /// <summary>The application's lookups (unique values, TN-15); <see langword="null"/> = none.</summary>
    public StoreLookups? Lookups { get; init; }

    /// <summary>The application's reactions: the store writes their outbox rows (<c>Store.Outbox</c>).</summary>
    public ReactionSet? Reactions { get; init; }

    /// <summary>
    /// Run the reactions a change causes, and theirs, before <c>When</c> returns (no worker, no retries;
    /// <see cref="ChangeTestOutcome.Reactions"/>). Reactions to the history given with <c>Given</c> do not run.
    /// </summary>
    public bool RunReactions { get; init; }

    /// <summary>A resource permission to run (step 10); <see langword="null"/> = none (then a principal is a set-up error).</summary>
    public IChangeAuthorization? Authorization { get; init; }
}
