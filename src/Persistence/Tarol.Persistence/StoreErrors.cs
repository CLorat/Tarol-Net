using Tarol.Results;

namespace Tarol.Persistence;

/// <summary>The failures an <see cref="IAggregateStore"/> append returns (all <see cref="ErrorType.Conflict"/>, HTTP 409).</summary>
public static class StoreErrors
{
    /// <summary>The code of a version conflict.</summary>
    public const string VersionConflictCode = "Stream.VersionConflict";

    /// <summary>The code of a key that was stored meanwhile.</summary>
    public const string DuplicateKeyCode = "Idempotency.DuplicateKey";

    /// <summary>The code of a unique lookup key that belongs to another aggregate.</summary>
    public const string LookupTakenCode = "Lookup.Taken";

    /// <summary>The code of <see cref="KeyReused"/>.</summary>
    public const string KeyReusedCode = "Idempotency.KeyReused";

    /// <summary>Someone else committed to the stream first (or it exists, for an expected version of 0).</summary>
    /// <param name="expected">The expected version.</param>
    /// <param name="actual">The stream's version.</param>
    /// <returns>The error.</returns>
    public static Error VersionConflict(long expected, long actual)
        => Error.Conflict(VersionConflictCode, $"expected version {expected}, found {actual}");

    /// <summary>The idempotency key was stored by a concurrent commit.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The error.</returns>
    public static Error DuplicateKey(IdempotencyKey key) => Error.Conflict(DuplicateKeyCode, $"{key.Scope}: {key.Key}");

    /// <summary>A unique lookup key belongs to another aggregate.</summary>
    /// <param name="lookup">The lookup.</param>
    /// <param name="key">The key.</param>
    /// <returns>The error.</returns>
    public static Error LookupTaken(string lookup, string key) => Error.Conflict(LookupTakenCode, $"{lookup}: {key}");

    /// <summary>An idempotency key presented again for a different request (another fingerprint): 422, nothing replayed.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The error.</returns>
    public static Error KeyReused(IdempotencyKey key) => Error.UnprocessableEntity(KeyReusedCode, $"{key.Scope}: {key.Key}");

    /// <summary>Whether <paramref name="result"/> is a version conflict (the caller may reload and decide again).</summary>
    /// <param name="result">An append result.</param>
    /// <returns><see langword="true"/> for <see cref="VersionConflictCode"/>.</returns>
    public static bool IsVersionConflict(Result result)
        => result.TryGetValue(out Violation? violation) && violation is Error { Type: ErrorType.Conflict, Code: VersionConflictCode };
}
