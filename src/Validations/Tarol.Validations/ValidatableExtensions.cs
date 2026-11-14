using Tarol.Results;

namespace Tarol.Validations;

/// <summary>Runs an <see cref="IValidatable" /> and returns the outcome as a <see cref="Result{T}" />.</summary>
public static class ValidatableExtensions
{
    /// <summary>Validates <paramref name="value" />: the value itself when every rule holds, otherwise one <see cref="Invalid" /> with all field errors.</summary>
    /// <typeparam name="T">The input type.</typeparam>
    /// <param name="value">The input.</param>
    /// <returns>The input, or <see cref="Invalid" />. A valid input allocates nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is <see langword="null" />.</exception>
    public static Result<T> Validated<T>(this T value)
        where T : IValidatable
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = default(ValidationErrors);
        value.Validate(ref errors);
        return errors.ToInvalid() is { } invalid ? invalid : value;
    }
}
