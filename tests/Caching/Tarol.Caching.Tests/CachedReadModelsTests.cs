using Microsoft.Extensions.Time.Testing;
using Tarol.Persistence;
using Tarol.Queries;
using static Tarol.Caching.Tests.Lab;

namespace Tarol.Caching.Tests;

public sealed class CachedReadModelsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_same_query_at_the_same_checkpoint_is_a_hit()
    {
        var lab = new Lab();
        lab.Batch(TenantA, ("Lot 1", "ada"), ("Lot 2", "bob"));
        var query = new ListQuery(TenantA) { Sort = "ends_at" };

        var first = await lab.Page(query);
        var second = await lab.Page(query);

        lab.Searches.ShouldBe(1);
        second.ShouldBeSameAs(first);
        second.Items.Count.ShouldBe(2);
        lab.Cache.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_batch_moves_the_checkpoint_and_the_next_request_misses_and_shows_it()
    {
        var lab = new Lab();
        var opened = lab.Batch(TenantA, ("Lot 1", "ada"));
        var query = new ListQuery(TenantA);
        var before = await lab.Page(query);
        var id = before.Items[0].Id;

        var bid = lab.Bid(TenantA, id, 50m);
        var after = await lab.Page(query);

        lab.Searches.ShouldBe(2);
        bid.ShouldBeGreaterThan(opened);
        after.Items[0].HighestBid.ShouldBe(50m);
        after.ETag.ShouldNotBe(before.ETag);
        (await lab.Page(query)).ShouldBeSameAs(after);
        lab.Searches.ShouldBe(2);
        lab.Cache.Count.ShouldBe(1);                                      // replaced, not added
    }

    [Fact]
    public async Task The_tenant_is_part_of_the_key()
    {
        var lab = new Lab();
        lab.Batch(TenantA, ("Lot A", "ada"));
        lab.Batch(TenantB, ("Lot B", "ada"));

        var a = await lab.Page(new ListQuery(TenantA));
        var b = await lab.Page(new ListQuery(TenantB));

        lab.Searches.ShouldBe(2);
        a.Items.ShouldHaveSingleItem().Title.ShouldBe("Lot A");
        b.Items.ShouldHaveSingleItem().Title.ShouldBe("Lot B");
        (await lab.Page(new ListQuery(TenantB))).ShouldBeSameAs(b);
    }

    [Fact]
    public async Task Filters_are_part_of_the_key_in_any_order_and_a_claim_value_is_a_filter()
    {
        var lab = new Lab();
        lab.Batch(TenantA, ("Lot 1", "ada"), ("Lot 2", "bob"), ("Lot 3", "ada"));
        var status = new ColumnFilter("status", AuctionStatus.Open);

        // a [FromClaim] member becomes a filter with the caller's claim value: one entry per claim value
        var ada = await lab.Page(new ListQuery(TenantA) { Filters = [status, new("seller", "ada")] });
        var bob = await lab.Page(new ListQuery(TenantA) { Filters = [status, new("seller", "bob")] });
        var adaAgain = await lab.Page(new ListQuery(TenantA) { Filters = [new("seller", "ada"), status] });

        ada.Items.Count.ShouldBe(2);
        bob.Items.ShouldHaveSingleItem().Seller.ShouldBe("bob");
        adaAgain.ShouldBeSameAs(ada);
        lab.Searches.ShouldBe(2);
    }

    [Fact]
    public async Task Every_member_of_the_query_is_part_of_the_key()
    {
        var lab = new Lab();
        lab.Batch(TenantA, ("Lot 1", "ada"), ("Lot 2", "bob"), ("Lot 3", "carla"));
        var plain = new ListQuery(TenantA) { Limit = 2 };
        var next = (await lab.Page(plain)).Next!;
        ListQuery[] variants =
        [
            plain with { Sort = "ends_at" },
            plain with { Sort = "ends_at", Descending = true },
            plain with { After = next },
            plain with { Offset = 1 },
            plain with { Limit = 3 },
            plain with { Count = true },
        ];
        foreach (var variant in variants)
            await lab.Page(variant);
        lab.Searches.ShouldBe(1 + variants.Length);

        await lab.Page(plain with { Sort = "id" });                       // the id sort is the default sort
        await lab.Page(plain with { Offset = 0 });                        // offset 0 is no offset
        lab.Searches.ShouldBe(1 + variants.Length);
    }

    [Fact]
    public async Task A_batch_during_the_search_stamps_the_entry_with_the_checkpoint_read_before_it_so_the_next_request_misses()
    {
        var lab = new Lab();
        lab.Batch(TenantA, ("Lot 1", "ada"));
        var query = new ListQuery(TenantA);
        lab.Counting.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lab.Counting.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var searching = lab.Page(query);
        await lab.Counting.Entered.Task.WaitAsync(Ct);
        var during = lab.Batch(TenantA, ("Lot 2", "bob"));               // lands while the search runs
        lab.Counting.Gate.SetResult();
        await searching;
        lab.Counting.Gate = null;

        var next = await lab.Page(query);
        lab.Searches.ShouldBe(2);
        next.Items.Count.ShouldBe(2);
        next.Position.ShouldBe(during);
        (await lab.Page(query)).ShouldBeSameAs(next);
        lab.Searches.ShouldBe(2);
    }

    [Fact]
    public async Task A_replace_during_the_search_does_not_leave_the_page_read_before_it_cached()
    {
        var lab = new Lab();
        lab.Batch(TenantA, ("Lot 1", "ada"));
        var query = new ListQuery(TenantA);
        var row = (await lab.Page(query)).Items.ShouldHaveSingleItem();
        lab.Cache.Clear();
        // an erasure replaces the row (the checkpoint stays) after the search read it, before its page is cached
        lab.Counting.AfterSearch = () => lab.Store.Replace([new Versioned<AuctionRow>(new RowKey(TenantA, row.Id), 2, row with { Seller = "erased" })], Ct).AsTask();

        (await lab.Page(query)).Items.Single().Seller.ShouldBe("ada");                       // read before the erasure

        (await lab.Page(query)).Items.Single().Seller.ShouldBe("erased");                    // not served from the cache
    }

    [Fact]
    public async Task Nothing_is_cached_while_the_projection_builds()
    {
        var lab = new Lab(current: false);
        var query = new ListQuery(TenantA);

        await lab.Page(query);
        await lab.Page(query);

        lab.Searches.ShouldBe(2);
        lab.Cache.Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_refused_query_is_answered_by_the_store_and_not_cached()
    {
        var lab = new Lab();
        var bad = new ListQuery(TenantA) { After = "not-a-cursor" };

        (await lab.Store.Search(bad, Ct)).IsFailure.ShouldBeTrue();
        (await lab.Store.Search(bad, Ct)).IsFailure.ShouldBeTrue();
        (await lab.Store.Search(new ListQuery(TenantA) { Limit = 0 }, Ct)).IsFailure.ShouldBeTrue();

        lab.Searches.ShouldBe(3);
        lab.Cache.Count.ShouldBe(0);
        await Should.ThrowAsync<ArgumentException>(async () => _ = await lab.Store.Search(new ListQuery(TenantA) { Sort = "nope" }, Ct));
    }

    [Fact]
    public async Task Prepare_Checkpoint_Load_Write_and_Find_pass_through()
    {
        var lab = new Lab();
        var store = lab.Store;
        var key = new RowKey(TenantA, Guid.CreateVersion7(Now));
        var row = new AuctionRow(key.Id, "Lot", "ada", Now.AddDays(1), AuctionStatus.Open, 0m);

        await store.Prepare(Ct);
        (await store.Write(new ProjectionBatch<AuctionRow>([new(key, 1, row)], lab.Checkpoint, new GlobalPosition(9_000, 1), ProjectionState.Current), Ct)).ShouldBeTrue();
        (await store.Checkpoint(Ct)).Position.ShouldBe(new GlobalPosition(9_000, 1));
        (await store.Find(key, Ct))!.Row.ShouldBe(row);
        (await store.Load([key], Ct))[key].Version.ShouldBe(1);

        lab.Counting.Calls.ShouldBe(new Dictionary<string, int> { ["Prepare"] = 1, ["Write"] = 1, ["Checkpoint"] = 1, ["Find"] = 1, ["Load"] = 1 }, ignoreOrder: true);
        lab.Cached.Store<AuctionRow>().ShouldBeSameAs(store);
        lab.Cached.Inner.ShouldBeSameAs(lab.Counting);
    }

    [Fact]
    public async Task An_idle_entry_expires_on_the_clock()
    {
        var time = new FakeTimeProvider(Now);
        var lab = new Lab(new CacheOptions { IdleExpiry = TimeSpan.FromMinutes(5) }, time);
        lab.Batch(TenantA, ("Lot 1", "ada"));
        var query = new ListQuery(TenantA);
        await lab.Page(query);

        time.Advance(TimeSpan.FromMinutes(4));
        await lab.Page(query);                                            // a hit renews the entry
        time.Advance(TimeSpan.FromMinutes(4));
        await lab.Page(query);
        lab.Searches.ShouldBe(1);

        time.Advance(TimeSpan.FromMinutes(5));
        await lab.Page(query);
        lab.Searches.ShouldBe(2);
        lab.Cache.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_entry_cap_evicts_the_least_recently_used()
    {
        var time = new FakeTimeProvider(Now);
        var lab = new Lab(new CacheOptions { MaxEntries = 10 }, time);
        lab.Batch(TenantA, ("Lot 1", "ada"));
        for (var limit = 1; limit <= 10; limit++)
        {
            await lab.Page(new ListQuery(TenantA) { Limit = limit });
            time.Advance(TimeSpan.FromSeconds(1));
        }
        await lab.Page(new ListQuery(TenantA) { Limit = 1 });            // the oldest is used again
        time.Advance(TimeSpan.FromSeconds(1));
        lab.Cache.Count.ShouldBe(10);

        await lab.Page(new ListQuery(TenantA) { Limit = 11 });            // over the cap: trimmed to 90 %

        lab.Cache.Count.ShouldBe(9);
        var searches = lab.Searches;
        await lab.Page(new ListQuery(TenantA) { Limit = 1 });            // kept: recently used
        await lab.Page(new ListQuery(TenantA) { Limit = 11 });           // kept: the newest
        lab.Searches.ShouldBe(searches);
        await lab.Page(new ListQuery(TenantA) { Limit = 2 });            // evicted: the least recently used
        lab.Searches.ShouldBe(searches + 1);
    }

    [Fact]
    public async Task The_byte_cap_bounds_the_estimated_size_and_a_page_above_it_is_not_cached()
    {
        var lab = new Lab(new CacheOptions { MaxBytes = 2_000 });
        lab.Batch(TenantA, [.. Enumerable.Range(0, 20).Select(i => ($"Lot {i}", "ada"))]);

        await lab.Page(new ListQuery(TenantA) { Limit = 200 });          // ~2.6 KB, over the cap: not cached
        lab.Cache.Count.ShouldBe(0);
        for (var limit = 1; limit <= 8; limit++)
            await lab.Page(new ListQuery(TenantA) { Limit = limit });

        lab.Cache.Bytes.ShouldBeLessThanOrEqualTo(2_000);
        lab.Cache.Count.ShouldBeGreaterThan(0);
        lab.Cache.Count.ShouldBeLessThan(8);
    }

    [Fact]
    public async Task The_size_is_the_rows_json_and_the_key_or_an_estimate_per_row_without_the_context()
    {
        var lab = new Lab();
        lab.Batch(TenantA, ("Lot 1", "ada"));
        await lab.Page(new ListQuery(TenantA));
        var measured = lab.Cache.Bytes;
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes((await lab.Page(new ListQuery(TenantA))).Items[0], CachingJson.Default.AuctionRow).Length;
        measured.ShouldBeGreaterThan(json);
        measured.ShouldBeLessThan(json + 400);

        var cache = new MemoryPageCache();
        var unmeasured = new CachedReadModels(lab.InMemory, new ProjectionPositions(lab.InMemory), cache);
        (await unmeasured.Store<AuctionRow>().Search(new ListQuery(TenantA), Ct)).IsSuccess.ShouldBeTrue();
        cache.Bytes.ShouldBeGreaterThanOrEqualTo(256);
    }
}
