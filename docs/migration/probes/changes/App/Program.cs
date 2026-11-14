#pragma warning disable TVAL006 // the probe builds inputs as JSON would (trusting constructor); the pipeline validates them
using ArtFlow;
using Microsoft.Extensions.Time.Testing;
using Tarol.Changes;
using Tarol.Changes.Generated;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

var fails = 0;
void Check(bool ok, string what, object? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}{(detail is null ? "" : $": {detail}")}");
    if (!ok) fails++;
}

var start = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
var time = new FakeTimeProvider(start);
var store = new InMemoryAggregateStore(time);
var pipeline = new ChangePipeline(store, time);
var tenant = (Option<Id<ITenant>>)Id<ITenant>.New(start);
OperationContext User(string sub) => OperationContext.Start(Actor.User(sub), tenant, time);

Console.WriteLine("== create, close (user over REST, here in process) ==");
var auctionId = Id<Auction>.New(time);
var created = await pipeline.Run(new CreateAuction(auctionId, new Title("Sunflowers"), new Amount(100), start.AddDays(7)), User("alice"));
Check(created.TryGetValue(out ChangeOutcome c1) && c1.Version == 1, "create auction", created);
var tooSoon = await pipeline.Run(new CreateAuction(Id<Auction>.New(time), new Title("Iris"), new Amount(100), start.AddHours(1)), User("alice"));
Check(tooSoon.TryGetValue(out Violation? v0) && v0 is Invalid, "creation guard on an input field is Invalid (400)", v0);
var invalid = await pipeline.Run(new CreateAuction(Id<Auction>.New(time), new Title("x"), new Amount(0), start.AddDays(7)), User("alice"));
Check(invalid.TryGetValue(out Violation? v1) && v1 is Invalid { Errors.Count: 2 }, "invalid input: all fields at once, before any load", v1);
var loadsBefore = store.Loads;
var invalidClose = await pipeline.Run(new CloseAuction(Id<Auction>.Empty), User("alice"));
Check(invalidClose.TryGetValue(out Violation? v2) && v2 is Invalid && store.Loads == loadsBefore, "Id<T>.Empty rejected before load", v2);
var again = await pipeline.Run(new CreateAuction(auctionId, new Title("Sunflowers"), new Amount(100), start.AddDays(7)), User("alice"));
Check(again.TryGetValue(out Violation? v3) && v3 is Error { Code: "Auction.AlreadyExists" }, "same id twice: Conflict", v3);
var bid = await pipeline.Run(new PlaceBid(auctionId, new Amount(150)), User("bob"));
Check(bid.IsSuccess, "bid", bid);
var closed = await pipeline.Run(new CloseAuction(auctionId), User("alice"));
Check(closed.TryGetValue(out ChangeOutcome c2) && c2.Version == 3, "close", closed);
var closedTwice = await pipeline.Run(new CloseAuction(auctionId), User("alice"));
Check(closedTwice.TryGetValue(out Violation? v4) && v4 is Error { Type: ErrorType.Conflict, Code: "AuctionStatus.NotAllowed" }, "close twice: 409 from the lifecycle guard", v4);
var missing = await pipeline.Run(new CloseAuction(Id<Auction>.New(time)), User("alice"));
Check(missing.TryGetValue(out Violation? v5) && v5 is Error { Type: ErrorType.NotFound }, "missing: 404", v5);
var otherTenant = OperationContext.Start(Actor.User("alice"), (Option<Id<ITenant>>)Id<ITenant>.New(time), time);
var cross = await pipeline.Run(new CloseAuction(auctionId), otherTenant);
Check(cross.TryGetValue(out Violation? v6) && v6 is Error { Type: ErrorType.NotFound }, "another tenant: 404, not 403", v6);

