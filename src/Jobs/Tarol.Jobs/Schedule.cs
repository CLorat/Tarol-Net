using System.Globalization;
using Tarol.Cron;

namespace Tarol.Jobs;

/// <summary>
/// When a job is due: a 5-field Unix <see cref="Cron"/> expression read in a time zone, or a fixed interval
/// (<see cref="Every"/>). Occurrences are whole seconds in UTC (an occurrence must round-trip through the store unchanged).
/// </summary>
public abstract class Schedule
{
    private protected Schedule()
    {
    }

    /// <summary>The schedule's text as configured (the cron expression, or <c>every 900s</c>).</summary>
    public abstract string Text { get; }

    /// <summary>A fixed interval from the Unix epoch in UTC (<c>Every(TimeSpan.FromMinutes(15))</c> = :00, :15, :30, :45 of every hour, in any zone).</summary>
    /// <param name="interval">A whole number of seconds, at least one.</param>
    /// <returns>The schedule.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is shorter than a second or not whole seconds.</exception>
    public static Schedule Every(TimeSpan interval) => new Interval(interval);

    /// <summary>The first occurrence strictly after <paramref name="after"/>.</summary>
    /// <param name="after">Any instant.</param>
    /// <param name="zone">The zone the schedule is read in (ignored by intervals).</param>
    /// <returns>The occurrence: a whole second, in UTC.</returns>
    /// <exception cref="InvalidOperationException">The schedule has no occurrence in the next 10 years (e.g. <c>0 0 30 2 *</c>).</exception>
    public abstract DateTimeOffset After(DateTimeOffset after, TimeZoneInfo zone);

    /// <summary>The latest occurrence at or before <paramref name="now"/>, at or after <paramref name="from"/> (an occurrence itself).</summary>
    internal virtual DateTimeOffset Latest(DateTimeOffset from, DateTimeOffset now, TimeZoneInfo zone)
    {
        var latest = from;
        for (var next = After(from, zone); next <= now; next = After(next, zone))
            latest = next;
        return latest;
    }

    /// <summary>The text stored in the job row: a different text at startup recomputes the cursor.</summary>
    internal abstract string Stored(TimeZoneInfo zone);

    private sealed class Interval : Schedule
    {
        private readonly long _seconds;

        public Interval(TimeSpan interval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.FromSeconds(1));
            if (interval.Ticks % TimeSpan.TicksPerSecond != 0)
                throw new ArgumentOutOfRangeException(nameof(interval), interval, "An interval is a whole number of seconds.");
            _seconds = interval.Ticks / TimeSpan.TicksPerSecond;
        }

        public override string Text => "every " + _seconds.ToString(CultureInfo.InvariantCulture) + "s";

        public override DateTimeOffset After(DateTimeOffset after, TimeZoneInfo zone)
            => DateTimeOffset.FromUnixTimeSeconds((Math.DivRem(after.ToUnixTimeSeconds(), _seconds, out var rest) - (rest < 0 ? 1 : 0) + 1) * _seconds);

        internal override DateTimeOffset Latest(DateTimeOffset from, DateTimeOffset now, TimeZoneInfo zone)
        {
            var last = After(now, zone).AddSeconds(-_seconds);
            return last < from ? from : last;
        }

        internal override string Stored(TimeZoneInfo zone) => Text;
    }
}

/// <summary>
/// A 5-field Unix cron schedule (minute hour day-of-month month day-of-week) with Vixie cron's meaning (J-3):
/// <list type="bullet">
/// <item>lists, ranges and steps (<c>0,30</c>, <c>9-17</c>, <c>*/15</c>, <c>5/15</c>), names (<c>JAN</c>, <c>MON-FRI</c>), weekday 7 = Sunday;</item>
/// <item>day-of-month and day-of-week both restricted = <b>either</b> (<c>0 2 13 * 5</c> = every Friday and every 13th);</item>
/// <item>no seconds, no <c>L W # ?</c>;</item>
/// <item>a local time that the zone skips (spring forward) runs at the end of the gap; a local time that repeats (fall back)
/// runs once, at its first instant.</item>
/// </list>
/// </summary>
/// <example><c>Cron.Parse("0 2 * * *").After(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna"))</c></example>
public sealed class Cron : Schedule
{
    private const int YearsAhead = 10;
    private readonly CronFields _fields;

