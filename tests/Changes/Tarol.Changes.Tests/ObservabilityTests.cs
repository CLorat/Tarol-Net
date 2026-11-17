using System.Collections.Concurrent;
using System.Diagnostics;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.StronglyTypedId;
using Tarol.Testing.Metrics;

namespace Tarol.Changes.Tests;

#pragma warning disable TVAL006 // the tests build inputs as JSON would; the pipeline validates them

/// <summary>The pipeline's meter, span and outcome tags (step 15B, O-1, O-6; docs/migration/operations.md §11).</summary>
public sealed class ObservabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_run_counts_and_times_its_change_and_outcome_without_the_tenant()
    {
        using var meters = new Measurements();
        var store = new InMemoryAggregateStore(TimeProvider.System);
        var pipeline = new ChangePipeline(store, ChangeTest.At(Start).Clock, meters: meters);
        var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);

        (await pipeline.Run(new CreateAuction("L-1", new Title("Sunflowers"), new Amount(100), Start.AddDays(7)), context, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        (await pipeline.Run(new CreateAuction("L-1", new Title("x"), new Amount(0), Start.AddDays(7)), context, cancellationToken: Ct)).IsSuccess.ShouldBeFalse();
        (await pipeline.Run(new PlaceBid(Id<Auction>.New(), new Amount(150)), context, cancellationToken: Ct)).IsSuccess.ShouldBeFalse();

        var runs = meters.Of("tarol.changes.runs");
        runs.Select(m => ((string)m.Tags["tarol.change"]!, (string)m.Tags["tarol.outcome"]!, m.Value))
            .ShouldBe([("CreateAuction", "committed", 1), ("CreateAuction", "invalid", 1), ("PlaceBid", "not_found", 1)]);
        runs.ShouldAllBe(m => m.Unit == "{change}" && m.Meter == ChangePipeline.MeterName);
        var durations = meters.Of("tarol.changes.duration");
        durations.Count.ShouldBe(3);
        durations.ShouldAllBe(m => m.Unit == "s" && m.Value >= 0);
        meters.All.ShouldAllBe(m => m.Tags.Keys.All(k => k == "tarol.change" || k == "tarol.outcome"));    // never the tenant
    }

    [Fact]
    public void The_outcome_tag_maps_every_failure_kind()
    {
        ChangePipeline.OutcomeOf(new ChangeOutcome(Guid.NewGuid(), 1, 1)).ShouldBe("committed");
        ChangePipeline.OutcomeOf(StoreErrors.VersionConflict(1, 2)).ShouldBe("conflict");
        ChangePipeline.OutcomeOf(Tarol.Results.Error.Forbidden("Auth.Forbidden", "")).ShouldBe("denied");
        ChangePipeline.OutcomeOf(Tarol.Results.Error.Unauthorized("Auth.Required", "")).ShouldBe("denied");
        ChangePipeline.OutcomeOf(Tarol.Results.Error.NotFound("Auction.NotFound", "")).ShouldBe("not_found");
        ChangePipeline.OutcomeOf(Tarol.Results.Error.UnprocessableEntity("Auction.Closed", "")).ShouldBe("invalid");
        ChangePipeline.OutcomeOf(Tarol.Results.Error.Internal("Boom", "")).ShouldBe("error");
    }

    [Fact]
    public async Task Every_run_is_one_internal_span_named_by_its_change_under_the_callers_activity()
    {
        using var caller = new ActivitySource("Tarol.Changes.Tests.Caller");
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is ChangePipeline.ActivitySourceName || s == caller,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var test = ChangeTest.At(Start);
        var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);

        ActivityTraceId trace;
        using (var request = caller.StartActivity("request"))
        {
            trace = request!.TraceId;
            _ = await test.Pipeline.Run(new CreateAuction("L-1", new Title("Sunflowers"), new Amount(100), Start.AddDays(7)), context, cancellationToken: Ct);
        }

        var change = spans.Single(s => s.TraceId == trace && s.Source.Name == ChangePipeline.ActivitySourceName);
        (change.DisplayName, change.Kind).ShouldBe(("change CreateAuction", ActivityKind.Internal));
        change.GetTagItem("tarol.change").ShouldBe("CreateAuction");
        change.GetTagItem("tarol.outcome").ShouldBe("committed");
        change.Parent!.DisplayName.ShouldBe("request");
        change.TagObjects.ShouldNotContain(t => t.Key.Contains("tenant", StringComparison.Ordinal));
    }
}
