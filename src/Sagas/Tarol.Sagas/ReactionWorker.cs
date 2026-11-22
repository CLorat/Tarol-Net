using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Changes;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;

namespace Tarol.Sagas;

/// <summary>What happened to one claimed row.</summary>
/// <param name="Row">The row as claimed.</param>
/// <param name="Verdict">Done, retried or buried.</param>
/// <param name="Reason">Why it was not done (the error code, or the exception).</param>
/// <param name="Kept">Whether the outbox accepted the verdict; <see langword="false"/>: the lease was lost to another worker.</param>
public sealed record ReactionAttempt(OutboxRow Row, Verdict Verdict, Option<string> Reason, bool Kept);

/// <summary>
/// One worker loop over an <see cref="IOutbox"/> (docs/architecture/sagas.md §3): claim a batch, run each row through its
/// <see cref="IOutboxRowHandler"/> (a reaction: <see cref="ReactionSet.Run"/> through the <see cref="ChangePipeline"/>, as
/// the system on behalf of the user, caused by the event, keyed by reaction and event id; a publication or an effect of
/// <c>Tarol.Messaging</c>), then complete, retry with backoff, or bury the row.
/// </summary>
/// <remarks>
/// Every row runs in its own <see cref="ActivityKind.Consumer"/> activity of <see cref="ActivitySourceName"/>, named by its
/// handler (<c>reaction &lt;name&gt;</c>, <c>publish &lt;contract&gt;</c>, <c>effect &lt;name&gt;</c>), linked to
/// the trace that committed the event (a saga's next step can be days later; it is never a child). The reaction's own
/// commit records that activity, so the next hop links to it.
/// </remarks>
public sealed partial class ReactionWorker
{
    /// <summary>The name of the <see cref="ActivitySource"/> of reactions: add it to your tracing.</summary>
    public const string ActivitySourceName = "Tarol.Sagas";

    /// <summary>The name of the reactions' meter (step 15): <c>tarol.reactions.runs</c>, <c>.duration</c>, <c>.lateness</c>.</summary>
    public const string MeterName = "Tarol.Sagas";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter SharedMeter = new(MeterName);
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _lateness;

    private readonly IOutbox _outbox;
    private readonly IOutboxRowHandler _rows;
    private readonly ChangePipeline _pipeline;
    private readonly TimeProvider _time;
    private readonly ReactionOptions _options;
    private readonly ILogger _log;