Console.WriteLine("\n== settle (background job, system actor, TimeProvider) ==");
var job = OperationContext.Start(Actor.System("Jobs.AuctionSettlement"), tenant, time);
var early = await pipeline.Run(new SettleAuction(auctionId), job);
Check(early.TryGetValue(out Violation? v7) && v7 is Error { Code: "Auction.NotEnded" }, "before the end time: guard with now from the pipeline's TimeProvider", v7);
time.Advance(TimeSpan.FromDays(8));
var settled = await pipeline.Run(new SettleAuction(auctionId), job);
Check(settled.IsSuccess, "after the end time", settled);
var last = store.Log[^1];
Check(last.Context.Actor.Kind == ActorKind.System && last.Context.Actor.Id == "Jobs.AuctionSettlement", "events record the system actor", last.Context.Actor);
Check(last.RecordedAt == time.GetUtcNow(), "RecordedAt from the injected TimeProvider", last.RecordedAt);

Console.WriteLine("\n== reaction to Bid.Won (after commit, outbox, at least once) ==");
var won = store.Log.Single(e => e.Event.Event is Bid.Won);
Check(GeneratedChanges.ReactionsTo(won.Event.Event) is ["MarkPayoutDue"], "generated reaction table", string.Join(",", GeneratedChanges.ReactionsTo(won.Event.Event)));
Check(GeneratedChanges.ReactionsTo(new Bid.Lost()).Count == 0, "no reaction to Bid.Lost");
var r1 = await pipeline.React("MarkPayoutDue", won);
Check(r1.IsSuccess && DeliveryPolicy.Decide(r1, notFoundIsTransient: false).Decision == Delivery.Acknowledge, "first delivery commits, ack", r1);
var payoutEvent = store.Log[^1];
Check(payoutEvent.Context.CausationId.TryGetValue(out Id<ICausation> cause) && cause.Value == won.EventId, "causation = the triggering event id", cause);
Check(payoutEvent.Context.CorrelationId == won.Context.CorrelationId, "correlation carried over", payoutEvent.Context.CorrelationId);
Check(payoutEvent.Context.Actor is { Kind: ActorKind.System, Id: "Reactions.MarkPayoutDue" } && payoutEvent.Context.Actor.OnBehalfOf.IsNone,
    "reaction acts as System (on behalf of the job's original actor: none)", payoutEvent.Context.Actor);
var countBefore = store.Log.Count;
var r2 = await pipeline.React("MarkPayoutDue", won);
Check(r2.TryGetValue(out ChangeOutcome o2) && o2.Replayed && store.Log.Count == countBefore, "redelivery: same outcome, nothing written (key = event id)", r2);
Check(DeliveryPolicy.Decide(r2, false).Decision == Delivery.Acknowledge, "redelivery is acknowledged", DeliveryPolicy.Decide(r2, false).Reason);

Console.WriteLine("\n== optimistic concurrency: retry with a fresh load ==");
var a2 = Id<Auction>.New(time);
await pipeline.Run(new CreateAuction(a2, new Title("Irises"), new Amount(10), time.GetUtcNow().AddDays(2)), User("alice"));
store.BeforeNextAppend = () =>
{
    var other = Auction.Rehydrate(a2, [.. store.Log.Where(e => e.StreamId == a2.Value).Select(e => e.Event)]);
    other.PlaceBid("carol", new Amount(20), time.GetUtcNow());
    store.CommitBehindTheBack(other, User("carol"));
};
var loads = store.Loads;
var raced = await pipeline.Run(new PlaceBid(a2, new Amount(30)), User("bob"));
Check(raced.TryGetValue(out ChangeOutcome o3) && o3.Version == 3 && store.Loads == loads + 2, "lost the race once, reloaded, decided again on the new state", raced);
store.BeforeNextAppend = () =>
{
    var other = Auction.Rehydrate(a2, [.. store.Log.Where(e => e.StreamId == a2.Value).Select(e => e.Event)]);
    other.PlaceBid("carol", new Amount(1000), time.GetUtcNow());
    store.CommitBehindTheBack(other, User("carol"));
};
var rejected = await pipeline.Run(new PlaceBid(a2, new Amount(40)), User("bob"));
Check(rejected.TryGetValue(out Violation? v8) && v8 is Error { Code: "Bid.TooLow" }, "the retry re-runs the guards: the new state rejects it (409 Bid.TooLow, not a version conflict)", v8);
var ifMatch = await pipeline.Run(new PlaceBid(a2, new Amount(5000)), User("bob"), new ChangeOptions(ExpectedVersion: 3));
Check(ifMatch.TryGetValue(out Violation? v9) && v9 is Error { Code: "Stream.VersionConflict" }, "If-Match with a stale version: 409, no retry", v9);

