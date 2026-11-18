using Tarol.Results;

namespace Tarol.Persistence;

/// <summary>The failures of data protection (DP-3).</summary>
public static class DataProtectionErrors
{
    /// <summary>The code of <see cref="SubjectErased"/>.</summary>
    public const string SubjectErasedCode = "DataProtection.SubjectErased";

    /// <summary>New personal data for an erased subject is refused (409); a returning person is a new subject.</summary>
    /// <param name="subject">The erased subject.</param>
    /// <returns>The error.</returns>
    public static Error SubjectErased(Guid subject) => Error.Conflict(SubjectErasedCode, $"subject {subject} is erased");
}
