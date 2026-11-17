using Microsoft.Extensions.Configuration;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Jobs.Tests;

/// <summary>Ticks <see cref="Counter.Main"/> (or the counter of the row's tenant) once per occurrence.</summary>
public sealed class TickJob : IJob
{
    public List<JobOccurrence> Seen { get; } = [];

    public async ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken)
    {
        Seen.Add(run.Occurrence);
        return (await run.Change(new TickCounter(Counter.Main, run.Occurrence.At), cancellationToken)).TryGetValue(out Violation? v) ? v : Result.Success();
    }
}

/// <summary>Returns the scripted results in order (the last one forever), or throws for <c>null</c>.</summary>
public sealed class ScriptedJob(params Result?[] script) : IJob
{
    public List<(DateTimeOffset At, int Attempt)> Seen { get; } = [];

    public ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken)
    {
        Seen.Add((run.Occurrence.At, run.Attempt));
        var next = script[Math.Min(Seen.Count - 1, script.Length - 1)];
        return next is { } result ? ValueTask.FromResult(result) : throw new InvalidOperationException("boom");
    }
}

/// <summary>A fan-out over three counters that fails after the second change on its first attempt.</summary>
public sealed class FanOutJob(Id<Counter>[] counters) : IJob
{
    public async ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken)
    {
        for (var i = 0; i < counters.Length; i++)
        {
            if (run.Attempt == 1 && i == 2)
                return Error.Unavailable("Ledger.Down");
            if ((await run.Change(new TickCounter(counters[i], run.Occurrence.At), cancellationToken)).TryGetValue(out Violation? v))
                return v;
        }
        return Result.Success();
    }
}

