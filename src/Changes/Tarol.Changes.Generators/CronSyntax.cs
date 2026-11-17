// Shared source: compiled into the Changes generator (TCHG010, netstandard2.0) and linked into Tarol.Jobs (the parser, 14C),
// so a literal the compiler accepts is a schedule the runtime accepts. Keep it free of APIs newer than netstandard2.0.
namespace Tarol.Cron;

/// <summary>
/// The fields of a 5-field Unix cron expression (minute hour day-of-month month day-of-week) as bitmasks, parsed with
/// Vixie cron's rules (jobs.md §4, J-3): lists, ranges, steps (<c>*/15</c>, <c>1-30/5</c>, <c>5/15</c> = <c>5-59/15</c>),
/// month and weekday names (<c>JAN</c>, <c>MON</c>), weekday 7 = Sunday. No seconds, no <c>L W # ?</c>, no <c>@daily</c>.
/// </summary>
internal sealed class CronFields
{
    private static readonly string[] MonthNames = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
    private static readonly string[] DayNames = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
    private static readonly char[] Blanks = { ' ', '\t' };

    private CronFields(ulong minutes, ulong hours, ulong days, ulong months, ulong weekdays, bool dayStar, bool weekdayStar)
    {
        Minutes = minutes;
        Hours = hours;
        Days = days;
        Months = months;
        Weekdays = weekdays;
        DayStar = dayStar;
        WeekdayStar = weekdayStar;
    }

    /// <summary>Bits 0–59.</summary>
    public ulong Minutes { get; }

    /// <summary>Bits 0–23.</summary>
    public ulong Hours { get; }

    /// <summary>Bits 1–31.</summary>
    public ulong Days { get; }

    /// <summary>Bits 1–12.</summary>
    public ulong Months { get; }

    /// <summary>Bits 0–6 (0 = Sunday).</summary>
    public ulong Weekdays { get; }

    /// <summary>The day-of-month field starts with <c>*</c>: Vixie matches day-of-month AND day-of-week then, otherwise OR.</summary>
    public bool DayStar { get; }

    /// <summary>The day-of-week field starts with <c>*</c>.</summary>
    public bool WeekdayStar { get; }

    /// <summary>Whether a day matches: both fields when either starts with <c>*</c>, else either (Unix cron, unlike Cronos' AND).</summary>
    public bool MatchesDay(int day, int weekday)
    {
        var byDay = (Days & (1UL << day)) != 0;
        var byWeekday = (Weekdays & (1UL << weekday)) != 0;
        return DayStar || WeekdayStar ? byDay && byWeekday : byDay || byWeekday;
    }

    /// <summary>Parses <paramref name="text"/>.</summary>
    /// <param name="text">The expression.</param>
    /// <param name="fields">The fields, or <see langword="null"/>.</param>
    /// <returns><see langword="null"/>, or why the expression is invalid.</returns>
    public static string? TryParse(string? text, out CronFields? fields)
    {
        fields = null;
        if (text is null || text.Trim().Length == 0)
            return "the expression is empty";
        var parts = text.Split(Blanks, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return "a schedule has 5 fields (minute hour day-of-month month day-of-week), found " + parts.Length
                   + (parts.Length == 6 ? " (seconds are not supported)" : "");
        string? error;
        if ((error = Field(parts[0], 0, 59, null, out var minutes)) is not null)
            return "minute: " + error;
        if ((error = Field(parts[1], 0, 23, null, out var hours)) is not null)
            return "hour: " + error;
        if ((error = Field(parts[2], 1, 31, null, out var days)) is not null)
            return "day-of-month: " + error;
        if ((error = Field(parts[3], 1, 12, MonthNames, out var months)) is not null)
            return "month: " + error;
        if ((error = Field(parts[4], 0, 7, DayNames, out var weekdays)) is not null)
            return "day-of-week: " + error;
        if ((weekdays & (1UL << 7)) != 0)
            weekdays = (weekdays & ~(1UL << 7)) | 1UL;                                     // 7 = Sunday
        fields = new CronFields(minutes, hours, days, months, weekdays, parts[2][0] == '*', parts[4][0] == '*');
        return null;
    }

    private static string? Field(string field, int min, int max, string[]? names, out ulong bits)
    {
        bits = 0;
        foreach (var item in field.Split(','))
        {
            if (item.Length == 0)
                return "an empty list item in '" + field + "'";
            if (item.IndexOf('#') >= 0 || item.IndexOf('?') >= 0)
                return "'#' and '?' are not supported";
            var step = 1;
            var range = item;
            var slash = item.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(item.Substring(slash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out step)
                    || step < 1 || step > max - min + 1)
                    return "the step in '" + item + "' must be 1–" + (max - min + 1);
                range = item.Substring(0, slash);
            }
            int from, to;
            if (range == "*")
                (from, to) = (min, max);
            else
            {
                var dash = range.IndexOf('-');
                string? error;
                if (dash < 0)
                {
                    if ((error = Value(range, min, max, names, out from)) is not null)
                        return error;
                    to = slash >= 0 ? max : from;                                           // Vixie: 5/15 = 5-max/15
                }
                else
                {
                    if ((error = Value(range.Substring(0, dash), min, max, names, out from)) is not null
                        || (error = Value(range.Substring(dash + 1), min, max, names, out to)) is not null)
                        return error;
                    if (to < from)
                        return "the range '" + range + "' runs backwards";
                }
            }
            for (var v = from; v <= to; v += step)
                bits |= 1UL << v;
        }
        return null;
    }

    private static bool IsQuartzLetter(string text)
    {
        var upper = text.ToUpperInvariant();
        return upper is "L" or "W" or "LW" || (char.IsDigit(upper[0]) && (upper.EndsWith("L", System.StringComparison.Ordinal) || upper.EndsWith("W", System.StringComparison.Ordinal)));
    }

    private static string? Value(string text, int min, int max, string[]? names, out int value)
    {
        value = 0;
        if (text.Length == 0)
            return "a value is missing";
        if (IsQuartzLetter(text))
            return "'" + text + "' is not supported (no L W # ?)";
        if (names is not null && char.IsLetter(text[0]))
        {
            for (var i = 0; i < names.Length; i++)
                if (string.Equals(names[i], text, System.StringComparison.OrdinalIgnoreCase))
                {
                    value = names == MonthNames ? i + 1 : i;
                    return null;
                }
            return "'" + text + "' is not a name (" + string.Join(" ", names) + ")";
        }
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
            return "'" + text + "' is not a number";
        return value < min || value > max ? "'" + text + "' is outside " + min + "–" + max : null;
    }
}
