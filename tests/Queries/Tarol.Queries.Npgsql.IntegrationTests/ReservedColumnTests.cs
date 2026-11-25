using static Tarol.Queries.Npgsql.IntegrationTests.Setup;

namespace Tarol.Queries.Npgsql.IntegrationTests;

/// <summary>
/// Tatalu FB-F05.8-3: a row with members <c>From</c>, <c>Until</c>, <c>Order</c>, <c>Limit</c> gives columns named like
/// reserved words. Unquoted, <c>CREATE TABLE</c> failed and the projection never became current.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReservedColumnTests(Postgres postgres)
{
    private static readonly Guid Mia = Guid.Parse("0196f000-0000-7000-8000-000000000011");
    private static readonly DateOnly Monday = new(2026, 11, 23);

    [Fact]
    public async Task Columns_named_like_reserved_words_are_created_written_filtered_and_sorted()
    {
        var setup = await postgres.Create();
        for (var d = 0; d < 3; d++)
            await setup.Commit(MedicationOrder.Place(Mia, Monday.AddDays(d), d == 0 ? null : Monday.AddDays(d + 7), $"dose {d}", 3 - d, Postgres.Now.AddMinutes(d)), TenantA);
        var host = setup.Host<MedicationOrderRow>();

        await setup.CatchUp(host);

        host.State.ShouldBe(ProjectionState.Current);
        var store = setup.ReadModels.Store<MedicationOrderRow>();
        (await store.All(Query(TenantA) with { Sort = "from", Descending = true })).Select(r => r.Order).ShouldBe(["dose 2", "dose 1", "dose 0"]);
        (await store.All(Query(TenantA) with { Sort = "limit" })).First().Order.ShouldBe("dose 2");
        (await store.All(Query(TenantA) with { Filters = [new ColumnFilter("until", null)] })).Single().Order.ShouldBe("dose 0");
        (await store.All(Query(TenantA) with { Filters = [new ColumnFilter("order", "dose 1")] })).Single().From.ShouldBe(Monday.AddDays(1));
        (await store.All(Query(TenantA) with { Sort = "from", Limit = 1 })).Select(r => r.Order).ShouldBe(["dose 0", "dose 1", "dose 2"]);   // the cursor over "from"
    }
}
