using Tarol.Results;

namespace Tarol.Changes;

/// <summary>What an at-least-once entry does with a delivery after the pipeline answered.</summary>
public enum Verdict
{
    /// <summary>Committed, or a replayed key: acknowledge (a reaction's row is completed, a webhook answers 200).</summary>
    Done,

    /// <summary>Transient: deliver again after a backoff (a reaction's row is due again, a webhook answers 503).</summary>
    Retry,

    /// <summary>Give up: a dead letter for a human (a reaction's row moves to the dead letters, a webhook answers 422).</summary>
    Bury,
}

/// <summary>
/// The delivery classification every at-least-once entry shares (C-9, S-7; docs/architecture/changes.md §6): the reaction
/// worker (<c>Tarol.Sagas</c>), webhooks and consumers. Any <c>Error.Conflict</c>, a <c>NotFound</c> or a missing
/// <c>[Read]</c> (422), an unavailable dependency or an internal error retries; <c>Invalid</c>, <c>Forbidden</c>,
/// <c>Unauthorized</c> and <c>Gone</c> are buried at once.
/// </summary>
public static class Delivery
{
    /// <summary>The verdict for the pipeline's answer (before an entry's attempt limit).</summary>
    /// <param name="result">What <see cref="ChangePipeline.Run{T}(IChange{T}, Kernel.OperationContext, ChangeOptions, CancellationToken)"/> returned.</param>
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
}
