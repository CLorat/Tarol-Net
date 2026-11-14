using System.Text;
using System.Text.Json;
using Tarol.Primitives.Tests.Fixtures;
using Tarol.Primitives.Tests.Lifecycles;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests;

/// <summary>
/// Lifecycles (step 6 part C): generated guards, the live-only step check reported by Prepare, creation in the initial
/// state, replay of history that today's lifecycle forbids, and stored state.
/// </summary>
public class LifecycleTests
{
    private static readonly DateTimeOffset End = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterEnd = End.AddHours(1);
    private static readonly DateTimeOffset BeforeEnd = End.AddDays(-1);
    private static readonly Id<Bidder> Alice = Id<Bidder>.New();
    private static readonly Id<Bidder> Bob = Id<Bidder>.New();

    private static Result Forbidden(string detail) => Result.Failure(Error.Internal("Lifecycle.ForbiddenStep", detail));

    private static Auction OpenWithBids()
    {
        var auction = Auction.Open(Id<Auction>.New(), 100, End);
        auction.PlaceBid(Alice, 120, BeforeEnd).IsSuccess.ShouldBeTrue();
        auction.PlaceBid(Bob, 150, BeforeEnd).IsSuccess.ShouldBeTrue();
        return Commit(auction);
    }

    private static T Commit<T>(T aggregate) where T : AggregateRoot<T>
    {
        AggregateChanges.Prepare(aggregate).IsSuccess.ShouldBeTrue();
        AggregateChanges.Committed(aggregate);
        return aggregate;
    }

    // ── generated class ──

    [Fact]
    public void The_Generated_Lifecycle_Describes_The_Declaration()
    {
        AuctionStatusLifecycle.Initial.ShouldBe(AuctionStatus.Open);
        AuctionStatusLifecycle.IsEdge(AuctionStatus.Open, AuctionStatus.Closed).ShouldBeTrue();
        AuctionStatusLifecycle.IsEdge(AuctionStatus.Open, AuctionStatus.Settled).ShouldBeFalse();
        AuctionStatusLifecycle.IsEdge(AuctionStatus.Settled, AuctionStatus.Open).ShouldBeFalse();
        AuctionStatusLifecycle.IsTerminal(AuctionStatus.Settled).ShouldBeTrue();
        AuctionStatusLifecycle.IsTerminal(AuctionStatus.Closed).ShouldBeFalse();
        AuctionStatusLifecycle.Edges.ToArray().ShouldBe([(AuctionStatus.Open, AuctionStatus.Closed), (AuctionStatus.Closed, AuctionStatus.Settled)]);
        BidStatusLifecycle.Edges.Length.ShouldBe(3);
        TicketStatusLifecycle.Initial.ShouldBe(TicketStatus.Draft);
        TicketPriorityLifecycle.IsEdge(Ticket.Priority.Normal, Ticket.Priority.High).ShouldBeTrue();
    }

    [Fact]
    public void The_Diagram_Labels_The_Edges_With_Their_Transition_Events()
    {
        AuctionStatusLifecycle.Diagram.ShouldBe("stateDiagram-v2\n    [*] --> Open\n    Open --> Closed : Closed\n    Closed --> Settled : Settled\n    Settled --> [*]\n");
        BidStatusLifecycle.Diagram.ShouldContain("Active --> Won : Won\n");
        PayoutStatusLifecycle.Diagram.ShouldContain("    None --> Due\n"); // no [Transition]: unlabelled
    }

    // ── guards ──

    [Fact]
    public void RequireTransitionTo_Returns_The_Generated_Conflict()
    {
        var auction = OpenWithBids();
        auction.Close(AfterEnd).IsSuccess.ShouldBeTrue();
        Commit(auction);

        auction.Close(AfterEnd).ShouldBe(Result.Failure(Error.Conflict("AuctionStatus.NotAllowed", "Closed -> Closed")));
        auction.UncommittedChanges.Length.ShouldBe(0);
    }

