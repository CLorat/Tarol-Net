using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence.Contract;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql.IntegrationTests;

/// <summary>The outbox (step 8C): rows in the commit, claims with SKIP LOCKED, one stream's rows in order.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlOutboxTests(Postgres postgres)
{
    private static readonly string[] OnEarned = ["Notify", "Leaderboard"];
    private static readonly string[] OnJoined = ["Welcome"];
    private static readonly string[] Twice = ["Notify", "Notify"];
    private static readonly string[] OnlyNotify = ["Notify"];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReadOnlySpan<string> Reactions(DomainEvent e) => e switch
    {
        Member.Joined => OnJoined,
        Member.Earned => OnEarned,
        _ => ReadOnlySpan<string>.Empty,
    };

    private OperationContext Context => OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(_time), _time);

    private Task<NpgsqlAggregateStore> Store(ReactionTable? reactions = null, StoreLookups? lookups = null)
        => postgres.IsolatedStore(_time, lookups, reactions: reactions ?? Reactions);

    private static async Task<Member> Joined(NpgsqlAggregateStore store, OperationContext context, int earnings, string email = "")
    {
        var member = Member.Join(Id<Member>.New(), email);
        for (var i = 1; i <= earnings; i++)
            member.Earn(i).IsSuccess.ShouldBeTrue();
        (await store.Append(member, 0, context, Option.None, Ct)).ShouldBe(Result.Success());
        AggregateChanges.Committed(member);
        return member;
    }

    [Fact]
    public async Task A_commit_writes_one_row_per_event_and_reaction()
    {
        var store = await Store();
        var context = Context;
        var member = await Joined(store, context, earnings: 2);

        var heads = await store.Outbox.Claim(100, TimeSpan.FromMinutes(1), Ct);
        heads.Select(r => (r.Reaction, r.Event.Event.Event.Version)).ShouldBe([("Welcome", 1L), ("Notify", 2L), ("Leaderboard", 2L)], ignoreOrder: true);
        (await store.Outbox.Pending(Ct)).ShouldBe(5);

        var rows = new List<OutboxRow>(heads);
        foreach (var head in heads)
            (await store.Outbox.Complete(head, Ct)).ShouldBeTrue();
        rows.AddRange(await store.Outbox.Claim(100, TimeSpan.FromMinutes(1), Ct));

        rows.Select(r => (r.Reaction, r.Event.Event.Event.Version)).ShouldBe(
            [("Welcome", 1L), ("Notify", 2L), ("Leaderboard", 2L), ("Notify", 3L), ("Leaderboard", 3L)], ignoreOrder: true);
        rows.ShouldAllBe(r => r.Event.Event.StreamId == member.Id.Value && r.Event.Event.Context == context && r.Tenant == context.Tenant);
        rows.ShouldAllBe(r => r.Attempts == 1 && r.LastError.IsNone);
        rows.Single(r => r.Reaction == "Welcome").Event.Event.Event.Event.ShouldBe(new Member.Joined(""));
    }

    [Fact]
    public async Task A_refused_commit_writes_no_rows()
    {
        var store = await Store(lookups: new StoreLookups().Define<Member>("email", m => m.Email));
        var context = Context;
        var member = await Joined(store, context, earnings: 0, email: "a@x");
        (await store.Outbox.Pending(Ct)).ShouldBe(1);

        StoreErrors.IsVersionConflict(await store.Append(Member.Join(member.Id, "b@x"), 0, context, Option.None, Ct)).ShouldBeTrue();
        (await store.Append(Member.Join(Id<Member>.New(), "a@x"), 0, context, Option.None, Ct))
            .ShouldBe(Result.Failure(StoreErrors.LookupTaken("email", "a@x")));

        (await store.Outbox.Pending(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Without_a_reaction_table_no_rows_are_written()
    {
        var store = await postgres.IsolatedStore(_time);
        await Joined(store, Context, earnings: 1);
        (await store.Outbox.Pending(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_reaction_listed_twice_for_one_event_is_a_programming_error()
    {
        var store = await Store(e => e is Member.Joined ? Twice : ReadOnlySpan<string>.Empty);
        await Should.ThrowAsync<InvalidOperationException>(() => Joined(store, Context, earnings: 0));
        (await store.Outbox.Pending(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Four_workers_claim_every_row_exactly_once_in_stream_order()
    {
        var store = await Store(e => e is Member.Earned ? OnlyNotify : ReadOnlySpan<string>.Empty);
        var context = Context;
        for (var i = 0; i < 10; i++)
            await Joined(store, context, earnings: 5);

        var processed = new ConcurrentQueue<(Guid Stream, long Version)>();
        var inFlight = new ConcurrentDictionary<Guid, int>();
        var maxInFlight = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var idle = 0;
            while (idle < 5)
            {
                var rows = await store.Outbox.Claim(3, TimeSpan.FromMinutes(1), Ct);
                if (rows.Count == 0)
                {
                    idle++;
                    await Task.Delay(10, Ct);
                    continue;
                }
                idle = 0;
                foreach (var row in rows)
                {
                    var stream = row.Event.Event.StreamId;
                    var now = inFlight.AddOrUpdate(stream, 1, (_, n) => n + 1);
                    InterlockedMax(ref maxInFlight, now);
                    processed.Enqueue((stream, row.Event.Event.Event.Version));
                    await Task.Delay(1, Ct);
                    inFlight.AddOrUpdate(stream, 0, (_, n) => n - 1);
                    (await store.Outbox.Complete(row, Ct)).ShouldBeTrue();
                }
            }
        }, Ct)));

        processed.Count.ShouldBe(50);
        processed.Distinct().Count().ShouldBe(50);
        processed.GroupBy(p => p.Stream).ShouldAllBe(g => g.Select(p => p.Version).SequenceEqual(g.Select(p => p.Version).Order()));
        maxInFlight.ShouldBe(1);
        (await store.Outbox.Pending(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task The_next_row_of_a_stream_waits_for_the_head()
    {
        var store = await Store(e => e is Member.Earned ? OnlyNotify : ReadOnlySpan<string>.Empty);
        await Joined(store, Context, earnings: 2);

        var head = (await store.Outbox.Claim(10, TimeSpan.FromMinutes(1), Ct)).Single();
        head.Event.Event.Event.Version.ShouldBe(2);
        (await store.Outbox.Claim(10, TimeSpan.FromMinutes(1), Ct)).ShouldBeEmpty();   // version 3 waits

        (await store.Outbox.Complete(head, Ct)).ShouldBeTrue();
        (await store.Outbox.Claim(10, TimeSpan.FromMinutes(1), Ct)).Single().Event.Event.Event.Version.ShouldBe(3);
    }

    [Fact]
    public async Task An_expired_lease_is_claimed_again_and_the_first_worker_cannot_complete()
    {
        var store = await Store(e => e is Member.Joined ? OnJoined : ReadOnlySpan<string>.Empty);
        await Joined(store, Context, earnings: 0);

        var first = (await store.Outbox.Claim(10, TimeSpan.FromSeconds(30), Ct)).Single();
        (await store.Outbox.Claim(10, TimeSpan.FromSeconds(30), Ct)).ShouldBeEmpty();
        _time.Advance(TimeSpan.FromSeconds(31));
        var second = (await store.Outbox.Claim(10, TimeSpan.FromSeconds(30), Ct)).Single();

        second.Id.ShouldBe(first.Id);
        second.Attempts.ShouldBe(2);
        (await store.Outbox.Complete(first, Ct)).ShouldBeFalse();
        (await store.Outbox.Fail(first, _time.GetUtcNow(), "late", Ct)).ShouldBeFalse();
        (await store.Outbox.Complete(second, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Fail_delays_the_row_and_keeps_the_error()
    {
        var store = await Store(e => e is Member.Joined ? OnJoined : ReadOnlySpan<string>.Empty);
        await Joined(store, Context, earnings: 0);

        var row = (await store.Outbox.Claim(10, TimeSpan.FromMinutes(1), Ct)).Single();
        (await store.Outbox.Fail(row, _time.GetUtcNow().AddMinutes(5), "Auction.NotSettled", Ct)).ShouldBeTrue();
        (await store.Outbox.Claim(10, TimeSpan.FromMinutes(1), Ct)).ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(5));
        var retry = (await store.Outbox.Claim(10, TimeSpan.FromMinutes(1), Ct)).Single();
        retry.Attempts.ShouldBe(2);
        retry.LastError.ShouldBe("Auction.NotSettled");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
