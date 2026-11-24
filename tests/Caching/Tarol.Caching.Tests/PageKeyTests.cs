using Tarol.Queries;

namespace Tarol.Caching.Tests;

public sealed class PageKeyTests
{
    private static PageKey KeyOf(ListQuery query)
    {
        ListQueryPlan<AuctionRow>.Create(query).TryGetValue(out ListQueryPlan<AuctionRow>? plan).ShouldBeTrue();
        return PageKey.Of(plan!);
    }

    [Fact]
    public void The_key_starts_with_the_tenant_then_the_projection_and_its_version()
    {
        var key = KeyOf(new ListQuery(Lab.TenantA) { Sort = "ends_at", Descending = true, Filters = [new("status", AuctionStatus.Open)], Limit = 20, Count = true });

        key.Tenant.ShouldBe(Lab.TenantA);
        key.Projection.ShouldBe("auction-list");
        key.Version.ShouldBe(1);
        key.Query.ShouldBe("s8:-ends_at;f6:status;=1:0;a-;o0;l20;c1");    // the enum in its stored form
    }

    [Fact]
    public void Values_are_length_prefixed_so_no_value_can_forge_another_query()
    {
        var one = KeyOf(new ListQuery(Lab.TenantA) { Filters = [new("seller", "a;f5:title=1:x"), new("title", "y")] });
        var two = KeyOf(new ListQuery(Lab.TenantA) { Filters = [new("seller", "a"), new("title", "x;f5:title=1:y")] });
        var nul = KeyOf(new ListQuery(Lab.TenantA) { Filters = [new("seller", null)] });
        var dash = KeyOf(new ListQuery(Lab.TenantA) { Filters = [new("seller", "-")] });

        one.ShouldNotBe(two);
        nul.ShouldNotBe(dash);
    }

    [Fact]
    public void Equal_queries_give_equal_keys_whatever_the_filter_order_and_the_value_form()
    {
        var at = Lab.Now;
        var one = KeyOf(new ListQuery(Lab.TenantA) { Filters = [new("seller", "ada"), new("ends_at", at)] });
        var two = KeyOf(new ListQuery(Lab.TenantA) { Filters = [new("ends_at", at.ToOffset(TimeSpan.FromHours(2))), new("seller", "ada")] });

        two.ShouldBe(one);
        KeyOf(new ListQuery(Lab.TenantA) { Offset = 3, After = null }).ShouldNotBe(KeyOf(new ListQuery(Lab.TenantA)));
    }

    [Fact]
    public void Two_callers_of_one_tenant_with_different_scopes_never_share_a_page()
    {
        var (one, two) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var plain = KeyOf(new ListQuery(Lab.TenantA));
        var parent = KeyOf(new ListQuery(Lab.TenantA).Within(ListScope.In(SetFilter.IdColumn, [one, two])));
        var sameParent = KeyOf(new ListQuery(Lab.TenantA).Within(ListScope.In(SetFilter.IdColumn, [two, one, two])));
        var otherParent = KeyOf(new ListQuery(Lab.TenantA).Within(ListScope.In(SetFilter.IdColumn, [one])));
        var nobody = KeyOf(new ListQuery(Lab.TenantA).Within(ListScope.None));

        sameParent.ShouldBe(parent);                                        // order and duplicates do not matter
        new[] { plain, parent, otherParent, nobody }.Distinct().Count().ShouldBe(4);
        nobody.Query.ShouldBe("s2:id;i2:id;n0;a-;o0;l50;c0");
    }
}
