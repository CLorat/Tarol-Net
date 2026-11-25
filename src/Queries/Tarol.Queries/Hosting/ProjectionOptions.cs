namespace Tarol.Queries;

/// <summary>How a <see cref="ProjectionHost"/> reads the feed; the defaults are the measured ones (queries.md §4.1, §4.3).</summary>
public sealed class ProjectionOptions
{
    /// <summary>Events read per batch (default 2,000: 133k events/s on catch-up). Rows and checkpoint are written once per batch.</summary>
    public int Batch { get; set; } = 2000;

    /// <summary>
    /// How long an idle host waits before it reads again. <see langword="null"/> (default): 1 s with an
    /// <see cref="IFeedSignal"/> (a NOTIFY wakes it sooner; the poll catches commits the xmin horizon held back), 100 ms without.
    /// </summary>
    public TimeSpan? Poll { get; set; }

    /// <summary>The first wait after a failed batch (default 100 ms); doubled per failure up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan Backoff { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait after failures (default 30 s).</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an erasure waits for this projection, when its host in this process is still building, before it re-folds the
    /// subject's streams (default 1 min; TN-25, Tatalu FB-F17.5-4): a batch read before the key was erased must be written first.
    /// </summary>
    public TimeSpan ErasureWait { get; set; } = TimeSpan.FromMinutes(1);

    internal TimeSpan PollFor(bool signalled) => Poll ?? (signalled ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(100));

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Batch, 1, nameof(Batch));
        if (Poll is { } poll)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(poll, TimeSpan.Zero, nameof(Poll));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Backoff, TimeSpan.Zero, nameof(Backoff));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBackoff, Backoff, nameof(MaxBackoff));
        ArgumentOutOfRangeException.ThrowIfLessThan(ErasureWait, TimeSpan.Zero, nameof(ErasureWait));
    }

    internal ProjectionOptions Copy() => new() { Batch = Batch, Poll = Poll, Backoff = Backoff, MaxBackoff = MaxBackoff, ErasureWait = ErasureWait };
}

/// <summary>
/// Wakes idle projection hosts when the store committed (the Npgsql store's opt-in feed NOTIFY, step 12B). Optional: without
/// it the hosts poll every 100 ms.
/// </summary>
public interface IFeedSignal
{
    /// <summary>Returns when a commit may have happened, or after <paramref name="max"/>.</summary>
    /// <param name="max">The longest wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task.</returns>
    ValueTask Wait(TimeSpan max, CancellationToken cancellationToken);
}
