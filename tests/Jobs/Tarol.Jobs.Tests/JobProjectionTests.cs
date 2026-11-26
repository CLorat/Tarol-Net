using Tarol.Changes;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Jobs.Tests;

/// <summary>The counters as a list (a job's targets, Tatalu FB-F03.4-6: care files due for renewal).</summary>
[Projection("counters")]
public sealed partial record CounterRow(Guid Id, int Ticks)
{
    public static CounterRow When(Counter.Started e, RecordedEvent r) => new(r.StreamId, 0);

    public CounterRow When(Counter.Ticked e) => this with { Ticks = Ticks + 1 };
}

/// <summary>Finds its targets in a projection (skill tarol-jobs rule 2), not in the store.</summary>
public sealed class CountTargets(IReadModelStore<CounterRow> counters) : IJob
{
    public List<int> Seen { get; } = [];

    public async ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken)
    {
        var page = await counters.Search(new ListQuery(Guid.Empty), cancellationToken);
        Seen.Add(page.TryGetValue(out Page<CounterRow>? rows) ? rows.Items.Count : -1);
        return Result.Success();
    }
}

public sealed class JobProjectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 0, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_job_reads_a_projection_over_the_test_store_caught_up_before_every_run()
    {
        var test = JobTest.At(Start, JobSet.Empty);
        var counters = ProjectionTest<CounterRow>.Over(test.Store);
        var job = new CountTargets(counters.ReadModels);
        test.Add(job, "0 * * * *").BeforeEachRun(counters.CatchUp);
        test.Given(Counter.Start(Id<Counter>.New()));

        await test.Advance(TimeSpan.FromHours(1));                                    // 01:00
        test.Given(Counter.Start(Id<Counter>.New())).Given(Counter.Start(Id<Counter>.New()));
        await test.Advance(TimeSpan.FromHours(1));                                    // 02:00

        job.Seen.ShouldBe([1, 3]);
    }
}
