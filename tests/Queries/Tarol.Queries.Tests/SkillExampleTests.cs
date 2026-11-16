using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Queries.Tests.Skill;

// skills/tarol-queries/SKILL.md, the example verbatim (the domain's Auction stands in for the app's)
public enum AuctionStatus { Open, Closed }

[Projection("auction-list", Version = 1)]
public sealed partial record AuctionRow(Guid Id, string Title, string Seller, [Sort] DateTimeOffset EndsAt,
    AuctionStatus Status, [Sort] decimal HighestBid, int Bids)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r)
        => new(r.StreamId, e.Title, e.Seller, e.EndsAt, AuctionStatus.Open, 0m, 0);
    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount, Bids = Bids + 1 };
    public AuctionRow When(Auction.Closed e) => this with { Status = AuctionStatus.Closed };
}

public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_example_runs()
    {
        var now = Fixtures.Now;
        var endsAt = now.AddDays(1);
        Option<Id<ITenant>> tenant = Fixtures.TenantA;

        var test = ProjectionTest<AuctionRow>.Create().Given(Auction.Open("ada", "Vase", endsAt, now), tenant);
        await test.CatchUp();
        var result = await test.Search(new ListQuery(tenant.GetValueOrThrow().Value)
        {
            Filters = [new ColumnFilter("status", AuctionStatus.Open)],
            Sort = "ends_at",
            Limit = 20,
        });
        result.TryGetValue(out Page<AuctionRow>? page).ShouldBeTrue();   // page.Items, page.Next, page.ETag, page.Position

        page!.Items.Single().Title.ShouldBe("Vase");
        page.Next.ShouldBeNull();
    }
}
