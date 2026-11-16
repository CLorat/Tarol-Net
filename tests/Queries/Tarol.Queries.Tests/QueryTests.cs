using Tarol.Results;
using static Tarol.Queries.Tests.Fixtures;

namespace Tarol.Queries.Tests;

public sealed class QueryTests
{
    private static async Task<(ProjectionTest<AuctionRow> Test, List<Auction> Auctions)> Seeded(int count)
    {
        var test = ProjectionTest<AuctionRow>.Create();
        var auctions = Seed(test, count, A);
        Seed(test, 3, B);
        await test.CatchUp();
        return (test, auctions);
    }

    [Fact]
    public async Task Cursor_pages_are_the_ordered_list_without_duplicates()
    {
        var (test, auctions) = await Seeded(23);

        var all = await All(test, Query(A) with { Sort = "ends_at", Limit = 5 });

        all.Select(r => r.Id).ShouldBe(auctions.Select(a => a.Id.Value));      // ends_at grows with the index
        all.Select(r => r.EndsAt).ShouldBeInOrder();
    }

    [Fact]
    public async Task Offset_pages_equal_cursor_pages_while_nothing_changes()
    {
        var (test, _) = await Seeded(17);
        var query = Query(A) with { Sort = "ends_at", Limit = 4 };

        var byCursor = await All(test, query);
        var byOffset = new List<AuctionRow>();
        for (var offset = 0; offset < 17; offset += 4)
            byOffset.AddRange((await test.Page(query with { Offset = offset })).Items);

        byOffset.ShouldBe(byCursor);
    }

    [Fact]
    public async Task A_filter_and_a_descending_sort_break_ties_by_the_id_descending()
    {
        var (test, _) = await Seeded(24);

        var open = await All(test, Query(A) with
        {
            Filters = [new ColumnFilter("status", AuctionStatus.Open)],
            Sort = "highest_bid",
            Descending = true,
            Limit = 3,
        });

        open.ShouldAllBe(r => r.Status == AuctionStatus.Open);
        open.Count.ShouldBe(test.Rows(A).Count(r => r.Status == AuctionStatus.Open));
        for (var i = 1; i < open.Count; i++)
        {
            var (before, after) = (open[i - 1], open[i]);
            (before.HighestBid > after.HighestBid || (before.HighestBid == after.HighestBid && ColumnValues.CompareIds(before.Id, after.Id) > 0))
                .ShouldBeTrue($"{before} before {after}");
        }
    }

    [Fact]
    public async Task Filters_on_several_columns_and_on_null()
    {
        var (test, _) = await Seeded(16);

        var adaWithoutBids = await All(test, Query(A) with { Filters = [new ColumnFilter("seller", "ada"), new ColumnFilter("winner", null)] });

        adaWithoutBids.ShouldNotBeEmpty();
        adaWithoutBids.ShouldBe(test.Rows(A).Where(r => r.Seller == "ada" && r.Winner is null).OrderBy(r => r.Id, Comparer<Guid>.Create(ColumnValues.CompareIds)));
    }

    [Fact]
    public async Task A_cursor_is_bound_to_its_sort_and_its_filters()
    {
        var (test, _) = await Seeded(10);
        var next = (await test.Page(Query(A) with { Sort = "ends_at", Limit = 2 })).Next.ShouldNotBeNull();

        (await test.Page(Query(A) with { Sort = "ends_at", Limit = 2, After = next })).Items.Count.ShouldBe(2);
        Bad(await test.Search(Query(A) with { Sort = "highest_bid", After = next })).ShouldBe("after");
        Bad(await test.Search(Query(A) with { Sort = "ends_at", Descending = true, After = next })).ShouldBe("after");
        Bad(await test.Search(Query(A) with { Sort = "ends_at", Filters = [new ColumnFilter("seller", "ada")], After = next })).ShouldBe("after");
        Bad(await test.Search(Query(A) with { Sort = "ends_at", After = "not-a-cursor" })).ShouldBe("after");
        Bad(await test.Search(Query(A) with { Sort = "ends_at", After = Cursor.Encode("ends_at", 0, "x", Guid.Empty) })).ShouldBe("after");
    }