    [Fact]
    public void A_Condition_Of_The_Step_Stays_A_Guard_Of_Its_Own()
        => OpenWithBids().Close(End.AddMinutes(-1)).ShouldBe(Result.Failure(Error.Conflict("Auction.NotEnded")));

    [Fact]
    public void CanTransitionTo_Lets_A_Behaviour_Choose_Its_Own_Code()
    {
        OpenWithBids().Settle().ShouldBe(Result.Failure(Error.Conflict("Auction.NotClosed")));
        new Ticket.Priority().CanTransitionTo(Ticket.Priority.High).ShouldBeTrue();
    }

    [Fact]
    public void A_Valid_Flow_Commits()
    {
        var auction = OpenWithBids();
        auction.Close(AfterEnd).IsSuccess.ShouldBeTrue();
        Commit(auction);
        auction.Settle().IsSuccess.ShouldBeTrue();
        Commit(auction);
        auction.Pay().IsSuccess.ShouldBeTrue();
        Commit(auction);

        auction.Status.ShouldBe(AuctionStatus.Settled);
        auction.Payout.ShouldBe(PayoutStatus.Paid);
        auction.Bids.Single(static b => b.Status == BidStatus.Won).Amount.ShouldBe(150);
        auction.Bids.Single(static b => b.Status == BidStatus.Lost).Amount.ShouldBe(120);
    }

    // ── the step check ──

    [Fact]
    public void A_Forgotten_Guard_Is_Reported_At_Commit()
    {
        var auction = OpenWithBids();
        auction.CloseAndSettle(AfterEnd).IsSuccess.ShouldBeTrue();
        Commit(auction);

        auction.ReopenWithoutGuard();          // Raise succeeds: the step is only recorded

        AggregateChanges.Prepare(auction).ShouldBe(Forbidden("Auction.Status: Settled -> Open by Reopened"));
        auction.Status.ShouldBe(AuctionStatus.Open);
    }

