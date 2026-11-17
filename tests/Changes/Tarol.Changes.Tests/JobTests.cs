using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.Tests;

#pragma warning disable TVAL006 // the tests build inputs as JSON would (trusting constructors); the pipeline validates them

// ── scheduled change entries (step 14A): changes with a [Scheduled] factory ────────────────────────────────────────────

/// <summary>A creation every night at 02:00 Vienna time: the payout of the previous local day.</summary>
public sealed partial record OpenDailyPayout(decimal Amount) : ICreation<Payout>
{
    [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]
    public static OpenDailyPayout From(JobOccurrence o) => new(o.Local.Day);

    public Result<Payout> Execute(ChangeContext context) => Payout.Due(Id<Payout>.New(context.Now), Amount);
}

/// <summary>A change of one aggregate per tenant, pinned name, every missed occurrence; validated input (TVAL006 exempt, C-13).</summary>
public sealed partial record TouchHouseAuction(Id<Auction> Auction, Amount Amount) : IChange<Auction>, Tarol.Validations.IValidatable
{
    public static readonly Id<Auction> House = (Id<Auction>)Guid.Parse("0196b5a1-0000-7000-8000-000000000001");

    [Scheduled("*/15 9-17 * * MON-FRI", Name = "Auctions.Touch", Missed = Missed.All, PerTenant = true)]
    public static TouchHouseAuction From(JobOccurrence o) => new(House, new Amount(1));

    public Result Execute(Auction auction, ChangeContext context) => auction.Touch();
}