    /// <summary>Creates a worker loop.</summary>
    /// <param name="outbox">The store's outbox.</param>
    /// <param name="rows">
    /// What runs the rows: the application's reactions (<c>GeneratedReactions.Set</c>, or a combined set), or
    /// <see cref="OutboxRows"/> joining them with publications and effects (<c>Tarol.Messaging</c>).
    /// </param>
    /// <param name="pipeline">The pipeline the reactions run through.</param>
    /// <param name="time">The clock of retry times (the store's clock decides what is due).</param>
    /// <param name="options">Batch, lease, poll interval and policy.</param>
    /// <param name="logger">Logs retries, dead letters and lost leases; <see langword="null"/> = none.</param>
    /// <param name="meters">Creates the meter <see cref="MeterName"/> (the host's <see cref="IMeterFactory"/>); <see langword="null"/> = a shared meter.</param>
    public ReactionWorker(IOutbox outbox, IOutboxRowHandler rows, ChangePipeline pipeline, TimeProvider time, ReactionOptions? options = null,
        ILogger? logger = null, IMeterFactory? meters = null)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(time);
        options ??= new ReactionOptions();
        options.Validate();
        _outbox = outbox;
        _rows = rows;
        _pipeline = pipeline;
        _time = time;
        _options = options;
        _log = logger ?? NullLogger.Instance;
        var meter = meters?.Create(MeterName) ?? SharedMeter;
        _runs = meter.CreateCounter<long>("tarol.reactions.runs", "{row}", "Outbox rows handled, by reaction and outcome (completed, retried, buried).");
        _duration = meter.CreateHistogram<double>("tarol.reactions.duration", "s", "Duration of a reaction row, its verdict included.");
        _lateness = meter.CreateHistogram<double>("tarol.reactions.lateness", "s", "Start of a reaction row minus the time it was due.");
    }

    /// <summary>Claims one batch and handles it.</summary>
    /// <param name="cancellationToken">
    /// Cancels between rows and the reaction in flight (its row keeps its lease and comes back after it); a reaction that
    /// has run still gets its verdict recorded.
    /// </param>
    /// <returns>What happened to each claimed row; empty when nothing was due.</returns>
    public async Task<IReadOnlyList<ReactionAttempt>> RunOnce(CancellationToken cancellationToken = default)
    {
        // the whole batch shares one lease: a row is started only while at least half of it is left, so a slow batch does not
        // run its tail after the lease expired, alongside another worker that claimed those rows again
        var startBefore = _time.GetUtcNow() + _options.Lease / 2;
        var rows = await _outbox.Claim(_options.Batch, _options.Lease, cancellationToken).ConfigureAwait(false);
        var attempts = new List<ReactionAttempt>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_time.GetUtcNow() >= startBefore)
            {
                await ReleaseRest(rows, i).ConfigureAwait(false);
                break;
            }
            attempts.Add(await Handle(rows[i], cancellationToken).ConfigureAwait(false));
        }
        return attempts;
    }

    /// <summary>Hands the rows from <paramref name="from"/> on back unrun (not counted as attempts); they are due again at once.</summary>
    private async Task ReleaseRest(IReadOnlyList<OutboxRow> rows, int from)
    {
        BatchOutlived(_log, rows.Count - from, rows.Count);
        for (var i = from; i < rows.Count; i++)
        {
            try
            {
                _ = await _outbox.Release(rows[i], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                LoopFailed(_log, e);                                                    // the lease brings the rest back
                return;
            }
        }
    }

    /// <summary>Runs rounds until nothing is due: an event, its reactions, their events' reactions, … (tests, one-off jobs).</summary>
    /// <param name="cancellationToken">Cancels the drain.</param>
    /// <returns>Every attempt, in order.</returns>
    public async Task<IReadOnlyList<ReactionAttempt>> Drain(CancellationToken cancellationToken = default)
    {
        var all = new List<ReactionAttempt>();
        for (IReadOnlyList<ReactionAttempt> round; (round = await RunOnce(cancellationToken).ConfigureAwait(false)).Count > 0;)
            all.AddRange(round);
        return all;
    }

    /// <summary>Loops until <paramref name="cancellationToken"/> is cancelled: claim, handle, wait for rows when idle.</summary>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>A task that completes when the loop stopped.</returns>
    public async Task Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if ((await RunOnce(cancellationToken).ConfigureAwait(false)).Count == 0)
                    await _outbox.WaitForRows(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // the store is unreachable or a claim failed: the rows stay, so wait and try again
                LoopFailed(_log, e);
                try
                {
                    await Task.Delay(_options.PollInterval * 10, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    internal async Task<ReactionAttempt> Handle(OutboxRow row, CancellationToken cancellationToken)
    {
        ActivityLink[]? links = row.Traceparent.TryGetValue(out string? traceparent) && ActivityContext.TryParse(traceparent, null, out var origin)
            ? [new ActivityLink(origin)]
            : null;
        using var activity = Source.StartActivity(ActivityKind.Consumer, default(ActivityContext), links: links, name: _rows.ActivityName(row.Reaction));
        activity?.SetTag("tarol.reaction", row.Reaction);
        activity?.SetTag("tarol.stream.type", row.Event.StreamType);
        activity?.SetTag("tarol.stream.id", row.Event.Event.StreamId.ToString());
        activity?.SetTag("messaging.message.id", row.Event.Event.EventId.ToString());
        activity?.SetTag("tarol.attempt", row.Attempts);
        var started = Stopwatch.GetTimestamp();
        if (row.DueAt is { } due)
            _lateness.Record(Math.Max(0, (_time.GetUtcNow() - due).TotalSeconds), new KeyValuePair<string, object?>("tarol.reaction", row.Reaction));

        Verdict verdict;
        string? reason = null;
        try
        {
            var result = await _rows.RunPositioned(_pipeline, row.Reaction, row.Event, cancellationToken).ConfigureAwait(false);
            verdict = DeliveryPolicy.Classify(result);
            if (result.TryGetValue(out Violation? violation))
                reason = Describe(violation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;                                                                  // the lease brings the row back
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            verdict = Verdict.Retry;
            reason = e.GetType().Name + ": " + e.Message;
        }
        if (verdict == Verdict.Retry && row.Attempts >= _options.Policy.MaxAttempts)
            verdict = Verdict.Bury;

        activity?.SetTag("tarol.verdict", verdict.ToString());
        if (verdict != Verdict.Done)
            activity?.SetStatus(ActivityStatusCode.Error, reason);

        // the reaction has run: record its verdict even while shutting down, so a finished row is not redelivered
        var kept = verdict switch
        {
            Verdict.Done => await _outbox.Complete(row, CancellationToken.None).ConfigureAwait(false),
            Verdict.Retry => await _outbox.Retry(row, _time.GetUtcNow() + _options.Policy.Backoff(row.Id, row.Attempts), reason ?? "", CancellationToken.None)
                .ConfigureAwait(false),
            _ => await _outbox.Bury(row, reason ?? "", CancellationToken.None).ConfigureAwait(false),
        };
        var tags = new TagList
        {
            { "tarol.reaction", row.Reaction },
            { "tarol.outcome", verdict switch { Verdict.Done => "completed", Verdict.Retry => "retried", _ => "buried" } },
        };
        _runs.Add(1, tags);
        _duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        if (!kept)
            LeaseLost(_log, row.Reaction, row.Id, row.Attempts);
        else if (verdict == Verdict.Retry)
            Retrying(_log, row.Reaction, row.Id, row.Attempts, reason);
        else if (verdict == Verdict.Bury)
            Buried(_log, row.Reaction, row.Id, row.Attempts, reason);
        return new ReactionAttempt(row, verdict, reason is null ? Option.None : reason, kept);
    }

    private static string Describe(Violation violation) => violation switch
    {
        Error e => $"{e.Type} {e.Code}" + (e.Detail is null ? "" : ": " + e.Detail),
        Invalid i => "Invalid " + string.Join(", ", i.Errors.Select(static f => f.Field + " " + f.Code)),
        _ => violation.ToString(),
    };

    [LoggerMessage(EventId = 9001, Level = LogLevel.Warning, Message = "Reaction {Reaction} (row {RowId}) failed at attempt {Attempt} and will be retried: {Reason}")]
    private static partial void Retrying(ILogger logger, string reaction, long rowId, int attempt, string? reason);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Error, Message = "Reaction {Reaction} (row {RowId}) was given up after attempt {Attempt} and is a dead letter: {Reason}")]
    private static partial void Buried(ILogger logger, string reaction, long rowId, int attempt, string? reason);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Warning, Message = "Reaction {Reaction} (row {RowId}, attempt {Attempt}) outlived its lease; another worker owns the row")]
    private static partial void LeaseLost(ILogger logger, string reaction, long rowId, int attempt);

    [LoggerMessage(EventId = 9012, Level = LogLevel.Warning, Message = "The batch outlived half its lease; {Count} of {Batch} rows were handed back unrun (lower ReactionOptions.Batch or raise Lease)")]
    private static partial void BatchOutlived(ILogger logger, int count, int batch);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Error, Message = "The reaction worker loop failed; it waits and claims again")]
    private static partial void LoopFailed(ILogger logger, Exception exception);
}