    [Fact]
    public async Task Limit_and_offset_are_bounded()
    {
        var (test, _) = await Seeded(3);

        Bad(await test.Search(Query(A) with { Limit = 0 })).ShouldBe("limit");
        Bad(await test.Search(Query(A) with { Limit = ListQuery.MaxLimit + 1 })).ShouldBe("limit");
        Bad(await test.Search(Query(A) with { Offset = ListQuery.MaxOffset + 1 })).ShouldBe("offset");
        Bad(await test.Search(Query(A) with { Offset = -1 })).ShouldBe("offset");
        (await test.Page(Query(A) with { Limit = ListQuery.MaxLimit, Offset = ListQuery.MaxOffset })).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_column_that_does_not_exist_or_is_not_a_sort_is_a_programming_error()
    {
        var (test, _) = await Seeded(1);

        await Should.ThrowAsync<ArgumentException>(async () => _ = await test.Search(Query(A) with { Filters = [new ColumnFilter("nope", 1)] }));
        await Should.ThrowAsync<ArgumentException>(async () => _ = await test.Search(Query(A) with { Sort = "title" }));
        await Should.ThrowAsync<ArgumentException>(async () => _ = await test.Search(Query(A) with { Filters = [new ColumnFilter("bids", "many")] }));
    }

    [Fact]
    public async Task The_page_ETag_is_stable_and_changes_with_a_bid_on_the_page_only()
    {
        var (test, auctions) = await Seeded(10);
        var query = Query(A) with { Sort = "ends_at", Limit = 3 };
        var first = await test.Page(query);
        (await test.Page(query)).ETag.ShouldBe(first.ETag);
        first.ETag.ShouldStartWith("W/\"");

        var offPage = auctions[8];                                             // open, ends late: not on page 1
        offPage.Bid("dora", 1000m).IsSuccess.ShouldBeTrue();
        Commit(test, offPage, A);
        await test.CatchUp();
        var unchanged = await test.Page(query);
        unchanged.ETag.ShouldBe(first.ETag);
        unchanged.Position.ShouldBeGreaterThan(first.Position);

        var onPage = auctions[1];
        onPage.Bid("dora", 1000m).IsSuccess.ShouldBeTrue();
        Commit(test, onPage, A);
        await test.CatchUp();
        (await test.Page(query)).ETag.ShouldNotBe(first.ETag);
    }

    [Fact]
    public async Task The_total_is_counted_only_when_asked()
    {
        var (test, _) = await Seeded(9);

        (await test.Page(Query(A) with { Limit = 2 })).Total.ShouldBeNull();
        (await test.Page(Query(A) with { Limit = 2, Count = true })).Total.ShouldBe(9);
        (await test.Page(Query(A) with { Limit = 2, Count = true, Filters = [new ColumnFilter("status", AuctionStatus.Closed)] })).Total.ShouldBe(3);
    }

    [Fact]
    public async Task Offset_repeats_a_row_under_an_insert_before_the_page_and_the_cursor_does_not()
    {
        var (test, _) = await Seeded(10);
        var query = Query(A) with { Sort = "ends_at", Limit = 4 };
        var page1 = await test.Page(query);

        test.Given(Auction.Open("ada", "Early", Now.AddMinutes(1), Now), A);   // sorts before page 1
        await test.CatchUp();
        var byOffset = await test.Page(query with { Offset = 4 });
        var byCursor = await test.Page(query with { After = page1.Next });

        byOffset.Items[0].ShouldBe(page1.Items[^1]);                           // the last row of page 1 again
        byCursor.Items.ShouldNotContain(page1.Items[^1]);
        byCursor.Items[0].EndsAt.ShouldBeGreaterThan(page1.Items[^1].EndsAt);
    }

    [Fact]
    public async Task Search_is_tenant_scoped()
    {
        var (test, _) = await Seeded(5);

        (await test.Page(Query(B) with { Count = true })).Total.ShouldBe(3);
        (await test.Page(Query(default) with { Count = true })).Total.ShouldBe(0);
    }

    [Fact]
    public async Task The_page_carries_the_projection_position()
    {
        var (test, _) = await Seeded(2);

        (await test.Page(Query(A))).Position.ShouldBe(test.Host.Position);
    }

    private static string Bad(Result<Page<AuctionRow>> result)
    {
        result.TryGetValue(out Violation? violation).ShouldBeTrue();
        return violation.ShouldBeOfType<Invalid>().Errors.Single().Field;
    }
}
