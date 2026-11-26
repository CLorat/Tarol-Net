using System.Text.Json.Serialization;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests.Audited;

/// <summary>A sheet whose events carry audit marks (TN-64).</summary>
public partial class Sheet : AggregateRoot<Sheet>
{
    public string Title { get; private set; } = "";

    public int Touches { get; private set; }

    public bool IsClosed { get; private set; }

    public static Sheet Open(string title, DateTimeOffset at)
    {
        var sheet = new Sheet(Id<Sheet>.New());
        sheet.Raise(new Opened(title, "x", at));
        return sheet;
    }

    public Result Touch() => Raise(new Touched());

    public Result Close() => Raise(new Closed());

    public sealed record Opened(string Title, [property: NotAudited(Reason = "a checksum")] string Checksum,
        [property: AuditTime, JsonPropertyName("on")] DateTimeOffset At) : Event;

    [NotAudited(Reason = "a heartbeat")]
    public sealed record Touched : Event;

    [AuditRemoved]
    public sealed record Closed : Event;

    private void On(Opened e) => Title = e.Title;

    private void On(Touched e) => Touches++;

    private void On(Closed e) => IsClosed = true;
}

/// <summary>The audit marks in the generated event catalog (TN-64): only events that differ from "every member" carry one.</summary>
public sealed class AuditCatalogTests
{
    private static EventAudit? Of<T>() => Sheet.EventTypes.Events.Single(e => e.ClrType == typeof(T)).Audit;

    [Fact]
    public void The_catalog_names_unaudited_members_the_device_time_and_the_removing_event()
    {
        var opened = Of<Sheet.Opened>().ShouldNotBeNull();
        (opened.Excluded, opened.Removed).ShouldBe((false, false));
        opened.NotAudited.ShouldBe([new AuditMember("Checksum")]);
        opened.Time.ShouldBe(new AuditMember("At", "on"));
        Of<Sheet.Touched>().ShouldNotBeNull().Excluded.ShouldBeTrue();
        Of<Sheet.Closed>().ShouldNotBeNull().Removed.ShouldBeTrue();
    }
}
