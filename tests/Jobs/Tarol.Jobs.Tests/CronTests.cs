namespace Tarol.Jobs.Tests;

public sealed class CronTests
{
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static List<DateTimeOffset> Take(Schedule schedule, DateTimeOffset after, int count, TimeZoneInfo zone)
    {
        var list = new List<DateTimeOffset>();
        for (var at = after; list.Count < count;)
            list.Add(at = schedule.After(at, zone));
        return list;
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute, int offset)
        => new(year, month, day, hour, minute, 0, TimeSpan.FromHours(offset));

    // ── the DST table of jobs.md §2.2 (Europe/Vienna) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_time_the_spring_forward_skips_runs_at_the_end_of_the_gap()
        => Take(Cron.Parse("30 2 * * *"), Local(2027, 3, 27, 0, 0, 1), 3, Vienna)
            .ShouldBe([Local(2027, 3, 27, 2, 30, 1), Local(2027, 3, 28, 3, 0, 2), Local(2027, 3, 29, 2, 30, 2)]);

    [Fact]
    public void A_time_the_fall_back_repeats_runs_once_at_its_first_instant()
        => Take(Cron.Parse("30 2 * * *"), Local(2026, 10, 24, 0, 0, 2), 3, Vienna)
            .ShouldBe([Local(2026, 10, 24, 2, 30, 2), Local(2026, 10, 25, 2, 30, 2), Local(2026, 10, 26, 2, 30, 1)]);

    [Fact]
    public void An_hourly_job_runs_once_per_clock_time_in_the_repeated_hour()
        => Take(Cron.Parse("0 * * * *"), Local(2026, 10, 25, 0, 30, 2), 4, Vienna)
            .ShouldBe([Local(2026, 10, 25, 1, 0, 2), Local(2026, 10, 25, 2, 0, 2), Local(2026, 10, 25, 3, 0, 1), Local(2026, 10, 25, 4, 0, 1)]);

    [Fact]
    public void Steps_through_the_spring_gap_run_once_at_its_end()
        => Take(Cron.Parse("*/30 * * * *"), Local(2027, 3, 28, 0, 59, 1), 4, Vienna)
            .ShouldBe([Local(2027, 3, 28, 1, 0, 1), Local(2027, 3, 28, 1, 30, 1), Local(2027, 3, 28, 3, 0, 2), Local(2027, 3, 28, 3, 30, 2)]);

    [Fact]
    public void From_inside_the_second_pass_of_the_repeated_hour_the_next_clock_time_follows()
        => Cron.Parse("15 * * * *").After(Local(2026, 10, 25, 2, 20, 1), Vienna).ShouldBe(Local(2026, 10, 25, 3, 15, 1));

