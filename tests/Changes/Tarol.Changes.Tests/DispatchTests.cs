using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.Tests;

#pragma warning disable TVAL006 // inputs built as JSON would

/// <summary>The generated <c>GeneratedChanges.Dispatch(object)</c> of this assembly (namespace = assembly name).</summary>
public sealed class DispatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_object_runs_as_its_change()
    {
        var auction = Auction.Create("seller", "L-1", new Title("Sunflowers"), new Amount(10), Start.AddDays(7), Start).GetValueOrThrow();
        var test = ChangeTest.At(Start).Given(auction);
        object fromOutbox = new CloseAuction(auction.Id);

        var result = await test.Pipeline.Dispatch(fromOutbox, test.Context(), cancellationToken: TestContext.Current.CancellationToken);

        result.GetValueOrThrow().Version.ShouldBe(2);
    }

    [Fact]
    public async Task A_creation_dispatches_too()
    {
        var test = ChangeTest.At(Start);

        var result = await test.Pipeline.Dispatch(new CreateAuction("L-1", new Title("Sunflowers"), new Amount(10), Start.AddDays(7)), test.Context(),
            cancellationToken: TestContext.Current.CancellationToken);

        result.GetValueOrThrow().Events.ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_object_throws()
    {
        var test = ChangeTest.At(Start);

        (await Should.ThrowAsync<ArgumentException>(async () => _ = await test.Pipeline.Dispatch("not a change", test.Context())))
            .Message.ShouldContain("String is not a change of Tarol.Changes.Tests");
        await Should.ThrowAsync<ArgumentNullException>(async () => _ = await test.Pipeline.Dispatch(null!, test.Context()));
    }
}

public sealed class ChangeTestTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_given_aggregate_that_breaks_an_invariant_is_refused()
    {
        var auction = Auction.Create("seller", "L-1", new Title("Sunflowers"), new Amount(10), Start.AddDays(7), Start).GetValueOrThrow();
        _ = auction.Sabotage();

        Should.Throw<InvalidOperationException>(() => ChangeTest.At(Start).Given(auction)).Message.ShouldContain("Auction.Reserve");
    }

    [Fact]
    public async Task The_test_records_its_actor_tenant_and_time()
    {
        var tenant = Id<ITenant>.New();
        var test = ChangeTest.At(Start).As(Actor.Service("billing")).InTenant(tenant).Later(TimeSpan.FromMinutes(3));

        var then = await test.When(new CreateAuction("L-1", new Title("Sunflowers"), new Amount(10), Start.AddDays(7)));

        var recorded = then.Recorded.Single();
        recorded.Context.Actor.ShouldBe(Actor.Service("billing"));
        recorded.Context.Tenant.ShouldBe((Option<Id<ITenant>>)tenant);
        recorded.RecordedAt.ShouldBe(Start.AddMinutes(3));
        test.Clock.GetUtcNow().ShouldBe(Start.AddMinutes(3));
    }

    [Fact]
    public void Loading_a_missing_aggregate_names_it()
        => Should.Throw<InvalidOperationException>(() => ChangeTest.At(Start).Load(Id<Auction>.New())).Message.ShouldContain("No Auction");

    [Fact]
    public async Task A_failed_change_reports_no_events()
    {
        var then = await ChangeTest.At(Start).When(new CloseAuction(Id<Auction>.New()));

        then.Result.IsSuccess.ShouldBeFalse();
        then.Events.ShouldBeEmpty();
        then.Recorded.ShouldBeEmpty();
    }
}