public sealed class JobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 16, 1, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

    [Fact]
    public void The_generated_set_lists_the_declared_schedules()
    {
        GeneratedJobs.Set.Definitions.ShouldBe([
            new JobDefinition("Auctions.Touch", "*/15 9-17 * * MON-FRI", "UTC", Missed.All, true),
            new JobDefinition("OpenDailyPayout", "0 2 * * *", "Europe/Vienna", Missed.Latest, false),
        ]);
        GeneratedJobs.Set.Contains("OpenDailyPayout").ShouldBeTrue();
        GeneratedJobs.Set.Contains("Nope").ShouldBeFalse();
    }

    [Fact]
    public async Task An_occurrence_runs_as_the_system_in_a_new_conversation_with_its_key()
    {
        var test = ChangeTest.At(Start);
        var occurrence = new JobOccurrence("OpenDailyPayout", Start, Vienna, Option.None);

        var outcome = (await GeneratedJobs.Set.Run(test.Pipeline, occurrence, test.Clock, Ct)).GetValueOrThrow();

        var due = test.Store.Recorded.Single(e => e.Event.Event is Payout.BecameDue);
        due.StreamId.ShouldBe(outcome.StreamId);
        ((Payout.BecameDue)due.Event.Event).Amount.ShouldBe(16);                   // 02:00 local on the 16th
        due.Context.Actor.ShouldBe(Actor.System("Jobs.OpenDailyPayout"));
        due.Context.Tenant.ShouldBe(Option.None);
        due.Context.CausationId.ShouldBe(Option.None);
        var key = JobSet.Key(occurrence);
        (key.Scope, key.Key).ShouldBe(("job:OpenDailyPayout", "2026-11-16T01:00:00.0000000Z"));
        (await test.Store.Seen(key, Ct)).GetValueOrThrow().ShouldBe(new StoredOutcome(outcome.StreamId, 1, 1));
    }

    [Fact]
    public async Task The_same_occurrence_again_replays_and_the_next_commits()
    {
        var test = ChangeTest.At(Start);
        var first = new JobOccurrence("OpenDailyPayout", Start, Vienna, Option.None);
        var original = (await GeneratedJobs.Set.Run(test.Pipeline, first, test.Clock, Ct)).GetValueOrThrow();

        var again = (await GeneratedJobs.Set.Run(test.Pipeline, first, test.Clock, Ct)).GetValueOrThrow();
        _ = (await GeneratedJobs.Set.Run(test.Pipeline, first with { }, test.Clock, Ct)).GetValueOrThrow();
        var next = (await GeneratedJobs.Set.Run(test.Pipeline, new JobOccurrence("OpenDailyPayout", Start.AddDays(1), Vienna, Option.None), test.Clock, Ct))
            .GetValueOrThrow();

        again.Replayed.ShouldBeTrue();
        again.StreamId.ShouldBe(original.StreamId);
        next.Replayed.ShouldBeFalse();
        test.Store.Recorded.Count(e => e.Event.Event is Payout.BecameDue).ShouldBe(2);
    }

    [Fact]
    public async Task A_tenant_occurrence_runs_in_its_tenant_and_a_failure_is_returned()
    {
        var tenant = Id<ITenant>.New();
        var test = ChangeTest.At(Start).InTenant(tenant);
        var auction = Auction.Create("seller", "L-1", new Title("Sunflowers"), new Amount(100), Start.AddDays(1), Start).GetValueOrThrow();
        test.Given(auction);
        var occurrence = new JobOccurrence("Auctions.Touch", Start, TimeZoneInfo.Utc, tenant);

        var missing = await GeneratedJobs.Set.Run(test.Pipeline, occurrence, test.Clock, Ct);

        missing.TryGetValue(out Violation? violation).ShouldBeTrue();          // the house auction does not exist in this tenant
        violation.ShouldBeOfType<Error>().Type.ShouldBe(ErrorType.NotFound);
        JobSet.Key(occurrence).Tenant.ShouldBe(Option.Some(tenant));
        JobSet.Context(occurrence, test.Clock).Tenant.ShouldBe(Option.Some(tenant));
    }

    [Fact]
    public void A_part_extends_the_key_and_an_empty_part_throws()
    {
        var occurrence = new JobOccurrence("Settle", Start, TimeZoneInfo.Utc, Option.None);

        JobSet.Key(occurrence, "0196b5a1").Key.ShouldBe("2026-11-16T01:00:00.0000000Z/0196b5a1");
        JobSet.Options(occurrence, "x").Key.GetValueOrThrow().Scope.ShouldBe("job:Settle");
        Should.Throw<ArgumentException>(() => JobSet.Key(occurrence, " "));
    }

    [Fact]
    public void An_occurrence_is_a_whole_second_in_UTC_with_its_local_time()
    {
        var local = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.FromHours(1));
        var occurrence = new JobOccurrence("X", local, Vienna, Option.None);

        occurrence.At.Offset.ShouldBe(TimeSpan.Zero);
        occurrence.At.ShouldBe(new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero));
        occurrence.Local.Offset.ShouldBe(TimeSpan.FromHours(1));
        occurrence.Local.Hour.ShouldBe(3);
        occurrence.KeyText.ShouldBe("2026-11-16T02:00:00.0000000Z");
        Should.Throw<ArgumentException>(() => new JobOccurrence("X", Start.AddTicks(1), Vienna, Option.None));
        Should.Throw<ArgumentException>(() => new JobOccurrence(" ", Start, Vienna, Option.None));
    }

    [Fact]
    public void Sets_combine_and_a_name_in_two_sets_throws()
    {
        static ValueTask<Result<ChangeOutcome>> Never(ChangePipeline p, JobOccurrence o, TimeProvider c, CancellationToken ct) => throw new InvalidOperationException();
        var other = new JobSet([new JobDefinition("Reports.Weekly", "0 6 * * MON", "UTC", Missed.Skip, false)], Never);

        var combined = JobSet.Combine(GeneratedJobs.Set, other);

        combined.Definitions.Select(d => d.Name).Order(StringComparer.Ordinal).ShouldBe(["Auctions.Touch", "OpenDailyPayout", "Reports.Weekly"]);
        Should.Throw<InvalidOperationException>(() => JobSet.Combine(GeneratedJobs.Set, GeneratedJobs.Set)).Message.ShouldContain("'Auctions.Touch'");
        Should.Throw<ArgumentException>(() => new JobSet([other.Definitions[0], other.Definitions[0]], Never));
        JobSet.Empty.Definitions.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_job_throws()
    {
        var test = ChangeTest.At(Start);
        var occurrence = new JobOccurrence("Gone", Start, TimeZoneInfo.Utc, Option.None);

        (await Should.ThrowAsync<InvalidOperationException>(async () => _ = await GeneratedJobs.Set.Run(test.Pipeline, occurrence, test.Clock, Ct)))
            .Message.ShouldContain("'Gone'");
        (await Should.ThrowAsync<InvalidOperationException>(async () => _ = await JobSet.Empty.Run(test.Pipeline, occurrence, test.Clock, Ct)))
            .Message.ShouldContain("'Gone'");
    }
}
