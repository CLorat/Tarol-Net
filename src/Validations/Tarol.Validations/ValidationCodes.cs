namespace Tarol.Validations;

/// <summary>The <see cref="Tarol.Results.FieldError.Code" /> values produced by the built-in rules.</summary>
/// <remarks>
///     The code names the rule, the field names the path; a code never repeats the path. Limits are in the error's
///     <see cref="Tarol.Results.FieldError.Detail" /> (<c>"3..10"</c>, <c>"0.."</c>, <c>"..100"</c>).
/// </remarks>
public static class ValidationCodes
{
    /// <summary>The value is missing: <see langword="null" />, <c>Id&lt;T&gt;.Empty</c>, <see cref="Guid.Empty" />, or blank for <see cref="NotEmptyAttribute" />.</summary>
    public const string Required = "Required";

    /// <summary>A number outside <see cref="RangeAttribute" />.</summary>
    public const string Range = "Range";

    /// <summary>A text whose length is outside <see cref="LengthAttribute" />.</summary>
    public const string Length = "Length";

    /// <summary>A text that does not match <see cref="PatternAttribute" />, or whose match timed out.</summary>
    public const string Pattern = "Pattern";

    /// <summary>A text that is not an e-mail address (<see cref="EmailAttribute" />).</summary>
    public const string Email = "Email";

    /// <summary>A collection whose number of elements is outside <see cref="CountAttribute" />.</summary>
    public const string Count = "Count";
}
