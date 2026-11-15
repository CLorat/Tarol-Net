using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Contract;

/// <summary>
/// The <see cref="IOutbox"/> contract (docs/architecture/sagas.md §3–§4), written once and run against every store: rows in
/// the commit, heads per (tenant, type, stream, reaction) in version order, scheduled reactions by due time only, retries,
/// lease loss, dead letters out of the order and re-drive behind it. Each test gets an outbox of its own.
/// </summary>
public abstract class OutboxContract
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);
    private static readonly ActivitySource Source = new("Tarol.Persistence.Contract.Outbox");

    protected OutboxContract() => Time = new FakeTimeProvider(Start);

    /// <summary>The fake clock of the store (claims, due times, leases).</summary>
    protected FakeTimeProvider Time { get; }

    /// <summary>Creates a store with an outbox of its own (no rows of other tests), writing rows for <paramref name="reactions"/>.</summary>
    protected abstract Task<(IAggregateStore Store, IOutbox Outbox)> Create(TimeProvider time, IOutboxReactions reactions);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Welcome on Joined, Notify on Earned, Remind on Earned (scheduled: due Points hours after the start).</summary>
    private sealed class Reactions : IOutboxReactions
    {
        private static readonly string[] OnJoined = ["Welcome"];
        private static readonly string[] OnEarned = ["Notify", "Remind"];
        private static readonly string[] Twice = ["Notify", "Notify"];

        public bool ListTwice { get; init; }

        public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent) => domainEvent switch
        {
            Member.Joined => ListTwice ? Twice : OnJoined,
            Member.Earned => OnEarned,
            _ => ReadOnlySpan<string>.Empty,
        };

        public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction)
            => domainEvent is Member.Earned e && reaction == "Remind" ? Start.AddHours(e.Points) : null;

        public bool IsScheduled(string reaction) => reaction == "Remind";
    }

    private OperationContext Alice { get; } = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);

    private Task<(IAggregateStore Store, IOutbox Outbox)> Create(bool listTwice = false) => Create(Time, new Reactions { ListTwice = listTwice });

    private async Task<Member> Joined(IAggregateStore store, params int[] earnings)
    {
        var member = Member.Join(Id<Member>.New(), "");
        foreach (var points in earnings)
            member.Earn(points).ShouldBe(Result.Success());
        (await store.Append(member, 0, Alice, Option.None, Ct)).ShouldBe(Result.Success());
        AggregateChanges.Committed(member);
        return member;
    }

    private async Task Earn(IAggregateStore store, Member member, int points)
    {
        var expected = member.Version;
        member.Earn(points).ShouldBe(Result.Success());
        (await store.Append(member, expected, Alice, Option.None, Ct)).ShouldBe(Result.Success());
        AggregateChanges.Committed(member);
    }

    /// <summary>Claims every due row and returns those of <paramref name="reaction"/> (the others stay claimed).</summary>
    private static async Task<OutboxRow[]> Claim(IOutbox outbox, string reaction)
        => [.. (await outbox.Claim(100, Lease, Ct)).Where(r => r.Reaction == reaction)];

    [Fact]
    public async Task A_commit_writes_one_row_per_event_and_reaction_with_its_event_tenant_and_trace()
    {
        var (store, outbox) = await Create();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == Source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        Member member;
        string traceparent;
        using (var request = Source.StartActivity("POST /members")!)
        {
            member = await Joined(store, 2);
            traceparent = request.Id!;
        }

        var rows = await outbox.Claim(100, Lease, Ct);

        rows.Select(r => (r.Reaction, r.Event.Event.Event.Version)).ShouldBe([("Welcome", 1L), ("Notify", 2L)], ignoreOrder: true);   // Remind is due later
        rows.ShouldAllBe(r => r.Event.Event.StreamId == member.Id.Value && r.Event.StreamType == "member" && r.Tenant == Alice.Tenant
                              && r.Event.Event.Context == Alice && r.Attempts == 1 && r.LastError.IsNone);
        rows.ShouldAllBe(r => r.Traceparent == Option.Some(traceparent));
        (await outbox.Pending(Ct)).ShouldBe(3);
    }

    [Fact]
    public async Task One_streams_rows_reach_a_reaction_one_at_a_time_in_version_order()
    {
        var (store, outbox) = await Create();
        var member = await Joined(store, 1, 2);
        await Earn(store, member, 3);

        var versions = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var head = (await Claim(outbox, "Notify")).ShouldHaveSingleItem();
            versions.Add(head.Event.Event.Event.Version);
            (await Claim(outbox, "Notify")).ShouldBeEmpty();                 // the next waits while the head is claimed
            (await outbox.Complete(head, Ct)).ShouldBeTrue();
        }

        versions.ShouldBe([2L, 3L, 4L]);
        (await Claim(outbox, "Notify")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Scheduled_rows_run_by_due_time_and_neither_block_nor_wait()
    {
        var (store, outbox) = await Create();
        var member = await Joined(store, 3);                                // Remind due at +3 h
        await Earn(store, member, 1);                                       // Remind due at +1 h, a later row

        var now = await outbox.Claim(100, Lease, Ct);
        now.ShouldNotContain(r => r.Reaction == "Remind");
        now.Count(r => r.Reaction == "Notify").ShouldBe(1);                 // unscheduled rows are not held back by them

        Time.Advance(TimeSpan.FromHours(1));
        var first = (await Claim(outbox, "Remind")).ShouldHaveSingleItem();
        first.Event.Event.Event.Version.ShouldBe(3);                        // the later row, due first

        (await outbox.Complete(first, Ct)).ShouldBeTrue();

        Time.Advance(TimeSpan.FromHours(2));
        (await Claim(outbox, "Remind")).ShouldHaveSingleItem().Event.Event.Event.Version.ShouldBe(2);
    }

    [Fact]
    public async Task Retry_delays_the_head_keeps_the_error_and_holds_back_the_next_row()
    {
        var (store, outbox) = await Create();
        _ = await Joined(store, 1, 2);
        var head = (await Claim(outbox, "Notify")).Single();

        (await outbox.Retry(head, Time.GetUtcNow().AddSeconds(5), "Conflict Member.Busy", Ct)).ShouldBeTrue();
        (await Claim(outbox, "Notify")).ShouldBeEmpty();

        Time.Advance(TimeSpan.FromSeconds(5));
        var again = (await Claim(outbox, "Notify")).ShouldHaveSingleItem();
        (again.Id, again.Attempts, again.LastError).ShouldBe((head.Id, 2, Option.Some("Conflict Member.Busy")));
    }

    [Fact]
    public async Task A_worker_that_lost_its_lease_cannot_complete_retry_or_bury()
    {
        var (store, outbox) = await Create();
        _ = await Joined(store);
        var first = (await outbox.Claim(10, TimeSpan.FromSeconds(30), Ct)).Single();
        (await outbox.Claim(10, TimeSpan.FromSeconds(30), Ct)).ShouldBeEmpty();

        Time.Advance(TimeSpan.FromSeconds(31));
        var second = (await outbox.Claim(10, TimeSpan.FromSeconds(30), Ct)).Single();

        (second.Id, second.Attempts).ShouldBe((first.Id, 2));
        (await outbox.Complete(first, Ct)).ShouldBeFalse();
        (await outbox.Retry(first, Time.GetUtcNow(), "late", Ct)).ShouldBeFalse();
        (await outbox.Bury(first, "late", Ct)).ShouldBeFalse();
        (await outbox.Complete(second, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_buried_row_becomes_a_dead_letter_and_the_next_row_runs()
    {
        var (store, outbox) = await Create();
        var member = await Joined(store, 1, 2);
        var head = (await Claim(outbox, "Notify")).Single();

        (await outbox.Bury(head, "Conflict Note.Poison", Ct)).ShouldBeTrue();

        var dead = (await outbox.DeadLetters(10, Ct)).ShouldHaveSingleItem();
        dead.ShouldBe(new DeadLetter(head.Id, "Notify", Alice.Tenant, "member", member.Id.Value, head.Event.Event.EventId, 1,
            "Conflict Note.Poison", Time.GetUtcNow()));
        (await Claim(outbox, "Notify")).ShouldHaveSingleItem().Event.Event.Event.Version.ShouldBe(3);
        (await outbox.Pending(Ct)).ShouldBe(4);                             // Welcome, Notify v3, two Remind; the dead letter is not pending
    }

    [Fact]
    public async Task A_redriven_dead_letter_runs_again_behind_the_rows_that_waited()
    {
        var (store, outbox) = await Create();
        _ = await Joined(store, 1, 2);
        var poison = (await Claim(outbox, "Notify")).Single();
        (await outbox.Bury(poison, "Conflict Note.Poison", Ct)).ShouldBeTrue();
        var waited = (await Claim(outbox, "Notify")).Single();               // v3, now the head

        (await outbox.Redrive(poison.Id, Ct)).ShouldBeTrue();
        (await outbox.DeadLetters(10, Ct)).ShouldBeEmpty();
        (await Claim(outbox, "Notify")).ShouldBeEmpty();                    // behind v3, which is still claimed
        (await outbox.Complete(waited, Ct)).ShouldBeTrue();

        var redriven = (await Claim(outbox, "Notify")).ShouldHaveSingleItem();
        (redriven.Event.Event.EventId, redriven.Attempts).ShouldBe((poison.Event.Event.EventId, 1));
        redriven.Id.ShouldNotBe(poison.Id);
        (await outbox.Redrive(poison.Id, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_reaction_listed_twice_for_one_event_refuses_the_commit()
    {
        var (store, outbox) = await Create(listTwice: true);
        var member = Member.Join(Id<Member>.New(), "");

        await Should.ThrowAsync<InvalidOperationException>(async () => _ = await store.Append(member, 0, Alice, Option.None, Ct));

        (await outbox.Pending(Ct)).ShouldBe(0);
        (await store.Load(member.Id, Alice, Ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task WaitForRows_returns_after_the_timeout_at_the_latest()
    {
        var (_, outbox) = await Create();

        var wait = outbox.WaitForRows(TimeSpan.FromSeconds(10), Ct);
        Time.Advance(TimeSpan.FromSeconds(10));

        (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(30), TimeProvider.System, Ct))).ShouldBe(wait);
    }
}
