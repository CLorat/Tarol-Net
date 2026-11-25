using static Tarol.Queries.Tests.Fixtures;

namespace Tarol.Queries.Tests;

/// <summary>DateOnly and TimeOnly columns (TN-7): calendar days and times of day are filterable and sortable, not instants.</summary>
public sealed class DateColumnTests
{
    private static readonly Guid Mia = Guid.Parse("0196f000-0000-7000-8000-000000000001");
    private static readonly Guid Leo = Guid.Parse("0196f000-0000-7000-8000-000000000002");
    private static readonly DateOnly Monday = new(2026, 11, 23);

    /// <summary>Two children over five days; Mia arrives 7:30 + day minutes, Leo 8:15; Leo's contract ends on Wednesday.</summary>
    private static async Task<ProjectionTest<AttendanceRow>> Seeded()
    {
        var test = ProjectionTest<AttendanceRow>.Create();
        for (var d = 0; d < 5; d++)
        {
            test.Given(Attendance.CheckIn(Mia, Monday.AddDays(d), new TimeOnly(7, 30 + d), null, Now.AddMinutes(d)), A);
            test.Given(Attendance.CheckIn(Leo, Monday.AddDays(d), new TimeOnly(8, 15, 0, 123, 456).Add(TimeSpan.FromTicks(7)), Monday.AddDays(2), Now.AddMinutes(10 + d)), A);
        }
        await test.CatchUp();
        return test;
    }

    private static IReadOnlyList<ProjectionColumn<T>> Columns<T>()
        where T : class, IProjection<T>
        => T.Columns;

    private static async Task<List<AttendanceRow>> All(ProjectionTest<AttendanceRow> test, ListQuery query)
    {
        var rows = new List<AttendanceRow>();
        for (var q = query; ;)
        {
            var result = await test.Search(q);
            result.TryGetValue(out Page<AttendanceRow>? page).ShouldBeTrue(result.ToString());
            rows.AddRange(page!.Items);
            if (page.Next is null)
                return rows;
            q = q with { After = page.Next };
        }
    }

    [Fact]
    public void The_generator_maps_DateOnly_to_date_and_TimeOnly_to_time()
    {
        var columns = Columns<AttendanceRow>();
        columns.Single(c => c.Name == "day").Type.ShouldBe(ColumnType.Date);
        columns.Single(c => c.Name == "day").Sortable.ShouldBeTrue();
        columns.Single(c => c.Name == "arrived").Type.ShouldBe(ColumnType.Time);
        columns.Single(c => c.Name == "contract_ends").Nullable.ShouldBeTrue();
    }

    [Fact]
    public async Task A_day_is_an_equality_filter_and_a_sort_with_cursor_pages()
    {
        var test = await Seeded();

        var wednesday = await All(test, Query(A) with { Filters = [new ColumnFilter("day", Monday.AddDays(2))], Sort = "arrived", Limit = 1 });
        var byDay = await All(test, Query(A) with { Filters = [new ColumnFilter("child", Mia)], Sort = "day", Descending = true, Limit = 2 });

        wednesday.Select(r => r.Child).ShouldBe([Mia, Leo]);
        byDay.Select(r => r.Day).ShouldBe([.. Enumerable.Range(0, 5).Select(d => Monday.AddDays(4 - d))]);
    }

    [Fact]
    public async Task A_nullable_day_filters_on_null_and_a_set_of_days()
    {
        var test = await Seeded();

        (await All(test, Query(A) with { Filters = [new ColumnFilter("contract_ends", null)] })).ShouldAllBe(r => r.Child == Mia);
        (await All(test, Query(A) with { Filters = [new ColumnFilter("contract_ends", Monday.AddDays(2))] })).Count.ShouldBe(5);
        (await All(test, Query(A).Within(ListScope.In("day", [Monday, Monday.AddDays(4)])))).Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_time_of_day_is_stored_to_the_microsecond_as_PostgreSQL_does()
    {
        var test = await Seeded();
        var leo = new TimeOnly(8, 15, 0, 123, 456);

        (await All(test, Query(A) with { Filters = [new ColumnFilter("arrived", leo)] })).Count.ShouldBe(5);
        ColumnValues.Time(leo.Add(TimeSpan.FromTicks(7))).ShouldBe(leo);
        ColumnValues.Normalize(ColumnType.Time, leo.Add(TimeSpan.FromTicks(9))).ShouldBe(leo);
    }

    [Theory]
    [InlineData(ColumnType.Date)]
    [InlineData(ColumnType.Time)]
    public void The_cursor_text_round_trips(ColumnType type)
    {
        object value = type == ColumnType.Date ? new DateOnly(2026, 11, 24) : new TimeOnly(23, 59, 59, 999, 999);

        ColumnValues.TryParse(type, ColumnValues.Format(type, value), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(value);
        ColumnValues.TryParse(type, "x", out _).ShouldBeFalse();
        ColumnValues.TryParse(type, "99999999999999", out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => ColumnValues.Normalize(type, DateTimeOffset.UnixEpoch));   // an instant is not a day
    }

    [Fact]
    public async Task A_range_of_days_lists_the_days_between_its_bounds_with_cursor_pages()
    {
        var test = await Seeded();

        var week = await All(test, Query(A) with
        {
            Ranges = [new RangeFilter("day", RangeBound.AtLeast, Monday.AddDays(1)), new RangeFilter(nameof(AttendanceRow.Day), RangeBound.AtMost, Monday.AddDays(3))],
            Sort = "day",
            Limit = 2,
        });
        var ended = await All(test, Query(A) with { Ranges = [new RangeFilter("contract_ends", RangeBound.AtLeast, Monday)] });
        var late = await All(test, Query(A) with { Ranges = [new RangeFilter("arrived", RangeBound.AtLeast, new TimeOnly(8, 0))] });

        week.Select(r => r.Day).Distinct().ShouldBe([Monday.AddDays(1), Monday.AddDays(2), Monday.AddDays(3)]);
        week.Count.ShouldBe(6);
        ended.Count.ShouldBe(5); // Mia's rows have no contract end: null never matches a range
        ended.ShouldAllBe(r => r.Child == Leo);
        late.ShouldAllBe(r => r.Child == Leo);
    }

    [Fact]
    public async Task A_range_binds_the_cursor_and_rejects_arrays_and_nulls()
    {
        var test = await Seeded();
        var start = Query(A) with { Ranges = [new RangeFilter("day", RangeBound.AtLeast, Monday.AddDays(1))], Sort = "day", Limit = 1 };
        var first = await test.Search(start);
        first.TryGetValue(out Page<AttendanceRow>? page).ShouldBeTrue();

        var moved = await test.Search(start with { Ranges = [new RangeFilter("day", RangeBound.AtLeast, Monday)], After = page!.Next });
        var unchanged = await test.Search(Query(A) with { Sort = "day", Limit = 1 });

        moved.IsSuccess.ShouldBeFalse(); // another range is another list: the cursor does not fit
        unchanged.IsSuccess.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => ListQueryPlan<AttendanceRow>.Create(Query(A) with { Ranges = [new RangeFilter("day", RangeBound.AtLeast, null!)] }));
        Should.Throw<ArgumentException>(() => ListQueryPlan<LogRow>.Create(Query(A) with { Ranges = [new RangeFilter("tagged", RangeBound.AtMost, Guid.Empty)] }));
    }
}
