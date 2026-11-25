using System.Collections.Concurrent;
using System.Diagnostics;
using Npgsql;

namespace Tarol.Queries.Npgsql.IntegrationTests;

/// <summary>
/// Tatalu FB-ASPIRE-1: the listener and poll loop of <see cref="NpgsqlProjectionPositions"/> started inside the first request
/// that read a list, and every poll (1/s) became a child span of that request's trace, forever.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DetachedLoopTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_poll_loop_of_the_positions_does_not_join_the_trace_of_the_request_that_started_it()
    {
        var setup = await postgres.Create(pushCheckpoints: true);
        var spans = new ConcurrentQueue<(string Source, ActivityTraceId Trace)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static _ => true,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => spans.Enqueue((a.Source.Name, a.TraceId)),
        };
        ActivitySource.AddActivityListener(listener);
        await using var db = new NpgsqlDataSourceBuilder(postgres.ConnectionString).Build();   // emits a span per command
        await using var positions = new NpgsqlProjectionPositions(db, setup.Schema) { PollInterval = TimeSpan.FromMilliseconds(50) };
        using var source = new ActivitySource("Tatalu.Test");

        ActivityTraceId request;
        using (var activity = source.StartActivity("GET /anwesenheit/belegung"))
        {
            request = activity!.TraceId;
            _ = await positions.Checkpoint<AuctionRow>(Ct);                                 // the first read starts the loop
        }
        var polledBefore = spans.Count(s => s.Source == "Npgsql");
        var watch = Stopwatch.StartNew();
        while (spans.Count(s => s.Source == "Npgsql") < polledBefore + 5)
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "the poll loop runs and its commands are traced");
            await Task.Delay(20, Ct);
        }

        spans.Where(s => s.Source == "Npgsql").Skip(polledBefore).Where(s => s.Trace == request).ShouldBeEmpty();
    }
}
