namespace Tarol.Kernel;

/// <summary>
/// Finds a time zone by an IANA id (<c>Europe/Berlin</c>) or a Windows id (<c>W. Europe Standard Time</c>) on every system
/// (Tatalu FB-WIN-1). <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> converts between the two only with ICU; an app with
/// <c>InvariantGlobalization</c> (the default of Native AOT apps) on Windows does not know <c>Europe/Berlin</c>, and on Linux
/// does not know <c>W. Europe Standard Time</c>. Order: the system's own id, ICU's conversion, then a small built-in table of
/// the zones of Europe and a few others; anything else needs the id the system knows.
/// </summary>
public static class TimeZones
{
    // IANA id → Windows id: the zones of Europe and some others, written for Tarol (not a copy of a data set). An id
    // missing here still works where the system knows it, which is always the case for the system's own kind of id.
    private static readonly Dictionary<string, string> IanaToWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Etc/UTC"] = "UTC",
        ["Etc/GMT"] = "UTC",
        ["UTC"] = "UTC",
        ["Europe/London"] = "GMT Standard Time",
        ["Europe/Dublin"] = "GMT Standard Time",
        ["Europe/Lisbon"] = "GMT Standard Time",
        ["Atlantic/Canary"] = "GMT Standard Time",
        ["Atlantic/Faroe"] = "GMT Standard Time",
        ["Atlantic/Madeira"] = "GMT Standard Time",
        ["Europe/Guernsey"] = "GMT Standard Time",
        ["Europe/Isle_of_Man"] = "GMT Standard Time",
        ["Europe/Jersey"] = "GMT Standard Time",
        ["Atlantic/Reykjavik"] = "Greenwich Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time",
        ["Europe/Vienna"] = "W. Europe Standard Time",
        ["Europe/Zurich"] = "W. Europe Standard Time",
        ["Europe/Amsterdam"] = "W. Europe Standard Time",
        ["Europe/Luxembourg"] = "W. Europe Standard Time",
        ["Europe/Rome"] = "W. Europe Standard Time",
        ["Europe/Stockholm"] = "W. Europe Standard Time",
        ["Europe/Oslo"] = "W. Europe Standard Time",
        ["Europe/Vaduz"] = "W. Europe Standard Time",
        ["Europe/Monaco"] = "W. Europe Standard Time",
        ["Europe/Andorra"] = "W. Europe Standard Time",
        ["Europe/Malta"] = "W. Europe Standard Time",
        ["Europe/San_Marino"] = "W. Europe Standard Time",
        ["Europe/Vatican"] = "W. Europe Standard Time",
        ["Europe/Gibraltar"] = "W. Europe Standard Time",
        ["Europe/Busingen"] = "W. Europe Standard Time",
        ["Europe/Paris"] = "Romance Standard Time",
        ["Europe/Brussels"] = "Romance Standard Time",
        ["Europe/Copenhagen"] = "Romance Standard Time",
        ["Europe/Madrid"] = "Romance Standard Time",
        ["Europe/Budapest"] = "Central Europe Standard Time",
        ["Europe/Prague"] = "Central Europe Standard Time",
        ["Europe/Bratislava"] = "Central Europe Standard Time",
        ["Europe/Ljubljana"] = "Central Europe Standard Time",
        ["Europe/Belgrade"] = "Central Europe Standard Time",
        ["Europe/Podgorica"] = "Central Europe Standard Time",
        ["Europe/Tirane"] = "Central Europe Standard Time",
        ["Europe/Warsaw"] = "Central European Standard Time",
        ["Europe/Zagreb"] = "Central European Standard Time",
        ["Europe/Sarajevo"] = "Central European Standard Time",
        ["Europe/Skopje"] = "Central European Standard Time",
        ["Europe/Athens"] = "GTB Standard Time",
        ["Europe/Bucharest"] = "GTB Standard Time",
        ["Asia/Nicosia"] = "GTB Standard Time",
        ["Europe/Helsinki"] = "FLE Standard Time",
        ["Europe/Kyiv"] = "FLE Standard Time",
        ["Europe/Kiev"] = "FLE Standard Time",
        ["Europe/Riga"] = "FLE Standard Time",
        ["Europe/Sofia"] = "FLE Standard Time",
        ["Europe/Tallinn"] = "FLE Standard Time",
        ["Europe/Vilnius"] = "FLE Standard Time",
        ["Europe/Mariehamn"] = "FLE Standard Time",
        ["Europe/Chisinau"] = "E. Europe Standard Time",
        ["Europe/Kaliningrad"] = "Kaliningrad Standard Time",
        ["Europe/Minsk"] = "Belarus Standard Time",
        ["Europe/Moscow"] = "Russian Standard Time",
        ["Europe/Istanbul"] = "Turkey Standard Time",
        ["America/New_York"] = "Eastern Standard Time",
        ["America/Chicago"] = "Central Standard Time",
        ["America/Denver"] = "Mountain Standard Time",
        ["America/Los_Angeles"] = "Pacific Standard Time",
        ["Asia/Tokyo"] = "Tokyo Standard Time",
        ["Asia/Shanghai"] = "China Standard Time",
        ["Asia/Kolkata"] = "India Standard Time",
        ["Australia/Sydney"] = "AUS Eastern Standard Time",
    };

    // Windows id → its main IANA id (the reverse of the first IANA id named above per Windows id)
    private static readonly Dictionary<string, string> WindowsToIana = Reverse();

    /// <summary>The zone of <paramref name="id"/> (IANA or Windows; <c>UTC</c>, <c>Etc/UTC</c> are <see cref="TimeZoneInfo.Utc"/>).</summary>
    /// <param name="id">The id.</param>
    /// <returns>The zone.</returns>
    /// <exception cref="TimeZoneNotFoundException">Neither the system nor the table knows it; the message names both kinds of id.</exception>
    public static TimeZoneInfo Find(string id) => Find(id, System);

    /// <summary>Like <see cref="Find(string)"/>, without an exception.</summary>
    /// <param name="id">The id.</param>
    /// <param name="zone">The zone, when found.</param>
    /// <returns>Whether it was found.</returns>
    public static bool TryFind(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = TryFind(id, System);
        return zone is not null;
    }

    internal static TimeZoneInfo Find(string id, Func<string, TimeZoneInfo?> system)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return TryFind(id, system) ?? throw new TimeZoneNotFoundException(
            $"The time zone '{id}' is unknown here. With InvariantGlobalization (the default of Native AOT apps) .NET converts between IANA ids (Europe/Berlin) "
            + "and Windows ids (W. Europe Standard Time) only for the zones Tarol.Kernel.TimeZones knows; use the id of this system's kind, or install tzdata (Linux images).");
    }

    internal static TimeZoneInfo? TryFind(string id, Func<string, TimeZoneInfo?> system)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        if (id is "UTC" or "Etc/UTC" or "Etc/GMT")
            return TimeZoneInfo.Utc;
        if (system(id) is { } direct)
            return direct;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows) && system(windows) is { } fromIcu)
            return fromIcu;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) && system(iana) is { } fromIcuIana)
            return fromIcuIana;
        if (IanaToWindows.TryGetValue(id, out var mapped) && system(mapped) is { } fromTable)
            return fromTable;
        if (WindowsToIana.TryGetValue(id, out var reverse) && system(reverse) is { } fromTableIana)
            return fromTableIana;
        return null;
    }

    private static TimeZoneInfo? System(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Reverse()
    {
        var reverse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (iana, windows) in IanaToWindows)
            reverse.TryAdd(windows, iana);
        return reverse;
    }
}
