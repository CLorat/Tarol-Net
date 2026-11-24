using static Tarol.Queries.Tests.Fixtures;

namespace Tarol.Queries.Tests;

/// <summary>
/// Set filters and list scopes (TN-6) on the in-memory store: "parents see only their own children" over a uuid column, a
/// uuid[] column and the row id; an empty set is an empty page, never an unfiltered one; the set binds the cursor.
/// </summary>
public sealed class ListScopeTests
{
    private static readonly Guid Mia = Guid.Parse("0196f000-0000-7000-8000-000000000001");
    private static readonly Guid Leo = Guid.Parse("0196f000-0000-7000-8000-000000000002");
    private static readonly Guid Ida = Guid.Parse("0196f000-0000-7000-8000-000000000003");
    private static readonly Guid Ben = Guid.Parse("0196f000-0000-7000-8000-000000000004");

    /// <summary>Two entries per child in tenant A, two group entries, one entry for Mia in tenant B.</summary>
    private static async Task<ProjectionTest<LogRow>> Seeded()
    {
        var test = ProjectionTest<LogRow>.Create();
        var at = Now;
        foreach (var child in new[] { Mia, Leo, Ida, Ben })
            for (var i = 0; i < 2; i++)
                test.Given(LogEntry.Write(child, [], $"{Name(child)} {i}", at = at.AddMinutes(1)), A);
        test.Given(LogEntry.Write(Guid.Empty, [Mia, Ida], "Mia and Ida painted", at = at.AddMinutes(1)), A);
        test.Given(LogEntry.Write(Guid.Empty, [Ben], "Ben slept", at = at.AddMinutes(1)), A);
        test.Given(LogEntry.Write(Mia, [], "Mia elsewhere", at.AddMinutes(1)), B);
        await test.CatchUp();
        return test;
    }

    private static string Name(Guid child) => child == Mia ? "Mia" : child == Leo ? "Leo" : child == Ida ? "Ida" : "Ben";

    private static async Task<List<string>> Texts(ProjectionTest<LogRow> test, ListQuery query)
    {
        var texts = new List<string>();
        for (var q = query; ;)
        {
            var result = await test.Search(q);
            result.TryGetValue(out Page<LogRow>? page).ShouldBeTrue(result.ToString());
            texts.AddRange(page!.Items.Select(r => r.Text));
            if (page.Next is null)
                return texts;
            q = q with { After = page.Next };
        }
    }

    private static ListQuery Logs => Query(A) with { Sort = "at", Limit = 2 };

    [Fact]
    public async Task A_parent_with_two_children_sees_the_entries_of_both_and_no_other()
    {
        var test = await Seeded();

        var texts = await Texts(test, Logs.Within(ListScope.In(nameof(LogRow.Child), [Mia, Leo])));

        texts.ShouldBe(["Mia 0", "Mia 1", "Leo 0", "Leo 1"]);
    }

    [Fact]
    public async Task A_set_over_an_array_column_matches_rows_that_share_a_value()
    {
        var test = await Seeded();

        var texts = await Texts(test, Logs.Within(ListScope.In("tagged", [Leo, Ida])));

        texts.ShouldBe(["Mia and Ida painted"]);
    }

    [Fact]
    public async Task An_empty_set_lists_an_empty_page_never_an_unfiltered_one()
    {
        var test = await Seeded();

        foreach (var scope in new[] { ListScope.None, ListScope.In<Guid>("child", []), ListScope.In<Guid>("tagged", []) })
        {
            var result = await test.Search(Logs.Within(scope) with { Count = true });
            result.TryGetValue(out Page<LogRow>? page).ShouldBeTrue();
            page!.Items.ShouldBeEmpty();
            page.Next.ShouldBeNull();
            page.Total.ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_set_on_the_id_and_together_with_the_requests_own_filters()
    {
        var test = await Seeded();
        var leo = test.Rows(A).Where(r => r.Child == Leo).Select(r => r.Id).ToArray();

        (await Texts(test, Logs.Within(ListScope.In(SetFilter.IdColumn, leo)))).ShouldBe(["Leo 0", "Leo 1"]);
        var both = Logs with { Filters = [new ColumnFilter("text", "Mia 1")] };
        (await Texts(test, both.Within(ListScope.In("child", [Mia, Leo])))).ShouldBe(["Mia 1"]);
        (await Texts(test, both.Within(ListScope.In("child", [Leo])))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_scope_never_reaches_another_tenant()
    {
        var test = await Seeded();

        (await Texts(test, Query(B).Within(ListScope.In("child", [Mia])))).ShouldBe(["Mia elsewhere"]);
        (await Texts(test, Logs.Within(ListScope.In("child", [Mia])))).ShouldNotContain("Mia elsewhere");
    }

    [Fact]
    public async Task The_cursor_is_bound_to_the_set_but_not_to_its_order_or_duplicates()
    {
        var test = await Seeded();
        var first = await test.Search(Logs.Within(ListScope.In("child", [Mia, Leo])));
        first.TryGetValue(out Page<LogRow>? page).ShouldBeTrue();
        var next = page!.Next.ShouldNotBeNull();

        var same = await test.Search(Logs.Within(ListScope.In("child", [Leo, Mia, Leo])) with { After = next });
        var other = await test.Search(Logs.Within(ListScope.In("child", [Mia])) with { After = next });
        var unscoped = await test.Search(Logs with { After = next });

        same.TryGetValue(out Page<LogRow>? second).ShouldBeTrue(same.ToString());
        second!.Items.Select(r => r.Text).ShouldBe(["Leo 0", "Leo 1"]);
        other.IsSuccess.ShouldBeFalse();                                   // Query.Cursor: start again, never a wrong page
        unscoped.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Without_sets_the_filter_hash_is_the_one_before_sets_existed()
    {
        var filters = new[] { ("status", ColumnType.Int4, (object?)1) };

        Cursor.Hash(filters, []).ShouldBe(Cursor.Hash(filters));
        Cursor.Hash(filters, [("child", ColumnType.Uuid, [Mia])]).ShouldNotBe(Cursor.Hash(filters));
        Cursor.Hash(filters, [("child", ColumnType.Uuid, [])]).ShouldNotBe(Cursor.Hash(filters));
    }

    [Fact]
    public async Task Programming_errors_throw()
    {
        var test = await Seeded();

        await Should.ThrowAsync<ArgumentException>(async () => _ = await test.Search(Logs with { Filters = [new ColumnFilter("tagged", Mia)] }));
        await Should.ThrowAsync<ArgumentException>(async () => _ = await test.Search(Logs with { Sets = [new SetFilter("child", [Mia, null!])] }));
        await Should.ThrowAsync<ArgumentException>(async () => _ = await test.Search(Logs with { Sets = [SetFilter.Of("nope", [Mia])] }));
        await Should.ThrowAsync<ArgumentException>(async () => _ = await test.Search(Logs with { Sets = [SetFilter.Of("child", ["not a guid"])] }));
    }

    [Fact]
    public void A_scope_combines_and_All_adds_nothing()
    {
        ListScope.All.IsAll.ShouldBeTrue();
        var logs = Logs;
        logs.Within(ListScope.All).ShouldBeSameAs(logs);
        var scope = ListScope.Where("text", "x").AndIn("child", [Mia]);
        scope.Filters.ShouldHaveSingleItem().Column.ShouldBe("text");
        scope.Sets.ShouldHaveSingleItem().Values.ShouldBe([Mia]);
        ListScope.None.Sets.ShouldHaveSingleItem().Values.ShouldBeEmpty();
    }
}