Console.WriteLine("\n== idempotency key (REST Idempotency-Key, webhook delivery id) ==");
var key = new IdempotencyKey(tenant, "payments", "evt_1");
var pay = new RecordPayment(auctionId, new Amount(150), "pay_1");
var p1 = await pipeline.Run(pay, OperationContext.Start(Actor.Service("webhook:payments"), tenant, time), new ChangeOptions(Key: key));
var p2 = await pipeline.Run(pay, OperationContext.Start(Actor.Service("webhook:payments"), tenant, time), new ChangeOptions(Key: key));
Check(p1.IsSuccess && p2.TryGetValue(out ChangeOutcome o4) && o4.Replayed, "second delivery of evt_1 returns the stored outcome", p2);
var p3 = await pipeline.Run(pay with { PaymentId = "pay_2" }, OperationContext.Start(Actor.Service("webhook:payments"), tenant, time), new ChangeOptions(Key: key with { Key = "evt_2" }));
Check(p3.TryGetValue(out Violation? v10) && v10 is Error { Code: "Auction.NotSettled" }, "a new delivery after the auction is paid: guard says no", v10);
Check(DeliveryPolicy.Decide(p3, true).Decision == Delivery.Acknowledge, "guard Conflict in a reaction/webhook: acknowledge (recorded, not retried)", DeliveryPolicy.Decide(p3, true).Reason);

Console.WriteLine("\n== delivery policy table ==");
foreach (var (label, r, foreign) in new (string, Result<ChangeOutcome>, bool)[]
{
    ("Invalid", Invalid.Field("Amount", "Range"), false),
    ("VersionConflict", Error.Conflict("Stream.VersionConflict"), false),
    ("NotFound own", Error.NotFound("Payout.NotFound"), false),
    ("NotFound foreign", Error.NotFound("Auction.NotFound"), true),
    ("Forbidden", Error.Forbidden(), false),
    ("Unavailable", Error.Unavailable("Db.Down"), false),
    ("Internal", Error.Internal("Lifecycle.ForbiddenStep"), false),
})
    Console.WriteLine($"     {label,-17} -> {DeliveryPolicy.Decide(r, foreign)}");

Console.WriteLine("\n== typed dispatcher (saga outbox row -> change, no reflection) ==");
object fromOutbox = new SettleAuction(a2);
var d = await pipeline.Dispatch(fromOutbox, job);
Check(d.TryGetValue(out Violation? v11) && v11 is Error { Code: "Auction.NotEnded" }, "object -> generated switch -> Run", v11);
try { await pipeline.Dispatch("not a change", job); Check(false, "unknown type throws"); }
catch (ArgumentException e) { Check(true, "unknown type throws", e.Message); }

