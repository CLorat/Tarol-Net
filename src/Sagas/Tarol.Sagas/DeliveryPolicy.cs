using Tarol.Changes;
using Tarol.Results;

namespace Tarol.Sagas;

/// <summary>What the worker does with a row after running its reaction.</summary>
public enum Verdict
{
    /// <summary>Committed, or a replayed key: the row is completed.</summary>
    Done,

    /// <summary>Transient: the row is due again after the backoff and stays the head of its stream.</summary>
    Retry,

    /// <summary>Given up: the row becomes a dead letter for a human, and the next row of its stream runs.</summary>
    Bury,
}

/// <summary>
/// The delivery policy of reactions (C-9, S-7, S-13; docs/architecture/sagas.md §3): any <c>Error.Conflict</c>, a
/// <c>NotFound</c> or a missing <c>[Read]</c> (422), an unavailable dependency or an exception retries with exponential
/// backoff; <c>Invalid</c>, <c>Forbidden</c>, <c>Unauthorized</c> and <c>Gone</c> are dead-lettered at once; a retry at
/// <see cref="MaxAttempts"/> is dead-lettered.
/// </summary>
public sealed record DeliveryPolicy
{
    private readonly int _maxAttempts = 10;
    private readonly TimeSpan _firstDelay = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _maxDelay = TimeSpan.FromMinutes(5);

    /// <summary>The attempt at which a retry becomes a dead letter (default 10: about 12 minutes of retries).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int MaxAttempts
    {
        get => _maxAttempts;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxAttempts = value;
        }
    }

    /// <summary>The delay after the first failed attempt (default 1 s); it doubles per attempt.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive.</exception>
    public TimeSpan FirstDelay
    {
        get => _firstDelay;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _firstDelay = value;
        }
    }

    /// <summary>The longest delay (default 5 min).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive.</exception>
    public TimeSpan MaxDelay
    {
        get => _maxDelay;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _maxDelay = value;
        }
    }

    /// <summary>The verdict for the pipeline's answer, before the attempt limit.</summary>
    /// <param name="result">What <see cref="ReactionSet.Run"/> returned.</param>
    /// <returns><see cref="Verdict.Done"/>, <see cref="Verdict.Retry"/> or <see cref="Verdict.Bury"/>.</returns>
    public static Verdict Classify(Result<ChangeOutcome> result)
    {
        if (!result.TryGetValue(out Violation? violation))
            return Verdict.Done;
        return violation switch
        {
            Invalid => Verdict.Bury,                                              // a mapping bug or a contract change
            Error { Type: ErrorType.Conflict } => Verdict.Retry,                  // C-9: guards and versions alike
            Error { Type: ErrorType.NotFound or ErrorType.UnprocessableEntity } => Verdict.Retry,   // S-7: ordering across streams
            Error { Type: ErrorType.Unavailable or ErrorType.Timeout or ErrorType.TooManyRequests or ErrorType.Internal } => Verdict.Retry,
            _ => Verdict.Bury,                                                    // Forbidden, Unauthorized, Gone
        };
    }

    /// <summary>
    /// The delay after attempt <paramref name="attempt"/>: <see cref="FirstDelay"/> · 2^(attempt − 1), at most
    /// <see cref="MaxDelay"/>, with ±20 % jitter derived from (row, attempt), so it spreads retries yet is predictable in tests.
    /// </summary>
    /// <param name="rowId">The outbox row.</param>
    /// <param name="attempt">The attempt that failed (1 for the first).</param>
    /// <returns>The delay.</returns>
    public TimeSpan Backoff(long rowId, int attempt)
    {
        var exponential = FirstDelay.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        var capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);
        var jitter = 0.8 + 0.4 * (Mix(rowId, attempt) / (double)uint.MaxValue);
        return TimeSpan.FromMilliseconds(capped * jitter);
    }

    private static uint Mix(long id, int attempt)
    {
        unchecked
        {
            var h = ((ulong)id * 0x9E3779B97F4A7C15UL) ^ ((ulong)attempt * 0xC2B2AE3D27D4EB4FUL);
            h ^= h >> 31;
            return (uint)((h * 0x94D049BB133111EBUL) >> 32);
        }
    }
}
