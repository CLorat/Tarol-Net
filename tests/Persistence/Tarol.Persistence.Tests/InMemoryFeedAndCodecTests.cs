using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Tests;

/// <summary>What the in-memory store adds in step 8: the feed, the codec round trip, the replay fallback, cheap state loads.</summary>
public sealed class InMemoryFeedAndCodecTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private OperationContext Context(Option<Id<ITenant>> tenant) => OperationContext.Start(Actor.User("alice"), tenant, _time);

    private static async Task<List<PositionedEvent>> Read(InMemoryAggregateStore feed, GlobalPosition after, int limit)
    {
        var list = new List<PositionedEvent>();
        await foreach (var e in feed.ReadAll(after, limit, TestContext.Current.CancellationToken))
            list.Add(e);
        return list;
    }

    [Fact]
    public async Task The_feed_returns_every_tenant_in_commit_order_and_pages()
    {
        var store = new InMemoryAggregateStore(_time);
        var a = Account.Open("A");
        a.Deposit(1).IsSuccess.ShouldBeTrue();
        (await store.Append(a, 0, Context(Id<ITenant>.New(_time)), Option.None, _ct)).IsSuccess.ShouldBeTrue();
        (await store.Append(Account.Open("B"), 0, Context(Option.None), Option.None, _ct)).IsSuccess.ShouldBeTrue();

        var all = await Read(store, GlobalPosition.Start, 10);
        all.Select(e => e.Event.StreamId).ShouldBe([a.Id.Value, a.Id.Value, all[2].Event.StreamId]);
        all.Select(e => e.Position).ShouldBeInOrder();
        all[0].Position.Transaction.ShouldBe(all[1].Position.Transaction);   // one commit
        all[2].Position.Transaction.ShouldBeGreaterThan(all[1].Position.Transaction);
        all.ShouldAllBe(e => e.StreamType == "account");

        (await Read(store, GlobalPosition.Start, 2)).Count.ShouldBe(2);
        (await Read(store, all[1].Position, 10)).ShouldBe([all[2]]);
        (await Read(store, all[2].Position, 10)).ShouldBeEmpty();
    }

    [Fact]
    public void Positions_order_by_transaction_then_position()
    {
        var early = new GlobalPosition(1, 9);
        var late = new GlobalPosition(2, 3);
        (early < late).ShouldBeTrue();
        (late > early).ShouldBeTrue();
        (early <= late).ShouldBeTrue();
        (new GlobalPosition(1, 9) <= early).ShouldBeTrue();
        (late >= early).ShouldBeTrue();
        GlobalPosition.Start.ShouldBe(new GlobalPosition(0, 0));
    }

    [Fact]
    public async Task With_a_codec_events_are_copies_read_back_from_json()
    {
        var codec = new EventCodec(new System.Text.Json.JsonSerializerOptions(CodecJson.Default.Options)).Add<Subscription>();
        var store = new InMemoryAggregateStore(_time, new InMemoryStoreOptions { Codec = codec });
        var s = Subscription.Start("pro");
        s.Cancel("price");
        (await store.Append(s, 0, Context(Option.None), Option.None, _ct)).IsSuccess.ShouldBeTrue();

        var history = store.History(s.Id, Option.None);
        history[1].Event.ShouldBe(new Subscription.Cancelled("price"));
        history[1].Event.ShouldNotBeSameAs(s.UncommittedChanges[1].Event);
        (await store.Load(s.Id, Context(Option.None), _ct)).GetValueOrThrow().CancelledFor.ShouldBe("price");   // from the state, with the codec's options
        store.StateLoads.ShouldBe(1);
    }

    [Fact]
    public async Task A_codec_without_the_aggregate_fails_the_append()
    {
        var codec = new EventCodec(new System.Text.Json.JsonSerializerOptions(CodecJson.Default.Options)).Add<Subscription>();
        var store = new InMemoryAggregateStore(_time, new InMemoryStoreOptions { Codec = codec });
        await Should.ThrowAsync<InvalidOperationException>(async () => _ = await store.Append(Account.Open("A"), 0, Context(Option.None), Option.None, _ct));
    }

    [Fact]
    public async Task A_state_that_does_not_parse_falls_back_to_a_replay()
    {
        var store = new InMemoryAggregateStore(_time, TestJson.Default.Options);
        var a = Account.Open("A");
        a.Deposit(7).IsSuccess.ShouldBeTrue();
        (await store.Append(a, 0, Context(Option.None), Option.None, _ct)).IsSuccess.ShouldBeTrue();
        store.CorruptStateForTests<Account>(a.Id, Option.None);

        (await store.Load(a.Id, Context(Option.None), _ct)).GetValueOrThrow().Balance.ShouldBe(7);
        store.StateLoads.ShouldBe(0);
    }

    [Fact]
    public async Task A_state_load_does_not_copy_a_long_history()
    {
        var store = new InMemoryAggregateStore(_time, TestJson.Default.Options);
        var a = Account.Open("A");
        for (var i = 0; i < 5_000; i++)
            a.Deposit(1).IsSuccess.ShouldBeTrue();
        (await store.Append(a, 0, Context(Option.None), Option.None, _ct)).IsSuccess.ShouldBeTrue();
        _ = await store.Load(a.Id, Context(Option.None), _ct);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = await store.Load(a.Id, Context(Option.None), _ct);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBeLessThan(5_000 * 8);   // the history alone would be 5,000 × 40 bytes
    }

    [Fact]
    public async Task Lookups_can_be_shared_and_defined_once()
    {
        var lookups = new StoreLookups().Define<Account>("number", x => x.Number);
        Should.Throw<InvalidOperationException>(() => lookups.Define<Account>("number", x => x.Number));
        var store = new InMemoryAggregateStore(_time, new InMemoryStoreOptions { StateOptions = TestJson.Default.Options, Lookups = lookups });
        var a = Account.Open("A-1");
        (await store.Append(a, 0, Context(Option.None), Option.None, _ct)).IsSuccess.ShouldBeTrue();
        (await store.Find<Account>("number", "A-1", Context(Option.None), _ct)).ShouldBe(a.Id);
        lookups.KeysOf(a).ShouldBe([("number", "A-1")]);
        lookups.IsDefined<Counter>("number").ShouldBeFalse();
    }
}
