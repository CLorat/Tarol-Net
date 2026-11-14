namespace Tarol.Validations;

/// <summary>The collection has between <see cref="Min" /> and <see cref="Max" /> elements (inclusive). Code <see cref="ValidationCodes.Count" />.</summary>
/// <remarks>On an array or <c>IReadOnlyList&lt;T&gt;</c> parameter of an <see cref="IValidatable" /> record or a value object. One-sided: <c>[Count(Min = 1)]</c>.</remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class CountAttribute : Attribute
{
    /// <summary>A one-sided count: set <see cref="Min" /> or <see cref="Max" />.</summary>
    public CountAttribute()
    {
    }

    /// <summary>A count range.</summary>
    /// <param name="min">The inclusive minimum number of elements.</param>
    /// <param name="max">The inclusive maximum number of elements.</param>
    public CountAttribute(int min, int max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>The inclusive minimum number of elements (default 0).</summary>
    public int Min { get; set; }

    /// <summary>The inclusive maximum number of elements (default <see cref="int.MaxValue" />).</summary>
    public int Max { get; set; } = int.MaxValue;
}