    private Cron(string text, CronFields fields)
    {
        Text = text;
        _fields = fields;
    }

    /// <summary>The expression, with its fields separated by single spaces.</summary>
    public override string Text { get; }

    /// <summary>Parses <paramref name="expression"/>.</summary>
    /// <param name="expression">A 5-field cron expression.</param>
    /// <returns>The schedule.</returns>
    /// <exception cref="FormatException">The expression is invalid; the message says why (as TCHG010 does at compile time).</exception>
    public static Cron Parse(string expression)
        => TryParse(expression, out var cron, out var error) ? cron : throw new FormatException($"The cron schedule '{expression}' is invalid: {error}.");

    /// <summary>Parses <paramref name="expression"/>.</summary>
    /// <param name="expression">A 5-field cron expression.</param>
    /// <param name="cron">The schedule, when valid.</param>
    /// <param name="error">Why it is invalid, otherwise.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool TryParse(string? expression, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Cron? cron,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        cron = null;
        error = CronFields.TryParse(expression, out var fields);
        if (error is not null)
            return false;
        if (!CanMatch(fields!))
        {
            error = "the day-of-month never falls in the month (e.g. 30 2)";
            return false;
        }
        cron = new Cron(string.Join(' ', expression!.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)), fields!);
        return true;
    }

    /// <inheritdoc />
    public override DateTimeOffset After(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var utc = after.ToUniversalTime();
        var wall = TimeZoneInfo.ConvertTime(utc, zone).DateTime;
        var start = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var limit = start.Date.AddYears(YearsAhead);
        for (var day = start.Date; day <= limit; day = day.AddDays(1))
        {
            if ((_fields.Months & (1UL << day.Month)) == 0)
            {
                day = new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1).AddDays(-1);
                continue;
            }
            if (!_fields.MatchesDay(day.Day, (int)day.DayOfWeek))
                continue;
            var today = day == start.Date;
            for (var hour = today ? start.Hour : 0; hour < 24; hour++)
            {
                if ((_fields.Hours & (1UL << hour)) == 0)
                    continue;
                for (var minute = today && hour == start.Hour ? start.Minute : 0; minute < 60; minute++)
                {
                    if ((_fields.Minutes & (1UL << minute)) == 0)
                        continue;
                    var instant = Resolve(day.AddHours(hour).AddMinutes(minute), zone);
                    if (instant > utc)
                        return instant;                                                // a repeated wall time already passed is skipped
                }
            }
        }
        throw new InvalidOperationException($"The cron schedule '{Text}' has no occurrence in the {YearsAhead} years after {after:O}.");
    }

    internal override string Stored(TimeZoneInfo zone) => Text + " " + zone.Id;

    /// <summary>The instant of a wall-clock time in <paramref name="zone"/>, with the DST rules of jobs.md §2.2.</summary>
    private static DateTimeOffset Resolve(DateTime wall, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(wall))
        {
            var end = wall;
            while (zone.IsInvalidTime(end))                                               // skipped (spring forward): the end of the gap
                end = end.AddMinutes(1);
            return new DateTimeOffset(end, zone.GetUtcOffset(end)).ToUniversalTime();
        }
        if (zone.IsAmbiguousTime(wall))                                                   // repeated (fall back): the first instant
            return new DateTimeOffset(wall, zone.GetAmbiguousTimeOffsets(wall).Max()).ToUniversalTime();
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall)).ToUniversalTime();
    }

    /// <summary>Whether a (month, day) the fields allow exists: with day-of-week <c>*…</c> Vixie ANDs, so <c>0 0 30 2 *</c> never runs.</summary>
    private static bool CanMatch(CronFields fields)
    {
        if (fields.DayStar || !fields.WeekdayStar)
            return true;                                   // every month has day 1, or OR with the weekdays: some day always matches
        int[] lengths = [0, 31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
        for (var month = 1; month <= 12; month++)
            if ((fields.Months & (1UL << month)) != 0)
                for (var day = 1; day <= lengths[month]; day++)
                    if ((fields.Days & (1UL << day)) != 0)
                        return true;
        return false;
    }
}
