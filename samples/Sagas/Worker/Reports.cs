using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Jobs;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace ArtFlow;

// ── the nightly settlement report (step 14): a job class in the worker (docs/jobs/README.md) ─────────────────────────

/// <summary>A day's report: how many auctions were settled when it was written.</summary>
public partial class DayReport : AggregateRoot<DayReport>
{
    public DateTimeOffset At { get; private set; }
    public long Settled { get; private set; }

    public static Result<DayReport> Write(DateTimeOffset at, long settled, DateTimeOffset now)
    {
        var report = new DayReport(Id<DayReport>.New(now));
        report.Raise(new Written(at, settled));
        return report;
    }

    public sealed record Written(DateTimeOffset At, long Settled) : Event;

    private void On(Written e) => (At, Settled) = (e.At, e.Settled);
}

/// <summary>Records one report.</summary>
public sealed partial record WriteDayReport(DateTimeOffset At, long Settled) : ICreation<DayReport>
{
    public Result<DayReport> Execute(ChangeContext context) => DayReport.Write(At, Settled, context.Now);
}

/// <summary>
/// Every night at 02:15 Vienna time (the AppHost overrides it to every minute with <c>Tarol:Jobs:SettlementReport:Cron</c>):
/// counts the settled auctions in the auction list (a projection may be read to <i>find</i> things) and records a report,
/// keyed by the occurrence, so a re-run after a crash replays it.
/// </summary>
public sealed class SettlementReport(IReadModels reads) : IJob
{
    public async ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken)
    {
        var page = await reads.Store<AuctionRow>().Search(new ListQuery(Guid.Empty) { Filters = [new ColumnFilter("settled", true)], Count = true, Limit = 1 },
            cancellationToken);
        if (!page.TryGetValue(out Page<AuctionRow>? settled))
            return page.TryGetValue(out Violation? refused) ? refused : Result.Success();
        return (await run.Create(new WriteDayReport(run.Occurrence.At, settled.Total ?? 0), "report", cancellationToken)).TryGetValue(out Violation? v)
            ? v
            : Result.Success();
    }
}

[JsonSerializable(typeof(DayReport.Written))]
internal sealed partial class AppJson;
