using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Contract;

/// <summary>
/// The <see cref="IAggregateStore"/> contract (README of Tarol.Persistence), written once and run against every store.
/// Each test works in a fresh tenant, so a shared database needs no cleanup and test classes may run in parallel.
/// </summary>
public abstract class StoreContract
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly Option<Id<ITenant>> _tenant;
    private IAggregateStore? _store;

    protected StoreContract()
    {
        Time = new FakeTimeProvider(Start);
        _tenant = Id<ITenant>.New(Time);
    }

    // created on first use: a derived class's constructor (its fixture) has run by then
    private IAggregateStore Store => _store ??= CreateStore(Time, new StoreLookups().Define<Member>("email", m => m.Email));

    /// <summary>The fake clock the store must read <see cref="RecordedEvent.RecordedAt"/> from.</summary>
    protected FakeTimeProvider Time { get; }

    /// <summary>Creates the store under test: stored state on (with <see cref="CreateCodec"/>'s options), these lookups.</summary>
    protected abstract IAggregateStore CreateStore(TimeProvider time, StoreLookups lookups);

    /// <summary>The codec for the contract models.</summary>
    protected static EventCodec CreateCodec() => new EventCodec(new System.Text.Json.JsonSerializerOptions(ContractJson.Default.Options))
        .Add<Member>().Add<Tally>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private OperationContext Alice => OperationContext.Start(Actor.User("alice"), _tenant, Time);
    private OperationContext OtherTenant => OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(Time), Time);
    private Member NewMember(string email) => Member.Join(Id<Member>.New(Time), email);

    private async Task<T> Committed<T>(T aggregate, long expected = 0, OperationContext? context = null)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        (await Store.Append(aggregate, expected, context ?? Alice, Option.None, Ct)).ShouldBe(Result.Success());
        AggregateChanges.Committed(aggregate);
        return aggregate;
    }

    private async Task<T> Loaded<T>(Id<T> id, OperationContext? context = null)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => (await Store.Load(id, context ?? Alice, Ct)).GetValueOrThrow();

    [Fact]
    public async Task A_missing_stream_is_None() => (await Store.Load(Id<Member>.New(Time), Alice, Ct)).IsNone.ShouldBeTrue();

    [Fact]
    public async Task An_aggregate_with_children_round_trips()
    {
        var member = NewMember("a@x");
        member.Earn(5).IsSuccess.ShouldBeTrue();
        member.Award("gold");
        await Committed(member);

        var loaded = await Loaded(member.Id);

        loaded.Version.ShouldBe(3);
        loaded.Points.ShouldBe(5);
        loaded.Badges.Single().Name.ShouldBe("gold");
        loaded.UncommittedChanges.Length.ShouldBe(0);
    }

    [Fact]
    public async Task A_loaded_aggregate_continues_its_stream()
    {
        var member = await Committed(NewMember("a@x"));
        var loaded = await Loaded(member.Id);
        loaded.Earn(3).IsSuccess.ShouldBeTrue();
        await Committed(loaded, expected: 1);
        (await Loaded(member.Id)).Points.ShouldBe(3);
    }

    [Fact]
    public async Task Expected_version_0_refuses_an_existing_stream()
    {
        var member = await Committed(NewMember("a@x"));
        var result = await Store.Append(Member.Join(member.Id, "b@x"), 0, Alice, Option.None, Ct);
        result.ShouldBe(Result.Failure(StoreErrors.VersionConflict(0, 1)));
    }

    [Fact]
    public async Task A_stale_version_conflicts_and_writes_nothing()
    {
        var member = await Committed(NewMember("a@x"));
        var first = await Loaded(member.Id);
        var second = await Loaded(member.Id);
        first.Earn(1).IsSuccess.ShouldBeTrue();
        second.Earn(2).IsSuccess.ShouldBeTrue();

        (await Store.Append(first, 1, Alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        (await Store.Append(second, 1, Alice, Option.None, Ct)).ShouldBe(Result.Failure(StoreErrors.VersionConflict(1, 2)));
        (await Loaded(member.Id)).Points.ShouldBe(1);
    }

    [Fact]
    public async Task Changes_that_do_not_follow_the_expected_version_throw()
    {
        var member = NewMember("a@x");
        await Should.ThrowAsync<ArgumentException>(async () => _ = await Store.Append(member, 3, Alice, Option.None, Ct));
    }

    [Fact]
    public async Task Nothing_to_append_succeeds_and_stores_no_key()
    {
        var member = await Committed(NewMember("a@x"));
        var key = new IdempotencyKey(_tenant, "test", "k-empty");
        (await Store.Append(member, 1, Alice, key, Ct)).IsSuccess.ShouldBeTrue();
        (await Store.Seen(key, Ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task Another_tenant_does_not_see_the_stream_or_its_lookups()
    {
        var member = await Committed(NewMember("a@x"));
        (await Store.Load(member.Id, OtherTenant, Ct)).IsNone.ShouldBeTrue();
        (await Store.Load(member.Id, OperationContext.Start(Actor.User("alice"), Option.None, Time), Ct)).IsNone.ShouldBeTrue();
        (await Store.Find<Member>("email", "a@x", OtherTenant, Ct)).IsNone.ShouldBeTrue();
        await Committed(Member.Join(member.Id, "a@x"), context: OtherTenant);   // same id and key, another tenant
    }

    [Fact]
    public async Task A_key_is_stored_with_the_commit_and_returns_its_outcome()
    {
        var key = new IdempotencyKey(_tenant, "payments", "evt_1");
        var member = NewMember("a@x");
        member.Earn(1).IsSuccess.ShouldBeTrue();
        (await Store.Append(member, 0, Alice, key, Ct)).IsSuccess.ShouldBeTrue();
        (await Store.Seen(key, Ct)).ShouldBe(new StoredOutcome(member.Id.Value, 2, 2));
    }

    [Fact]
    public async Task A_key_stored_meanwhile_refuses_the_commit()
    {
        var key = new IdempotencyKey(_tenant, "payments", "evt_1");
        (await Store.Append(NewMember("a@x"), 0, Alice, key, Ct)).IsSuccess.ShouldBeTrue();
        var second = NewMember("b@x");
        (await Store.Append(second, 0, Alice, key, Ct)).ShouldBe(Result.Failure(StoreErrors.DuplicateKey(key)));
        (await Store.Load(second.Id, Alice, Ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_commit_stores_no_key()
    {
        var member = await Committed(NewMember("a@x"));
        var key = new IdempotencyKey(_tenant, "payments", "evt_2");
        StoreErrors.IsVersionConflict(await Store.Append(Member.Join(member.Id, "z@x"), 0, Alice, key, Ct)).ShouldBeTrue();
        (await Store.Seen(key, Ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task A_keys_fingerprint_is_stored_with_its_outcome_and_not_part_of_its_identity()
    {
        var key = new IdempotencyKey(_tenant, "http", "k-1") { Fingerprint = "sha256:abc" };
        var member = NewMember("a@x");
        (await Store.Append(member, 0, Alice, key, Ct)).IsSuccess.ShouldBeTrue();

        (await Store.Seen(key, Ct)).ShouldBe(new StoredOutcome(member.Id.Value, 1, 1, "sha256:abc"));
        (await Store.Seen(new IdempotencyKey(_tenant, "http", "k-1") { Fingerprint = "sha256:other" }, Ct)).GetValueOrThrow().Fingerprint
            .ShouldBe("sha256:abc");                                                    // found by (tenant, scope, key)
        (await Store.Seen(new IdempotencyKey(_tenant, "http", "k-1"), Ct)).IsSome.ShouldBeTrue();
        (await Store.Append(NewMember("b@x"), 0, Alice, key with { Fingerprint = "sha256:other" }, Ct))
            .ShouldBe(Result.Failure(StoreErrors.DuplicateKey(key with { Fingerprint = "sha256:other" })));
    }

    [Fact]
    public async Task A_key_without_a_fingerprint_stores_none()
    {
        var key = new IdempotencyKey(_tenant, "payments", "evt_3");
        (await Store.Append(NewMember("a@x"), 0, Alice, key, Ct)).IsSuccess.ShouldBeTrue();
        (await Store.Seen(key, Ct)).GetValueOrThrow().Fingerprint.IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task Keys_are_scoped_by_tenant_and_scope()
    {
        var key = new IdempotencyKey(_tenant, "payments", "evt_1");
        (await Store.Append(NewMember("a@x"), 0, Alice, key, Ct)).IsSuccess.ShouldBeTrue();
        (await Store.Seen(new IdempotencyKey(_tenant, "payments", "evt_1"), Ct)).IsSome.ShouldBeTrue();
        (await Store.Seen(new IdempotencyKey(OtherTenant.Tenant, "payments", "evt_1"), Ct)).IsNone.ShouldBeTrue();
        (await Store.Seen(new IdempotencyKey(_tenant, "webhooks", "evt_1"), Ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task A_lookup_finds_moves_and_refuses_a_taken_key()
    {
        var member = await Committed(NewMember("a@x"));
        (await Store.Find<Member>("email", "a@x", Alice, Ct)).ShouldBe(member.Id);

        var taken = NewMember("a@x");
        (await Store.Append(taken, 0, Alice, Option.None, Ct)).ShouldBe(Result.Failure(StoreErrors.LookupTaken("email", "a@x")));
        (await Store.Load(taken.Id, Alice, Ct)).IsNone.ShouldBeTrue();

        var loaded = await Loaded(member.Id);
        loaded.ChangeEmail("b@x");
        await Committed(loaded, expected: 1);
        (await Store.Find<Member>("email", "a@x", Alice, Ct)).IsNone.ShouldBeTrue();
        (await Store.Find<Member>("email", "b@x", Alice, Ct)).ShouldBe(member.Id);
        await Committed(NewMember("a@x"));   // the old key is free again
    }

    [Fact]
    public async Task An_empty_lookup_key_is_not_indexed()
    {
        await Committed(NewMember(""));
        await Committed(NewMember(""));
        (await Store.Find<Member>("email", "", Alice, Ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task An_undefined_lookup_throws()
        => await Should.ThrowAsync<InvalidOperationException>(async () => _ = await Store.Find<Member>("nope", "1", Alice, Ct));

    [Fact]
    public async Task A_load_by_replay_aggregate_round_trips()
    {
        var tally = Tally.Start(Id<Tally>.New(Time));
        tally.Bump();
        tally.Bump();
        await Committed(tally);
        (await Loaded(tally.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Every_event_is_recorded_with_the_operation_and_one_commit_time()
    {
        var feed = (IEventFeed)Store;   // every store of the contract serves the feed
        var member = NewMember("a@x");
        member.Earn(1).IsSuccess.ShouldBeTrue();
        var badge = member.Award("gold");
        var context = Alice.AsSystem("Jobs.Test").CausedBy(Id<ICausation>.New(Time));
        (await Store.Append(member, 0, context, Option.None, Ct)).IsSuccess.ShouldBeTrue();

        // A database feed returns a commit once every older transaction has finished (P-14), so poll for a while.
        var mine = new List<PositionedEvent>();
        var waited = System.Diagnostics.Stopwatch.StartNew();   // real waiting, not domain time
        while (mine.Count < 3 && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            mine.Clear();
            var after = GlobalPosition.Start;
            while (true)
            {
                var page = new List<PositionedEvent>();
                await foreach (var e in feed.ReadAll(after, 500, Ct))
                    page.Add(e);
                if (page.Count == 0)
                    break;
                mine.AddRange(page.Where(e => e.Event.StreamId == member.Id.Value));
                after = page[^1].Position;
            }
            if (mine.Count < 3)
                await Task.Delay(50, Ct);
        }

        mine.Select(e => e.Event.Event.Version).ShouldBe([1L, 2, 3], string.Join(" ", mine.Select(e => $"{e.Position}/{e.Event.EventId}/{e.Event.Context.Tenant}")));
        mine.ShouldAllBe(e => e.StreamType == Member.StreamType && e.Event.Context == context && e.Event.RecordedAt == Time.GetUtcNow());
        mine.Select(e => e.Event.EventId).Distinct().Count().ShouldBe(3);
        mine.ShouldAllBe(e => e.Event.EventId.Version == 7);
        mine[2].Event.Event.EntityId.ShouldBe(badge.Id.Value);
        mine[2].Event.Event.ParentId.ShouldBe(member.Id.Value);
        mine[0].Event.Event.Event.ShouldBe(new Member.Joined("a@x"));
        mine.Select(e => e.Position).ShouldBeInOrder();
    }

    [Fact]
    public async Task Concurrent_appends_to_one_stream_commit_exactly_one()
    {
        var member = await Committed(NewMember("a@x"));
        var copies = new List<Member>();
        for (var i = 0; i < 16; i++)
        {
            var copy = await Loaded(member.Id);
            copy.Earn(i + 1).IsSuccess.ShouldBeTrue();
            copies.Add(copy);
        }

        var results = await Task.WhenAll(copies.Select(c => Task.Run(async () => await Store.Append(c, 1, Alice, Option.None, Ct), Ct)));

        results.Count(static r => r.IsSuccess).ShouldBe(1);
        results.Count(StoreErrors.IsVersionConflict).ShouldBe(15);
        (await Loaded(member.Id)).Version.ShouldBe(2);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_operation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => _ = await Store.Load(Id<Member>.New(Time), Alice, cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => _ = await Store.Append(NewMember("a@x"), 0, Alice, Option.None, cts.Token));
    }
}
