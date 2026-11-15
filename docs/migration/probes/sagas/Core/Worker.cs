using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Tarol.Changes;
using Tarol.Results;

namespace Tarol.Sagas;

/// <summary>What the worker does with a row.</summary>
public enum Verdict { Done, Retry, Bury }

/// <summary>C-9 delivery policy: changes.md §6 table, exponential backoff with deterministic jitter, dead letter after N.</summary>
public sealed record DeliveryPolicy
{
    public int MaxAttempts { get; init; } = 10;
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The pipeline's answer → the verdict (before the attempt limit).</summary>
    public static Verdict Classify(Result<ChangeOutcome> result)
    {
        if (result.IsSuccess)
            return Verdict.Done;                                                  // committed, or a replayed key
        _ = result.TryGetValue(out Violation? violation);
        return violation switch
        {
            Invalid => Verdict.Bury,                                              // mapping bug or contract change
            Error { Type: ErrorType.Conflict } => Verdict.Retry,                  // C-9: guard or version, both retry
            Error { Type: ErrorType.NotFound or ErrorType.UnprocessableEntity } => Verdict.Retry,   // ordering across streams (S-?)
            Error { Type: ErrorType.Unavailable or ErrorType.Timeout or ErrorType.TooManyRequests or ErrorType.Internal } => Verdict.Retry,
            _ => Verdict.Bury,                                                    // Forbidden, Unauthorized, Gone
        };
    }

    /// <summary>Delay before attempt <paramref name="attempt"/> + 1: 1 s, 2 s, 4 s … capped, ±20 % jitter from the row id.</summary>
    public TimeSpan Backoff(long rowId, int attempt)
    {
        var exponential = FirstDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);
        var jitter = 0.8 + 0.4 * (Hash(rowId, attempt) / (double)uint.MaxValue);   // deterministic: tests can predict it
        return TimeSpan.FromMilliseconds(capped * jitter);
    }

    private static uint Hash(long id, int attempt)
    {
        unchecked
        {
            var h = (ulong)id * 0x9E3779B97F4A7C15UL ^ (ulong)attempt * 0xC2B2AE3D27D4EB4FUL;
            h ^= h >> 31;
            return (uint)(h * 0x94D049BB133111EBUL >> 32);
        }
    }
}

/// <summary>Worker options.</summary>
public sealed record WorkerOptions
{
    public int Batch { get; init; } = 32;
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public DeliveryPolicy Policy { get; init; } = new();
}

/// <summary>One worker loop over an <see cref="IReactionOutbox"/>: claim → run through the pipeline → complete / retry / bury.</summary>
public sealed class ReactionWorker(IReactionOutbox outbox, ReactionSet reactions, ChangePipeline pipeline, TimeProvider time, WorkerOptions options)
{
    public static readonly ActivitySource Source = new("Tarol.Sagas");

    /// <summary>Called after each row (metrics in the probe).</summary>
    public Action<ClaimedRow, Verdict, bool>? Observed { get; init; }

    /// <summary>Waits for work: a poll delay, or a LISTEN/NOTIFY wake-up with the poll as the fallback.</summary>
    public Func<CancellationToken, Task>? Wait { get; init; }

    /// <summary>Claims one batch and handles it; returns how many rows it handled.</summary>
    public async Task<int> RunOnce(CancellationToken ct)
    {
        var rows = await outbox.Claim(options.Batch, options.Lease, ct);
        foreach (var row in rows)
            await Handle(row, ct);
        return rows.Count;
    }

    /// <summary>Drains until nothing is due (tests: event → reaction → change → its reactions …).</summary>
    public async Task<int> Drain(CancellationToken ct)
    {
        var total = 0;
        for (int n; (n = await RunOnce(ct)) > 0;)
            total += n;
        return total;
    }

