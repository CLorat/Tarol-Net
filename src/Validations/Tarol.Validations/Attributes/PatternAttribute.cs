using System.Diagnostics.CodeAnalysis;

namespace Tarol.Validations;

/// <summary>The text matches a regular expression. Code <see cref="ValidationCodes.Pattern" />.</summary>
/// <remarks>
///     The generator emits a <c>[GeneratedRegex]</c> (compiled ahead of time, also under Native AOT) with
///     <see cref="System.Text.RegularExpressions.RegexOptions.CultureInvariant" /> and a timeout of
///     <see cref="TimeoutMilliseconds" />. A match that times out is a <see cref="ValidationCodes.Pattern" /> error, not an
///     exception. A pattern that does not parse is TVAL005.
/// </remarks>
/// <param name="pattern">The regular expression; anchor it (<c>^…$</c>) to match the whole text.</param>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class PatternAttribute([StringSyntax(StringSyntaxAttribute.Regex)] string pattern) : Attribute
{
    /// <summary>The regular expression.</summary>
    public string Pattern { get; } = pattern;

    /// <summary>The match timeout in milliseconds (default 100).</summary>
    public int TimeoutMilliseconds { get; set; } = 100;

    /// <summary>Whether the match ignores case (invariant culture).</summary>
    public bool IgnoreCase { get; set; }
}