    [Fact]
    public void Two_Steps_In_One_Commit_Are_Each_An_Edge()
    {
        // first/last would see Open -> Settled, which is not an edge; every step is checked instead
        var auction = OpenWithBids();
        auction.CloseAndSettle(AfterEnd).IsSuccess.ShouldBeTrue();
        auction.UncommittedChanges.Length.ShouldBe(4); // Closed, Won, Lost, Settled
        AggregateChanges.Prepare(auction).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_Step_Check_Runs_Before_The_Invariants()
    {
        var auction = OpenWithBids();
        auction.SettleWithoutGuard();          // Open -> Settled, and both bids are still active (an invariant fails too)

        AggregateChanges.Prepare(auction).ShouldBe(Forbidden("Auction.Status: Open -> Settled by Settled"));
    }

    [Fact]
    public void The_First_Forbidden_Step_Of_A_Commit_Is_Kept()
    {
        var auction = OpenWithBids();
        auction.SettleWithoutGuard();
        auction.ReopenWithoutGuard();          // Settled -> Open: also forbidden, not the first

        AggregateChanges.Prepare(auction).ShouldBe(Forbidden("Auction.Status: Open -> Settled by Settled"));
        AggregateChanges.Prepare(auction).ShouldBe(Forbidden("Auction.Status: Open -> Settled by Settled"));
    }

    [Fact]
    public void An_Entity_Lifecycle_Is_Guarded_And_Checked()
    {
        var auction = OpenWithBids();
        var bid = auction.Bids[0];
        bid.Withdraw().IsSuccess.ShouldBeTrue();
        Commit(auction);
        bid.Withdraw().ShouldBe(Result.Failure(Error.Conflict("Bid.NotActive")));

        bid.ReinstateWithoutGuard();

        AggregateChanges.Prepare(auction).ShouldBe(Forbidden("Bid.Status: Withdrawn -> Active by Reinstated"));
    }

    [Fact]
    public void A_Step_Applied_By_An_Owner_Handler_Is_Checked()
    {
        // Auction.On(Bid.Won) moves the root's Payout: Paid -> Due is not an edge
        var other = OpenWithBids();
        other.CloseAndSettle(AfterEnd).IsSuccess.ShouldBeTrue();
        other.Pay().IsSuccess.ShouldBeTrue();
        Commit(other);
        other.Bids.Single(static b => b.Status == BidStatus.Won).Decide(won: true);   // Won -> Won: no change of the bid

        AggregateChanges.Prepare(other).ShouldBe(Forbidden("Auction.Payout: Paid -> Due by Won"));
    }

    // ── creation (L-4) ──

    [Fact]
    public void A_Root_Created_In_Its_Initial_State_Commits()
        => Commit(Ticket.File("Printer")).Status.ShouldBe(TicketStatus.Draft);

    [Fact]
    public void A_Root_Created_In_Another_State_Is_Reported()
        => AggregateChanges.Prepare(Ticket.FileOpen("Printer")).ShouldBe(Forbidden("Ticket.Status: (new) -> Open by FiledOpen"));

    [Fact]
    public void A_Root_Whose_Creation_Event_Leaves_The_Default_That_Is_Not_Initial_Is_Reported()
        => AggregateChanges.Prepare(Ticket.FileWithoutState("Printer")).ShouldBe(Forbidden("Ticket.Status: (new) -> Archived by Noted"));

    [Fact]
    public void An_Entity_Created_In_Another_State_Is_Reported()
    {
        var auction = OpenWithBids();
        auction.PlaceWinningBidWithoutGuard(Alice, 200);

        AggregateChanges.Prepare(auction).ShouldBe(Forbidden("Bid.Status: (new) -> Won by PlacedAsWinner"));
    }

    [Fact]
    public void A_Named_Initial_State_And_A_Nested_Lifecycle_Work_Like_Any_Other()
    {
        var ticket = Commit(Ticket.File("Printer"));
        ticket.Publish().IsSuccess.ShouldBeTrue();
        ticket.Escalate().IsSuccess.ShouldBeTrue();
        ticket.Escalate().ShouldBe(Result.Failure(Error.Conflict("Priority.NotAllowed", "High -> High")));
        ticket.Finish().IsSuccess.ShouldBeTrue();
        ticket.Archive().IsSuccess.ShouldBeTrue();
        Commit(ticket).Status.ShouldBe(TicketStatus.Archived);
        ticket.Publish().ShouldBe(Result.Failure(Error.Conflict("TicketStatus.NotAllowed", "Archived -> Open")));
    }

    // ── replay is permissive ──

    [Fact]
    public void A_Forbidden_Historical_Step_Loads_And_The_Next_Live_Step_Is_Checked()
    {
        var id = Id<Auction>.New();
        StreamEvent[] history =
        [
            new(1, id.Value, Guid.Empty, new Auction.Opened(100, End)),
            new(2, id.Value, Guid.Empty, new Auction.Closed(AfterEnd)),
            new(3, id.Value, Guid.Empty, new Auction.Settled(null, 100)),
            new(4, id.Value, Guid.Empty, new Auction.Reopened()),          // Settled -> Open: allowed in 2025
        ];

        var old = Auction.Rehydrate(id, history);

        old.Status.ShouldBe(AuctionStatus.Open);
        old.Version.ShouldBe(4);
        AggregateChanges.Prepare(old).IsSuccess.ShouldBeTrue();
        old.Close(AfterEnd).IsSuccess.ShouldBeTrue();
        Commit(old);
        old.ReopenWithoutGuard();
        AggregateChanges.Prepare(old).ShouldBe(Forbidden("Auction.Status: Closed -> Open by Reopened"));
    }

    [Fact]
    public void Replay_Records_No_Creation_Violation()
    {
        var ticket = Ticket.FileOpen("Printer");
        Ticket.Rehydrate(ticket.Id, ticket.UncommittedChanges).Status.ShouldBe(TicketStatus.Open);
        AggregateChanges.Prepare(Ticket.Rehydrate(ticket.Id, ticket.UncommittedChanges)).IsSuccess.ShouldBeTrue();
    }

    // ── stored state (part B) ──

    [Fact]
    public void A_Lifecycle_Field_Is_Stored_As_Its_Name_And_The_Bookkeeping_Is_Not()
    {
        var auction = Auction.Open(Id<Auction>.New(), 100, End);
        auction.PlaceBid(Alice, 120, BeforeEnd).IsSuccess.ShouldBeTrue();
        auction.CloseAndSettle(AfterEnd).IsSuccess.ShouldBeTrue();

        var json = Encoding.UTF8.GetString(AggregateState.Write(auction, TestJsonOptions.Value));

        json.ShouldContain("\"status\":\"Settled\"");
        json.ShouldContain("\"payout\":\"Due\"");
        json.ShouldContain("\"status\":\"Won\"");
        json.ShouldNotContain("violation", Case.Insensitive);
        json.ShouldNotContain("live", Case.Insensitive);
        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["bids", "closedAt", "currentPrice", "endTime", "id", "payout", "schema", "status", "version", "winner"]);
        AggregateTest.StoredStateMatchesReplay(auction, TestJsonOptions.Value);
    }

