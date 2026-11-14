namespace Tarol.Validations;

/// <summary>The text has between <see cref="Min" /> and <see cref="Max" /> characters (inclusive). Code <see cref="ValidationCodes.Length" />.</summary>
/// <remarks>On a <see cref="string" /> parameter of a value object or an <see cref="IValidatable" /> record. One-sided: <c>[Length(Max = 100)]</c>.</remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class LengthAttribute : Attribute
{
    /// <summary>A one-sided length: set <see cref="Min" /> or <see cref="Max" />.</summary>
    public LengthAttribute()
    {
    }

    /// <summary>A length range.</summary>
    /// <param name="min">The inclusive minimum length.</param>
    /// <param name="max">The inclusive maximum length.</param>
    public LengthAttribute(int min, int max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>The inclusive minimum length (default 0).</summary>
    public int Min { get; set; }

    /// <summary>The inclusive maximum length (default <see cref="int.MaxValue" />).</summary>
    public int Max { get; set; } = int.MaxValue;
}
