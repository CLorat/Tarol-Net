using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Contract;

/// <summary>
/// The <see cref="IJobRows"/> contract (docs/architecture/jobs.md §5, J-4, J-7), written once and run against every store:
/// sync, one-row claims with leases, moves and advances guarded by the claim, heartbeats, retries, dead jobs and re-drives.
/// Each test gets rows of its own.
/// </summary>
public abstract class JobRowsContract
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 1, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    protected JobRowsContract() => Time = new FakeTimeProvider(Start);

    /// <summary>The fake clock of the store (claims, leases).</summary>
    protected FakeTimeProvider Time { get; }

    /// <summary>Creates job rows of their own (no rows of other tests) on <paramref name="time"/>.</summary>
    protected abstract Task<IJobRows> Create(TimeProvider time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobSchedule Nightly(DateTimeOffset first, Option<Id<ITenant>> tenant = default, string schedule = "0 2 * * * UTC", bool enabled = true)
        => new("Nightly", tenant, schedule, enabled, first);

    private async Task<IJobRows> WithNightly(DateTimeOffset first)
    {
        var rows = await Create(Time);
        (await rows.Sync([Nightly(first)], Ct)).ShouldBe([new JobSyncChange("Nightly", Option.None, JobSyncKind.Added)]);
        return rows;
    }

    private static async Task<JobRow> Claimed(IJobRows rows) => (await rows.Claim(Lease, Ct)).GetValueOrThrow();

    private static async Task Nothing(IJobRows rows) => (await rows.Claim(Lease, Ct)).ShouldBe(Option.None);

    [Fact]
    public async Task Sync_adds_a_row_at_its_first_occurrence_and_nothing_is_due_before()
    {
        var rows = await WithNightly(Start.AddHours(1));

        var state = (await rows.Rows(Ct)).ShouldHaveSingleItem();
        state.ShouldBe(new JobState(Option.None, "Nightly", "0 2 * * * UTC", true, Start.AddHours(1), Option.None, 0, Option.None));
        await Nothing(rows);
        (await rows.Overdue(Ct)).ShouldBe(Option.None);
    }

    [Fact]
    public async Task A_due_row_is_claimed_once_with_its_cursor_and_the_lease_holds_it()
    {
        var rows = await WithNightly(Start);

        var row = await Claimed(rows);

        (row.Name, row.Tenant, row.NextAt, row.Occurrence, row.Attempts, row.Redrive).ShouldBe(("Nightly", Option.None, Start, Start, 1, Option.None));
        row.Schedule.ShouldBe("0 2 * * * UTC");
        await Nothing(rows);
        Time.Advance(Lease - TimeSpan.FromSeconds(1));
        await Nothing(rows);
        (await rows.Overdue(Ct)).ShouldBe(Option.Some(Start));
    }

    [Fact]
    public async Task One_row_per_claim_the_oldest_cursor_first()
    {
        var rows = await Create(Time);
        _ = await rows.Sync([new JobSchedule("Late", Option.None, "a", true, Start), new JobSchedule("Early", Option.None, "b", true, Start.AddMinutes(-5))], Ct);

        (await Claimed(rows)).Name.ShouldBe("Early");
        (await Claimed(rows)).Name.ShouldBe("Late");
        await Nothing(rows);
        (await rows.Overdue(Ct)).ShouldBe(Option.Some(Start.AddMinutes(-5)));
    }

    [Fact]
    public async Task Move_keeps_the_claim_and_advance_releases_it()
    {
        var rows = await WithNightly(Start);
        var row = await Claimed(rows);

        (await rows.Move(row, Start.AddHours(1), Ct)).ShouldBeTrue();
        await Nothing(rows);                                                          // still claimed
        (await rows.Advance(row, Start.AddDays(1), Ct)).ShouldBeTrue();

        var state = (await rows.Rows(Ct)).Single();
        (state.NextAt, state.LeaseUntil, state.Attempts).ShouldBe((Start.AddDays(1), Option.None, 0));
        await Nothing(rows);
        Time.Advance(TimeSpan.FromDays(1));
        (await Claimed(rows)).Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task A_worker_that_lost_its_lease_changes_nothing()
    {
        var rows = await WithNightly(Start);
        var first = await Claimed(rows);
        Time.Advance(Lease);

        var second = await Claimed(rows);                                             // the lease passed: another worker has it

        second.ClaimId.ShouldNotBe(first.ClaimId);
        second.Attempts.ShouldBe(2);
        (await rows.Move(first, Start.AddHours(1), Ct)).ShouldBeFalse();
        (await rows.Advance(first, Start.AddDays(1), Ct)).ShouldBeFalse();
        (await rows.Extend(first, Lease, Ct)).ShouldBeFalse();
        (await rows.Retry(first, Start.AddHours(1), "late", Ct)).ShouldBeFalse();
        (await rows.Bury(first, Start.AddDays(1), "late", Ct)).ShouldBeFalse();
        (await rows.Rows(Ct)).Single().NextAt.ShouldBe(Start);
        (await rows.DeadJobs(10, Ct)).ShouldBeEmpty();
        (await rows.Advance(second, Start.AddDays(1), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_heartbeat_keeps_a_long_run_from_a_second_claim()
    {
        var rows = await WithNightly(Start);
        var row = await Claimed(rows);

        for (var beat = 0; beat < 6; beat++)                                          // 2 min of a run on a 1 min lease, a beat every lease/3
        {
            Time.Advance(Lease / 3);
            (await rows.Extend(row, Lease, Ct)).ShouldBeTrue();
            await Nothing(rows);
        }
        (await rows.Advance(row, Start.AddDays(1), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_retry_waits_its_backoff_and_runs_the_same_occurrence()
    {
        var rows = await WithNightly(Start);
        var row = await Claimed(rows);

        (await rows.Retry(row, Start.AddSeconds(2), "Conflict: Order.Changed", Ct)).ShouldBeTrue();

        await Nothing(rows);
        Time.Advance(TimeSpan.FromSeconds(2));
        var again = await Claimed(rows);
        (again.Occurrence, again.Attempts, again.LastError).ShouldBe((Start, 2, Option.Some("Conflict: Order.Changed")));
        (await rows.Advance(again, Start.AddDays(1), Ct)).ShouldBeTrue();
        var state = (await rows.Rows(Ct)).Single();
        (state.Attempts, state.LastError).ShouldBe((0, Option.None));
    }

    [Fact]
    public async Task Bury_moves_the_occurrence_to_the_dead_jobs_and_the_cursor_on()
    {
        var tenant = Id<ITenant>.New();
        var rows = await Create(Time);
        _ = await rows.Sync([Nightly(Start, tenant)], Ct);
        var row = await Claimed(rows);
        Time.Advance(TimeSpan.FromSeconds(30));

        (await rows.Bury(row, Start.AddDays(1), "gave up", Ct)).ShouldBeTrue();

        var dead = (await rows.DeadJobs(10, Ct)).ShouldHaveSingleItem();
        (dead.Tenant, dead.Name, dead.Occurrence, dead.Attempts, dead.LastError, dead.DeadAt, dead.Redriven)
            .ShouldBe((Option.Some(tenant), "Nightly", Start, 1, Option.Some("gave up"), Start.AddSeconds(30), false));
        var state = (await rows.Rows(Ct)).Single();
        (state.NextAt, state.Attempts, state.LeaseUntil).ShouldBe((Start.AddDays(1), 0, Option.None));
        await Nothing(rows);
    }

    [Fact]
    public async Task A_redrive_runs_the_dead_occurrence_first_and_never_beside_the_row()
    {
        var rows = await WithNightly(Start);
        _ = await rows.Bury(await Claimed(rows), Start.AddHours(1), "gave up", Ct);
        var dead = (await rows.DeadJobs(10, Ct)).Single();
        (await rows.Redrive(dead.Id + 1000, Ct)).ShouldBeFalse();

        (await rows.Redrive(dead.Id, Ct)).ShouldBeTrue();
        (await rows.Redrive(dead.Id, Ct)).ShouldBeFalse();                           // pending already
        (await rows.DeadJobs(10, Ct)).Single().Redriven.ShouldBeTrue();

        Time.Advance(TimeSpan.FromHours(2));                                          // the cursor is due too: the re-drive comes first
        var redrive = await Claimed(rows);
        (redrive.Redrive, redrive.Occurrence, redrive.NextAt).ShouldBe((Option.Some(dead.Id), Start, Start.AddHours(1)));
        await Nothing(rows);                                                          // one run per row at a time
        (await rows.Advance(redrive, redrive.NextAt, Ct)).ShouldBeTrue();

        (await rows.DeadJobs(10, Ct)).ShouldBeEmpty();
        var own = await Claimed(rows);
        (own.Redrive, own.Occurrence).ShouldBe((Option.None, Start.AddHours(1)));
    }

    [Fact]
    public async Task A_redrive_that_fails_again_stays_dead_and_the_cursor_stays()
    {
        var rows = await WithNightly(Start);
        _ = await rows.Bury(await Claimed(rows), Start.AddDays(1), "first", Ct);
        var dead = (await rows.DeadJobs(10, Ct)).Single();
        _ = await rows.Redrive(dead.Id, Ct);

        var redrive = await Claimed(rows);
        (await rows.Retry(redrive, Start.AddSeconds(1), "again", Ct)).ShouldBeTrue();
        Time.Advance(TimeSpan.FromSeconds(1));
        var retried = await Claimed(rows);
        (retried.Redrive, retried.Attempts, retried.LastError).ShouldBe((Option.Some(dead.Id), 2, Option.Some("again")));
        (await rows.Bury(retried, retried.NextAt, "still", Ct)).ShouldBeTrue();

        var still = (await rows.DeadJobs(10, Ct)).ShouldHaveSingleItem();
        (still.Id, still.Occurrence, still.Attempts, still.LastError, still.Redriven).ShouldBe((dead.Id, Start, 2, Option.Some("still"), false));
        (await rows.Rows(Ct)).Single().NextAt.ShouldBe(Start.AddDays(1));
        await Nothing(rows);
    }

    [Fact]
    public async Task Sync_with_the_same_schedule_keeps_the_cursor_and_the_claim()
    {
        var rows = await WithNightly(Start);
        var row = await Claimed(rows);

        (await rows.Sync([Nightly(Start.AddDays(1))], Ct)).ShouldBeEmpty();

        (await rows.Rows(Ct)).Single().NextAt.ShouldBe(Start);
        (await rows.Advance(row, Start.AddDays(1), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_changed_schedule_moves_the_cursor_to_its_first_occurrence_and_drops_the_claim()
    {
        var rows = await WithNightly(Start);
        var row = await Claimed(rows);

        (await rows.Sync([Nightly(Start.AddHours(5), schedule: "0 6 * * * UTC")], Ct))
            .ShouldBe([new JobSyncChange("Nightly", Option.None, JobSyncKind.Rescheduled)]);

        var state = (await rows.Rows(Ct)).Single();
        (state.Schedule, state.NextAt, state.LeaseUntil, state.Attempts).ShouldBe(("0 6 * * * UTC", Start.AddHours(5), Option.None, 0));
        (await rows.Advance(row, Start.AddDays(1), Ct)).ShouldBeFalse();              // the old schedule's holder changes nothing
        await Nothing(rows);
    }

    [Fact]
    public async Task Disabled_rows_are_not_claimed_and_start_again_at_their_next_occurrence()
    {
        var rows = await WithNightly(Start);

        (await rows.Sync([Nightly(Start, enabled: false)], Ct)).ShouldBe([new JobSyncChange("Nightly", Option.None, JobSyncKind.Disabled)]);
        await Nothing(rows);
        (await rows.Overdue(Ct)).ShouldBe(Option.None);
        Time.Advance(TimeSpan.FromDays(3));

        (await rows.Sync([Nightly(Start.AddDays(4))], Ct)).ShouldBe([new JobSyncChange("Nightly", Option.None, JobSyncKind.Enabled)]);
        (await rows.Rows(Ct)).Single().NextAt.ShouldBe(Start.AddDays(4));             // no catch-up for the pause
        await Nothing(rows);
    }

    [Fact]
    public async Task A_row_that_is_no_longer_declared_is_disabled_and_kept()
    {
        var a = Id<ITenant>.New();
        var b = Id<ITenant>.New();
        var rows = await Create(Time);
        _ = await rows.Sync([Nightly(Start, a), Nightly(Start, b), new JobSchedule("Gone", Option.None, "x", true, Start)], Ct);

        var changes = await rows.Sync([Nightly(Start, a)], Ct);

        changes.OrderBy(c => c.Name, StringComparer.Ordinal).ShouldBe([
            new JobSyncChange("Gone", Option.None, JobSyncKind.Disabled),
            new JobSyncChange("Nightly", Option.Some(b), JobSyncKind.Disabled),
        ]);
        (await rows.Rows(Ct)).Count.ShouldBe(3);
        (await Claimed(rows)).Tenant.ShouldBe(Option.Some(a));
        await Nothing(rows);
        (await rows.Sync([Nightly(Start, a)], Ct)).ShouldBeEmpty();                   // disabled once, not again
    }

    [Fact]
    public async Task Tenants_have_rows_of_their_own()
    {
        var a = Id<ITenant>.New();
        var b = Id<ITenant>.New();
        var rows = await Create(Time);
        _ = await rows.Sync([Nightly(Start, a), Nightly(Start, b), Nightly(Start)], Ct);

        var claimed = new[] { await Claimed(rows), await Claimed(rows), await Claimed(rows) };

        claimed.Select(r => r.Tenant).ShouldBe([Option.Some(a), Option.Some(b), Option.None], ignoreOrder: true);
        _ = await rows.Retry(claimed.Single(r => r.Tenant == Option.Some(a)), Start.AddMinutes(5), "a failed", Ct);
        (await rows.Advance(claimed.Single(r => r.Tenant == Option.Some(b)), Start.AddDays(1), Ct)).ShouldBeTrue();
        var states = await rows.Rows(Ct);
        states.Single(s => s.Tenant == Option.Some(b)).NextAt.ShouldBe(Start.AddDays(1));
        states.Single(s => s.Tenant == Option.Some(a)).NextAt.ShouldBe(Start);
    }

    [Fact]
    public async Task A_row_listed_twice_or_a_time_that_is_not_a_whole_second_throws()
    {
        var rows = await Create(Time);

        await Should.ThrowAsync<ArgumentException>(() => rows.Sync([Nightly(Start), Nightly(Start.AddDays(1))], Ct));
        await Should.ThrowAsync<ArgumentException>(() => rows.Sync([Nightly(Start.AddTicks(10))], Ct));
        _ = await rows.Sync([Nightly(Start)], Ct);
        var row = await Claimed(rows);
        await Should.ThrowAsync<ArgumentException>(() => rows.Move(row, Start.AddTicks(1), Ct));
        await Should.ThrowAsync<ArgumentException>(() => rows.Advance(row, Start.AddMilliseconds(1), Ct));
        await Should.ThrowAsync<ArgumentException>(() => rows.Bury(row, Start.AddMilliseconds(1), "x", Ct));
    }

    [Fact]
    public async Task The_cursor_round_trips_unchanged()
    {
        var at = new DateTimeOffset(2026, 11, 16, 3, 0, 59, TimeSpan.FromHours(2));   // an offset, a whole second
        var rows = await WithNightly(at);
        Time.SetUtcNow(at.AddSeconds(1));

        var row = await Claimed(rows);

        row.NextAt.ShouldBe(at);
        row.NextAt.UtcTicks.ShouldBe(at.UtcTicks);
        (await rows.Move(row, at.AddSeconds(1), Ct)).ShouldBeTrue();
        (await rows.Rows(Ct)).Single().NextAt.UtcTicks.ShouldBe(at.AddSeconds(1).UtcTicks);
    }
}
