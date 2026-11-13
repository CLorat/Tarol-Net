using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Tarol.Results.Json;

namespace Tarol.Results;

/// <summary>
/// A value that may be absent: either a <typeparamref name="T"/> or <see cref="None"/>. The same type and the same
/// code for value types (<c>Option&lt;int&gt;</c>, <c>Option&lt;Id&lt;User&gt;&gt;</c>) and reference types
/// (<c>Option&lt;User&gt;</c>).
/// </summary>
/// <typeparam name="T">The value type. Must not be nullable (<c>Option&lt;string?&gt;</c> and <c>Option&lt;int?&gt;</c> are CS8714).</typeparam>
/// <remarks>
/// <para>
/// A C# 15 custom union with the non-boxing access pattern. An <see cref="Option{T}"/> cannot be used where a
/// <typeparamref name="T"/> is expected (CS0029, CS1503), and a <c>switch</c> without the <see cref="None"/> arm is
/// CS8509, so absence must be handled:
/// </para>
/// <code>
/// string name = repository.FindByEmail(email) switch
/// {
///     User user =&gt; user.Name,
///     None      =&gt; "guest",
/// };
/// </code>
/// <para>
/// <c>default(Option&lt;T&gt;)</c> is <see cref="None"/>. Size: the value plus a flag (8 bytes for
/// <c>Option&lt;int&gt;</c>, 16 for <c>Option&lt;string&gt;</c>). JSON: the value, or <c>null</c> for
/// <see cref="None"/>, the same as <c>T?</c> (see <see cref="OptionJsonConverter{T}"/>).
/// Keep <c>T?</c> at edges that bind by reflection or by column type (SQL parameters and columns, ORM entities,
/// query strings, configuration) and convert with <see cref="OptionExtensions.ToOption{T}(T)"/> and
/// <see cref="OptionExtensions.ToNullable{T}(Option{T})"/>.
/// </para>
/// </remarks>
[Union]
[JsonConverter(typeof(OptionJsonConverter<>))]
[DebuggerDisplay("{ToString(),nq}")]
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The owner chose the name Option (docs/migration/options.md Q-1); it clashes with a VB keyword only, and Tarol targets C#.")]
public readonly struct Option<T> : IUnion, IEquatable<Option<T>>
    where T : notnull
{
    // Nullable<T> passes `!IsValueType`, so it is checked separately (Option<int?> is CS8714, but must not hold null).
    private static readonly bool CanBeNull = !typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null;

    private readonly T _value;
    private readonly bool _isSome;

    /// <summary>Creates an option that holds <paramref name="value"/>.</summary>
    /// <param name="value">The value; not <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>. Use <see cref="OptionExtensions.ToOption{T}(T)"/> for a value that may be <see langword="null"/>.</exception>
    public Option(T value)
    {
        // `value is null` alone boxes T when the JIT does not optimize: test the type first.
        if (CanBeNull && value is null)
        {
            ThrowNullValue();
        }

        _value = value;
        _isSome = true;
    }

    /// <summary>Creates an absent option; the same as <c>default</c>.</summary>
    /// <param name="none">The absent case.</param>
    public Option(None none)
    {
        _ = none;
        _value = default!;
        _isSome = false;
    }

    /// <summary><see langword="true"/> when the option holds a value.</summary>
    public bool IsSome
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _isSome;
    }

    /// <summary><see langword="true"/> when the option is <see cref="None"/> (including <c>default</c>).</summary>
    public bool IsNone => !_isSome;

    /// <summary>
    /// The active case as <see cref="object"/>: the value (boxed if it is a value type) or <see cref="None"/>.
    /// Required by the C# union protocol; use a pattern, <see cref="TryGetValue(out T)"/> or <see cref="GetValueOrThrow"/> instead (TRES003).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public object Value => _isSome ? _value : default(None);

    /// <summary>Always <see langword="true"/>: <see cref="None"/> is a case, not <see langword="null"/>. Part of the union protocol.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The C# union protocol requires an instance HasValue.")]
    public bool HasValue => true;

    /// <summary>Gets the value when the option holds one. Used by pattern matching; check the result (TRES005).</summary>
    /// <param name="value">The value, or <see langword="default"/> when the option is <see cref="None"/>.</param>
    /// <returns><see langword="true"/> when the option holds a value.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return _isSome;
    }

    /// <summary>Tells whether the option is <see cref="None"/>. Used by pattern matching.</summary>
    /// <param name="none">The absent case.</param>
    /// <returns><see langword="true"/> when the option is <see cref="None"/>.</returns>
    public bool TryGetValue(out None none)
    {
        none = default;
        return !_isSome;
    }

    /// <summary>Returns the value, or throws when the option is <see cref="None"/>. Use it only where absence is a bug.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The option is <see cref="None"/>.</exception>
    public T GetValueOrThrow() => _isSome ? _value : throw new InvalidOperationException($"The option of {typeof(T).Name} is None.");

    /// <summary>Returns the value, or <paramref name="fallback"/> when the option is <see cref="None"/>.</summary>
    /// <param name="fallback">The fallback.</param>
    /// <returns>The value or the fallback.</returns>
    public T GetValueOrDefault(T fallback) => _isSome ? _value : fallback;

    /// <summary>Returns the value, or a fallback computed only when the option is <see cref="None"/>.</summary>
    /// <param name="fallback">Computes the fallback.</param>
    /// <returns>The value or the fallback.</returns>
    public T GetValueOrDefault(Func<T> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return _isSome ? _value : fallback();
    }

    /// <summary>Wraps a value; <see langword="null"/> throws (see <see cref="Option{T}(T)"/>).</summary>
    /// <param name="value">The value.</param>
    public static implicit operator Option<T>(T value) => new(value);

    /// <summary>The absent option: <c>return Option.None;</c>.</summary>
    /// <param name="none">The absent case.</param>
    public static implicit operator Option<T>(None none) => new(none);

    /// <summary>Transforms the value; <see cref="None"/> stays <see cref="None"/>.</summary>
    /// <typeparam name="TNew">The new value type.</typeparam>
    /// <param name="map">The transformation; must not return <see langword="null"/>.</param>
    /// <returns>The transformed option.</returns>
    public Option<TNew> Map<TNew>(Func<T, TNew> map)
        where TNew : notnull
    {
        ArgumentNullException.ThrowIfNull(map);
        return _isSome ? new Option<TNew>(map(_value)) : default;
    }

    /// <summary>Continues with an operation whose result may be absent; <see cref="None"/> stays <see cref="None"/>.</summary>
    /// <typeparam name="TNew">The new value type.</typeparam>
    /// <param name="bind">The next operation.</param>
    /// <returns>Its option, or <see cref="None"/>.</returns>
    public Option<TNew> Bind<TNew>(Func<T, Option<TNew>> bind)
        where TNew : notnull
    {
        ArgumentNullException.ThrowIfNull(bind);
        return _isSome ? bind(_value) : default;
    }

    /// <summary>Keeps the value only when <paramref name="predicate"/> is true; otherwise <see cref="None"/>.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>This option, or <see cref="None"/>.</returns>
    public Option<T> Filter(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return _isSome && predicate(_value) ? this : default;
    }

    /// <summary>
    /// Folds the option into one value. Prefer a <c>switch</c> expression: a capturing lambda here allocates a
    /// closure and a delegate on every call; <c>static</c> lambdas do not.
    /// </summary>
    /// <typeparam name="TOut">The output type.</typeparam>
    /// <param name="some">Called with the value.</param>
    /// <param name="none">Called when the option is <see cref="None"/>.</param>
    /// <returns>The output of the called function.</returns>
    public TOut Match<TOut>(Func<T, TOut> some, Func<TOut> none)
    {
        ArgumentNullException.ThrowIfNull(some);
        ArgumentNullException.ThrowIfNull(none);
        return _isSome ? some(_value) : none();
    }

    /// <summary>Turns absence into a failure: the value, or <paramref name="whenNone"/>.</summary>
    /// <param name="whenNone">The violation when the option is <see cref="None"/>, typically <c>Error.NotFound(…)</c>.</param>
    /// <returns>The result.</returns>
    public Result<T> ToResult(Violation whenNone)
    {
        ArgumentNullException.ThrowIfNull(whenNone);
        return _isSome ? new Result<T>(_value) : new Result<T>(whenNone);
    }

    /// <summary>Turns absence into a failure computed only when the option is <see cref="None"/>.</summary>
    /// <param name="whenNone">Creates the violation.</param>
    /// <returns>The result.</returns>
    public Result<T> ToResult(Func<Violation> whenNone)
    {
        ArgumentNullException.ThrowIfNull(whenNone);
        return _isSome ? new Result<T>(_value) : new Result<T>(whenNone());
    }

    /// <summary>Two options are equal when both are <see cref="None"/>, or both hold equal values. <c>default</c> equals <see cref="Option.None"/>.</summary>
    /// <param name="other">The other option.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(Option<T> other) => _isSome
        ? other._isSome && EqualityComparer<T>.Default.Equals(_value, other._value)
        : !other._isSome;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Option<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _isSome ? HashCode.Combine(true, _value) : 0;

    /// <summary>Returns <c>Some(value)</c> or <c>None</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _isSome ? $"Some({_value})" : nameof(None);

    /// <summary>Equality, see <see cref="Equals(Option{T})"/>.</summary>
    /// <param name="left">The first option.</param>
    /// <param name="right">The second option.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(Option<T> left, Option<T> right) => left.Equals(right);

    /// <summary>Inequality, see <see cref="Equals(Option{T})"/>.</summary>
    /// <param name="left">The first option.</param>
    /// <param name="right">The second option.</param>
    /// <returns><see langword="true"/> when not equal.</returns>
    public static bool operator !=(Option<T> left, Option<T> right) => !left.Equals(right);

    [DoesNotReturn]
    private static void ThrowNullValue() => throw new ArgumentNullException("value", "An option needs a value; use Option.None (or ToOption() for a nullable) for the absence of one.");
}
