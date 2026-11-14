using Tarol.Results;

namespace Tarol.Changes;

/// <summary>What a reaction, webhook or consumer does with a delivery once the pipeline answered.</summary>
public enum Delivery
{
    /// <summary>Done (also: a replayed key, or a guard that says the change no longer applies).</summary>
    Acknowledge,
    /// <summary>Transient: deliver again later with backoff (the pipeline already retried version conflicts).</summary>
    Retry,
    /// <summary>Permanent: park it for a human (a bug, a bad contract, a denied actor).</summary>
    DeadLetter,
}

public static class DeliveryPolicy
{
    /// <summary>The default mapping of a pipeline answer to a delivery decision.</summary>
    /// <param name="notFoundIsTransient">true for foreign events (ordering across systems); false for own events after commit.</param>
    public static (Delivery Decision, string Reason) Decide(Result<ChangeOutcome> result, bool notFoundIsTransient)
    {
        if (result.TryGetValue(out ChangeOutcome outcome))
            return (Delivery.Acknowledge, outcome.Replayed ? "duplicate delivery" : "committed");
        result.TryGetValue(out Violation? violation);
        return violation switch
        {
            Invalid => (Delivery.DeadLetter, "input built from the event is invalid: mapping bug or contract change"),
            Error { Type: ErrorType.Conflict, Code: "Stream.VersionConflict" } => (Delivery.Retry, "still contended after the pipeline's retries"),
            Error { Type: ErrorType.Conflict } e => (Delivery.Acknowledge, $"rejected by a guard ({e.Code}): the state moved on; recorded, not retried"),
            Error { Type: ErrorType.NotFound } when notFoundIsTransient => (Delivery.Retry, "target not there yet (ordering)"),
            Error { Type: ErrorType.NotFound } => (Delivery.DeadLetter, "own event points at a missing aggregate"),
            Error { Type: ErrorType.Forbidden or ErrorType.Unauthorized } => (Delivery.DeadLetter, "the system actor is not allowed"),
            Error { Type: ErrorType.Unavailable or ErrorType.Timeout or ErrorType.TooManyRequests } => (Delivery.Retry, "transient"),
            _ => (Delivery.DeadLetter, "internal"),
        };
    }
}
