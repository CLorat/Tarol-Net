namespace Tarol.Sagas.Tests;

/// <summary>The example of skills/tarol-sagas/SKILL.md, verbatim (the records are in Changes.cs and Domain.cs).</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_test_example_runs()
    {
        var start = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var auction = Auction.Create("alice", 100, start);
        _ = auction.PlaceBid("bob", 120, start).GetValueOrThrow();

        // test
        var test = ReactionTest.At(start, GeneratedReactions.Set, AppJson.Default.Options).Given(auction);
        await test.When(new SettleAuction(auction.Id));
        await test.Drain();                                           // MarkPayoutDue, StartSettlement
        var expired = await test.Later(TimeSpan.FromDays(3)).Drain(); // ExpireSettlement, ReopenAuction

        expired.Select(a => a.Row.Reaction).ShouldBe(["ExpireSettlement", "ReopenAuction"]);
        test.Load(auction.Id).ReopenCount.ShouldBe(1);
    }
}
