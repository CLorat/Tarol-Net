using System.Numerics;
using System.Text.RegularExpressions;

namespace Tarol.Validations;

/// <summary>
///     The checks behind the rule attributes. Generated code calls them; hand-written factories and custom rules may too.
///     Every method is pure and allocation-free.
/// </summary>
public static class Rules
{
    /// <summary>Whether <paramref name="value" /> lies in <c>[min, max]</c>. <see cref="double.NaN" /> is never in range.</summary>
    /// <typeparam name="T">A comparable number type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="min">The inclusive lower bound.</param>
    /// <param name="max">The inclusive upper bound.</param>
    /// <returns><see langword="true" /> when <c>min &lt;= value &lt;= max</c>.</returns>
    public static bool InRange<T>(T value, T min, T max)
        where T : IComparisonOperators<T, T, bool>
        => value >= min && value <= max;

    /// <summary>Whether the length of <paramref name="value" /> lies in <c>[min, max]</c>. <see langword="null" /> has no length.</summary>
    /// <param name="value">The text.</param>
    /// <param name="min">The inclusive minimum length.</param>
    /// <param name="max">The inclusive maximum length.</param>
    /// <returns><see langword="true" /> when the text is not <see langword="null" /> and its length is in range.</returns>
    public static bool Length(string? value, int min, int max)
        => value is not null && value.Length >= min && value.Length <= max;

    /// <summary>Whether <paramref name="value" /> contains a character other than white space.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="false" /> for <see langword="null" />, empty and white-space-only text.</returns>
    public static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    ///     A structural e-mail check without a regular expression: one <c>@</c> with 1–64 characters before it, a domain of at
    ///     most 253 characters that contains a dot, does not start or end with one and has no empty label, no white space,
    ///     at most 254 characters in all. It does not prove the address exists.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true" /> when the text has the shape of an e-mail address.</returns>
    public static bool Email(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value.Length > 254)
            return false;

        var at = value.IndexOf('@');
        if (at < 1 || at > 64 || value[(at + 1)..].Contains('@'))
            return false;

        var domain = value[(at + 1)..];
        if (domain.IsEmpty || domain.Length > 253 || domain[0] == '.' || domain[^1] == '.' || !domain.Contains('.'))
            return false;

        if (domain.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Whether <paramref name="value" /> matches <paramref name="regex" />. A match that exceeds the regex's timeout is
    ///     <see langword="false" />, never a <see cref="RegexMatchTimeoutException" />: a crafted input becomes a field
    ///     error, not a server error.
    /// </summary>
    /// <param name="regex">The pattern; use <c>[GeneratedRegex]</c> with a timeout.</param>
    /// <param name="value">The text; <see langword="null" /> never matches.</param>
    /// <returns><see langword="true" /> when the text matches in time.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="regex" /> is <see langword="null" />.</exception>
    public static bool Matches(Regex regex, string? value)
    {
        ArgumentNullException.ThrowIfNull(regex);
        if (value is null)
            return false;

        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
