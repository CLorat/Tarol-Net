namespace Tarol.Results;

/// <summary>
/// The "absent" case of <see cref="Option{T}"/>. It carries nothing; every <see cref="None"/> is equal.
/// Use it as a pattern: <c>option switch { User user =&gt; …, None =&gt; … }</c>, and create an absent option with
/// <see cref="Option.None"/>.
/// </summary>
public readonly struct None : IEquatable<None>
{
    /// <summary>All <see cref="None"/> values are equal.</summary>
    /// <param name="other">The other value.</param>
    /// <returns>Always <see langword="true"/>.</returns>
    public bool Equals(None other) => true;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is None;

    /// <inheritdoc />
    public override int GetHashCode() => 0;

    /// <summary>Returns <c>"None"</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => nameof(None);

    /// <summary>All <see cref="None"/> values are equal.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Always <see langword="true"/>.</returns>
    public static bool operator ==(None left, None right) => true;

    /// <summary>All <see cref="None"/> values are equal.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Always <see langword="false"/>.</returns>
    public static bool operator !=(None left, None right) => false;
}
