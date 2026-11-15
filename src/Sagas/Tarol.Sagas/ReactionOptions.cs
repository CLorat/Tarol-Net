namespace Tarol.Sagas;

/// <summary>How the reaction worker runs (S-10, S-13); the defaults fit most applications.</summary>
public sealed class ReactionOptions
{
    /// <summary>Worker loops in this process (default 4). Any number of processes may run them.</summary>
    public int Workers { get; set; } = 4;

    /// <summary>Rows claimed per round trip (default 32).</summary>
    public int Batch { get; set; } = 32;

    /// <summary>How long a claimed row stays with its worker (default 1 min); longer than the slowest reaction plus clock skew.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long an idle worker waits before it claims again (default 100 ms; with <c>Notify</c> a commit wakes it sooner).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Retries, backoff and dead letters.</summary>
    public DeliveryPolicy Policy { get; set; } = new();

    /// <summary>
    /// Deletes idempotency keys older than a time, in batches (P-11), e.g. <c>o.PurgeKeys = store.PurgeKeys;</c> for the
    /// Npgsql store; <see langword="null"/> = no purge. The host calls it every <see cref="PurgeEvery"/>.
    /// </summary>
    public Func<DateTimeOffset, int, CancellationToken, Task<int>>? PurgeKeys { get; set; }

    /// <summary>How long keys are kept (default 7 days): a redelivery after it commits again.</summary>
    public TimeSpan KeyRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often the host purges keys (default 1 h).</summary>
    public TimeSpan PurgeEvery { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Keys deleted per purge statement (default 1,000).</summary>
    public int PurgeBatch { get; set; } = 1000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Workers, 1, nameof(Workers));
        ArgumentOutOfRangeException.ThrowIfLessThan(Batch, 1, nameof(Batch));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Lease, TimeSpan.Zero, nameof(Lease));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(PollInterval));
        ArgumentNullException.ThrowIfNull(Policy, nameof(Policy));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(KeyRetention, TimeSpan.Zero, nameof(KeyRetention));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PurgeEvery, TimeSpan.Zero, nameof(PurgeEvery));
        ArgumentOutOfRangeException.ThrowIfLessThan(PurgeBatch, 1, nameof(PurgeBatch));
    }
}