    [Fact]
    public void A_Forbidden_Step_Is_Not_Stored_With_The_State()
    {
        var auction = OpenWithBids();
        auction.SettleWithoutGuard();
        var json = AggregateState.Write(auction, TestJsonOptions.Value);
        var loaded = AggregateState.Read(auction.Id, json, TestJsonOptions.Value).GetValueOrThrow();

        // the violation belongs to the discarded instance, not to the state (a store never writes it: Prepare failed)
        // only the invariants see the corrupt state now (a settled auction without a closing time)
        AggregateChanges.Prepare(loaded).ShouldBe(Result.Failure(Error.Conflict("Auction.ClosedWithoutTime")));
    }

    [Fact]
    public void A_State_Loaded_From_Storage_Continues_Like_A_Replayed_One()
    {
        var auction = OpenWithBids();
        auction.Close(AfterEnd).IsSuccess.ShouldBeTrue();
        Commit(auction);
        var loaded = AggregateState.Read(auction.Id, AggregateState.Write(auction, TestJsonOptions.Value), TestJsonOptions.Value).GetValueOrThrow();

        loaded.Status.ShouldBe(AuctionStatus.Closed);
        loaded.Close(AfterEnd).ShouldBe(Result.Failure(Error.Conflict("AuctionStatus.NotAllowed", "Closed -> Closed")));
        loaded.Settle().IsSuccess.ShouldBeTrue();          // Closed -> Settled, bids Active -> Won/Lost: all edges
        AggregateChanges.Prepare(loaded).IsSuccess.ShouldBeTrue();
        AggregateChanges.Committed(loaded);
        loaded.ReopenWithoutGuard();
        AggregateChanges.Prepare(loaded).ShouldBe(Forbidden("Auction.Status: Settled -> Open by Reopened"));

        var withdrawn = AggregateState.Read(auction.Id, AggregateState.Write(auction, TestJsonOptions.Value), TestJsonOptions.Value).GetValueOrThrow();
        withdrawn.Bids[0].ReinstateWithoutGuard();          // Active -> Active: no step
        AggregateChanges.Prepare(withdrawn).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_Valid_Path_Allocates_Nothing()
    {
        // guards, and raising checked events (step check before and after apply) on a list that already has its capacity
        var ticket = Commit(Ticket.File("Printer"));
        Ticket.Event up = new Ticket.Escalated(), down = new Ticket.Deescalated();
        void Round(int n)
        {
            for (var i = 0; i < n; i++)
            {
                ticket.Replay(up);
                ticket.Replay(down);
            }
        }

        Round(1000);
        Commit(ticket);                         // keeps the list's capacity
        var status = AuctionStatus.Open;
        var ok = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        Round(1000);
        for (var i = 0; i < 1000; i++)
        {
            if (status.CanTransitionTo(AuctionStatus.Closed))
                ok++;
            if (status.RequireTransitionTo(AuctionStatus.Closed).IsSuccess)
                ok++;
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
        ok.ShouldBe(2000);
        Commit(ticket).Version.ShouldBe(4001);
    }
}
