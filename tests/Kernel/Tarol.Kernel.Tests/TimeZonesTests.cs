namespace Tarol.Kernel.Tests;

/// <summary>
/// Tatalu FB-WIN-1: with InvariantGlobalization on Windows, FindSystemTimeZoneById("Europe/Berlin") threw and the host did not
/// start. The "system" here is a Windows one without ICU: it knows Windows ids only.
/// </summary>
public sealed class TimeZonesTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static TimeZoneInfo? WindowsWithoutIcu(string id) => id switch
    {
        "W. Europe Standard Time" => Berlin,
        "GMT Standard Time" => TimeZoneInfo.FindSystemTimeZoneById("Europe/London"),
        _ => null,
    };

    private static TimeZoneInfo? LinuxWithoutIcu(string id) => id.Contains('/', StringComparison.Ordinal) ? TimeZoneInfo.FindSystemTimeZoneById(id) : null;

    [Theory]
    [InlineData("Europe/Berlin")]
    [InlineData("Europe/Vienna")]
    [InlineData("europe/zurich")]
    [InlineData("W. Europe Standard Time")]
    public void An_IANA_id_resolves_on_a_Windows_system_that_knows_only_Windows_ids(string id)
        => TimeZones.Find(id, WindowsWithoutIcu).ShouldBeSameAs(Berlin);

    [Fact]
    public void A_Windows_id_resolves_on_a_Linux_system_that_knows_only_IANA_ids()
        => TimeZones.Find("W. Europe Standard Time", LinuxWithoutIcu).GetUtcOffset(new DateTime(2026, 7, 1)).ShouldBe(TimeSpan.FromHours(2));

    [Fact]
    public void UTC_is_always_known_and_an_unknown_id_names_both_kinds()
    {
        TimeZones.Find("Etc/UTC", static _ => null).ShouldBe(TimeZoneInfo.Utc);
        var error = Should.Throw<TimeZoneNotFoundException>(() => TimeZones.Find("Europe/Atlantis", WindowsWithoutIcu));
        error.Message.ShouldContain("'Europe/Atlantis'");
        error.Message.ShouldContain("InvariantGlobalization");
        TimeZones.TryFind("Europe/Berlin", out var zone).ShouldBeTrue();
        zone!.GetUtcOffset(new DateTime(2026, 1, 1)).ShouldBe(TimeSpan.FromHours(1));
    }
}