Console.WriteLine("\n== C-16: auto-load by default, special changes load themselves ==");
Check(loadsBefore >= 0 && closed.IsSuccess, "default: CloseAuction(Id) is loaded by the pipeline (no load code in the change)");
var art = Artwork.Register("Starry Night", time.GetUtcNow());
store.CommitBehindTheBack(art, User("alice"));
var notVerified = await pipeline.Run(new CreateAuctionOfArtwork(art.Id, new Title("Starry"), new Amount(10), time.GetUtcNow().AddDays(3)), User("alice"));
Check(notVerified.TryGetValue(out Violation? v12) && v12 is Error { Code: "Artwork.NotVerified" }, "[Read] Artwork loaded read-only and handed to the factory: unverified → 409", v12);
var noArt = await pipeline.Run(new CreateAuctionOfArtwork(Id<Artwork>.New(time), new Title("Starry"), new Amount(10), time.GetUtcNow().AddDays(3)), User("alice"));
Check(noArt.TryGetValue(out Violation? v13) && v13 is Error { Type: ErrorType.UnprocessableEntity, Code: "Artwork.NotFound" }, "[Read] of a missing aggregate: 422 Artwork.NotFound", v13);
var artNow = Artwork.Rehydrate(art.Id, [.. store.Log.Where(e => e.StreamId == art.Id.Value).Select(e => e.Event)]);
artNow.Verify();
store.CommitBehindTheBack(artNow, User("expert"));
var artLog = store.Log.Count;
var ofArt = await pipeline.Run(new CreateAuctionOfArtwork(art.Id, new Title("Starry"), new Amount(10), time.GetUtcNow().AddDays(3)), User("alice"));
Check(ofArt.IsSuccess && store.Log.Skip(artLog).All(e => e.StreamId != art.Id.Value), "verified → created; the read aggregate gets no events", ofArt);
ofArt.TryGetValue(out ChangeOutcome lotAuction);
store.Index("lot", "L-42", (Id<Auction>)lotAuction.StreamId, User("alice"));
var byLot = await pipeline.Run(new CloseByLot("L-42"), User("alice"));
Check(byLot.TryGetValue(out ChangeOutcome o5) && o5.StreamId == lotAuction.StreamId && o5.Version == 2, "ILoads: target found by lot number, then the normal pipeline", byLot);
var noLot = await pipeline.Run(new CloseByLot("L-0"), User("alice"));
Check(noLot.TryGetValue(out Violation? v14) && v14 is Error { Type: ErrorType.NotFound }, "ILoads: its own NotFound", v14);
var payoutId = Id<Payout>.New(time);
var ap1 = await pipeline.Run(new ApprovePayout(payoutId, auctionId.Value, 150), job);
Check(ap1.TryGetValue(out ChangeOutcome o6) && o6.Version == 2 && o6.Events == 2, "ILoads load-or-create: created and approved in one commit (expected version 0)", ap1);
var ap2 = await pipeline.Run(new ApprovePayout(payoutId, auctionId.Value, 150), job);
Check(ap2.TryGetValue(out Violation? v15) && v15 is Error { Code: "Payout.AlreadyApproved" }, "second call loads the existing payout: guard 409", v15);

Console.WriteLine("\n== allocations per change (in process, warm) ==");
var benchStore = new InMemoryAggregateStore(time);
var benchPipeline = new ChangePipeline(benchStore, time);
var ctx = User("alice");
var ids = new List<Id<Auction>>();
for (var i = 0; i < 2400; i++)
{
    var id = Id<Auction>.New(time);
    ids.Add(id);
    await benchPipeline.Run(new CreateAuction(id, new Title("Bench"), new Amount(10), time.GetUtcNow().AddDays(2)), ctx);
}
async Task<long> Measure(int offset, Func<Id<Auction>, ValueTask> body)
{
    for (var i = offset; i < offset + 200; i++) await body(ids[i]);
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = offset + 200; i < offset + 1200; i++) await body(ids[i]);
    return (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
}
var viaPipeline = await Measure(0, async id => await benchPipeline.Run(new CloseAuction(id), ctx));
// hand-written baseline: the same steps without the pipeline (validate, load, call, Prepare, append, Committed)
var handWritten = await Measure(1200, async id =>
{
    var input = new CloseAuction(id);
    var errors = default(Tarol.Validations.ValidationErrors);
    input.Validate(ref errors);
    if (errors.ToInvalid() is not null) return;
    if (!(await benchStore.Load(id, ctx, default)).TryGetValue(out Auction? a)) return;
    var expected = a.Version;
    if (!a.Close(time.GetUtcNow()).IsSuccess || !AggregateChanges.Prepare(a).IsSuccess) return;
    if ((await benchStore.Append(a, expected, ctx, Option.None, default)).IsSuccess) AggregateChanges.Committed(a);
});
Console.WriteLine($"     close via pipeline: {viaPipeline} B/op, hand-written: {handWritten} B/op (both include the in-memory store's replay)");
Console.WriteLine($"     pipeline overhead: {viaPipeline - handWritten} B/op (broken down in Bench/)");

Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAILED");
return fails;
