using Tarol.Kernel;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.Tests;

/// <summary>The example of skills/tarol-changes/SKILL.md, verbatim (the records are in Changes.cs): every API name compiles and runs.</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_example_runs()
    {
        var start = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var auction = Auction.Create("seller", "L-1", Title.Create("Sunflowers").GetValueOrThrow(), Amount.Create(100).GetValueOrThrow(), start.AddDays(7), start).GetValueOrThrow();

        // test: the real pipeline on an in-memory store with a clock that stands still
        var test = ChangeTest.At(start).Given(auction);
        var then = await test.When(new CloseAuction(auction.Id), Actor.User("seller"));
        then.Result.IsSuccess.ShouldBeTrue();
        then.Events.ShouldBe([new Auction.Closed(start)]);
        then.Recorded[0].Context.Actor.ShouldBe(Actor.User("seller"));
        var twice = await test.When(new CloseAuction(auction.Id));
        twice.Result.TryGetValue(out Violation? conflict).ShouldBeTrue();      // Error.Conflict("AuctionStatus.NotAllowed", …)

        // a job
        var pipeline = test.Pipeline;
        var id = auction.Id;
        Option<Id<ITenant>> tenant = Option.None;
        var time = test.Clock;
        var ct = TestContext.Current.CancellationToken;
        var result = await pipeline.Run(new SettleAuction(id), OperationContext.Start(Actor.System("Jobs.AuctionSettlement"), tenant, time), cancellationToken: ct);

        conflict.ShouldBe(Error.Conflict("AuctionStatus.NotAllowed", "Closed -> Closed"));
        result.TryGetValue(out Violation? early).ShouldBeTrue();
        early.ShouldBe(Error.Conflict("Auction.NotEnded"));
    }
}
