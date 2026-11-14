using ArtFlow;
using ArtFlow.A;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

BenchmarkRunner.Run<Lifecycle>(args: args);

[MemoryDiagnoser, InProcess]
public class Lifecycle
{
    private static readonly DateTimeOffset End = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Before = End.AddDays(-1), After = End.AddHours(1);
    private static readonly Id<Bidder> Alice = Id<Bidder>.New();
    private AuctionStatus _status = AuctionStatus.Open;

    /// <summary>Open, 2 bids, 1 withdrawal, close, settle (2 bid decisions): 8 events and one commit.</summary>
    [Benchmark(Baseline = true)]
    public long C_Baseline()
    {
        var a = ArtFlow.C.Auction.Open(Id<ArtFlow.C.Auction>.New(), 100, End);
        a.PlaceBid(Alice, 110, Before).TryGetValue(out ArtFlow.C.Bid? first);
        a.PlaceBid(Alice, 120, Before);
        first!.Withdraw();
        a.Close(After); a.Settle();
        return AggregateChanges.Prepare(a).IsSuccess ? a.Version : -1;
    }

    [Benchmark]
    public long A_EnumLifecycle()
    {
        var a = ArtFlow.A.Auction.Open(Id<ArtFlow.A.Auction>.New(), 100, End);
        a.PlaceBid(Alice, 110, Before).TryGetValue(out ArtFlow.A.Bid? first);
        a.PlaceBid(Alice, 120, Before);
        first!.Withdraw();
        a.Close(After); a.Settle();
        return AggregateChanges.Prepare(a).IsSuccess ? a.Version : -1;
    }

    [Benchmark]
    public long B_ClosedHierarchy()
    {
        var a = ArtFlow.B.Auction.Open(Id<ArtFlow.B.Auction>.New(), 100, End);
        a.PlaceBid(Alice, 110, Before).TryGetValue(out ArtFlow.B.Bid? first);
        a.PlaceBid(Alice, 120, Before);
        first!.Withdraw();
        a.Close(After); a.Settle();
        return AggregateChanges.Prepare(a).IsSuccess ? a.Version : -1;
    }

    [Benchmark] public bool Guard_Equals() => _status == AuctionStatus.Open;
    [Benchmark] public bool Guard_CanTransitionTo() => _status.CanTransitionTo(AuctionStatus.Closed);
    [Benchmark] public bool Guard_RequireTransitionTo() => _status.RequireTransitionTo(AuctionStatus.Closed).IsSuccess;
}
