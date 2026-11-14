using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Tests;

public sealed class InMemoryAggregateStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly InMemoryAggregateStore _store;
    private readonly OperationContext _alice;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public InMemoryAggregateStoreTests()
    {
        _store = new InMemoryAggregateStore(_time, TestJson.Default.Options);
        _alice = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(_time), _time);
    }

    private async Task<Account> Committed(Account account, OperationContext? context = null, long expected = 0)
    {
        (await _store.Append(account, expected, context ?? _alice, Option.None, _ct)).ShouldBe(Result.Success());
        AggregateChanges.Committed(account);
        return account;
    }

    [Fact]
    public async Task A_missing_stream_loads_as_None()
        => (await _store.Load(Id<Account>.New(), _alice, _ct)).IsNone.ShouldBeTrue();

    [Fact]
    public async Task An_appended_aggregate_loads_with_its_state_and_version()
    {
        var account = Account.Open("A-1");
        account.Deposit(10).IsSuccess.ShouldBeTrue();
        await Committed(account);

        var loaded = (await _store.Load(account.Id, _alice, _ct)).GetValueOrThrow();

        loaded.Number.ShouldBe("A-1");
        loaded.Balance.ShouldBe(10);
        loaded.Version.ShouldBe(2);
        loaded.UncommittedChanges.Length.ShouldBe(0);
        _store.StateLoads.ShouldBe(1);
    }

    [Fact]
    public async Task Expected_version_0_refuses_an_existing_stream()
    {
        var account = await Committed(Account.Open("A-1"));
        var again = Account.Open(account.Id, "A-1");

        var result = await _store.Append(again, 0, _alice, Option.None, _ct);

        result.ShouldBe(Result.Failure(StoreErrors.VersionConflict(0, 1)));
        StoreErrors.IsVersionConflict(result).ShouldBeTrue();
    }

    [Fact]
    public async Task A_stale_expected_version_is_a_conflict_and_writes_nothing()
    {
        var account = await Committed(Account.Open("A-1"));
        var first = (await _store.Load(account.Id, _alice, _ct)).GetValueOrThrow();
        var second = (await _store.Load(account.Id, _alice, _ct)).GetValueOrThrow();
        first.Deposit(5).IsSuccess.ShouldBeTrue();
        second.Deposit(7).IsSuccess.ShouldBeTrue();

        (await _store.Append(first, 1, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue();
        var lost = await _store.Append(second, 1, _alice, Option.None, _ct);

        StoreErrors.IsVersionConflict(lost).ShouldBeTrue();
        _store.History(account.Id, _alice.Tenant).Length.ShouldBe(2);
        (await _store.Load(account.Id, _alice, _ct)).GetValueOrThrow().Balance.ShouldBe(5);
    }

    [Fact]
    public async Task Changes_that_do_not_follow_the_expected_version_are_a_programming_error()
    {
        var account = Account.Open("A-1");

        var thrown = await Should.ThrowAsync<ArgumentException>(async () => _ = await _store.Append(account, 3, _alice, Option.None, _ct));

        thrown.ParamName.ShouldBe("expectedVersion");
    }

    [Fact]
    public async Task Append_leaves_the_uncommitted_changes_to_the_caller()
    {
        var account = Account.Open("A-1");

        (await _store.Append(account, 0, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue();

        account.UncommittedChanges.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Nothing_to_append_succeeds_and_stores_no_key()
    {
        var account = await Committed(Account.Open("A-1"));
        var key = new IdempotencyKey(_alice.Tenant, "test", "k-0");

        (await _store.Append(account, 1, _alice, key, _ct)).IsSuccess.ShouldBeTrue();

        (await _store.Seen(key, _ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task Another_tenant_does_not_see_the_stream()
    {
        var account = await Committed(Account.Open("A-1"));
        var bob = OperationContext.Start(Actor.User("bob"), Id<ITenant>.New(_time), _time);
        var single = OperationContext.Start(Actor.User("carol"), Option.None, _time);

        (await _store.Load(account.Id, bob, _ct)).IsNone.ShouldBeTrue();
        (await _store.Load(account.Id, single, _ct)).IsNone.ShouldBeTrue();
        (await _store.Append(Account.Open(account.Id, "B-1"), 0, bob, Option.None, _ct)).IsSuccess.ShouldBeTrue();
        (await _store.Load(account.Id, bob, _ct)).GetValueOrThrow().Number.ShouldBe("B-1");
        (await _store.Load(account.Id, _alice, _ct)).GetValueOrThrow().Number.ShouldBe("A-1");
    }

    [Fact]
    public async Task A_key_is_stored_with_the_commit_and_returns_its_outcome()
    {
        var key = new IdempotencyKey(_alice.Tenant, "payments", "evt_1");
        var account = Account.Open("A-1");
        account.Deposit(1).IsSuccess.ShouldBeTrue();

        (await _store.Append(account, 0, _alice, key, _ct)).IsSuccess.ShouldBeTrue();

        (await _store.Seen(key, _ct)).GetValueOrThrow().ShouldBe(new StoredOutcome(account.Id.Value, 2, 2));
    }

    [Fact]
    public async Task A_key_stored_meanwhile_refuses_the_second_commit()
    {
        var key = new IdempotencyKey(_alice.Tenant, "payments", "evt_1");
        (await _store.Append(Account.Open("A-1"), 0, _alice, key, _ct)).IsSuccess.ShouldBeTrue();
        var other = Account.Open("A-2");

        var result = await _store.Append(other, 0, _alice, key, _ct);

        result.ShouldBe(Result.Failure(StoreErrors.DuplicateKey(key)));
        (await _store.Load(other.Id, _alice, _ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_commit_stores_no_key()
    {
        var account = await Committed(Account.Open("A-1"));
        var key = new IdempotencyKey(_alice.Tenant, "payments", "evt_2");
        var stale = Account.Open(account.Id, "A-1");

        StoreErrors.IsVersionConflict(await _store.Append(stale, 0, _alice, key, _ct)).ShouldBeTrue();

        (await _store.Seen(key, _ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task Keys_are_scoped_by_tenant_and_scope()
    {
        var key = new IdempotencyKey(_alice.Tenant, "payments", "evt_1");
        (await _store.Append(Account.Open("A-1"), 0, _alice, key, _ct)).IsSuccess.ShouldBeTrue();

        (await _store.Seen(key with { }, _ct)).IsSome.ShouldBeTrue();
        (await _store.Seen(new IdempotencyKey(Id<ITenant>.New(_time), "payments", "evt_1"), _ct)).IsNone.ShouldBeTrue();
        (await _store.Seen(new IdempotencyKey(_alice.Tenant, "orders", "evt_1"), _ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_key_is_rejected()
    {
        Should.Throw<ArgumentException>(() => new IdempotencyKey(Option.None, "", "k"));
        Should.Throw<ArgumentException>(() => new IdempotencyKey(Option.None, "s", " "));
    }

    [Fact]
    public async Task Every_event_is_recorded_with_the_operation_and_the_commit_time()
    {
        var job = OperationContext.Start(Actor.System("Jobs.Interest", "alice"), _alice.Tenant, _time).CausedBy(Id<ICausation>.New(_time));
        var account = Account.Open("A-1");
        account.Deposit(3).IsSuccess.ShouldBeTrue();
        _time.Advance(TimeSpan.FromMinutes(5));

        (await _store.Append(account, 0, job, Option.None, _ct)).IsSuccess.ShouldBeTrue();

        var recorded = _store.Recorded;
        recorded.Count.ShouldBe(2);
        recorded.Select(r => r.Event.Version).ShouldBe([1L, 2L]);
        recorded.ShouldAllBe(r => r.StreamId == account.Id.Value && r.RecordedAt == Start.AddMinutes(5) && r.Context == job);
        recorded.Select(r => r.EventId).Distinct().Count().ShouldBe(2);
        recorded.ShouldAllBe(r => r.EventId.Version == 7);
        recorded[1].Event.Event.ShouldBe(new Account.Deposited(3));
    }

    [Fact]
    public async Task Without_state_options_loads_replay()
    {
        var store = new InMemoryAggregateStore(_time);
        var account = Account.Open("A-1");
        (await store.Append(account, 0, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue();

        (await store.Load(account.Id, _alice, _ct)).GetValueOrThrow().Number.ShouldBe("A-1");

        store.StateLoads.ShouldBe(0);
    }

    [Fact]
    public async Task A_load_by_replay_aggregate_never_reads_state()
    {
        var counter = Counter.Start();
        counter.Bump();
        (await _store.Append(counter, 0, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue();

        var loaded = (await _store.Load(counter.Id, _alice, _ct)).GetValueOrThrow();

        loaded.Count.ShouldBe(1);
        _store.StateLoads.ShouldBe(0);
    }

    [Fact]
    public async Task The_stored_state_matches_a_replay_of_the_history()
    {
        var account = Account.Open("A-1");
        account.Deposit(4).IsSuccess.ShouldBeTrue();
        account.Renumber("A-9");
        await Committed(account);

        var fromState = (await _store.Load(account.Id, _alice, _ct)).GetValueOrThrow();
        var fromHistory = Account.Rehydrate(account.Id, _store.History(account.Id, _alice.Tenant));

        AggregateState.Write(fromState, TestJson.Default.Options).ShouldBe(AggregateState.Write(fromHistory, TestJson.Default.Options));
    }

    [Fact]
    public async Task A_lookup_finds_the_aggregate_by_its_key()
    {
        _store.DefineLookup<Account>("number", a => a.Number);
        var account = await Committed(Account.Open("A-1"));

        (await _store.Find<Account>("number", "A-1", _alice, _ct)).GetValueOrThrow().ShouldBe(account.Id);
        (await _store.Find<Account>("number", "A-2", _alice, _ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task A_changed_key_moves_the_lookup()
    {
        _store.DefineLookup<Account>("number", a => a.Number);
        var account = await Committed(Account.Open("A-1"));
        account.Renumber("A-9");
        await Committed(account, expected: 1);

        (await _store.Find<Account>("number", "A-1", _alice, _ct)).IsNone.ShouldBeTrue();
        (await _store.Find<Account>("number", "A-9", _alice, _ct)).GetValueOrThrow().ShouldBe(account.Id);
    }

    [Fact]
    public async Task A_taken_key_refuses_the_commit()
    {
        _store.DefineLookup<Account>("number", a => a.Number);
        await Committed(Account.Open("A-1"));
        var twin = Account.Open("A-1");

        var result = await _store.Append(twin, 0, _alice, Option.None, _ct);

        result.ShouldBe(Result.Failure(StoreErrors.LookupTaken("number", "A-1")));
        (await _store.Load(twin.Id, _alice, _ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task Lookups_are_tenant_scoped()
    {
        _store.DefineLookup<Account>("number", a => a.Number);
        var bob = OperationContext.Start(Actor.User("bob"), Id<ITenant>.New(_time), _time);
        await Committed(Account.Open("A-1"));

        (await _store.Append(Account.Open("A-1"), 0, bob, Option.None, _ct)).IsSuccess.ShouldBeTrue();
        (await _store.Find<Account>("number", "A-1", bob, _ct)).IsSome.ShouldBeTrue();
    }

    [Fact]
    public async Task An_undefined_lookup_is_a_programming_error()
        => (await Should.ThrowAsync<InvalidOperationException>(async () => await _store.Find<Account>("iban", "x", _alice, _ct)))
            .Message.ShouldContain("DefineLookup<Account>(\"iban\"");

    [Fact]
    public async Task A_cancelled_token_stops_every_operation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var account = Account.Open("A-1");

        await Should.ThrowAsync<OperationCanceledException>(async () => await _store.Load(account.Id, _alice, cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => _ = await _store.Append(account, 0, _alice, Option.None, cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await _store.Seen(new IdempotencyKey(Option.None, "s", "k"), cts.Token));
    }

    [Fact]
    public async Task Concurrent_appends_to_one_stream_commit_exactly_one()
    {
        var account = await Committed(Account.Open("A-1"));
        var contenders = new List<Account>();
        for (var i = 0; i < 16; i++)
        {
            var copy = (await _store.Load(account.Id, _alice, _ct)).GetValueOrThrow();
            copy.Deposit(1).IsSuccess.ShouldBeTrue();
            contenders.Add(copy);
        }

        var results = await Task.WhenAll(contenders.Select(c => Task.Run(async () => await _store.Append(c, 1, _alice, Option.None, _ct), _ct)));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Count(StoreErrors.IsVersionConflict).ShouldBe(15);
        _store.History(account.Id, _alice.Tenant).Length.ShouldBe(2);
    }
}
