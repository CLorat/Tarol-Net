using System.Globalization;

namespace Probe.Jobs;

/// <summary>A schedule: the first occurrence strictly after a time, in a time zone (the clock time of the expression).</summary>
public interface ISchedule
{
    DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone);
}

#if CRONOS
/// <summary>Cronos 0.12 (5 fields, or 6 with seconds).</summary>
public sealed class CronosSchedule(string expression) : ISchedule
{
    private readonly Cronos.CronExpression _cron = Cronos.CronExpression.Parse(expression,
        expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? Cronos.CronFormat.IncludeSeconds : Cronos.CronFormat.Standard);

    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone) => _cron.GetNextOccurrence(after, zone);
    public override string ToString() => _cron.ToString();
}
#endif

/// <summary>A fixed interval from an anchor (no cron): every <c>Every</c>, at anchor + k·Every (UTC, no time zone).</summary>
public sealed class IntervalSchedule(TimeSpan every, DateTimeOffset anchor) : ISchedule
{
    public override string ToString() => "every " + every.ToString("c", CultureInfo.InvariantCulture);
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        var k = Math.Floor((after - anchor) / every) + 1;
        return anchor + every * Math.Max(k, 0);
    }
}

/// <summary>
/// A small 5-field cron of our own (minute hour day-of-month month day-of-week; <c>*</c>, <c>a</c>, <c>a-b</c>, <c>/n</c>, lists;
/// no names, no <c>L W # ?</c>, no seconds). Clock time in the zone: a skipped time (spring forward) fires at the end of the gap,
/// an ambiguous time (fall back) fires once, at its first instant. Day-of-month and day-of-week restricted together match
/// either (Vixie cron).
/// </summary>
public sealed class MiniCron : ISchedule
{
    private readonly bool[] _minute = new bool[60], _hour = new bool[24], _dom = new bool[32], _month = new bool[13], _dow = new bool[7];
    private readonly bool _domStar, _dowStar;
    private readonly int[] _hours, _minutes;
    private readonly string _text;

    public MiniCron(string expression)
    {
        _text = expression;
        var f = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) throw new FormatException("5 fields expected: " + expression);
        Fill(f[0], _minute, 0, 59);
        Fill(f[1], _hour, 0, 23);
        _domStar = Fill(f[2], _dom, 1, 31);
        Fill(f[3], _month, 1, 12);
        var dow = new bool[8];
        _dowStar = Fill(f[4], dow, 0, 7);
        for (var i = 0; i < 7; i++) _dow[i] = dow[i];
        _dow[0] |= dow[7];
        _hours = Enumerable.Range(0, 24).Where(h => _hour[h]).ToArray();
        _minutes = Enumerable.Range(0, 60).Where(m => _minute[m]).ToArray();
    }

    public override string ToString() => _text;

    private static bool Fill(string field, bool[] set, int min, int max)
    {
        var star = field == "*";
        foreach (var part in field.Split(','))
        {
            var (range, step) = part.Split('/') is [var r, var s] ? (r, int.Parse(s, CultureInfo.InvariantCulture)) : (part, 1);
            if (step < 1) throw new FormatException("step < 1: " + field);
            int lo, hi;
            if (range == "*") (lo, hi) = (min, max);
            else if (range.Split('-') is [var a, var b]) (lo, hi) = (int.Parse(a, CultureInfo.InvariantCulture), int.Parse(b, CultureInfo.InvariantCulture));
            else { lo = int.Parse(range, CultureInfo.InvariantCulture); hi = part.Contains('/') ? max : lo; }
            if (lo < min || hi > max || lo > hi) throw new FormatException("out of range: " + field);
            for (var v = lo; v <= hi; v += step) set[v] = true;
        }
        return star;
    }

    private bool DayMatches(DateTime d)
    {
        if (!_month[d.Month]) return false;
        bool dom = _dom[d.Day], dow = _dow[(int)d.DayOfWeek];
        return _domStar && _dowStar ? true : _domStar ? dow : _dowStar ? dom : dom || dow;
    }

    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        var localAfter = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        for (var day = localAfter.Date.AddDays(-1); day < localAfter.Date.AddYears(5); day = day.AddDays(1))
        {
            if (!DayMatches(day)) continue;
            foreach (var h in _hours)
                foreach (var m in _minutes)
                {
                    var local = day.AddHours(h).AddMinutes(m);
                    if (Instant(local, zone) is { } at && at > after) return at;
                }
        }
        return null;
    }

    /// <summary>The instant of a clock time: a skipped time fires at the end of the gap, an ambiguous one at its first instant.</summary>
    internal static DateTimeOffset? Instant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            var probe = local;                                  // walk to the first valid minute: the end of the gap
            while (zone.IsInvalidTime(probe)) probe = probe.AddMinutes(1);
            return new DateTimeOffset(probe, zone.GetUtcOffset(probe));
        }
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var first = offsets.Max();                          // the larger offset is the earlier instant (still in DST)
            return new DateTimeOffset(local, first);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}

internal static class Schedules
{
    public static ISchedule Parse(string expression)
#if CRONOS
        => Environment.GetEnvironmentVariable("PROBE_CRON") == "mini" ? new MiniCron(expression) : new CronosSchedule(expression);
#else
        => new MiniCron(expression);
#endif

    /// <summary>The occurrences in (from, to] — what a scheduler finds missed after downtime.</summary>
    public static IEnumerable<DateTimeOffset> Between(ISchedule s, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        for (var t = s.Next(from, zone); t is { } at && at <= to; t = s.Next(at, zone))
            yield return at;
    }
}
