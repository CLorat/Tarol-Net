using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.Tests;

public enum EntryRole { Caregiver, Substitute }

/// <summary>Who may do what in the facility: domain data, not a claim (Tatalu's Membership).</summary>
public partial class Staff : AggregateRoot<Staff>
{
    public EntryRole Role { get; private set; }

    public static Staff Hire(string subject, EntryRole role)
    {
        var staff = new Staff(IdOf(subject));
        staff.Raise(new Hired(role));
        return staff;
    }

    public static Id<Staff> IdOf(string subject) => (Id<Staff>)new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(subject)).AsSpan(0, 16));

    public sealed record Hired(EntryRole Role) : Event;

    private void On(Hired e) => Role = e.Role;
}

public partial class Attendance : AggregateRoot<Attendance>
{
    public int CheckIns { get; private set; }

    public static Attendance Open(Id<Attendance> id)
    {
        var attendance = new Attendance(id);
        attendance.Raise(new Opened());
        return attendance;
    }

    public Result CheckIn(EntryRole role) => role == EntryRole.Substitute && CheckIns >= 1 ? Error.Conflict("Attendance.SubstituteLimit") : Raise(new CheckedIn(role));

    public sealed record Opened : Event;
    public sealed record CheckedIn(EntryRole By) : Event;

    private void On(Opened e) => CheckIns = 0;
    private void On(CheckedIn e) => CheckIns++;
}

/// <summary>TN-41: the role is read in Load and reaches Execute (Tatalu FB-F13.3-6: a private field _role per change).</summary>
public sealed partial record CheckInChild(Id<Attendance> Attendance) : ILoads<Attendance, EntryRole>
{
    public async ValueTask<Result<Loaded<Attendance, EntryRole>>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
    {
        if (!(await reader.Load(Attendance, context.Operation, cancellationToken)).TryGetValue(out Attendance? attendance))
            return Error.NotFound("Attendance.NotFound");
        if (!(await reader.Load(Staff.IdOf(context.Operation.Actor.Id), context.Operation, cancellationToken)).TryGetValue(out Staff? staff))
            return Error.Forbidden("Staff.Unknown");
        return new Loaded<Attendance, EntryRole>(attendance, staff.Role);
    }

    public Result Execute(Attendance attendance, EntryRole role, ChangeContext context) => attendance.CheckIn(role);
}

public sealed class LoadsWithDataTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_data_read_in_Load_decides_in_Execute()
    {
        var attendance = Attendance.Open(Id<Attendance>.New(Start));
        var test = ChangeTest.At(Start).Given(attendance).Given(Staff.Hire("vera", EntryRole.Substitute)).Given(Staff.Hire("ada", EntryRole.Caregiver));

        (await test.When(new CheckInChild(attendance.Id), Actor.User("vera"))).Result.IsSuccess.ShouldBeTrue();
        var second = await test.When(new CheckInChild(attendance.Id), Actor.User("vera"));
        var caregiver = await test.When(new CheckInChild(attendance.Id), Actor.User("ada"));
        var stranger = await test.When(new CheckInChild(attendance.Id), Actor.User("eve"));

        second.Result.TryGetValue(out Violation? limit).ShouldBeTrue();
        limit.ShouldBe(Error.Conflict("Attendance.SubstituteLimit"));
        caregiver.Events.ShouldBe([new Attendance.CheckedIn(EntryRole.Caregiver)]);
        stranger.Result.TryGetValue(out Violation? unknown).ShouldBeTrue();
        unknown.ShouldBe(Error.Forbidden("Staff.Unknown"));
    }

    [Fact]
    public void Execute_without_the_data_is_a_programming_error_with_a_clear_message()
    {
        Should.Throw<InvalidOperationException>(() => ExecuteDirectly(new CheckInChild(Id<Attendance>.New(Start)), Attendance.Open(Id<Attendance>.New(Start))))
            .Message.ShouldContain("ChangePipeline");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "The interface member is what the test calls.")]
    private static Result ExecuteDirectly<T>(IChange<T> change, T aggregate)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => change.Execute(aggregate, default);

    [Fact]
    public void LocalNow_and_Today_in_the_tenants_zone()
    {
        // CR-5 (Tatalu FB-F01.9-1): 23:30 UTC on 30 Nov is already 1 Dec in Berlin
        var context = new ChangeContext(OperationContext.Start(Actor.Anonymous, Tarol.Options.Option.None, TimeProvider.System), new DateTimeOffset(2026, 11, 30, 23, 30, 0, TimeSpan.Zero));
        var berlin = TimeZones.Find("Europe/Berlin");

        context.Today(berlin).ShouldBe(new DateOnly(2026, 12, 1));
        context.LocalNow(berlin).Offset.ShouldBe(TimeSpan.FromHours(1));
        context.Today(TimeZoneInfo.Utc).ShouldBe(new DateOnly(2026, 11, 30));
    }
}