    // ── Vixie semantics ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Day_of_month_and_day_of_week_both_restricted_mean_either()
        => Take(Cron.Parse("0 2 13 * 5"), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), 4, TimeZoneInfo.Utc)
            .Select(t => (t.Day, t.DayOfWeek)).ShouldBe([(6, DayOfWeek.Friday), (13, DayOfWeek.Friday), (20, DayOfWeek.Friday), (27, DayOfWeek.Friday)]);

    [Fact]
    public void A_day_of_month_alone_restricts_the_day()
        => Take(Cron.Parse("0 2 13 * *"), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), 2, TimeZoneInfo.Utc)
            .ShouldBe([new DateTimeOffset(2026, 11, 13, 2, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 13, 2, 0, 0, TimeSpan.Zero)]);

    [Fact]
    public void A_star_step_counts_as_a_star_and_the_fields_are_anded()
        => Take(Cron.Parse("0 0 */10 * MON"), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), 2, TimeZoneInfo.Utc)
            .Select(t => (t.Day, t.DayOfWeek)).ShouldAllBe(d => d.DayOfWeek == DayOfWeek.Monday && (d.Day - 1) % 10 == 0);

    [Theory]
    [InlineData("0 9 * * MON-FRI", "0 9 * * 1-5")]
    [InlineData("0 9 * JAN,jul *", "0 9 * 1,7 *")]
    [InlineData("0 9 * * 7", "0 9 * * 0")]
    [InlineData("0 9 * * SUN", "0 9 * * 0")]
    [InlineData("5/20 * * * *", "5,25,45 * * * *")]
    [InlineData("0  9\t*  * *", "0 9 * * *")]
    public void Names_weekday_seven_and_vixie_steps_mean_their_numbers(string written, string meant)
    {
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        Take(Cron.Parse(written), from, 30, Vienna).ShouldBe(Take(Cron.Parse(meant), from, 30, Vienna));
    }

    [Fact]
    public void After_is_strictly_after_and_whole_seconds_in_UTC()
    {
        var cron = Cron.Parse("0 2 * * *");
        var at = new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero);

        cron.After(at, TimeZoneInfo.Utc).ShouldBe(at.AddDays(1));
        cron.After(at.AddTicks(-1), TimeZoneInfo.Utc).ShouldBe(at);
        var next = cron.After(new DateTimeOffset(2026, 11, 16, 3, 15, 22, 333, TimeSpan.FromHours(5)), Vienna);
        next.Offset.ShouldBe(TimeSpan.Zero);
        (next.UtcTicks % TimeSpan.TicksPerSecond).ShouldBe(0);
        cron.Text.ShouldBe("0 2 * * *");
        Cron.Parse("0   2 * *\t*").Text.ShouldBe("0 2 * * *");
    }

    [Fact]
    public void Leap_days_run_every_four_years_and_an_impossible_day_is_refused()
    {
        Take(Cron.Parse("0 0 29 2 *"), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 2, TimeZoneInfo.Utc)
            .ShouldBe([new DateTimeOffset(2028, 2, 29, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2032, 2, 29, 0, 0, 0, TimeSpan.Zero)]);
        Cron.TryParse("0 0 30 2 *", out _, out var error).ShouldBeFalse();
        error.ShouldBe("the day-of-month never falls in the month (e.g. 30 2)");
        Cron.TryParse("0 0 30 2 MON", out _, out _).ShouldBeTrue();                    // OR: every Monday of February
    }

    [Theory]
    [InlineData("0 2 * *", "a schedule has 5 fields (minute hour day-of-month month day-of-week), found 4")]
    [InlineData("0 0 2 * * *", "a schedule has 5 fields (minute hour day-of-month month day-of-week), found 6 (seconds are not supported)")]
    [InlineData("60 2 * * *", "minute: '60' is outside 0–59")]
    [InlineData("0 2 L * *", "day-of-month: 'L' is not supported (no L W # ?)")]
    [InlineData("0 2 * * MON#2", "day-of-week: '#' and '?' are not supported")]
    [InlineData("0 2 * FOO *", "month: 'FOO' is not a name (JAN FEB MAR APR MAY JUN JUL AUG SEP OCT NOV DEC)")]
    [InlineData("*/0 * * * *", "minute: the step in '*/0' must be 1–60")]
    [InlineData("", "the expression is empty")]
    public void Invalid_expressions_name_the_field_as_TCHG010_does(string expression, string why)
    {
        Cron.TryParse(expression, out var cron, out var error).ShouldBeFalse();
        cron.ShouldBeNull();
        error.ShouldBe(why);
        Should.Throw<FormatException>(() => Cron.Parse(expression)).Message.ShouldBe($"The cron schedule '{expression}' is invalid: {why}.");
    }

    [Fact]
    public void Intervals_are_aligned_to_the_epoch_in_any_zone()
    {
        var every = Schedule.Every(TimeSpan.FromMinutes(15));

        Take(every, new DateTimeOffset(2026, 10, 25, 0, 50, 0, TimeSpan.Zero), 3, Vienna)
            .ShouldBe([new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 1, 15, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero)]);
        every.After(new DateTimeOffset(2026, 10, 25, 1, 15, 0, TimeSpan.Zero), TimeZoneInfo.Utc).ShouldBe(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero));
        every.Text.ShouldBe("every 900s");
        Schedule.Every(TimeSpan.FromSeconds(1)).After(new DateTimeOffset(1969, 12, 31, 23, 59, 58, 500, TimeSpan.Zero), TimeZoneInfo.Utc)
            .ShouldBe(new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero));                     // before the epoch too
        Should.Throw<ArgumentOutOfRangeException>(() => Schedule.Every(TimeSpan.FromMilliseconds(1500)));
        Should.Throw<ArgumentOutOfRangeException>(() => Schedule.Every(TimeSpan.Zero));
    }

    [Fact]
    public void Latest_finds_the_newest_occurrence_up_to_now()
    {
        var cron = Cron.Parse("0 2 * * *");
        var cursor = new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero);

        cron.Latest(cursor, cursor.AddDays(3).AddHours(5), TimeZoneInfo.Utc).ShouldBe(cursor.AddDays(3));
        cron.Latest(cursor, cursor, TimeZoneInfo.Utc).ShouldBe(cursor);
        Schedule.Every(TimeSpan.FromHours(1)).Latest(cursor, cursor.AddDays(30).AddMinutes(59), TimeZoneInfo.Utc).ShouldBe(cursor.AddDays(30));
    }

    /// <summary>
    /// The differential of the design (jobs.md §2.3) against the probe's own parser instead of Cronos: the same 14 expressions,
    /// UTC, Vienna and New York, 2 years. The two implementations agree on every occurrence.
    /// </summary>
    [Fact]
    public void The_parser_agrees_with_the_probe_over_two_years_in_three_zones()
    {
        string[] expressions = ["0 2 * * *", "30 2 * * *", "*/15 * * * *", "0 */6 * * *", "0 9 * * 1-5", "0 0 1 * *", "15 3 1,15 * *", "0 12 * * 0",
            "5 4 * * 7", "0 22 * * 1-5", "*/7 2-3 * * *", "0 2 13 * 5", "45 1-3 * * *", "0 0 29 2 *"];
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddYears(2);
        var compared = 0;
        foreach (var zone in new[] { TimeZoneInfo.Utc, Vienna, NewYork })
            foreach (var expression in expressions)
            {
                var ours = new List<DateTimeOffset>();
                for (var at = Cron.Parse(expression).After(from, zone); at <= to; at = Cron.Parse(expression).After(at, zone))
                    ours.Add(at);
                var probe = new List<DateTimeOffset>();
                var oracle = new ProbeCron(expression);
                for (var at = oracle.Next(from, zone); at is { } t && t <= to; at = oracle.Next(t, zone))
                    probe.Add(t.ToUniversalTime());
                ours.ShouldBe(probe, $"{expression} in {zone.Id}");
                compared += ours.Count;
            }
        compared.ShouldBe(273_665);                     // the design's 273,703 counted max(Cronos, ours): Cronos' 38 extra runs in the repeated hour
    }
}
