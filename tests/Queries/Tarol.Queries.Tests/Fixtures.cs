using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace Tarol.Queries.Tests;

internal static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    public static readonly Id<ITenant> TenantA = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-00000000000a");
    public static readonly Id<ITenant> TenantB = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-00000000000b");

    public static Option<Id<ITenant>> A => TenantA;

    public static Option<Id<ITenant>> B => TenantB;

    public static OperationContext Context(Option<Id<ITenant>> tenant) => new(Actor.User("bidder"), tenant, Id<ICorrelation>.New(Now), Option.None);

    /// <summary>Opens <paramref name="count"/> auctions ending one hour apart, with i % 4 bids each; every third one closed.</summary>
    public static List<Auction> Seed(ProjectionTest<AuctionRow> test, int count, Option<Id<ITenant>> tenant, string seller = "ada")
    {
        var auctions = new List<Auction>(count);
        for (var i = 0; i < count; i++)
        {
            var auction = Auction.Open(i % 2 == 0 ? seller : "bob", $"Lot {i}", Now.AddHours(i + 1), Now.AddSeconds(i));
            for (var b = 1; b <= i % 4; b++)
                auction.Bid("carla", 10m * b).IsSuccess.ShouldBeTrue();
            if (i % 3 == 0)
                auction.Close().IsSuccess.ShouldBeTrue();
            test.Given(auction, tenant);
            auctions.Add(auction);
        }
        return auctions;
    }

    /// <summary>Commits a behaviour of an already committed auction.</summary>
    public static void Commit(ProjectionTest<AuctionRow> test, Auction auction, Option<Id<ITenant>> tenant) => test.Given(auction, tenant);

    public static Page<AuctionRow> Ok(this Tarol.Results.Result<Page<AuctionRow>> result)
    {
        result.TryGetValue(out Page<AuctionRow>? page).ShouldBeTrue(result.ToString());
        return page!;
    }

    public static async Task<Page<AuctionRow>> Page(this ProjectionTest<AuctionRow> test, ListQuery query) => (await test.Search(query)).Ok();

    public static async Task<List<AuctionRow>> All(ProjectionTest<AuctionRow> test, ListQuery query)
    {
        var all = new List<AuctionRow>();
        for (var q = query; ;)
        {
            var page = await test.Page(q);
            all.AddRange(page.Items);
            if (page.Next is null)
                return all;
            q = q with { After = page.Next };
        }
    }

    public static ListQuery Query(Option<Id<ITenant>> tenant) => new(tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty);

    /// <summary>A feed that throws on the first <paramref name="failures"/> reads, then serves <paramref name="inner"/>.</summary>
    public sealed class FlakyFeed(IEventFeed inner, int failures) : IEventFeed
    {
        private int _left = failures;

        public int Reads { get; private set; }

        public IAsyncEnumerable<PositionedEvent> ReadAll(GlobalPosition after, int limit, CancellationToken cancellationToken)
        {
            Reads++;
            if (Interlocked.Decrement(ref _left) >= 0)
                throw new InvalidOperationException("feed unreachable");
            return inner.ReadAll(after, limit, cancellationToken);
        }
    }
}
