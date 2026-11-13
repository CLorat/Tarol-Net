namespace Tarol.Results;

/// <summary>
/// The success case of the non-generic <see cref="Result"/>: an operation that succeeded without a value.
/// Use it as a pattern: <c>result switch { Success =&gt; …, Violation v =&gt; … }</c>.
/// </summary>
public readonly struct Success : IEquatable<Success>
{
    /// <summary>All successes are equal.</summary>
    /// <param name="other">The other success.</param>
    /// <returns>Always <see langword="true"/>.</returns>
    public bool Equals(Success other) => true;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Success;

    /// <inheritdoc />
    public override int GetHashCode() => 0;

    /// <summary>Returns <c>"Success"</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => nameof(Success);

    /// <summary>All successes are equal.</summary>
    /// <param name="left">The first success.</param>
    /// <param name="right">The second success.</param>
    /// <returns>Always <see langword="true"/>.</returns>
    public static bool operator ==(Success left, Success right) => true;

    /// <summary>All successes are equal.</summary>
    /// <param name="left">The first success.</param>
    /// <param name="right">The second success.</param>
    /// <returns>Always <see langword="false"/>.</returns>
    public static bool operator !=(Success left, Success right) => false;
}
