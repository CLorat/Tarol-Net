using System.Diagnostics;
using ArtFlow;
using Microsoft.Extensions.Time.Testing;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;
using static Lab;

internal static class Functional
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    public static async Task<int> Run()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Tarol.Sagas" or "Probe.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        Console.WriteLine("\n## composition and policy (S-3, C-9 table)");
        Composition();
        Classifier();
        Console.WriteLine("\n## chain: commit → outbox row → worker → reaction change → its commit (Npgsql, DL-B)");
        await Chain(stopped);
        Console.WriteLine("\n## saga timeout with a fake TimeProvider, compensation (Npgsql)");
        await Timeout();
        Console.WriteLine("\n## a shortened deadline: head per (stream, reaction) (T-1) vs scheduled reactions ordered by time only (T-2)");
        await Rescheduled(ordered: true);
        await Rescheduled(ordered: false);
        Console.WriteLine("\n## redelivery: run, no Complete, lease expires → runs again, commits once (Npgsql)");
        await Redelivery();
        Console.WriteLine("\n## crash: a worker process dies between run and Complete (Environment.FailFast)");
        await Crash.Parent();
        foreach (var design in new[] { DeadLetterDesign.Column, DeadLetterDesign.Table })
        {
            Console.WriteLine($"\n## C-9: Conflict retries with backoff, dead letter after N, the stream is unblocked (DL-{(design == DeadLetterDesign.Column ? "A column" : "B table")})");
            await Poison(design);
        }
        Console.WriteLine("\n## ordering across streams: NotFound for a target another reaction creates (Npgsql)");
        await NotFoundOrdering();
        Console.WriteLine("\n## a row of a reaction that no longer exists (renamed without a pin)");
        await UnknownReaction();
        Console.WriteLine("\n## in-memory parity: InMemoryAggregateStore + InMemoryOutbox, same chain and timeout");
        await InMemory();
        Console.WriteLine("\n## SA-B: commands stored in the saga's event need generated polymorphism metadata");
        var (plain, modified, roundTrip) = SagaBCheck.Run();
        Check("without metadata the commands serialize empty (data loss, no exception)", plain.Contains("\"commands\":[{},{}]", StringComparison.Ordinal), plain);
        Check("with a generated modifier they round-trip", roundTrip && modified.Contains("\"$type\":\"request-payment\",\"auction\"", StringComparison.Ordinal), modified);

        Console.WriteLine(Failures == 0 ? "\nALL CHECKS PASSED" : $"\n{Failures} CHECK(S) FAILED");
        return Failures == 0 ? 0 : 1;
    }

    private static void Composition()
    {
        var combined = ReactionSet.Combine(GeneratedReactions.Set, OtherAssemblyReactions.Distinct);
        Check("Combine: names of both assemblies", combined.Names.Count == 7);
        Check("Combine: the table concatenates per event", combined.Table(new Bid.Won("bob", 1)).ToArray() is ["MarkPayoutDue", "StartSettlement", "NotifyBuyer"]);
        Check("one set: its own span (no copy)", GeneratedReactions.Set.Table(new Bid.Won("bob", 1)).Length == 2);
        try
        {
            _ = ReactionSet.Combine(GeneratedReactions.Set, OtherAssemblyReactions.Clashing);
            Check("Combine: a duplicate name throws", false);
        }
        catch (InvalidOperationException ex)
        {
            Check("Combine: a duplicate name throws", ex.Message.Contains("MarkPayoutDue", StringComparison.Ordinal));
        }
    }

    private static void Classifier()
    {
        Verdict V(Result<ChangeOutcome> r) => DeliveryPolicy.Classify(r);
        Check("classify: success / replayed → Done", V(new ChangeOutcome(Guid.Empty, 1, 1)) == Verdict.Done && V(new ChangeOutcome(Guid.Empty, 1, 1, true)) == Verdict.Done);
        Check("classify: Invalid → Bury", V(Invalid.Field("Amount", "Range")) == Verdict.Bury);
        Check("classify: any Conflict (guard, version, duplicate key) → Retry",
            V(Error.Conflict("Settlement.Underpaid")) == Verdict.Retry && V(StoreErrors.VersionConflict(1, 2)) == Verdict.Retry
            && V(StoreErrors.DuplicateKey(new IdempotencyKey(Option.None, "s", "k"))) == Verdict.Retry);
        Check("classify: NotFound / 422 → Retry (ordering), Forbidden → Bury, Unavailable → Retry",
            V(Error.NotFound("Settlement.NotFound")) == Verdict.Retry && V(Error.UnprocessableEntity("Artwork.NotFound")) == Verdict.Retry
            && V(Error.Forbidden("x")) == Verdict.Bury && V(Error.Unavailable("x")) == Verdict.Retry);
        var policy = new DeliveryPolicy();
        var delays = Enumerable.Range(1, 12).Select(a => policy.Backoff(42, a).TotalSeconds).ToArray();
        Check("backoff: 1, 2, 4 … s ±20 %, capped at 5 min, deterministic per (row, attempt)",
            delays[0] is >= 0.8 and <= 1.2 && delays[2] is >= 3.2 and <= 4.8 && delays[11] is >= 240 and <= 360 && policy.Backoff(42, 3) == policy.Backoff(42, 3),
            string.Join(", ", delays.Select(d => d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static async Task Chain(List<Activity> stopped)
    {
        var time = new FakeTimeProvider(T0);
        await using var env = await Env.Create("chain", time, DeadLetterDesign.Table);
        var alice = Alice(time);
        var auction = await env.AuctionWithBid(alice);
        Check("no rows for events without reactions", await env.Outbox.Pending(default) == 0);

        Activity? http;
        using (http = Http.StartActivity("POST /auctions/{id}/settle"))
            env.Pipeline.Run(new SettleAuction(auction), alice).GetValueOrThrow();
        Check("settle commits Bid.Won → two rows in the same commit (MarkPayoutDue, StartSettlement)", await env.Outbox.Pending(default) == 2);

        var verdicts = new List<(string, Verdict)>();
        var handled = await env.Worker(observed: (r, v, _) => verdicts.Add((r.Reaction, v))).Drain(default);
        var events = await env.Events();
        var won = events.Single(e => e.Event.Event is Bid.Won);
        var due = events.Single(e => e.Event.Event is Payout.Due);
        Check("worker ran both reactions", handled == 2 && verdicts.All(v => v.Item2 == Verdict.Done), string.Join(",", verdicts));
        Check("payout context: System actor on behalf of alice, same tenant and correlation, causation = Bid.Won event id",
            due.Context.Actor is { Kind: ActorKind.System, Id: "Reactions.MarkPayoutDue" } && due.Context.Actor.OnBehalfOf == "alice"
            && due.Context.Tenant == alice.Tenant && due.Context.CorrelationId == alice.CorrelationId
            && due.Context.CausationId == (Id<ICausation>)won.EventId, due.Context.ToString());
        var key = ReactionSet.Options("MarkPayoutDue", won).Key.GetValueOrThrow();
        Check("key (tenant, reaction:MarkPayoutDue, event id) stored with the payout's commit",
            (await env.Store.Seen(key, default)).TryGetValue(out StoredOutcome stored) && stored.StreamId == due.StreamId, $"{key.Scope} {key.Key}");
        Check("the saga started: Settlement with the auction's id, deadline row waits (due in 3 days)",
            (await env.Store.Load((Id<Settlement>)auction.Value, alice, default)).TryGetValue(out Settlement? s) && s.Status == SettlementStatus.AwaitingPayment
            && await env.Outbox.Pending(default) == 1
            && await env.Scalar("SELECT extract(epoch FROM due_at - $1)::bigint FROM {s}.outbox", time.GetUtcNow()) == 3 * 86400);

        var reactionActivities = stopped.Where(a => a.Source.Name == "Tarol.Sagas").ToList();
        var payoutActivity = reactionActivities.Single(a => (string?)a.GetTagItem("tarol.reaction") == "MarkPayoutDue");
        Check("tracing: one Consumer activity per row, linked (not parented) to the committing request",
            reactionActivities.Count == 2 && payoutActivity.Kind == ActivityKind.Consumer && payoutActivity.ParentId is null
            && payoutActivity.Links.Single().Context.TraceId == http!.TraceId && payoutActivity.TraceId != http.TraceId,
            $"link {payoutActivity.Links.Single().Context.TraceId}, own {payoutActivity.TraceId}");
        var stored_tp = await env.Scalar("SELECT count(*) FROM {s}.events WHERE type = 'payout' AND traceparent LIKE '00-' || $1 || '-%'", payoutActivity.TraceId.ToHexString());
        Check("tracing: the reaction's commit stores the worker activity's traceparent (the next hop links to it)", stored_tp == 1);
        Check("tracing: tags", (string?)payoutActivity.GetTagItem("messaging.message.id") == won.EventId.ToString()
            && (int?)payoutActivity.GetTagItem("tarol.attempt") == 1 && (string?)payoutActivity.GetTagItem("tarol.stream.type") == "auction");

        env.Pipeline.Run(new RecordPayment(auction, 120, "pay-1"), alice).GetValueOrThrow();
        _ = await env.Worker().Drain(default);
        Check("payment → ConfirmSettlementPayment → Completed",
            (await env.Store.Load((Id<Settlement>)auction.Value, alice, default)).TryGetValue(out Settlement? paid) && paid.Status == SettlementStatus.Completed);
        time.Advance(TimeSpan.FromDays(3));
        var late = new List<Verdict>();
        _ = await env.Worker(observed: (_, v, _) => late.Add(v)).Drain(default);
        var after = await env.Events();
        Check("the timeout after completion is a no-op success (no Expired, no Reopened, row completed)",
            late is [Verdict.Done] && !after.Any(e => e.Event.Event is Settlement.Expired or Auction.Reopened) && await env.Outbox.Pending(default) == 0);
    }

    private static async Task Timeout()
    {
        var time = new FakeTimeProvider(T0);
        await using var env = await Env.Create("timeout", time, DeadLetterDesign.Table);
        var alice = Alice(time);
        var auction = await env.AuctionWithBid(alice);
        env.Pipeline.Run(new SettleAuction(auction), alice).GetValueOrThrow();
        var worker = env.Worker();
        _ = await worker.Drain(default);
        time.Advance(TimeSpan.FromDays(2));
        Check("2 days later: nothing due", await worker.RunOnce(default) == 0);
        time.Advance(TimeSpan.FromDays(1));
        var ran = await worker.Drain(default);
        var events = await env.Events();
        var expired = events.Single(e => e.Event.Event is Settlement.Expired);
        var reopened = events.SingleOrDefault(e => e.Event.Event is Auction.Reopened);
        Check("3 days later: ExpireSettlement → Expired → ReopenAuction → Reopened (2 rows)", ran == 2 && reopened is not null);
        Check("compensation context: System on behalf of alice through three hops, correlation kept, causation = Expired",
            reopened!.Context.Actor is { Id: "Reactions.ReopenAuction" } && reopened.Context.Actor.OnBehalfOf == "alice"
            && reopened.Context.CorrelationId == alice.CorrelationId && reopened.Context.CausationId == (Id<ICausation>)expired.EventId);
        Check("Expire ran with now from the TimeProvider (RecordedAt = T0 + 3 d)", expired.RecordedAt == T0.AddDays(3));
        env.Pipeline.Run(new RecordPayment(auction, 120, "late"), alice).GetValueOrThrow();
        var late = new List<Verdict>();
        _ = await env.Worker(observed: (_, v, _) => late.Add(v)).Drain(default);
        Check("a late payment after expiry: Pay is a no-op success, not a Conflict that would dead-letter", late is [Verdict.Done]);

        // restart after the end (old S7): the re-opened auction is won again → the same saga id starts a second round
        var bob = alice with { Actor = Actor.User("carol") };
        env.Pipeline.Run(new PlaceBid(auction, 130), bob).GetValueOrThrow();
        env.Pipeline.Run(new SettleAuction(auction), alice).GetValueOrThrow();
        var round2 = new List<(string, Verdict)>();
        _ = await env.Worker(observed: (r, v, _) => round2.Add((r.Reaction, v))).Drain(default);
        Check("restart: a second Bid.Won restarts the ended saga in place (load-or-create), round 2 awaits payment",
            round2.Contains(("StartSettlement", Verdict.Done))
            && (await env.Store.Load((Id<Settlement>)auction.Value, alice, default)).TryGetValue(out Settlement? again)
            && again.Rounds == 2 && again.Status == SettlementStatus.AwaitingPayment && again.Buyer == "carol", string.Join(",", round2));
        var second = (await env.Events()).Last(e => e.Event.Event is Bid.Won);
        var other = second with { EventId = Guid.CreateVersion7() };          // another start message while round 2 runs (old N1)
        var n1 = (await env.Reactions.Run(env.Pipeline, "StartSettlement", other, default)).GetValueOrThrow();
        Check("a second start while running is a no-op success (old N1: the start handler ran again)",
            n1.Events == 0 && (await env.Store.Load((Id<Settlement>)auction.Value, alice, default)).TryGetValue(out Settlement? same) && same.Rounds == 2);
    }

    private static async Task Rescheduled(bool ordered)
    {
        var time = new FakeTimeProvider(T0);
        await using var env = await Env.Create(ordered ? "t1" : "t2", time, DeadLetterDesign.Table, unordered: ordered ? null : ["ExpireSettlement"]);
        var alice = Alice(time);
        var auction = await env.AuctionWithBid(alice);
        env.Pipeline.Run(new SettleAuction(auction), alice).GetValueOrThrow();
        var worker = env.Worker();
        _ = await worker.Drain(default);
        env.Pipeline.Run(new RescheduleSettlement((Id<Settlement>)auction.Value, T0.AddDays(1)), alice).GetValueOrThrow();   // shorten 3 d → 1 d
        _ = await worker.Drain(default);
        time.Advance(TimeSpan.FromDays(1));
        var atOneDay = await worker.Drain(default);
        var expiredAt = (await env.Events()).SingleOrDefault(e => e.Event.Event is Settlement.Expired)?.RecordedAt;
        if (ordered)
            Check("T-1 (head rule for every reaction): the 1-day timeout waits behind the 3-day row: nothing runs at +1 d", atOneDay == 0 && expiredAt is null);
        else
            Check("T-2 (scheduled reactions by time only): the 1-day timeout fires at +1 d; the 3-day row later is a stale no-op",
                atOneDay == 2 && expiredAt == T0.AddDays(1));
        time.Advance(TimeSpan.FromDays(2));
        _ = await worker.Drain(default);
        expiredAt = (await env.Events()).SingleOrDefault(e => e.Event.Event is Settlement.Expired)?.RecordedAt;
        Check($"{(ordered ? "T-1" : "T-2")}: at +3 d exactly one Expired, recorded at {expiredAt:MM-dd}", expiredAt == (ordered ? T0.AddDays(3) : T0.AddDays(1))
            && await env.Outbox.Pending(default) == 0);
    }

    private static async Task Redelivery()
    {
        var time = new FakeTimeProvider(T0);
        await using var env = await Env.Create("redeliver", time, DeadLetterDesign.Table);
        var alice = Alice(time);
        var auction = await env.AuctionWithBid(alice);
        env.Pipeline.Run(new SettleAuction(auction), alice).GetValueOrThrow();
        var first = (await env.Outbox.Claim(10, TimeSpan.FromSeconds(30), default)).Single(r => r.Reaction == "MarkPayoutDue");
        var run1 = (await env.Reactions.Run(env.Pipeline, first.Reaction, first.Event, default)).GetValueOrThrow();
        // the worker "dies" here: no Complete
        time.Advance(TimeSpan.FromSeconds(31));
        var second = (await env.Outbox.Claim(10, TimeSpan.FromSeconds(30), default)).Single(r => r.Reaction == "MarkPayoutDue");
        var run2 = (await env.Reactions.Run(env.Pipeline, second.Reaction, second.Event, default)).GetValueOrThrow();
        Check("second delivery: attempts 2, the key returns the first outcome without running", second.Attempts == 2 && run2.Replayed && run2.StreamId == run1.StreamId);
        Check("the first worker's Complete fails (lease lost); the second completes", !await env.Outbox.Complete(first, default) && await env.Outbox.Complete(second, default));
        Check("one payout", await env.Scalar("SELECT count(DISTINCT stream_id) FROM {s}.events WHERE type = 'payout'") == 1);
    }

    private static async Task Poison(DeadLetterDesign design)
    {
        var time = new FakeTimeProvider(T0);
        await using var env = await Env.Create(design == DeadLetterDesign.Column ? "dla" : "dlb", time, design);
        var alice = Alice(time);
        var auction = await env.AuctionWithBid(alice);
        env.Pipeline.Run(new NoteAuction(auction, "poison"), alice).GetValueOrThrow();
        env.Pipeline.Run(new NoteAuction(auction, "ok"), alice).GetValueOrThrow();
        var options = new WorkerOptions { Policy = new DeliveryPolicy { MaxAttempts = 4 } };
        var verdicts = new List<(int, Verdict)>();
        var worker = env.Worker(options, (r, v, _) => verdicts.Add((r.Attempts, v)));
        var delays = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            Check($"attempt {i + 1}: only the head (poison) is claimable", await worker.RunOnce(default) == 1);
            var wait = await env.Scalar("SELECT coalesce(extract(epoch FROM (due_at - $1)) * 1000, -1)::bigint FROM {s}.outbox WHERE id = (SELECT min(id) FROM {s}.outbox)", time.GetUtcNow());
            delays.Add(wait / 1000.0);
            time.Advance(TimeSpan.FromSeconds(10));
        }
        Check("three retries with growing backoff, then buried at attempt 4",
            verdicts.Select(v => v.Item2).SequenceEqual([Verdict.Retry, Verdict.Retry, Verdict.Retry, Verdict.Bury]),
            "retry delays (s): " + string.Join(", ", delays.Take(3).Select(d => d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));
        var dead = await env.Outbox.DeadLetters(default);
        Check("dead letter visible with attempts and the guard's code", dead is [{ Attempts: 4, LastError: "Conflict Note.Poison" }], dead.FirstOrDefault()?.ToString());
        _ = await worker.Drain(default);
        Check("the stream is unblocked: 'ok' acknowledged after the burial",
            (await env.Store.Load(auction, alice, default)).TryGetValue(out Auction? a) && a.Acknowledged is ["ok"]);

        // re-drive while a newer row of the same (stream, reaction) waits
        time.Advance(TimeSpan.FromSeconds(10));
        env.Pipeline.Run(new NoteAuction(auction, "later"), alice).GetValueOrThrow();
        Check("re-drive", await env.Outbox.Redrive(dead[0].Id, default));
        var next = await env.Outbox.Claim(10, TimeSpan.FromMinutes(1), default);
        var text = next.Select(r => ((Auction.Noted)r.Event.Event.Event).Text).ToArray();
        Check(design == DeadLetterDesign.Column
                ? "DL-A: the re-driven row keeps its id: it is the head again and blocks 'later'"
                : "DL-B: the re-driven row is new: 'later' runs first, the re-driven row waits behind it",
            design == DeadLetterDesign.Column ? text is ["poison"] : text is ["later"], string.Join(",", text));
    }

    private static async Task NotFoundOrdering()
    {
        var time = new FakeTimeProvider(T0);
        await using var env = await Env.Create("notfound", time, DeadLetterDesign.Table);
        var alice = Alice(time);
        var auction = await env.AuctionWithBid(alice);
        env.Pipeline.Run(new RecordPayment(auction, 120, "early"), alice).GetValueOrThrow();   // paid before the saga exists
        var seen = new List<(string, Verdict, string?)>();
        var worker = env.Worker(observed: (r, v, _) => seen.Add((r.Reaction, v, r.LastError)));
        _ = await worker.Drain(default);
        env.Pipeline.Run(new SettleAuction(auction), alice).GetValueOrThrow();
        _ = await worker.Drain(default);
        time.Advance(TimeSpan.FromSeconds(2));
        _ = await worker.Drain(default);
        Check("ConfirmSettlementPayment: NotFound → Retry, then Done once StartSettlement created the saga",
            seen.Where(s => s.Item1 == "ConfirmSettlementPayment").Select(s => s.Item2).SequenceEqual([Verdict.Retry, Verdict.Done])
            && (await env.Store.Load((Id<Settlement>)auction.Value, alice, default)).TryGetValue(out Settlement? s) && s.Status == SettlementStatus.Completed,
            string.Join(" | ", seen));
    }

    private static async Task UnknownReaction()
    {
        var time = new FakeTimeProvider(T0);
        await using var env = await Env.Create("unknown", time, DeadLetterDesign.Table);
        var alice = Alice(time);
        _ = await env.AuctionWithBid(alice);
        _ = await env.Scalar("""
            WITH i AS (INSERT INTO {s}.outbox (tenant, reaction, type, stream_id, version, event_id, due_at)
                       SELECT tenant, 'RenamedAway', type, stream_id, version, event_id, $1 FROM {s}.events LIMIT 1 RETURNING 1)
            SELECT count(*) FROM i
            """, time.GetUtcNow());
        var worker = env.Worker(new WorkerOptions { Policy = new DeliveryPolicy { MaxAttempts = 2 } });
        _ = await worker.RunOnce(default);
        time.Advance(TimeSpan.FromSeconds(5));
        _ = await worker.RunOnce(default);
        var dead = await env.Outbox.DeadLetters(default);
        Check("an unknown name throws in Run → retried like an exception, then buried with the message",
            dead is [{ Reaction: "RenamedAway" }] && dead[0].LastError!.StartsWith("InvalidOperationException: No reaction named", StringComparison.Ordinal), dead.FirstOrDefault()?.LastError);
    }

    private static async Task InMemory()
    {
        var time = new FakeTimeProvider(T0);
        var store = new InMemoryAggregateStore(time, new InMemoryStoreOptions { Codec = Codec() });
        var outbox = new InMemoryOutbox(store, GeneratedReactions.Set, time);
        var pipeline = new ChangePipeline(store, time);
        var worker = new ReactionWorker(outbox, GeneratedReactions.Set, pipeline, time, new WorkerOptions());
        var alice = Alice(time);
        var auction = Auction.Create("alice", 100, time.GetUtcNow());
        _ = auction.PlaceBid("bob", 120, time.GetUtcNow());
        (await store.Append(auction, 0, alice, Option.None, default)).GetValueOrThrow();
        (await pipeline.Run(new SettleAuction(auction.Id), alice)).GetValueOrThrow();
        Check("in memory: settle → 2 reactions → payout and saga", await worker.Drain(default) == 2
            && store.Recorded.Single(e => e.Event.Event is Payout.Due).Context.CausationId == (Id<ICausation>)store.Recorded.Single(e => e.Event.Event is Bid.Won).EventId);
        time.Advance(TimeSpan.FromDays(2));
        Check("in memory: 2 days later nothing due", await worker.Drain(default) == 0);
        time.Advance(TimeSpan.FromDays(1));
        Check("in memory: 3 days later the timeout and its compensation run", await worker.Drain(default) == 2
            && (await store.Load(auction.Id, alice, default)).TryGetValue(out Auction? a) && a.ReopenCount == 1);
        var redelivered = (await GeneratedReactions.Set.Run(pipeline, "MarkPayoutDue", store.Recorded.Single(e => e.Event.Event is Bid.Won), default)).GetValueOrThrow();
        Check("in memory: a redelivery is replayed by the key", redelivered.Replayed);
    }
}
