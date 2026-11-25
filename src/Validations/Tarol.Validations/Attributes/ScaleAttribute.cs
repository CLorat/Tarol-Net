namespace Tarol.Validations;

/// <summary>
/// A <see cref="decimal"/> has at most <see cref="Digits"/> digits after the decimal point (TN-53): <c>[Scale(2)]</c> for an
/// amount of money (12.50 passes, 12.505 is refused with code <c>Scale</c>, detail <c>2</c>). The value stays a JSON number;
/// the OpenAPI schema says <c>multipleOf: 0.01</c>, which tarol-codegen turns into the client rule <c>Scale</c>.
/// </summary>
/// <example><code>public readonly partial record struct Euro([Range(Min = 0), Scale(2)] decimal Value);</code></example>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ScaleAttribute : Attribute
{
    /// <summary>Allows <paramref name="digits"/> digits after the decimal point.</summary>
    /// <param name="digits">0 to 28.</param>
    public ScaleAttribute(int digits) => Digits = digits;

    /// <summary>The digits allowed after the decimal point.</summary>
    public int Digits { get; }
}