public sealed class JobTestTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Hour = new(2026, 11, 16, 1, 0, 0, TimeSpan.Zero);

    private static JobTest Ticks(TickJob job, Missed missed = Missed.Latest, Action<JobOptions>? more = null)
        => JobTest.At(Midnight, JobSet.Empty, stateOptions: TestJson.Default.Options)
            .Given(Counter.Start(Counter.Main))
            .Add(job, "0 * * * *", o => { o.Name = "Ticks"; o.Missed = missed; more?.Invoke(o); });

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    // ── the design's example (jobs.md §8) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_nightly_change_runs_once_at_two_Vienna_time_and_its_reaction_is_drained()
    {
        var test = JobTest.At(Midnight, GeneratedJobs.Set, reactions: GeneratedReactions.Set, stateOptions: TestJson.Default.Options);

        var runs = await test.Advance(TimeSpan.FromHours(2));

        var run = runs.ShouldHaveSingleItem();
        (run.Job, run.Occurrence, run.Attempt, run.Verdict, run.Redrive).ShouldBe(("CloseDay", Hour, 1, Verdict.Done, false));   // 02:00+01
        var closed = test.Store.Recorded.Single(r => r.Event.Event is DayBook.Closed);
        ((DayBook.Closed)closed.Event.Event).Day.ShouldBe(new DateOnly(2026, 11, 15));
        closed.Context.Actor.ShouldBe(Actor.System("Jobs.CloseDay"));
        closed.Context.CausationId.ShouldBe(Option.None);
        (await test.Store.Seen(new IdempotencyKey(Option.None, "job:CloseDay", "2026-11-16T01:00:00.0000000Z"), TestContext.Current.CancellationToken)).TryGetValue(out StoredOutcome _)
            .ShouldBeTrue();
        test.Reactions.ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Done);
        test.Store.Recorded.Count(r => r.Event.Event is Notice.Opened).ShouldBe(1);
        (await test.JobRows()).Single().NextAt.ShouldBe(Hour.AddDays(1));

        (await test.Advance(TimeSpan.FromHours(20))).ShouldBeEmpty();                  // nothing before tomorrow 02:00
    }

    [Fact]
    public async Task Each_occurrence_starts_a_conversation_of_its_own()
    {
        var test = JobTest.At(Midnight, GeneratedJobs.Set, stateOptions: TestJson.Default.Options);

        _ = await test.Advance(TimeSpan.FromDays(2));

        var contexts = test.Store.Recorded.Where(r => r.Event.Event is DayBook.Closed).Select(r => r.Context).ToList();
        contexts.Count.ShouldBe(2);
        contexts[0].CorrelationId.ShouldNotBe(contexts[1].CorrelationId);
    }

    // ── missed runs (J-5) ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Latest_runs_the_newest_missed_occurrence_once()
    {
        var job = new TickJob();
        var test = Ticks(job);
        _ = await test.Advance(TimeSpan.FromHours(1));

        var runs = await test.Down(TimeSpan.FromHours(72)).Advance(TimeSpan.Zero);

        runs.ShouldHaveSingleItem().Occurrence.ShouldBe(Hour.AddHours(72));
        (await test.JobRows()).Single().NextAt.ShouldBe(Hour.AddHours(73));
        test.Load(Counter.Main).Ticks.Count.ShouldBe(2);
    }

    [Fact]
    public async Task All_runs_every_missed_occurrence_in_order()
    {
        var job = new TickJob();
        var test = Ticks(job, Missed.All);
        _ = await test.Advance(TimeSpan.FromHours(1));

        var runs = await test.Down(TimeSpan.FromHours(72)).Advance(TimeSpan.Zero);

        runs.Select(r => r.Occurrence).ShouldBe(Enumerable.Range(1, 72).Select(h => Hour.AddHours(h)));
        test.Load(Counter.Main).Ticks.Count.ShouldBe(73);
        (await test.JobRows()).Single().NextAt.ShouldBe(Hour.AddHours(73));
    }

    [Fact]
    public async Task All_is_capped_per_claim_and_the_next_claims_go_on()
    {
        var job = new TickJob();
        var test = Ticks(job, Missed.All);
        _ = await test.Advance(TimeSpan.FromHours(1));
        test.Down(TimeSpan.FromHours(240));
        var cursor = (await test.JobRows()).Single().NextAt;
        var registry = new JobsBuilder(null).Add(job, Cron.Parse("0 * * * *"), o => o.Missed = Missed.All, test.Pipeline, test.Clock)
            .Build(test.Pipeline, test.Clock, null, null, [], null);

        var (run, next, _) = JobWorker.Pick(registry.All[0], cursor, test.Now);

        (run.Count, next).ShouldBe((100, cursor.AddHours(100)));                        // 240 missed: 100 + 100 + 40 in three claims
        (await test.Advance(TimeSpan.Zero)).Count.ShouldBe(240);
        test.Load(Counter.Main).Ticks.ShouldBe(test.Load(Counter.Main).Ticks.Order());
    }

    [Fact]
    public async Task Skip_runs_nothing_after_the_grace_and_the_newest_within_it()
    {
        var job = new TickJob();
        var test = Ticks(job, Missed.Skip);
        _ = await test.Advance(TimeSpan.FromHours(1));

        (await test.Down(TimeSpan.FromHours(72) + TimeSpan.FromMinutes(5)).Advance(TimeSpan.Zero)).ShouldBeEmpty();
        (await test.JobRows()).Single().NextAt.ShouldBe(Hour.AddHours(73));
        (await test.Down(TimeSpan.FromMinutes(55) + TimeSpan.FromSeconds(30)).Advance(TimeSpan.Zero)).ShouldHaveSingleItem().Occurrence
            .ShouldBe(Hour.AddHours(73));                                                // 30 s late: within the grace
    }

    // ── failures (J-7) ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failing_occurrence_is_retried_with_backoff_then_dead_and_the_cursor_moves_on()
    {
        var job = new ScriptedJob(Error.Conflict("Ledger.Busy"));
        var test = JobTest.At(Midnight, JobSet.Empty).Add(job, "0 * * * *", o =>
        {
            o.Name = "Busy";
            o.Policy = new DeliveryPolicy { MaxAttempts = 3 };
        });

        var runs = await test.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(10));

        runs.Select(r => (r.Attempt, r.Verdict)).ShouldBe([(1, Verdict.Retry), (2, Verdict.Retry), (3, Verdict.Bury)]);
        job.Seen.ShouldAllBe(s => s.At == Hour);                                         // the same occurrence each time
        (job.Seen.Count, runs[0].Reason).ShouldBe((3, Option.Some("Conflict Ledger.Busy")));
        var dead = (await test.DeadJobs()).ShouldHaveSingleItem();
        (dead.Name, dead.Occurrence, dead.Attempts, dead.LastError).ShouldBe(("Busy", Hour, 3, Option.Some("Conflict Ledger.Busy")));
        (await test.JobRows()).Single().NextAt.ShouldBe(Hour.AddHours(1));
    }

    [Fact]
    public async Task A_redriven_dead_job_runs_its_occurrence_again()
    {
        var job = new ScriptedJob(Error.Conflict("Ledger.Busy"), Result.Success());
        var test = JobTest.At(Midnight, JobSet.Empty).Add(job, "0 * * * *", o =>
        {
            o.Name = "Busy";
            o.Policy = new DeliveryPolicy { MaxAttempts = 1 };
        });
        _ = await test.Advance(TimeSpan.FromHours(1));
        var dead = (await test.DeadJobs()).Single();

        (await test.Redrive(dead.Id)).ShouldBeTrue();
        var runs = await test.Advance(TimeSpan.Zero);

        runs.ShouldHaveSingleItem().ShouldBe(new JobAttempt("Busy", Option.None, Hour, 1, Verdict.Done, Option.None, Redrive: true));
        (await test.DeadJobs()).ShouldBeEmpty();
        (await test.JobRows()).Single().NextAt.ShouldBe(Hour.AddHours(1));
    }

    [Fact]
    public async Task Invalid_is_dead_at_once_and_an_exception_is_retried()
    {
        var invalid = new ScriptedJob(Invalid.Field("Day", "Range"));
        var throwing = new ScriptedJob(null, Result.Success());
        var test = JobTest.At(Midnight, JobSet.Empty)
            .Add(invalid, "0 * * * *", o => o.Name = "Invalid")
            .Add(throwing, "0 * * * *", o => o.Name = "Throwing");

        var runs = await test.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5));

        runs.Where(r => r.Job == "Invalid").Select(r => r.Verdict).ShouldBe([Verdict.Bury]);
        runs.Where(r => r.Job == "Throwing").Select(r => (r.Verdict, r.Reason)).ShouldBe([
            (Verdict.Retry, Option.Some("InvalidOperationException: boom")), (Verdict.Done, Option.None)]);
    }

    [Fact]
    public async Task A_job_with_nothing_to_do_returns_success_and_moves_on()
    {
        var job = new ScriptedJob(Result.Success());
        var test = JobTest.At(Midnight, JobSet.Empty).Add(job, "0 * * * *", o => o.Name = "Idle");

        (await test.Advance(TimeSpan.FromHours(3))).Select(r => r.Verdict).ShouldBe([Verdict.Done, Verdict.Done, Verdict.Done]);
        test.Store.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_retried_fan_out_replays_what_committed_and_finishes_the_rest()
    {
        Id<Counter>[] counters = [Id<Counter>.New(), Id<Counter>.New(), Id<Counter>.New()];
        var test = JobTest.At(Midnight, JobSet.Empty, stateOptions: TestJson.Default.Options);
        foreach (var counter in counters)
            test.Given(Counter.Start(counter));
        test.Add(new FanOutJob(counters), "0 * * * *", o => o.Name = "FanOut");

        var runs = await test.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5));

        runs.Select(r => r.Verdict).ShouldBe([Verdict.Retry, Verdict.Done]);
        counters.Select(c => test.Load(c).Ticks.Count).ShouldBe([1, 1, 1]);                  // the first two replayed on attempt 2
        (await test.Store.Seen(new IdempotencyKey(Option.None, "job:FanOut", "2026-11-16T01:00:00.0000000Z/" + counters[0].Value.ToString("N")), TestContext.Current.CancellationToken))
            .TryGetValue(out StoredOutcome _).ShouldBeTrue();
    }

    // ── configuration (J-2) ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Configuration_overrides_the_declared_schedule_and_zone()
    {
        var test = JobTest.At(Midnight, GeneratedJobs.Set, stateOptions: TestJson.Default.Options)
            .WithConfiguration(Configuration(("Tarol:Jobs:CloseDay:Cron", "30 3 * * *"), ("Tarol:Jobs:CloseDay:TimeZone", "America/New_York")));

        var runs = await test.Advance(TimeSpan.FromHours(10));

        runs.ShouldHaveSingleItem().Occurrence.ShouldBe(new DateTimeOffset(2026, 11, 16, 8, 30, 0, TimeSpan.Zero));   // 03:30-05:00
        (await test.JobRows()).Single().Schedule.ShouldBe("30 3 * * * America/New_York");
    }

    [Fact]
    public async Task A_changed_schedule_at_startup_moves_the_cursor_and_the_old_occurrence_never_runs()
    {
        var test = JobTest.At(Midnight, GeneratedJobs.Set, stateOptions: TestJson.Default.Options);
        _ = await test.Advance(TimeSpan.FromHours(2));                                    // 02:00 Vienna ran; cursor at tomorrow 02:00

        test.WithConfiguration(Configuration(("Tarol:Jobs:CloseDay:Cron", "0 4 * * *")));
        (await test.Sync()).ShouldBe([new JobSyncChange("CloseDay", Option.None, JobSyncKind.Rescheduled)]);
        var runs = await test.Advance(TimeSpan.FromDays(1));

        runs.ShouldHaveSingleItem().Occurrence.ShouldBe(new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero));     // 04:00+01 today
        test.Store.Recorded.Count(r => r.Event.Event is DayBook.Closed).ShouldBe(2);                                   // tomorrow's 02:00 never ran
    }

    [Fact]
    public async Task A_disabled_job_does_not_run_and_resumes_at_its_next_occurrence()
    {
        var job = new TickJob();
        var test = Ticks(job, Missed.All);
        _ = await test.Advance(TimeSpan.FromHours(1));

        test.WithConfiguration(Configuration(("Tarol:Jobs:Ticks:Enabled", "false")));
        (await test.Sync()).ShouldBe([new JobSyncChange("Ticks", Option.None, JobSyncKind.Disabled)]);
        (await test.Advance(TimeSpan.FromHours(5))).ShouldBeEmpty();
        test.WithConfiguration(Configuration(("Tarol:Jobs:Ticks:Enabled", "true")));
        (await test.Sync()).ShouldBe([new JobSyncChange("Ticks", Option.None, JobSyncKind.Enabled)]);

        (await test.Advance(TimeSpan.FromHours(1))).ShouldHaveSingleItem().Occurrence.ShouldBe(Hour.AddHours(6));    // no catch-up for the pause
    }

    [Theory]
    [InlineData("Tarol:Jobs:CloseDay:Cron", "0 0 2 * * *", "Tarol:Jobs:CloseDay:Cron '0 0 2 * * *' is invalid: a schedule has 5 fields")]
    [InlineData("Tarol:Jobs:CloseDay:TimeZone", "Europe/Atlantis", "The time zone 'Europe/Atlantis' of the job 'CloseDay' is unknown")]
    [InlineData("Tarol:Jobs:CloseDay:Enabled", "maybe", "Tarol:Jobs:CloseDay:Enabled 'maybe' is not true or false.")]
    [InlineData("Tarol:Jobs:CloseDya:Enabled", "false", "Tarol:Jobs:CloseDya configures no declared job (declared: CloseDay).")]
    public async Task Invalid_configuration_fails_at_startup(string key, string value, string message)
    {
        var test = JobTest.At(Midnight, GeneratedJobs.Set).WithConfiguration(Configuration((key, value)));

        (await Should.ThrowAsync<InvalidOperationException>(() => test.Sync())).Message.ShouldStartWith(message);
    }

    [Fact]
    public async Task Declarations_are_checked_when_the_jobs_are_built()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => JobTest.At(Midnight, GeneratedJobs.Set, j => j.Configure("Nope", o => o.Enabled = false)).Sync());
        await Should.ThrowAsync<InvalidOperationException>(() => JobTest.At(Midnight, GeneratedJobs.Set).Add(new TickJob(), "0 * * * *", o => o.Name = "CloseDay").Sync());
        Should.Throw<InvalidOperationException>(() => JobTest.At(Midnight, JobSet.Empty, j => j.Add<TickJob>("0 * * * *")));          // no container
        Should.Throw<FormatException>(() => JobTest.At(Midnight, JobSet.Empty).Add(new TickJob(), "0 * * *"));
        Should.Throw<ArgumentOutOfRangeException>(() => JobTest.At(Midnight, JobSet.Empty).Add(new TickJob(), "0 * * * *", o => o.MaxCatchUp = 0));
    }

    [Fact]
    public async Task Configure_changes_a_generated_entry_before_the_override()
    {
        var test = JobTest.At(Midnight, GeneratedJobs.Set, j => j.Configure("CloseDay", o => o.TimeZone = "UTC"), stateOptions: TestJson.Default.Options);

        (await test.Advance(TimeSpan.FromHours(3))).ShouldHaveSingleItem().Occurrence.ShouldBe(Midnight.AddHours(2));
    }

    // ── tenants (J-6) ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_per_tenant_job_runs_once_per_tenant_in_its_tenant_and_follows_the_directory()
    {
        var a = Id<ITenant>.New();
        var b = Id<ITenant>.New();
        var job = new TickJob();
        var test = JobTest.At(Midnight, JobSet.Empty, stateOptions: TestJson.Default.Options)
            .Given(Counter.Start(Counter.Main), a).Given(Counter.Start(Counter.Main), b)
            .Add(job, "0 * * * *", o => { o.Name = "Ticks"; o.PerTenant = true; })
            .WithTenants(a, b);

        var runs = await test.Advance(TimeSpan.FromHours(1));

        runs.Select(r => r.Tenant).ShouldBe([Option.Some(a), Option.Some(b)], ignoreOrder: true);
        test.Load(Counter.Main, a).Ticks.Count.ShouldBe(1);
        test.Load(Counter.Main, b).Ticks.Count.ShouldBe(1);
        test.Store.Recorded.Where(r => r.Event.Event is Counter.Ticked).Select(r => r.Context.Tenant).ShouldBe([Option.Some(a), Option.Some(b)], ignoreOrder: true);

        test.WithTenants(a);
        (await test.Sync()).ShouldBe([new JobSyncChange("Ticks", Option.Some(b), JobSyncKind.Disabled)]);
        (await test.Advance(TimeSpan.FromHours(1))).ShouldHaveSingleItem().Tenant.ShouldBe(Option.Some(a));
    }

    [Fact]
    public async Task A_failing_tenant_does_not_block_another()
    {
        var a = Id<ITenant>.New();
        var b = Id<ITenant>.New();
        var job = new TickJob();
        var test = JobTest.At(Midnight, JobSet.Empty, stateOptions: TestJson.Default.Options)
            .Given(Counter.Start(Counter.Main), a)                                          // b has no counter: NotFound, retried
            .Add(job, "0 * * * *", o => { o.Name = "Ticks"; o.PerTenant = true; o.Policy = new DeliveryPolicy { MaxAttempts = 2 }; })
            .WithTenants(a, b);

        var runs = await test.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(5));

        runs.Where(r => r.Tenant == Option.Some(a)).Select(r => r.Verdict).ShouldBe([Verdict.Done, Verdict.Done]);
        runs.Where(r => r.Tenant == Option.Some(b)).Select(r => r.Verdict).ShouldBe([Verdict.Retry, Verdict.Bury, Verdict.Retry, Verdict.Bury]);
        (await test.DeadJobs()).Select(d => d.Tenant).Distinct().ShouldBe([Option.Some(b)]);
    }

    [Fact]
    public async Task Create_keys_a_creation_by_its_part()
    {
        var test = JobTest.At(Midnight, JobSet.Empty, stateOptions: TestJson.Default.Options);
        var job = new CreatingJob();
        test.Add(job, "0 * * * *", o => o.Name = "Books");

        _ = await test.Advance(TimeSpan.FromHours(1));
        var occurrence = new JobOccurrence("Books", Hour, TimeZoneInfo.Utc, Option.None);
        var again = await new JobRunAccess(occurrence, test).Create(new CloseDay(new DateOnly(2026, 11, 15)), "vienna");

        again.GetValueOrThrow().Replayed.ShouldBeTrue();
        test.Store.Recorded.Count(r => r.Event.Event is DayBook.Closed).ShouldBe(1);
    }

    private sealed class CreatingJob : IJob
    {
        public async ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken)
            => (await run.Create(new CloseDay(new DateOnly(2026, 11, 15)), "vienna", cancellationToken)).TryGetValue(out Violation? v) ? v : Result.Success();
    }

    private sealed class JobRunAccess(JobOccurrence occurrence, JobTest test)
    {
        public ValueTask<Result<ChangeOutcome>> Create(CloseDay creation, string part) => new JobRun(occurrence, 2, test.Pipeline, test.Clock).Create(creation, part);
    }
}
