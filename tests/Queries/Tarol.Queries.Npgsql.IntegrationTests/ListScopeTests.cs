using static Tarol.Queries.Npgsql.IntegrationTests.Setup;

namespace Tarol.Queries.Npgsql.IntegrationTests;

/// <summary>Set filters on PostgreSQL (TN-6): = ANY over a uuid column, &amp;&amp; over uuid[] and text[], the id, the schema upgrade.</summary>
[Trait("Category", "Integration")]
public sealed class ListScopeTests(Postgres postgres)
{
    private static readonly Guid Mia = Guid.Parse("0196f000-0000-7000-8000-000000000001");
    private static readonly Guid Leo = Guid.Parse("0196f000-0000-7000-8000-000000000002");
    private static readonly Guid Ida = Guid.Parse("0196f000-0000-7000-8000-000000000003");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Setup> Seeded(Postgres postgres)
    {
        var setup = await postgres.Create();
        var at = Postgres.Now;
        foreach (var child in new[] { Mia, Leo, Ida })
            for (var i = 0; i < 2; i++)
                await setup.Commit(LogEntry.Write(child, [], [], $"{child.ToString()[^1]} {i}", at = at.AddMinutes(1)), TenantA);
        await setup.Commit(LogEntry.Write(Guid.Empty, [Mia, Ida], ["paint", "a \"quoted\", {odd} \\ label"], "group", at.AddMinutes(1)), TenantA);
        await setup.Commit(LogEntry.Write(Mia, [], [], "elsewhere", at.AddMinutes(2)), TenantB);
        await setup.CatchUp(setup.Host<LogRow>());
        return setup;
    }


    private static ListQuery Logs => Query(TenantA) with { Sort = "at", Limit = 2 };

    private static async Task<List<string>> Texts(IReadModelStore<LogRow> store, ListQuery query) => [.. (await store.All(query)).Select(r => r.Text)];

    [Fact]
    public async Task Set_filters_answer_like_the_in_memory_store()
    {
        var setup = await Seeded(postgres);
        var store = setup.ReadModels.Store<LogRow>();
        var memory = ProjectionTest<LogRow>.Over(setup.Store);
        await setup.CatchUp(memory.Host);
        var group = (await store.All(Logs)).Single(r => r.Text == "group").Id;

        foreach (var scope in new[]
        {
            ListScope.In("child", [Mia, Leo]), ListScope.In(nameof(LogRow.Tagged), [Leo, Ida]), ListScope.In("labels", ["a \"quoted\", {odd} \\ label"]),
            ListScope.In(SetFilter.IdColumn, [group]), ListScope.None, ListScope.In<Guid>("tagged", []), ListScope.In("child", [Mia]).AndIn("tagged", [Mia]),
        })
        {
            var sql = await Texts(store, Logs.Within(scope));
            sql.ShouldBe(await Texts(memory.ReadModels, Logs.Within(scope)));
            var counted = await store.Search(Logs.Within(scope) with { Count = true }, Ct);
            counted.TryGetValue(out Page<LogRow>? page).ShouldBeTrue(counted.ToString());
            page!.Total.ShouldBe(sql.Count);
        }
        (await Texts(store, Logs.Within(ListScope.In("child", [Mia, Leo])))).ShouldBe(["1 0", "1 1", "2 0", "2 1"]);
        (await Texts(store, Logs.Within(ListScope.In("tagged", [Leo, Ida])))).ShouldBe(["group"]);
        (await Texts(store, Logs.Within(ListScope.None))).ShouldBeEmpty();
        (await Texts(store, Query(TenantB).Within(ListScope.In("child", [Mia])))).ShouldBe(["elsewhere"]);
    }

    [Fact]
    public async Task Array_columns_are_stored_as_arrays_with_a_gin_index()
    {
        var setup = await Seeded(postgres);
        var table = $"{setup.Schema}.rm_log_list_v1";

        (await postgres.Scalar($"SELECT data_type FROM information_schema.columns WHERE table_schema = '{setup.Schema}' AND table_name = 'rm_log_list_v1' AND column_name = 'tagged'"))
            .ShouldBe("ARRAY");
        (await postgres.Scalar($"SELECT labels[2] FROM {table} WHERE text = 'group'")).ShouldBe("a \"quoted\", {odd} \\ label");
        (await postgres.Scalar($"SELECT cardinality(tagged) FROM {table} WHERE text = 'group'")).ShouldBe(2);
        (await postgres.Scalar($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{setup.Schema}' AND tablename = 'rm_log_list_v1' AND indexdef LIKE '%USING gin%'"))
            .ShouldBe(2L);
    }

    [Fact]
    public async Task Prepare_adds_and_fills_a_column_a_table_of_an_older_release_lacks()
    {
        var setup = await Seeded(postgres);
        var table = $"{setup.Schema}.rm_log_list_v1";
        var store = setup.ReadModels.Store<LogRow>();
        var checkpoint = await store.Checkpoint(Ct);
        // the table as 0.1.0-preview.5 created it: an array member had no column
        await postgres.Execute($"DROP INDEX {setup.Schema}.rm_log_list_v1_a2; DROP INDEX {setup.Schema}.rm_log_list_v1_a3; ALTER TABLE {table} DROP COLUMN tagged, DROP COLUMN labels");

        await store.Prepare(Ct);

        (await postgres.Scalar($"SELECT count(*) FROM {table} WHERE tagged IS NULL OR labels IS NULL")).ShouldBe(0L);
        (await postgres.Scalar($"SELECT is_nullable FROM information_schema.columns WHERE table_schema = '{setup.Schema}' AND table_name = 'rm_log_list_v1' AND column_name = 'tagged'"))
            .ShouldBe("NO");
        (await Texts(store, Logs.Within(ListScope.In("tagged", [Ida])))).ShouldBe(["group"]);
        (await store.Checkpoint(Ct)).ShouldBe(checkpoint);                // filled from the rows, not rebuilt
        await store.Prepare(Ct);                                          // idempotent
    }
}
