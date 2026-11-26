using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Queries.Tests;

/// <summary>A caregiver whose first event is any of three (Tatalu FB-F01.2-2: master data, tax or bank first).</summary>
public partial class Caregiver : AggregateRoot<Caregiver>
{
    public string Name { get; private set; } = "";
    public string? Office { get; private set; }
    public bool Bank { get; private set; }

    public static Caregiver New() => new(Id<Caregiver>.New());

    public Success Named(string name) => Raise(new MasterDataSet(name));

    public Success Taxed(string office) => Raise(new TaxSet(office));

    public Success Banked(string iban) => Raise(new BankSet(iban));

    public sealed record MasterDataSet(string Name) : Event;
    public sealed record TaxSet(string Office) : Event;
    public sealed record BankSet(string Iban) : Event;

    private void On(MasterDataSet e) => Name = e.Name;
    private void On(TaxSet e) => Office = e.Office;
    private void On(BankSet e) => Bank = e.Iban.Length > 0;
}

/// <summary>No static When: every instance When starts from Empty on a new stream (TN-54).</summary>
[Projection("caregiver-changes")]
public sealed partial record CaregiverChangesRow(Guid Id, string? Name, string? TaxOffice, bool HasBank, int Changes)
{
    public static CaregiverChangesRow Empty(RecordedEvent r) => new(r.StreamId, null, null, false, 0);

    public CaregiverChangesRow When(Caregiver.MasterDataSet e) => this with { Name = e.Name, Changes = Changes + 1 };

    public CaregiverChangesRow When(Caregiver.TaxSet e) => this with { TaxOffice = e.Office, Changes = Changes + 1 };

    public CaregiverChangesRow When(Caregiver.BankSet e, RecordedEvent r) => this with { HasBank = true, Changes = Changes + 1 };
}

public sealed class EmptyRowTests
{
    [Fact]
    public async Task A_stream_may_start_with_any_event_an_instance_When_handles()
    {
        var test = ProjectionTest<CaregiverChangesRow>.Create();
        var taxFirst = Caregiver.New();
        _ = taxFirst.Taxed("Finanzamt Mitte");
        _ = taxFirst.Named("Ada");
        var bankFirst = Caregiver.New();
        _ = bankFirst.Banked("DE00");
        test.Given(taxFirst).Given(bankFirst);

        await test.CatchUp();

        test.Rows().OrderBy(r => r.Changes).ShouldBe([
            new CaregiverChangesRow(bankFirst.Id.Value, null, null, true, 1),
            new CaregiverChangesRow(taxFirst.Id.Value, "Ada", "Finanzamt Mitte", false, 2)]);
    }
}
