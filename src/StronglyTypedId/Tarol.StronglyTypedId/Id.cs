using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Tarol.StronglyTypedId;

/// <summary>
/// A zero-allocation, strongly-typed wrapper around a <see cref="Guid"/>.
/// <para>
/// This struct uses <see cref="MethodImplOptions.AggressiveInlining"/> to ensure
/// that at runtime it performs exactly the same as a raw <see cref="Guid"/>.
/// </para>
/// </summary>
/// <typeparam name="T">
/// A "phantom type" marker used strictly for compile-time safety.
/// It differentiates <c>Id&lt;User&gt;</c> from <c>Id&lt;Order&gt;</c>.
/// </typeparam>
[DebuggerDisplay("{Value} ({typeof(T).Name,nq})")]
// System.Text.Json closes the open generic converter for every Id<X> it meets: the source generator
// emits `new IdJsonConverter<X>()` into the JsonSerializerContext, the reflection resolver closes it
// at run time. No factory, no registry and no MakeGenericType on the AOT path (verified on .NET 11,
// see docs/migration/stronglytypedid.md).
[JsonConverter(typeof(IdJsonConverter<>))]
// NOTE: deliberately NO [TypeConverter(typeof(IdTypeConverter<>))] here. TypeDescriptor cannot close an
// open generic converter type. Closed IdTypeConverter<X> instances are registered by the bundled source
// generator through IdConverterRegistry.Register<X>() when the declaring assembly loads.
public readonly record struct Id<T> :
    IStronglyTypedId,
    IComparable, // non-generic sorting (DataView, grids, LINQ-to-objects boxing paths)
    IComparable<Id<T>>,
    IComparisonOperators<Id<T>, Id<T>, bool>, // generic math constraints (where TId : IComparisonOperators<...>)
    ISpanParsable<Id<T>>, // also brings IParsable<T>: minimal API and MVC route/query binding
    IUtf8SpanParsable<Id<T>>,
    ISpanFormattable,
    IUtf8SpanFormattable
{
    /// <summary>
    /// Represents an empty ID (00000000-0000-0000-0000-000000000000).
    /// </summary>
    public static readonly Id<T> Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="Id{T}"/> struct.
    /// Internal to force usage of safe factories like <c>Id&lt;T&gt;.New()</c> or <see cref="Parse(string, IFormatProvider?)"/>.
    /// </summary>
    /// <param name="value">The raw Guid value.</param>
    internal Id(Guid value) => Value = value;

    /// <inheritdoc />
    int IComparable.CompareTo(object? obj)
    {
        return obj switch
        {
            null => 1,
            Id<T> other => CompareTo(other),
            _ => throw new ArgumentException($"Object must be of type {typeof(Id<T>)}.", nameof(obj))
        };
    }

    // --- High-Performance Wrapper Methods ---

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Id<T> other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Id<T> other) => Value.Equals(other.Value);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ToString([StringSyntax(StringSyntaxAttribute.GuidFormat)] string? format,
        IFormatProvider? formatProvider)
    {
        return Value.ToString(format, formatProvider);
    }

    // --- Span Formatting (Zero Allocation) ---

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFormat(Span<char> destination, out int charsWritten,
        [StringSyntax(StringSyntaxAttribute.GuidFormat)]
        ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        return Value.TryFormat(destination, out charsWritten, format);
    }

    // --- Parsing Logic ---

    /// <summary>
    ///     Parses a string into a strongly-typed <see cref="Id{T}" />.
    /// </summary>
    /// <param name="s">The string to parse.</param>
    /// <param name="provider">An object that supplies culture-specific formatting information. Ignored by Guid parsing.</param>
    /// <returns>The result of parsing <paramref name="s" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="s" /> is <c>null</c>.</exception>
    /// <exception cref="FormatException"><paramref name="s" /> is not in a recognized Guid format.</exception>
    [StackTraceHidden]
    public static Id<T> Parse(string s, IFormatProvider? provider)
    {
        return new Id<T>(Guid.Parse(s, provider));
    }

    /// <summary>
    ///     Tries to parse a string into a strongly-typed <see cref="Id{T}" />.
    /// </summary>
    /// <param name="s">The string to parse.</param>
    /// <param name="provider">An object that supplies culture-specific formatting information. Ignored by Guid parsing.</param>
    /// <param name="result">
    ///     When this method returns, contains the result of successfully parsing <paramref name="s" />, or
    ///     <see cref="Empty" /> on failure.
    /// </param>
    /// <returns><c>true</c> if <paramref name="s" /> was successfully parsed; otherwise, <c>false</c>.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Id<T> result)
    {
        if (Guid.TryParse(s, provider, out var guid))
        {
            result = new Id<T>(guid);
            return true;
        }

        result = Empty;
        return false;
    }

    /// <summary>
    ///     Parses a span of characters into a strongly-typed <see cref="Id{T}" />.
    /// </summary>
    /// <param name="s">The span of characters to parse.</param>
    /// <param name="provider">An object that supplies culture-specific formatting information. Ignored by Guid parsing.</param>
    /// <returns>The result of parsing <paramref name="s" />.</returns>
    [StackTraceHidden]
    public static Id<T> Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
    {
        return new Id<T>(Guid.Parse(s, provider));
    }

    /// <summary>
    ///     Tries to parse a span of characters into a strongly-typed <see cref="Id{T}" />.
    /// </summary>
    /// <param name="s">The span of characters to parse.</param>
    /// <param name="provider">An object that supplies culture-specific formatting information. Ignored by Guid parsing.</param>
    /// <param name="result">
    ///     When this method returns, contains the result of successfully parsing <paramref name="s" />, or
    ///     <see cref="Empty" /> on failure.
    /// </param>
    /// <returns><c>true</c> if <paramref name="s" /> was successfully parsed; otherwise, <c>false</c>.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Id<T> result)
    {
        if (Guid.TryParse(s, provider, out var guid))
        {
            result = new Id<T>(guid);
            return true;
        }

        result = Empty;
        return false;
    }

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten,
        [StringSyntax(StringSyntaxAttribute.GuidFormat)]
        ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        return Value.TryFormat(utf8Destination, out bytesWritten, format);
    }

    /// <summary>
    ///     Determines whether the underlying <see cref="Guid" /> equals the specified <see cref="Guid" />.
    ///     <para>
    ///         Note: this type deliberately does NOT implement <see cref="IEquatable{Guid}" />.
    ///         Doing so would create asymmetric equality (<c>id.Equals(guid)</c> true, but
    ///         <c>guid.Equals(id)</c> false), which misbehaves in generic code. This method
    ///         exists purely as a direct-call convenience.
    ///     </para>
    /// </summary>
    /// <param name="other">The raw Guid to compare against.</param>
    /// <returns><c>true</c> if the values are equal; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Guid other)
    {
        return Value.Equals(other);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => Value.GetHashCode();

    // --- Operators ---

    /// <summary>
    /// Explicitly converts a <see cref="Guid"/> to a strongly-typed <see cref="Id{T}"/>.
    /// Kept explicit on purpose: an implicit conversion would silently reintroduce
    /// the primitive-obsession bugs this type exists to prevent.
    /// </summary>
    /// <param name="value">The raw Guid value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static explicit operator Id<T>(Guid value) => new(value);

    /// <summary>
    /// Explicitly converts a strongly-typed <see cref="Id{T}"/> back to a raw <see cref="Guid"/>.
    /// </summary>
    /// <param name="id">The strongly typed ID.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static explicit operator Guid(Id<T> id) => id.Value;

    /// <summary>Indicates whether the left ID is less than the right ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(Id<T> left, Id<T> right) => left.Value < right.Value;

    /// <summary>Indicates whether the left ID is less than or equal to the right ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(Id<T> left, Id<T> right) => left.Value <= right.Value;

    /// <summary>Indicates whether the left ID is greater than the right ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(Id<T> left, Id<T> right) => left.Value > right.Value;

    /// <summary>Indicates whether the left ID is greater than or equal to the right ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(Id<T> left, Id<T> right) => left.Value >= right.Value;

    // --- String Formatting ---

    /// <summary>
    /// Returns the string representation of the ID in "D" (hyphenated) format.
    /// </summary>
    /// <returns>A string like <c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString()
    {
        return Value.ToString();
    }

    /// <summary>
    /// Formats the value of the current instance using the specified format.
    /// </summary>
    /// <param name="format">The format to use (e.g., "D", "N", "B").</param>
    /// <returns>The string representation of the ID.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ToString([StringSyntax(StringSyntaxAttribute.GuidFormat)] string? format)
    {
        return Value.ToString(format);
    }

    /// <summary>
    /// UNSAFE: Reinterprets this ID as an ID of a different entity type, bypassing
    /// the compile-time safety this library exists to provide.
    /// <para>
    /// Only use this at legitimate domain boundaries (e.g., a <c>Customer</c> aggregate
    /// that shares its identity with a <c>User</c>). If you find yourself reaching for
    /// this in ordinary application code, it is almost always a modeling mistake.
    /// </para>
    /// </summary>
    /// <typeparam name="TNew">The target entity type.</typeparam>
    /// <returns>A new <see cref="Id{TNew}"/> with the same underlying Guid value.</returns>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public Id<TNew> UnsafeAs<TNew>()
    {
        return new Id<TNew>(Value);
    }

    /// <summary>
    /// Parses a span of UTF-8 characters into a strongly-typed <see cref="Id{T}"/>.
    /// </summary>
    /// <param name="utf8Text">The UTF-8 span to parse.</param>
    /// <param name="provider">An object that supplies culture-specific formatting information. Ignored by Guid parsing.</param>
    /// <returns>The result of parsing <paramref name="utf8Text"/>.</returns>
    [StackTraceHidden]
    public static Id<T> Parse(ReadOnlySpan<byte> utf8Text, IFormatProvider? provider)
    {
        return new Id<T>(Guid.Parse(utf8Text, provider));
    }

    /// <summary>
    ///     Tries to parse a span of UTF-8 characters into a strongly-typed <see cref="Id{T}" />.
    /// </summary>
    /// <param name="utf8Text">The UTF-8 span to parse.</param>
    /// <param name="provider">An object that supplies culture-specific formatting information. Ignored by Guid parsing.</param>
    /// <param name="result">
    ///     When this method returns, contains the result of successfully parsing <paramref name="utf8Text" />
    ///     , or <see cref="Empty" /> on failure.
    /// </param>
    /// <returns><c>true</c> if <paramref name="utf8Text" /> was successfully parsed; otherwise, <c>false</c>.</returns>
    public static bool TryParse(ReadOnlySpan<byte> utf8Text, IFormatProvider? provider, out Id<T> result)
    {
        if (Guid.TryParse(utf8Text, provider, out var guid))
        {
            result = new Id<T>(guid);
            return true;
        }

        result = Empty;
        return false;
    }
}
