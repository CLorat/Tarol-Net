using static Tarol.Queries.Npgsql.IntegrationTests.Setup;

namespace Tarol.Queries.Npgsql.IntegrationTests;

/// <summary>Rows per entity in PostgreSQL (TN-54, P-1): the key is the entity, an indexed stream_id column, removal by stream.</summary>
[Trait("Category", "Integration")]
public sealed class PerEntityTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Entities_are_rows_with_their_stream_and_are_removed_by_it_like_the_in_memory_store()
    {
        var setup = await postgres.Create();
        var north = Kennel.Open("North");
        var rex = north.Add("Rex");
        north.Add("Bello");
        var south = Kennel.Open("South");
        var luna = south.Add("Luna");
        await setup.Commit(north, TenantA);
        await setup.Commit(south, TenantA);
        rex.Rename("Rexy");
        await setup.Commit(north, TenantA);
        await setup.CatchUp(setup.Host<DogRow>());
        var store = setup.ReadModels.Store<DogRow>();
        var memory = ProjectionTest<DogRow>.Over(setup.Store);
        await setup.CatchUp(memory.Host);

        var sql = await store.All(Query(TenantA) with { Sort = "name" });
        var row = await store.Find(new RowKey(TenantA.Value, rex.Id.Value), Ct);
        var table = $"{setup.Schema}.rm_dog_list_v1";

        sql.Select(r => r.Name).ShouldBe(["Bello", "Luna", "Rexy"]);
        sql.ShouldBe(await memory.ReadModels.All(Query(TenantA) with { Sort = "name" }));
        row.ShouldNotBeNull().Stream.ShouldBe(north.Id.Value);
        (await postgres.Scalar($"SELECT count(*) FROM {table} WHERE stream_id = '{north.Id.Value}'")).ShouldBe(2L);
        (await postgres.Scalar($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{setup.Schema}' AND indexname = 'rm_dog_list_v1_stream'")).ShouldBe(1L);

        (await store.RemoveStreams([new RowKey(TenantA.Value, north.Id.Value)], Ct)).ShouldBe(2);
        (await store.All(Query(TenantA))).Select(r => r.Id).ShouldBe([luna.Id.Value]);
    }
}
