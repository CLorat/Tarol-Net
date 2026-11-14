using System.Text.Json;
using System.Text.Json.Serialization;
using ArtFlow;
using ArtFlow.A;   // the generated extension members live next to the enum
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

var end = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
var afterEnd = end.AddHours(1);
var alice = Id<Bidder>.New();
var bob = Id<Bidder>.New();
var failures = 0;
void Check(string name, bool ok, object? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? "" : ": " + detail)}");
    if (!ok) failures++;
}
static Result Commit<T>(AggregateRoot<T> a) where T : AggregateRoot<T>
{
    var r = AggregateChanges.Prepare(a);
    if (r.IsSuccess) AggregateChanges.Committed(a);
    return r;
}

Console.WriteLine("== (a) enum + lifecycle ==");
{
    var a = ArtFlow.A.Auction.Open(Id<ArtFlow.A.Auction>.New(), 100, end);
    a.PlaceBid(alice, 120, end.AddDays(-1));
    a.PlaceBid(bob, 150, end.AddDays(-1));
    Check("open + 2 bids commits", Commit(a).IsSuccess);
    Check("close before end is the step's own guard", a.Close(end.AddMinutes(-1)) is { IsSuccess: false } r1 && r1.ToString().Contains("Auction.NotEnded"), a.Close(end.AddMinutes(-1)));
    Check("close", a.Close(afterEnd).IsSuccess && Commit(a).IsSuccess);
    var again = a.Close(afterEnd);
    Check("close twice: generated RequireTransitionTo", !again.IsSuccess, again);
    Check("settle", a.Settle().IsSuccess && Commit(a).IsSuccess, $"{a.Status}, winner bid {a.Bids.Single(b => b.Status == BidStatus.Won).Amount}");
    a.ReopenWithoutGuard();
    var forgotten = AggregateChanges.Prepare(a);
    Check("forgotten guard: Prepare reports the forbidden step", !forgotten.IsSuccess, forgotten.TryGetValue(out Violation? v) ? $"{v.GetType().Name} {(v as Error)?.Type} {(v as Error)?.Code}, \"{(v as Error)?.Detail}\"" : "");

    var b = ArtFlow.A.Auction.Open(Id<ArtFlow.A.Auction>.New(), 100, end);
    b.PlaceBid(alice, 120, end.AddDays(-1));
    Commit(b);
    Check("Open -> Closed -> Settled in one commit (every step is an edge)", b.CloseAndSettle(afterEnd).IsSuccess && Commit(b).IsSuccess, b.Status);
    var w = b.Bids[0].Withdraw();
    Check("won bid cannot be withdrawn (entity lifecycle)", !w.IsSuccess, w);

    // A stream recorded under 2025 rules: Settled -> Open is forbidden today.
    var id = Id<ArtFlow.A.Auction>.New();
    StreamEvent[] history =
    [
        new(1, id.Value, Guid.Empty, new ArtFlow.A.Auction.Opened(100, end)),
        new(2, id.Value, Guid.Empty, new ArtFlow.A.Auction.Closed(afterEnd)),
        new(3, id.Value, Guid.Empty, new ArtFlow.A.Auction.Settled(null, 100)),
        new(4, id.Value, Guid.Empty, new ArtFlow.A.Auction.Reopened()),
    ];
    var old = ArtFlow.A.Auction.Rehydrate(id, history);
    Check("replay of a forbidden historical step loads", old.Status == ArtFlow.A.AuctionStatus.Open && old.Version == 4, old.Status);
    Check("... and records no violation", AggregateChanges.Prepare(old).IsSuccess);
    Check("... and the next live step is checked normally", old.Close(afterEnd).IsSuccess && Commit(old).IsSuccess, old.Status);

    Console.WriteLine();
    Console.WriteLine(ArtFlow.A.AuctionStatusLifecycle.Diagram);
    Console.WriteLine(BidStatusLifecycle.Diagram);
    Console.WriteLine("edges: " + string.Join(", ", ArtFlow.A.AuctionStatusLifecycle.Edges.ToArray().Select(e => $"{e.From}->{e.To}")));
    Console.WriteLine($"terminal: Settled={ArtFlow.A.AuctionStatusLifecycle.IsTerminal(ArtFlow.A.AuctionStatus.Settled)}, initial={ArtFlow.A.AuctionStatusLifecycle.Initial}");
}

Console.WriteLine();
Console.WriteLine("== (b) closed hierarchy ==");
{
    var a = ArtFlow.B.Auction.Open(Id<ArtFlow.B.Auction>.New(), 100, end);
    a.PlaceBid(alice, 120, end.AddDays(-1));
    Commit(a);
    Check("close + settle", a.Close(afterEnd).IsSuccess && a.Settle().IsSuccess && Commit(a).IsSuccess, a.Describe());
    a.ReopenWithoutGuard();
    var forgotten = AggregateChanges.Prepare(a);
    Check("forgotten guard: Prepare reports the forbidden step", !forgotten.IsSuccess, forgotten.TryGetValue(out Violation? v) ? $"{v.GetType().Name} {(v as Error)?.Type} {(v as Error)?.Code}, \"{(v as Error)?.Detail}\"" : "");
}

