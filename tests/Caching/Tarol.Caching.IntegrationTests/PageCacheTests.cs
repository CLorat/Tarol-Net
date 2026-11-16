using Npgsql;
using Tarol.Queries;
using static Tarol.Caching.IntegrationTests.Lab;

namespace Tarol.Caching.IntegrationTests;

/// <summary>The "postgres" page rows of migration/caching.md §3, with the checkpoint pushed from the worker's batch.</summary>
[Trait("Category", "Integration")]
public sealed class PageCacheTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_is_a_hit_per_tenant_and_a_commit_of_tenant_B_makes_the_entries_miss()
    {
        await using var lab = await postgres.Create();
        for (var i = 0; i < 4; i++)
            await lab.Commit(Auction.Open($"A {i}", Postgres.Now.AddHours(i + 1), Postgres.Now), TenantA);
        await lab.Project(await lab.Commit(Auction.Open("B 0", Postgres.Now.AddHours(1), Postgres.Now), TenantB));
        var a = new ListQuery(TenantA.Value) { Sort = "ends_at", Limit = 3 };
        var b = new ListQuery(TenantB.Value) { Sort = "ends_at", Limit = 3 };

        var first = await Page(lab.Cached, a);
        (await Page(lab.Cached, a)).ShouldBeSameAs(first);                       // a hit: the same page, no query
        var other = await Page(lab.Cached, b);
        other.Items.ShouldHaveSingleItem().Title.ShouldBe("B 0");                   // the tenant is in the key
        first.Items.Select(r => r.Title).ShouldBe(["A 0", "A 1", "A 2"]);

        await lab.Project(await lab.Commit(Auction.Open("B 1", Postgres.Now.AddHours(2), Postgres.Now), TenantB));

        var again = await Page(lab.Cached, a);                                     // any batch moves the checkpoint: a miss
        again.ShouldNotBeSameAs(first);
        again.ETag.ShouldBe(first.ETag);                                           // with the same rows
        (await Page(lab.Cached, b)).Items.Count.ShouldBe(2);
        (await Page(lab.Cached, a)).ShouldBeSameAs(again);
    }

    [Fact]
    public async Task A_bid_on_page_one_makes_the_entry_miss_and_the_next_page_shows_it()
    {
        await using var lab = await postgres.Create();
        var auction = Auction.Open("Lot 1", Postgres.Now.AddHours(1), Postgres.Now);
        await lab.Project(await lab.Commit(auction, TenantA));
        var query = new ListQuery(TenantA.Value);
        var before = await Page(lab.Cached, query);

        auction.Bid(120m).IsSuccess.ShouldBeTrue();
        await lab.Project(await lab.Commit(auction, TenantA));

        var after = await Page(lab.Cached, query);
        after.Items.ShouldHaveSingleItem().HighestBid.ShouldBe(120m);
        after.ETag.ShouldNotBe(before.ETag);
        after.ETag.ShouldBe((await Page(lab.Uncached, query)).ETag);
    }
}

/// <summary>
/// Holds the xmin horizon with an open transaction. The horizon is cluster-wide, so it hides commits from every other test
/// on the container: it runs alone, after the parallel collections.
/// </summary>
[CollectionDefinition(nameof(XminHorizon), DisableParallelization = true)]
public sealed class XminHorizon;

[Collection(nameof(XminHorizon))]
[Trait("Category", "Integration")]
public sealed class PageCacheXminTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_an_open_transaction_elsewhere_the_cached_page_equals_the_uncached_page_and_after_it_both_show_the_bid()
    {
        await using var lab = await postgres.Create();
        var auction = Auction.Open("Lot 1", Postgres.Now.AddHours(1), Postgres.Now);
        await lab.Project(await lab.Commit(auction, TenantA));
        var query = new ListQuery(TenantA.Value);
        var cached = await Page(lab.Cached, query);

        await using var open = await postgres.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await open.BeginTransactionAsync(Ct);
        await using (var xid = new NpgsqlCommand("SELECT pg_current_xact_id()", open, transaction))
            await xid.ExecuteScalarAsync(Ct);                                      // older than the bid's commit
        auction.Bid(75m).IsSuccess.ShouldBeTrue();
        var bid = await lab.Commit(auction, TenantA);
        await lab.Host.CatchUp(Ct);                                                // the feed hides the bid
        lab.Host.Position.ShouldBeLessThan(bid);

        (await Page(lab.Cached, query)).ShouldBeSameAs(cached);
        (await Page(lab.Uncached, query)).ETag.ShouldBe(cached.ETag);              // the uncached path shows no more
        cached.Items[0].HighestBid.ShouldBe(0m);

        await transaction.CommitAsync(Ct);
        await lab.Project(bid);
        (await Page(lab.Cached, query)).Items[0].HighestBid.ShouldBe(75m);
        (await Page(lab.Uncached, query)).Items[0].HighestBid.ShouldBe(75m);
    }
}
