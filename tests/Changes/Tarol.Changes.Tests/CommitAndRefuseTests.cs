using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;

#pragma warning disable TVAL006 // the tests build inputs as JSON would (trusting constructors); the pipeline validates them

namespace Tarol.Changes.Tests;

/// <summary>TN-46 (Tatalu FB-F15.1-4): a change that commits its events and answers a refusal; a replay answers the same refusal.</summary>
public sealed class CommitAndRefuseTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static (ChangeTest Test, Auction Auction) Seeded()
    {
        var test = ChangeTest.At(Start, TestJson.Default.Options);
        var auction = Auction.Create("seller", "L-1", new Title("Sunflowers"), new Amount(100), Start.AddDays(7), Start).GetValueOrThrow();
        test.Given(auction);
        return (test, auction);
    }

    [Fact]
    public async Task A_rejected_result_commits_the_raised_event_and_answers_the_refusal()
    {
        var (test, auction) = Seeded();

        var then = await test.When(new CountedLowBid(auction.Id, new Amount(120)), Actor.User("bob"));

        var refusal = then.Result.TryGetValue(out Violation? v) ? v.ShouldBeOfType<Error>() : throw new InvalidOperationException("expected a refusal");
        (refusal.Code, refusal.Detail, refusal.Commits).ShouldBe(("Auction.BelowReserve", "counted", true));
        test.History(auction.Id).Count.ShouldBe(2);                       // created + the counted bid
    }

    [Fact]
    public async Task A_plain_failure_still_discards_the_raised_event()
    {
        var (test, auction) = Seeded();

        var then = await test.When(new DiscardedLowBid(auction.Id, new Amount(120)), Actor.User("bob"));

        then.Result.IsFailure.ShouldBeTrue();
        test.History(auction.Id).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_replay_under_the_key_answers_the_stored_refusal_and_counts_once()
    {
        var (test, auction) = Seeded();
        var key = new ChangeOptions(new IdempotencyKey(Option.None, "http", "attempt-1"));

        var first = await test.When(new CountedLowBid(auction.Id, new Amount(120)), Actor.User("bob"), key);
        var replay = await test.When(new CountedLowBid(auction.Id, new Amount(120)), Actor.User("bob"), key);

        var replayed = replay.Result.TryGetValue(out Violation? v) ? v.ShouldBeOfType<Error>() : throw new InvalidOperationException("expected a refusal");
        (replayed.Type, replayed.Code, replayed.Detail).ShouldBe((ErrorType.Conflict, "Auction.BelowReserve", "counted"));
        replayed.Reasons.ShouldBe(["Auction.Attempts"]);
        first.Result.IsFailure.ShouldBeTrue();
        test.History(auction.Id).Count.ShouldBe(2);                       // one counted attempt, not two
    }
}
