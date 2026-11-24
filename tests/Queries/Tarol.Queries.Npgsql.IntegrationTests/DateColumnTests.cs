using static Tarol.Queries.Npgsql.IntegrationTests.Setup;

namespace Tarol.Queries.Npgsql.IntegrationTests;

/// <summary>DateOnly and TimeOnly columns on PostgreSQL (TN-7): date and time, filters, sorts, cursors, the schema upgrade.</summary>
[Trait("Category", "Integration")]
public sealed class DateColumnTests(Postgres postgres)
{
    private static readonly Guid Mia = Guid.Parse("0196f000-0000-7000-8000-000000000001");
    private static readonly Guid Leo = Guid.Parse("0196f000-0000-7000-8000-000000000002");
    private static readonly DateOnly Monday = new(2026, 11, 23);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Setup> Seeded(Postgres postgres)
    {
        var setup = await postgres.Create();
        for (var d = 0; d < 5; d++)
        {
            await setup.Commit(Attendance.CheckIn(Mia, Monday.AddDays(d), new TimeOnly(7, 30 + d), null, Postgres.Now.AddMinutes(d)), TenantA);
            await setup.Commit(Attendance.CheckIn(Leo, Monday.AddDays(d), new TimeOnly(8, 15, 0, 123, 456).Add(TimeSpan.FromTicks(7)), Monday.AddDays(2),
                Postgres.Now.AddMinutes(10 + d)), TenantA);
        }
        await setup.CatchUp(setup.Host<AttendanceRow>());
        return setup;
    }


    [Fact]
    public async Task Days_and_times_of_day_are_date_and_time_columns()
    {
        var setup = await Seeded(postgres);

        async Task<object?> Type(string column) => await postgres.Scalar(
            $"SELECT data_type FROM information_schema.columns WHERE table_schema = '{setup.Schema}' AND table_name = 'rm_attendance_list_v1' AND column_name = '{column}'");
        (await Type("day")).ShouldBe("date");
        (await Type("arrived")).ShouldBe("time without time zone");
        (await Type("contract_ends")).ShouldBe("date");
        (await postgres.Scalar($"SELECT count(*) FROM {setup.Schema}.rm_attendance_list_v1 WHERE arrived = '08:15:00.123456'")).ShouldBe(5L);
    }

    [Fact]
    public async Task Filters_sorts_and_cursors_answer_like_the_in_memory_store()
    {
        var setup = await Seeded(postgres);
        var store = setup.ReadModels.Store<AttendanceRow>();
        var memory = ProjectionTest<AttendanceRow>.Over(setup.Store);
        await setup.CatchUp(memory.Host);

        foreach (var query in new[]
        {
            Query(TenantA) with { Sort = "day", Descending = true, Limit = 3 },
            Query(TenantA) with { Sort = "arrived", Limit = 4 },
            Query(TenantA) with { Filters = [new ColumnFilter("day", Monday.AddDays(2))], Sort = "arrived", Limit = 1 },
            Query(TenantA) with { Filters = [new ColumnFilter("contract_ends", null)], Sort = "day", Limit = 2 },
            Query(TenantA) with { Filters = [new ColumnFilter("arrived", new TimeOnly(8, 15, 0, 123, 456))] },
            Query(TenantA).Within(ListScope.In("day", [Monday, Monday.AddDays(4)])) with { Sort = "day" },
        })
        {
            var sql = await store.All(query);
            sql.ShouldNotBeEmpty();
            sql.ShouldBe(await memory.ReadModels.All(query));
        }
    }

    [Fact]
    public async Task Prepare_adds_and_fills_the_columns_a_table_of_an_older_release_lacks()
    {
        var setup = await Seeded(postgres);
        var table = $"{setup.Schema}.rm_attendance_list_v1";
        var store = setup.ReadModels.Store<AttendanceRow>();
        var checkpoint = await store.Checkpoint(Ct);
        // 0.1.0-preview.5 kept DateOnly and TimeOnly in the row's jsonb only
        await postgres.Execute($"DROP INDEX {setup.Schema}.rm_attendance_list_v1_s2; DROP INDEX {setup.Schema}.rm_attendance_list_v1_s3; "
                               + $"ALTER TABLE {table} DROP COLUMN day, DROP COLUMN arrived, DROP COLUMN contract_ends");

        await store.Prepare(Ct);

        (await postgres.Scalar($"SELECT count(*) FROM {table} WHERE day IS NULL OR arrived IS NULL")).ShouldBe(0L);
        (await postgres.Scalar($"SELECT count(*) FROM {table} WHERE contract_ends = '2026-11-25'")).ShouldBe(5L);
        (await postgres.Scalar($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{setup.Schema}' AND tablename = 'rm_attendance_list_v1'")).ShouldBe(3L);
        (await store.All(Query(TenantA) with { Sort = "day", Filters = [new ColumnFilter("child", Mia)] })).Select(r => r.Day)
            .ShouldBe([.. Enumerable.Range(0, 5).Select(d => Monday.AddDays(d))]);
        (await store.Checkpoint(Ct)).ShouldBe(checkpoint);
    }
}