Console.WriteLine();
Console.WriteLine("== (c) baseline ==");
{
    var a = ArtFlow.C.Auction.Open(Id<ArtFlow.C.Auction>.New(), 100, end);
    Commit(a);
    Check("close + settle", a.Close(afterEnd).IsSuccess && a.Settle().IsSuccess && Commit(a).IsSuccess);
    a.ReopenWithoutGuard();
    Check("forgotten guard is NOT caught: the bad step commits", Commit(a).IsSuccess, a.Status);
}

Console.WriteLine();
Console.WriteLine("== allocations per raised event (valid path, 2 events per round: Placed + Withdrawn) ==");
{
    const int n = 2000;
    long A()
    {
        var a = ArtFlow.A.Auction.Open(Id<ArtFlow.A.Auction>.New(), 0, end);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= n; i++) { a.PlaceBid(alice, i, end.AddDays(-1)).TryGetValue(out ArtFlow.A.Bid? bid); bid!.Withdraw(); }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    long B()
    {
        var a = ArtFlow.B.Auction.Open(Id<ArtFlow.B.Auction>.New(), 0, end);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= n; i++) { a.PlaceBid(alice, i, end.AddDays(-1)).TryGetValue(out ArtFlow.B.Bid? bid); bid!.Withdraw(); }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    long C()
    {
        var a = ArtFlow.C.Auction.Open(Id<ArtFlow.C.Auction>.New(), 0, end);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= n; i++) { a.PlaceBid(alice, i, end.AddDays(-1)).TryGetValue(out ArtFlow.C.Bid? bid); bid!.Withdraw(); }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    for (var warm = 0; warm < 3; warm++) { A(); B(); C(); }
    long a = A(), b = B(), c = C();
    Console.WriteLine($"(a) {a / (2.0 * n):F1} B/event   (b) {b / (2.0 * n):F1} B/event   (c) {c / (2.0 * n):F1} B/event   (a)-(c) = {(a - c) / (2.0 * n):F1} B/event");
    Check("step check allocates nothing on the valid path", a == c, $"{a} vs {c} bytes");

    long Guards()
    {
        var s = ArtFlow.A.AuctionStatus.Open;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var ok = 0;
        for (var i = 0; i < n; i++)
        {
            if (s.CanTransitionTo(ArtFlow.A.AuctionStatus.Closed)) ok++;
            if (s.RequireTransitionTo(ArtFlow.A.AuctionStatus.Closed).IsSuccess) ok++;
        }
        return GC.GetAllocatedBytesForCurrentThread() - before + (ok == 2 * n ? 0 : 1_000_000);
    }
    Guards();
    Check("CanTransitionTo / RequireTransitionTo (success) allocate nothing", Guards() == 0, Guards());
}

Console.WriteLine();
Console.WriteLine("== JSON (source-generated context, same assembly) ==");
{
    var settledA = new ArtFlow.A.Auction.Settled(alice, 150);
    Console.WriteLine("(a) event:  " + JsonSerializer.Serialize(settledA, Json.Default.SettledA));
    var rowA = new RowA(ArtFlow.A.AuctionStatus.Closed, [BidStatus.Active, BidStatus.Won]);
    var rowJson = JsonSerializer.Serialize(rowA, Json.Default.RowA);
    Console.WriteLine("(a) state:  " + rowJson);
    Check("(a) state round-trips", JsonSerializer.Deserialize(rowJson, Json.Default.RowA)! is { Status: ArtFlow.A.AuctionStatus.Closed } back && back.Bids.SequenceEqual(rowA.Bids));
    Check("(a) a state name that no longer exists fails to load", Throws(() => JsonSerializer.Deserialize("{\"status\":\"Cancelled\",\"bids\":[]}", Json.Default.RowA)));

    var rowB = new RowB(new ArtFlow.B.AuctionState.Settled(afterEnd, alice, 150));
    var rowBJson = JsonSerializer.Serialize(rowB, Json.Default.RowB);
    Console.WriteLine("(b) state:  " + rowBJson);
    Check("(b) state round-trips with its data", JsonSerializer.Deserialize(rowBJson, Json.Default.RowB)!.State is ArtFlow.B.AuctionState.Settled { Price: 150 });
    Console.WriteLine("(b) event:  " + JsonSerializer.Serialize(new ArtFlow.B.Auction.Closed(afterEnd), Json.Default.ClosedB));
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures;

static bool Throws(Action action) { try { action(); return false; } catch (JsonException) { return true; } }

public sealed record RowA(ArtFlow.A.AuctionStatus Status, BidStatus[] Bids);
public sealed record RowB(ArtFlow.B.AuctionState State);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ArtFlow.A.Auction.Settled), TypeInfoPropertyName = "SettledA")]
[JsonSerializable(typeof(ArtFlow.B.Auction.Closed), TypeInfoPropertyName = "ClosedB")]
[JsonSerializable(typeof(RowA))]
[JsonSerializable(typeof(RowB))]
internal sealed partial class Json : JsonSerializerContext;
