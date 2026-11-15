using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Sagas.Tests;

/// <summary>The ArtFlow settlement saga (SA-A, sagas.md §5) and its reactions, end to end on <see cref="ReactionTest"/>.</summary>
public sealed class SagaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static ReactionTest Test(DeliveryPolicy? policy = null) => ReactionTest.At(T0, GeneratedReactions.Set, AppJson.Default.Options, policy);

    /// <summary>An auction with bob's bid of 120, given as history, settled by alice.</summary>
    private static async Task<(ReactionTest Test, Id<Auction> Auction)> Settled(ReactionTest? test = null)
    {
        test ??= Test();
        var auction = Auction.Create("alice", 100, T0);
        _ = auction.PlaceBid("bob", 120, T0).GetValueOrThrow();
        test.Given(auction);
        (await test.When(new SettleAuction(auction.Id), Actor.User("alice"))).Result.IsSuccess.ShouldBeTrue();
        return (test, auction.Id);
    }

    private static Settlement SettlementOf(ReactionTest test, Id<Auction> auction) => test.Load((Id<Settlement>)auction.Value);

    [Fact]
    public async Task A_won_bid_marks_the_payout_due_and_starts_the_saga_as_the_system_for_the_user()
    {
        var (test, auction) = await Settled();

        var attempts = await test.Drain();

        attempts.Select(a => (a.Row.Reaction, a.Verdict)).ShouldBe([("MarkPayoutDue", Verdict.Done), ("StartSettlement", Verdict.Done)], ignoreOrder: true);
        var won = test.Store.Recorded.Single(e => e.Event.Event is Bid.Won);
        var due = test.Store.Recorded.Single(e => e.Event.Event is Payout.Due);
        due.Context.Actor.Id.ShouldBe("Reactions.MarkPayoutDue");
        due.Context.Actor.OnBehalfOf.ShouldBe(Option.Some("alice"));
        due.Context.CausationId.ShouldBe(Option.Some((Id<ICausation>)won.EventId));
        due.Context.CorrelationId.ShouldBe(won.Context.CorrelationId);
        SettlementOf(test, auction).Status.ShouldBe(SettlementStatus.AwaitingPayment);
        (await test.Outbox.Pending(TestContext.Current.CancellationToken)).ShouldBe(1);                                      // the deadline, due in 3 days
    }

    [Fact]
    public async Task A_payment_completes_the_saga_and_the_timeout_later_is_a_no_op()
    {
        var (test, auction) = await Settled();
        _ = await test.Drain();

        (await test.When(new RecordPayment(auction, 120, "pay-1"))).Result.IsSuccess.ShouldBeTrue();
        _ = await test.Drain();
        SettlementOf(test, auction).Status.ShouldBe(SettlementStatus.Completed);

        var late = await test.Later(TimeSpan.FromDays(3)).Drain();
        late.ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Done);
        test.Store.Recorded.ShouldNotContain(e => e.Event.Event is Settlement.Expired || e.Event.Event is Auction.Reopened);
    }

    [Fact]
    public async Task Without_payment_the_saga_expires_and_its_compensation_reopens_the_auction()
    {
        var (test, auction) = await Settled();
        _ = await test.Drain();

        (await test.Later(TimeSpan.FromDays(2)).Drain()).ShouldBeEmpty();
        var expired = await test.Later(TimeSpan.FromDays(1)).Drain();

        expired.Select(a => a.Row.Reaction).ShouldBe(["ExpireSettlement", "ReopenAuction"]);
        test.Load(auction).ReopenCount.ShouldBe(1);
        var reopened = test.Store.Recorded.Single(e => e.Event.Event is Auction.Reopened);
        var expiredEvent = test.Store.Recorded.Single(e => e.Event.Event is Settlement.Expired);
        reopened.Context.Actor.OnBehalfOf.ShouldBe(Option.Some("alice"));                     // through three hops
        reopened.Context.CausationId.ShouldBe(Option.Some((Id<ICausation>)expiredEvent.EventId));
        expiredEvent.RecordedAt.ShouldBe(T0.AddDays(3));
    }

    [Fact]
    public async Task A_late_payment_after_expiry_is_a_success_not_a_conflict()
    {
        var (test, auction) = await Settled();
        _ = await test.Drain();
        _ = await test.Later(TimeSpan.FromDays(3)).Drain();

        (await test.When(new RecordPayment(auction, 120, "late"))).Result.IsSuccess.ShouldBeTrue();

        (await test.Drain()).ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Done);
        SettlementOf(test, auction).Status.ShouldBe(SettlementStatus.Expired);
    }

    [Fact]
    public async Task A_reopened_auction_won_again_restarts_the_saga_in_place()
    {
        var (test, auction) = await Settled();
        _ = await test.Drain();
        _ = await test.Later(TimeSpan.FromDays(3)).Drain();                                   // expired, auction reopened

        (await test.When(new PlaceBid(auction, 130), Actor.User("carol"))).Result.IsSuccess.ShouldBeTrue();
        (await test.When(new SettleAuction(auction))).Result.IsSuccess.ShouldBeTrue();
        _ = await test.Drain();

        var settlement = SettlementOf(test, auction);
        (settlement.Rounds, settlement.Status, settlement.Buyer).ShouldBe((2, SettlementStatus.AwaitingPayment, "carol"));
    }

    [Fact]
    public async Task A_second_start_while_the_saga_runs_changes_nothing()
    {
        var (test, auction) = await Settled();
        _ = await test.Drain();
        var won = test.Store.Recorded.Single(e => e.Event.Event is Bid.Won);

        var again = await GeneratedReactions.Set.Run(test.Pipeline, "StartSettlement", won with { EventId = Guid.CreateVersion7(T0) }, TestContext.Current.CancellationToken);

        again.GetValueOrThrow().Events.ShouldBe(0);
        SettlementOf(test, auction).Rounds.ShouldBe(1);
    }

    [Fact]
    public async Task A_shortened_deadline_fires_on_time_and_the_old_one_is_stale()
    {
        var (test, auction) = await Settled();
        _ = await test.Drain();
        (await test.When(new RescheduleSettlement((Id<Settlement>)auction.Value, T0.AddDays(1)))).Result.IsSuccess.ShouldBeTrue();
        _ = await test.Drain();

        (await test.Later(TimeSpan.FromDays(1)).Drain()).Select(a => a.Row.Reaction).ShouldBe(["ExpireSettlement", "ReopenAuction"]);
        (await test.Later(TimeSpan.FromDays(2)).Drain()).ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Done);   // the 3-day row: a no-op

        test.Store.Recorded.Single(e => e.Event.Event is Settlement.Expired).RecordedAt.ShouldBe(T0.AddDays(1));
    }

    [Fact]
    public async Task A_payment_before_the_saga_exists_is_retried_until_the_saga_starts()
    {
        var test = Test();
        var auction = Auction.Create("alice", 100, T0);
        _ = auction.PlaceBid("bob", 120, T0).GetValueOrThrow();
        test.Given(auction);
        (await test.When(new RecordPayment(auction.Id, 120, "early"))).Result.IsSuccess.ShouldBeTrue();

        var early = (await test.Drain()).ShouldHaveSingleItem();
        (early.Verdict, early.Reason).ShouldBe((Verdict.Retry, Option.Some("NotFound Settlement.NotFound: " + auction.Id)));
        (await test.When(new SettleAuction(auction.Id))).Result.IsSuccess.ShouldBeTrue();
        _ = await test.Drain();
        _ = await test.Later(TimeSpan.FromSeconds(2)).Drain();

        SettlementOf(test, auction.Id).Status.ShouldBe(SettlementStatus.Completed);
    }

    [Fact]
    public async Task A_reaction_that_keeps_failing_backs_off_becomes_a_dead_letter_and_unblocks_its_stream()
    {
        var test = Test(new DeliveryPolicy { MaxAttempts = 4 });
        var auction = Auction.Create("alice", 100, T0);
        test.Given(auction);
        (await test.When(new NoteAuction(auction.Id, "poison"))).Result.IsSuccess.ShouldBeTrue();
        (await test.When(new NoteAuction(auction.Id, "ok"))).Result.IsSuccess.ShouldBeTrue();

        for (var i = 0; i < 3; i++)
        {
            (await test.Drain()).ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Retry);    // only the head: "ok" waits
            test.Later(TimeSpan.FromSeconds(10));
        }

        var last = await test.Drain();                                                          // buried, and the stream moves on

        last.Select(a => (a.Row.Attempts, a.Verdict)).ShouldBe([(4, Verdict.Bury), (1, Verdict.Done)]);
        var dead = (await test.DeadLetters()).ShouldHaveSingleItem();
        (dead.Reaction, dead.Attempts, dead.LastError).ShouldBe(("AcknowledgeNote", 4, Option.Some("Conflict Note.Poison")));
        test.Load(auction.Id).Acknowledged.ShouldBe(["ok"]);
    }

    [Fact]
    public async Task A_redriven_dead_letter_runs_again()
    {
        var test = Test(new DeliveryPolicy { MaxAttempts = 1 });
        var auction = Auction.Create("alice", 100, T0);
        test.Given(auction);
        (await test.When(new NoteAuction(auction.Id, "poison"))).Result.IsSuccess.ShouldBeTrue();
        (await test.Drain()).ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Bury);

        (await test.Outbox.Redrive((await test.DeadLetters()).Single().Id, TestContext.Current.CancellationToken)).ShouldBeTrue();

        (await test.Drain()).ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Bury);            // still poison: buried again
        (await test.DeadLetters()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Given_history_does_not_run_its_reactions()
    {
        var test = Test();
        var auction = Auction.Create("alice", 100, T0);
        _ = auction.PlaceBid("bob", 120, T0).GetValueOrThrow();
        auction.Settle().IsSuccess.ShouldBeTrue();                                              // Bid.Won in the history
        test.Given(auction);

        (await test.Drain()).ShouldBeEmpty();
        test.Store.Recorded.ShouldNotContain(e => e.Event.Event is Payout.Due);
    }
}
