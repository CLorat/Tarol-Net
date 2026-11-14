namespace Tarol.Validations;

/// <summary>
///     The number lies in <c>[Min, Max]</c> (both inclusive). Code <see cref="ValidationCodes.Range" />.
/// </summary>
/// <remarks>
///     <para>
///         On a primary-constructor parameter of a value object or an <see cref="IValidatable" /> record whose type is a
///         number (<see cref="int" />, <see cref="long" />, <see cref="decimal" />, <see cref="double" />, …, or the
///         nullable form). The bounds are converted to the parameter's type at compile time; a bound the type cannot hold
///         is TVAL005.
///     </para>
///     <para>
///         Decimal bounds are written as invariant strings (<c>[Range("0.01", "999.99")]</c>), because C# attributes
///         cannot take <see cref="decimal" /> arguments. One-sided: <c>[Range(Min = 0)]</c> or <c>[Range(Max = 100)]</c>.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class RangeAttribute : Attribute
{
    /// <summary>A one-sided range: set <see cref="Min" /> or <see cref="Max" />.</summary>
    public RangeAttribute()
    {
    }

    /// <summary>An integer range.</summary>
    /// <param name="min">The inclusive lower bound.</param>
    /// <param name="max">The inclusive upper bound.</param>
    public RangeAttribute(long min, long max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>A floating-point range.</summary>
    /// <param name="min">The inclusive lower bound.</param>
    /// <param name="max">The inclusive upper bound.</param>
    public RangeAttribute(double min, double max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>A range given as invariant number strings (for <see cref="decimal" />).</summary>
    /// <param name="min">The inclusive lower bound, e.g. <c>"0.01"</c>.</param>
    /// <param name="max">The inclusive upper bound.</param>
    public RangeAttribute(string min, string max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>The inclusive lower bound (a number or an invariant number string), or <see langword="null" /> for none.</summary>
    public object? Min { get; set; }

    /// <summary>The inclusive upper bound (a number or an invariant number string), or <see langword="null" /> for none.</summary>
    public object? Max { get; set; }
}
