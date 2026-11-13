using Tarol.Options;

namespace Tarol.Results;

/// <summary>
/// <see cref="Option{T}"/> (Tarol.Options) to <see cref="Result{T}"/>: absence becomes a failure. The way back is
/// <see cref="Result{T}.ToOption"/>.
/// </summary>
public static class OptionResultExtensions
{
    /// <summary>Turns absence into a failure: the value, or <paramref name="whenNone"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="option">The option.</param>
    /// <param name="whenNone">The violation when the option is <see cref="None"/>, typically <c>Error.NotFound(…)</c>.</param>
    /// <returns>The result.</returns>
    public static Result<T> ToResult<T>(this Option<T> option, Violation whenNone)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(whenNone);
        return option.TryGetValue(out T? value) ? new Result<T>(value) : new Result<T>(whenNone);
    }

    /// <summary>Turns absence into a failure computed only when the option is <see cref="None"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="option">The option.</param>
    /// <param name="whenNone">Creates the violation.</param>
    /// <returns>The result.</returns>
    public static Result<T> ToResult<T>(this Option<T> option, Func<Violation> whenNone)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(whenNone);
        return option.TryGetValue(out T? value) ? new Result<T>(value) : new Result<T>(whenNone());
    }
}
