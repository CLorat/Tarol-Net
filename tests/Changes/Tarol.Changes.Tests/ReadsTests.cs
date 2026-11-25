using Tarol.Kernel;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.Tests;

/// <summary>The day-care facility: one per tenant (Tatalu's ChildDayCare).</summary>
[PerTenant]
public partial class Facility : AggregateRoot<Facility>
{
    public int Places { get; private set; }

    public static Facility Open(Id<ITenant> tenant, int places)
    {
        var facility = new Facility(TenantAggregates.IdOf<Facility>(tenant));
        facility.Raise(new Opened(places));
        return facility;
    }

    public sealed record Opened(int Places) : Event;

    private void On(Opened e) => Places = e.Places;
}

/// <summary>A child; its care file has the same Guid (Tatalu FB-F03.4-1).</summary>
public partial class Kid : AggregateRoot<Kid>
{
    public bool Vaccinated { get; private set; }

    public static Kid Register(Guid id, bool vaccinated)
    {
        var kid = new Kid((Id<Kid>)id);
        kid.Raise(new Registered(vaccinated));
        return kid;
    }

    public sealed record Registered(bool Vaccinated) : Event;

    private void On(Registered e) => Vaccinated = e.Vaccinated;
}

/// <summary>Who else is in the group: optional (Tatalu FB-F03.3-2, CareFacts.ReadOccupancy).</summary>
public partial class Occupancy : AggregateRoot<Occupancy>
{
    public int Taken { get; private set; }

    public static Occupancy Count(Guid id, int taken)
    {
        var occupancy = new Occupancy((Id<Occupancy>)id);
        occupancy.Raise(new Counted(taken));
        return occupancy;
    }

    public sealed record Counted(int Taken) : Event;

    private void On(Counted e) => Taken = e.Taken;
}

public partial class CareFile : AggregateRoot<CareFile>
{
    public bool Confirmed { get; private set; }

    public static CareFile Start(Guid kid)
    {
        var file = new CareFile((Id<CareFile>)kid);
        file.Raise(new Started());
        return file;
    }

    public Result Confirm(Facility facility, Kid kid, Occupancy? occupancy)
        => !kid.Vaccinated ? Error.Conflict("CareFile.NotVaccinated")
         : (occupancy?.Taken ?? 0) >= facility.Places ? Error.Conflict("CareFile.Full")
         : Raise(new WasConfirmed());

    public sealed record Started : Event;
    public sealed record WasConfirmed : Event;

    private void On(Started e) => Confirmed = false;
    private void On(WasConfirmed e) => Confirmed = true;
}

/// <summary>TN-40: no input member names the facility or the child; nothing is sent for them.</summary>
[Reads<Facility>]
[Reads<Kid>(SameIdAs = nameof(File))]
[Reads<Occupancy>(SameIdAs = nameof(Group), Optional = true)]
public sealed partial record ConfirmCare(Id<CareFile> File, Guid Group) : IChange<CareFile>
{
    public Result Execute(CareFile file, ChangeContext context)
        => file.Confirm(context.Read<Facility>(), context.Read<Kid>(), context.TryRead(out Occupancy? occupancy) ? occupancy : null);
}

/// <summary>TN-40 (Tatalu FB-F02.1-7): a [Read] of the target's type next to the target, and an optional [Read].</summary>
public sealed partial record LinkSibling(Id<Kid> Kid, [property: Read] Id<Kid> Sibling, [property: Read(Optional = true)] Id<Occupancy> Group) : IChange<Kid>
{
    public Result Execute(Kid kid, ChangeContext context)
        => context.Read<Kid>(nameof(Sibling)).Vaccinated && !context.TryRead(out Occupancy? _) ? Result.Success() : Error.Conflict("Kid.Unexpected");
}

internal static class RefusalExtensions
{
    public static Violation? Refused(this Result<ChangeOutcome> result) => result.TryGetValue(out Violation? violation) ? violation : null;
}

public sealed class ReadsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly Id<ITenant> Tenant = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-0000000000cc");

    private static (ChangeTest Test, Guid Kid) Given(bool vaccinated = true, int? taken = null)
    {
        var kid = Guid.CreateVersion7();
        var test = ChangeTest.At(Start).InTenant(Tenant).Given(Facility.Open(Tenant, places: 2)).Given(Kid.Register(kid, vaccinated)).Given(CareFile.Start(kid));
        return (test, kid);
    }

    [Fact]
    public async Task A_per_tenant_aggregate_and_one_under_the_same_Guid_are_read_without_input_members()
    {
        var (test, kid) = Given();

        var then = await test.When(new ConfirmCare((Id<CareFile>)kid, Guid.CreateVersion7()));          // no occupancy yet: optional

        then.Result.IsSuccess.ShouldBeTrue(then.Result.ToString());
        then.Events.ShouldBe([new CareFile.WasConfirmed()]);
    }

    [Fact]
    public async Task The_reads_decide_and_an_optional_one_is_used_when_it_exists()
    {
        var (unvaccinated, kid) = Given(vaccinated: false);
        (await unvaccinated.When(new ConfirmCare((Id<CareFile>)kid, Guid.CreateVersion7()))).Result.Refused().ShouldBe(Error.Conflict("CareFile.NotVaccinated"));

        var (full, other) = Given();
        var group = Guid.CreateVersion7();
        full.Given(Occupancy.Count(group, taken: 2));
        (await full.When(new ConfirmCare((Id<CareFile>)other, group))).Result.Refused().ShouldBe(Error.Conflict("CareFile.Full"));
    }

    [Fact]
    public async Task A_missing_required_read_is_422_NotFound_and_without_a_tenant_too()
    {
        var test = ChangeTest.At(Start).InTenant(Tenant);
        var kid = Guid.CreateVersion7();
        test.Given(CareFile.Start(kid)).Given(Kid.Register(kid, vaccinated: true));

        (await test.When(new ConfirmCare((Id<CareFile>)kid, Guid.Empty))).Result.Refused().ShouldBe(Error.UnprocessableEntity("Facility.NotFound", "Facility"));
    }

    [Fact]
    public async Task A_read_of_the_targets_type_next_to_the_target_is_allowed()
    {
        var (test, kid) = Given();
        var sibling = Guid.CreateVersion7();
        test.Given(Kid.Register(sibling, vaccinated: true));

        (await test.When(new LinkSibling((Id<Kid>)kid, (Id<Kid>)sibling, (Id<Occupancy>)Guid.CreateVersion7()))).Result.IsSuccess.ShouldBeTrue();
        (await test.When(new LinkSibling((Id<Kid>)kid, (Id<Kid>)Guid.CreateVersion7(), (Id<Occupancy>)Guid.CreateVersion7()))).Result
            .Refused().ShouldBe(Error.UnprocessableEntity("Kid.NotFound", "Sibling"));
    }
}
