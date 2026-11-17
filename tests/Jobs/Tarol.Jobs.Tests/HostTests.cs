using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tarol.Changes;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.Sagas;

namespace Tarol.Jobs.Tests;

/// <summary>A job that waits until the test lets it finish, and sees whether its token was cancelled.</summary>
public sealed class GatedJob : IJob
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Runs { get; private set; }
    public bool Cancelled { get; private set; }
    public Task Started => _started.Task;

    public void Release() => _release.TrySetResult();

    public async ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken)
    {
        Runs++;
        _started.TrySetResult();
        try
        {
            await _release.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            throw;
        }
        return Result.Success();
    }
}

/// <summary>A job class with a dependency, resolved from a scope by the host.</summary>
public sealed class CountingJob(CountingJob.Counter counter) : IJob
{
    public ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken)
    {
        counter.Runs.Add(run.Occurrence);
        return ValueTask.FromResult(Result.Success());
    }

    public sealed class Counter
    {
        public List<JobOccurrence> Runs { get; } = [];
    }
}

public sealed class HostTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 0, 59, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (InMemoryAggregateStore Store, JobWorker Worker, JobRegistry Registry) Worker(FakeTimeProvider time, IJob job, string schedule = "0 * * * *")
    {
        var store = new InMemoryAggregateStore(time);
        var pipeline = new ChangePipeline(store, time);
        var registry = new JobsBuilder(null).Add(job, Cron.Parse(schedule), o => o.Name = "Gated", pipeline, time).Build(pipeline, time, null, null, [], null);
        return (store, new JobWorker(store.Jobs, registry, EmptyProvider.Instance, time, new JobHostOptions(), NullLogger.Instance), registry);
    }

    /// <summary>Advances the fake clock in steps smaller than lease/3 and yields, so the heartbeat's delays fire (jobs.md §5 item 9).</summary>
    private static async Task Pass(FakeTimeProvider time, TimeSpan total)
    {
        for (var passed = TimeSpan.Zero; passed < total; passed += TimeSpan.FromSeconds(5))
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(5, Ct);
        }
    }

    [Fact]
    public async Task A_heartbeat_keeps_a_run_longer_than_the_lease_from_a_second_claim()
    {
        var time = new FakeTimeProvider(Start);
        var job = new GatedJob();
        var (store, worker, registry) = Worker(time, job);
        _ = await JobHost.Sync(registry, store.Jobs, null, time, NullLogger.Instance, Ct);
        time.Advance(TimeSpan.FromMinutes(1));                                           // 01:00 is due

        var running = worker.RunOnce(Ct);
        await job.Started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Pass(time, TimeSpan.FromMinutes(3));                                      // three leases long

        (await store.Jobs.Claim(TimeSpan.FromMinutes(1), Ct)).ShouldBe(Option.None);      // no overlapping run
        job.Release();
        var attempts = (await running).GetValueOrThrow();

        attempts.ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Done);
        job.Runs.ShouldBe(1);                                                            // one run of 01:00, not two
        (await store.Jobs.Rows(Ct)).Single().NextAt.ShouldBe(new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero));
        // three hours later the next claim runs the newest occurrence only (Latest): 02:00 and 03:00 do not run, 04:00 does
        time.Advance(TimeSpan.FromHours(3));
        (await worker.RunOnce(Ct)).GetValueOrThrow().ShouldHaveSingleItem().Occurrence.ShouldBe(new DateTimeOffset(2026, 11, 16, 4, 0, 0, TimeSpan.Zero));
        job.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task A_run_that_lost_its_claim_is_cancelled_and_writes_nothing()
    {
        var time = new FakeTimeProvider(Start);
        var job = new GatedJob();
        var (store, worker, registry) = Worker(time, job);
        _ = await JobHost.Sync(registry, store.Jobs, null, time, NullLogger.Instance, Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        var running = worker.RunOnce(Ct);
        await job.Started.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // a new deploy changes the schedule: Sync drops the claim, so the next heartbeat finds it lost
        _ = await store.Jobs.Sync([new JobSchedule("Gated", Option.None, "30 * * * * UTC", true, Start.AddMinutes(31))], Ct);
        await Pass(time, TimeSpan.FromSeconds(30));
        var attempts = (await running.WaitAsync(TimeSpan.FromSeconds(10), Ct)).GetValueOrThrow();

        job.Cancelled.ShouldBeTrue();
        attempts.ShouldBeEmpty();
        var row = (await store.Jobs.Rows(Ct)).Single();
        (row.NextAt, row.Schedule).ShouldBe((Start.AddMinutes(31), "30 * * * * UTC"));     // the old holder's Advance never happened
    }

    [Fact]
    public async Task A_row_of_a_job_this_process_does_not_declare_is_left_for_another()
    {
        var time = new FakeTimeProvider(Start);
        var (store, worker, _) = Worker(time, new GatedJob());
        _ = await store.Jobs.Sync([new JobSchedule("OtherVersion", Option.None, "x", true, Start)], Ct);

        (await worker.RunOnce(Ct)).GetValueOrThrow().ShouldBeEmpty();

        var row = (await store.Jobs.Rows(Ct)).Single();
        (row.NextAt, row.LastError).ShouldBe((Start, Option.Some("not declared in this process")));
        (await worker.RunOnce(Ct)).ShouldBe(Option.None);                                   // held back for a lease
    }

    [Fact]
    public async Task The_system_jobs_purge_keys_and_run_the_registered_maintenance()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryAggregateStore(time);
        var pipeline = new ChangePipeline(store, time);
        var purge = new FakePurge();
        var maintained = new List<DateTimeOffset>();
        var registry = new JobsBuilder(null).Build(pipeline, time, purge, new ReactionOptions { KeyRetention = TimeSpan.FromDays(2), PurgeBatch = 10 },
            [new MaintenanceTask("tarol.test", TimeSpan.FromMinutes(30), (_, at, _) => { maintained.Add(at); return Task.FromResult(3); })], null);
        var worker = new JobWorker(store.Jobs, registry, EmptyProvider.Instance, time, new JobHostOptions(), NullLogger.Instance);
        _ = await JobHost.Sync(registry, store.Jobs, null, time, NullLogger.Instance, Ct);
        time.Advance(TimeSpan.FromHours(1));

        while ((await worker.RunOnce(Ct)).TryGetValue(out IReadOnlyList<JobAttempt>? _))
        {
        }

        registry.All.Select(j => (j.Name, j.Stored)).ShouldBe([("tarol.purge-keys", "every 3600s"), ("tarol.test", "every 1800s")]);
        purge.Calls.ShouldBe([(new DateTimeOffset(2026, 11, 14, 1, 0, 0, TimeSpan.Zero), 10), (new DateTimeOffset(2026, 11, 14, 1, 0, 0, TimeSpan.Zero), 10)]);
        maintained.ShouldBe([new DateTimeOffset(2026, 11, 16, 1, 30, 0, TimeSpan.Zero)]);   // Latest: the newest of 01:00 and 01:30
    }

    [Fact]
    public async Task AddJobs_runs_generated_entries_and_classes_and_takes_over_the_key_purge()
    {
        var time = new FakeTimeProvider(Start);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<CountingJob.Counter>();
        var purge = new FakePurge();
        services.AddSingleton<IIdempotencyKeyPurge>(purge);
        services.AddTarol(TestJson.Default, codec => codec.Add<DayBook>().Add<Counter>().Add<Notice>())
            .UseInMemoryStore()
            .AddReactionWorker(o => o.PollInterval = TimeSpan.FromMilliseconds(20), GeneratedReactions.Set)
            .AddJobs(j => j.Add(GeneratedJobs.Set).Add<CountingJob>("0 * * * *"), o => o.PollInterval = TimeSpan.FromSeconds(1));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var host = provider.GetServices<IHostedService>().OfType<JobHost>().ShouldHaveSingleItem();
        host.Jobs.ShouldBe(["CloseDay", "CountingJob", "tarol.purge-keys"]);
        provider.GetRequiredService<IMaintenanceHost>().ShouldNotBeNull();
        _ = provider.GetServices<IHostedService>().OfType<ReactionHost>().ShouldHaveSingleItem();
        provider.GetRequiredService<ReactionOptions>().PurgeKeys.ShouldBeNull();           // J-9: once per cluster, not per process

        await host.StartAsync(Ct);
        var rows = provider.GetRequiredService<IJobRows>();
        for (var i = 0; i < 200 && (await rows.Rows(Ct)).Count < 3; i++)
            await Task.Delay(10, Ct);
        (await rows.Rows(Ct)).Select(r => r.Name).ShouldBe(["CloseDay", "CountingJob", "tarol.purge-keys"]);
        await Pass(time, TimeSpan.FromMinutes(2));                                        // past 01:00 (CloseDay is 01:00Z = 02:00 Vienna)
        var counter = provider.GetRequiredService<CountingJob.Counter>();
        for (var i = 0; i < 200 && (counter.Runs.Count == 0 || purge.Calls.Count == 0); i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, Ct);
        }
        await host.StopAsync(Ct);

        counter.Runs.ShouldHaveSingleItem().At.ShouldBe(new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero));
        purge.Calls.ShouldNotBeEmpty();
        provider.GetRequiredService<InMemoryAggregateStore>().Recorded.ShouldContain(r => r.Event.Event is DayBook.Closed);
    }

    [Fact]
    public async Task An_occurrence_is_counted_with_its_outcome_duration_and_lateness_on_the_hosts_meter()
    {
        using var meters = new Tarol.Testing.Metrics.Measurements();
        var time = new FakeTimeProvider(Start);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<System.Diagnostics.Metrics.IMeterFactory>(meters);
        services.AddSingleton<CountingJob.Counter>();
        services.AddTarol(TestJson.Default, codec => codec.Add<DayBook>().Add<Counter>().Add<Notice>())
            .UseInMemoryStore()
            .AddJobs(j => j.Add<CountingJob>("0 * * * *"), o => o.PollInterval = TimeSpan.FromSeconds(1));
        await using var provider = services.BuildServiceProvider();
        var host = provider.GetServices<IHostedService>().OfType<JobHost>().Single();
        await host.StartAsync(Ct);
        var rows = provider.GetRequiredService<IJobRows>();
        for (var i = 0; i < 200 && (await rows.Rows(Ct)).Count < 1; i++)
            await Task.Delay(10, Ct);
        await Pass(time, TimeSpan.FromMinutes(2));                                        // past 01:00
        var counter = provider.GetRequiredService<CountingJob.Counter>();
        for (var i = 0; i < 200 && counter.Runs.Count == 0; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, Ct);
        }
        await host.StopAsync(Ct);

        var occurrence = meters.Of("tarol.jobs.occurrences").ShouldHaveSingleItem();
        (occurrence.Unit, occurrence.Tags["tarol.job"], occurrence.Tags["tarol.outcome"]).ShouldBe(("{occurrence}", (object?)"CountingJob", (object?)"ran"));
        meters.Of("tarol.jobs.duration").ShouldHaveSingleItem().Unit.ShouldBe("s");
        meters.Of("tarol.jobs.lateness").ShouldHaveSingleItem().Value.ShouldBeGreaterThanOrEqualTo(0);
        meters.All.ShouldAllBe(m => m.Meter == JobHost.MeterName && m.Tags.Keys.All(k => k == "tarol.job" || k == "tarol.outcome"));
    }

    [Fact]
    public void Without_a_job_host_the_reaction_host_purges_keys_itself()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIdempotencyKeyPurge>(new FakePurge());
        services.AddTarol(TestJson.Default, codec => codec.Add<DayBook>().Add<Counter>().Add<Notice>()).UseInMemoryStore().AddReactionWorker(reactions: GeneratedReactions.Set);
        using var provider = services.BuildServiceProvider();

        _ = provider.GetServices<IHostedService>().OfType<ReactionHost>().ShouldHaveSingleItem();
        provider.GetRequiredService<ReactionOptions>().PurgeKeys.ShouldNotBeNull();
    }

    [Fact]
    public void AddJobs_twice_or_a_per_tenant_job_without_a_directory_throws()
    {
        var services = new ServiceCollection();
        var tarol = services.AddTarol(TestJson.Default, codec => codec.Add<DayBook>().Add<Counter>().Add<Notice>()).UseInMemoryStore();
        _ = tarol.AddJobs(j => j.Add<CountingJob>("0 * * * *", o => o.PerTenant = true));
        Should.Throw<InvalidOperationException>(() => tarol.AddJobs(j => j.Add(GeneratedJobs.Set)));
        services.AddSingleton<CountingJob.Counter>();
        using var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<JobHost>()).Message.ShouldContain("ITenantDirectory");
    }

    [Fact]
    public void Host_options_are_validated()
    {
        var tarol = new ServiceCollection().AddTarol(TestJson.Default, codec => codec.Add<DayBook>()).UseInMemoryStore();

        Should.Throw<ArgumentOutOfRangeException>(() => tarol.AddJobs(j => j.Add(GeneratedJobs.Set), o => o.Lease = TimeSpan.FromSeconds(1)));
        Should.Throw<ArgumentOutOfRangeException>(() => new ServiceCollection().AddTarol(TestJson.Default, codec => codec.Add<DayBook>())
            .AddJobs(j => j.Add(GeneratedJobs.Set), o => o.Workers = 0));
    }

    private sealed class FakePurge : IIdempotencyKeyPurge
    {
        public List<(DateTimeOffset OlderThan, int Batch)> Calls { get; } = [];

        public Task<int> PurgeKeys(DateTimeOffset olderThan, int batch, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add((olderThan, batch));
            return Task.FromResult(Calls.Count % 2 == 1 ? batch : 0);                      // one full batch, then an empty one
        }
    }

    private sealed class EmptyProvider : IServiceProvider
    {
        public static readonly EmptyProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
