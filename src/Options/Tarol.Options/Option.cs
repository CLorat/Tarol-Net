using System.Diagnostics.CodeAnalysis;

namespace Tarol.Options;

/// <summary>Creates <see cref="Option{T}"/> values.</summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The owner chose the name Option (docs/migration/options.md Q-1); it clashes with a VB keyword only, and Tarol targets C#.")]
public static class Option
{
    /// <summary>
    /// The absent case; converts to any <see cref="Option{T}"/>: <c>return Option.None;</c>. In a conditional write
    /// <c>found ? value : Option.None</c>, never <c>found ? value : default</c> (that is <c>Some(0)</c> for an
    /// <c>int</c>; TOPT004).
    /// </summary>
    public static None None => default;

    /// <summary>Creates an option that holds <paramref name="value"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value; not <see langword="null"/>.</param>
    /// <returns>The option.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static Option<T> Some<T>(T value)
        where T : notnull => new(value);
}

/// <summary>
/// Conversions between <c>T?</c> and <see cref="Option{T}"/> for reference types and nullable value types. The same call works for both kinds of type.
/// </summary>
public static class OptionExtensions
{
    /// <summary>A reference that may be <see langword="null"/> as an option: <see langword="null"/> is <see cref="None"/>.</summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The option.</returns>
    public static Option<T> ToOption<T>(this T? value)
        where T : class => value is null ? default : new Option<T>(value);

    /// <summary>A nullable value type as an option: no value is <see cref="None"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The option.</returns>
    public static Option<T> ToOption<T>(this T? value)
        where T : struct => value.HasValue ? new Option<T>(value.Value) : default;

    /// <summary>The option as a reference that may be <see langword="null"/>, for edges that bind <c>T?</c> (raw SQL parameters, configuration).</summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <param name="option">The option.</param>
    /// <returns>The value, or <see langword="null"/> for <see cref="None"/>.</returns>
    public static T? ToNullable<T>(this Option<T> option)
        where T : class => option.TryGetValue(out T? value) ? value : null;
}

/// <summary>
/// <see cref="Option{T}"/> to <c>Nullable&lt;T&gt;</c>. A class of its own because C# cannot overload
/// <c>ToNullable(this Option&lt;T&gt;)</c> on the <c>class</c>/<c>struct</c> constraint alone; the call site is the
/// same as for reference types.
/// </summary>
public static class OptionValueTypeExtensions
{
    /// <summary>The option as a nullable value type, for edges that bind <c>T?</c> (raw SQL parameters, configuration).</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="option">The option.</param>
    /// <returns>The value, or <see langword="null"/> for <see cref="None"/>.</returns>
    public static T? ToNullable<T>(this Option<T> option)
        where T : struct => option.TryGetValue(out T value) ? value : null;
}