    public async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int handled;
            try
            {
                handled = await RunOnce(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;                                                           // claimed rows come back when the lease ends
            }
            if (handled == 0)
            {
                try { await (Wait ?? (c => Task.Delay(options.PollInterval, time, c)))(ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task Handle(ClaimedRow row, CancellationToken ct)
    {
        ActivityLink[]? links = row.Traceparent is { } tp && ActivityContext.TryParse(tp, null, out var origin) ? [new ActivityLink(origin)] : null;
        using var activity = Source.StartActivity(ActivityKind.Consumer, default(ActivityContext), links: links, name: "reaction " + row.Reaction);
        activity?.SetTag("tarol.reaction", row.Reaction);
        activity?.SetTag("tarol.stream.type", row.StreamType);
        activity?.SetTag("tarol.stream.id", row.Event.StreamId.ToString());
        activity?.SetTag("messaging.message.id", row.Event.EventId.ToString());
        activity?.SetTag("tarol.attempt", row.Attempts);

        Verdict verdict;
        string error;
        try
        {
            var result = await reactions.Run(pipeline, row.Reaction, row.Event, ct);
            verdict = DeliveryPolicy.Classify(result);
            error = result.TryGetValue(out Violation? v) ? v switch { Error e => $"{e.Type} {e.Code}", Invalid i => "Invalid " + string.Join(",", i.Errors.Select(f => f.Field + ":" + f.Code)), _ => v.ToString() } : "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            verdict = Verdict.Retry;
            error = ex.GetType().Name + ": " + ex.Message;
        }
        if (verdict == Verdict.Retry && row.Attempts >= options.Policy.MaxAttempts)
            verdict = Verdict.Bury;
        activity?.SetTag("tarol.verdict", verdict.ToString());
        if (verdict != Verdict.Done)
            activity?.SetStatus(ActivityStatusCode.Error, error);

        var kept = verdict switch
        {
            Verdict.Done => await outbox.Complete(row, ct),
            Verdict.Retry => await outbox.Retry(row, time.GetUtcNow() + options.Policy.Backoff(row.Id, row.Attempts), error, ct),
            _ => await outbox.Bury(row, error, ct),
        };
        Observed?.Invoke(row, verdict, kept);                                      // kept = false: the lease was lost meanwhile
    }
}

/// <summary>One LISTEN connection that wakes every worker of this process (NOTIFY from the outbox insert trigger).</summary>
public sealed class OutboxWake(NpgsqlDataSource db, string channel, TimeSpan fallback, TimeProvider time) : IAsyncDisposable
{
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public async Task Start()
    {
        var connection = await db.OpenConnectionAsync(_stop.Token);
        connection.Notification += (_, _) => Interlocked.Exchange(ref _signal, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        await using (var listen = new NpgsqlCommand($"LISTEN {channel}", connection))
            await listen.ExecuteNonQueryAsync(_stop.Token);
        _loop = Task.Run(async () =>
        {
            await using var _ = connection;
            try
            {
                while (!_stop.IsCancellationRequested)
                    await connection.WaitAsync(_stop.Token);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Completes at the next NOTIFY, or after the fallback poll interval.</summary>
    public Task Wait(CancellationToken ct) => Task.WhenAny(Volatile.Read(ref _signal).Task, Task.Delay(fallback, time, ct));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
            await _loop;
        _stop.Dispose();
    }
}

/// <summary>The hosted service (question 7): N worker loops and the key purge (P-11), no Aspire dependency (D-15).</summary>
public sealed class ReactionHost(Func<int, ReactionWorker> worker, int workers, Func<CancellationToken, Task<int>>? purgeKeys, TimeSpan purgeEvery, TimeProvider time)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = Enumerable.Range(0, workers).Select(i => worker(i).Run(stoppingToken)).ToList();
        if (purgeKeys is not null)
            loops.Add(Purge(stoppingToken));
        return Task.WhenAll(loops);
    }

    private async Task Purge(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (await purgeKeys!(ct) > 0) { }
                await Task.Delay(purgeEvery, time, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }
}
