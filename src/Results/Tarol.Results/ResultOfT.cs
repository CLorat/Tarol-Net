using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Tarol.Results.Json;

namespace Tarol.Results;

/// <summary>
/// The outcome of an operation that produces a <typeparamref name="T"/>: either the value, or a
/// <see cref="Violation"/> (<see cref="Invalid"/> field errors or one <see cref="Error"/>). No exceptions.
/// </summary>
/// <typeparam name="T">The success value. Must not be <see langword="null"/>, a <see cref="Violation"/>, <see cref="object"/> or another result (TRES001).</typeparam>
/// <remarks>
/// <para>
/// A C# 15 custom union with the non-boxing access pattern: pattern matching calls
/// <see cref="TryGetValue(out T)"/> / <see cref="TryGetValue(out Violation)"/>, so value types are never boxed.
/// Size: the value plus one reference (16 bytes for <c>Result&lt;int&gt;</c>, 24 for <c>Result&lt;Guid&gt;</c>).
/// </para>
/// <code>
/// return await repository.GetById(id, ct) switch
/// {
///     User user       =&gt; TypedResults.Ok(user),
///     Invalid invalid =&gt; TypedResults.ValidationProblem(…),
///     Error error     =&gt; TypedResults.Problem(statusCode: error.ToHttpStatusCode(), title: error.Code),
/// };
/// </code>
/// <para>
/// <c>default(Result&lt;T&gt;)</c> is a failure whose violation is <see cref="Error.Uninitialized"/>; every exhaustive
/// <c>switch</c> handles it in its <c>Error</c> (or <c>Violation</c>) arm.
/// JSON: <c>{"value":…}</c> or <c>{"failure":{…}}</c> (see <see cref="ResultJsonConverter{T}"/>).
/// </para>
/// </remarks>
[Union]
[JsonConverter(typeof(ResultJsonConverter<>))]
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct Result<T> : IUnion, IEquatable<Result<T>>
    where T : notnull
{
    private readonly T _value;
    private readonly Violation? _state;

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The value; not <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public Result(T value)
    {
        // `value is null` alone boxes T when the JIT does not optimize (Debug, MinOpts): test value types out first.
        if (!typeof(T).IsValueType && value is null)
        {
            ThrowNullValue();
        }

        _value = value;
        _state = ResultState.Ok;
    }

    /// <summary>Creates a failed result.</summary>
    /// <param name="violation">Why the operation failed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="violation"/> is <see langword="null"/>.</exception>
    public Result(Violation violation)
    {
        ArgumentNullException.ThrowIfNull(violation);
        _value = default!;
        _state = violation;
    }

    /// <summary><see langword="true"/> when the result holds a value.</summary>
    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ReferenceEquals(_state, ResultState.Ok);
    }

    /// <summary><see langword="true"/> when the result holds a <see cref="Violation"/> (including <c>default</c>).</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// The active case as <see cref="object"/>: the value (boxed if it is a value type) or the violation.
    /// Required by the C# union protocol; use a pattern, <see cref="TryGetValue(out T)"/> or <see cref="GetValueOrThrow"/> instead (TRES003).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public object Value => IsSuccess ? _value : State;

    /// <summary>Always <see langword="true"/>: a result is never empty (<c>default</c> is a failure). Part of the union protocol.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool HasValue => true;

    private Violation State => _state ?? Error.Uninitialized;

    /// <summary>Gets the value when the result is a success. Used by pattern matching.</summary>
    /// <param name="value">The value, or <see langword="default"/> on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return IsSuccess;
    }

    /// <summary>Gets the violation when the result is a failure. Used by pattern matching.</summary>
    /// <param name="violation">The violation, or <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on failure.</returns>
    public bool TryGetValue([NotNullWhen(true)] out Violation? violation)
    {
        violation = IsSuccess ? null : State;
        return violation is not null;
    }

    /// <summary>Returns the value, or throws when the result is a failure. Use it only where a failure is a bug.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The result is a failure; the message contains the violation.</exception>
    public T GetValueOrThrow() => IsSuccess ? _value : throw new InvalidOperationException($"The result is a failure: {State}.");

    /// <summary>Returns the value, or <paramref name="defaultValue"/> on failure.</summary>
    /// <param name="defaultValue">The fallback.</param>
    /// <returns>The value or the fallback.</returns>
    public T GetValueOrDefault(T defaultValue) => IsSuccess ? _value : defaultValue;

    /// <summary>Returns the value, or a fallback computed from the violation.</summary>
    /// <param name="fallback">Computes the fallback.</param>
    /// <returns>The value or the fallback.</returns>
    public T GetValueOrDefault(Func<Violation, T> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return IsSuccess ? _value : fallback(State);
    }

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The result.</returns>
    public static Result<T> Success(T value) => new(value);

    /// <summary>Creates a failed result.</summary>
    /// <param name="violation">The violation.</param>
    /// <returns>The result.</returns>
    public static Result<T> Failure(Violation violation) => new(violation);

    /// <summary>Wraps a value as a success.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator Result<T>(T value) => new(value);

    /// <summary>Wraps a violation (<see cref="Invalid"/> or <see cref="Error"/>) as a failure.</summary>
    /// <param name="violation">The violation.</param>
    public static implicit operator Result<T>(Violation violation) => new(violation);

    /// <summary>Drops the value: success stays success, the violation is kept.</summary>
    /// <returns>The non-generic result.</returns>
    public Result ToResult() => IsSuccess ? Result.Success() : new Result(State);

    /// <summary>Transforms the value; a failure is passed on unchanged.</summary>
    /// <typeparam name="TNew">The new value type.</typeparam>
    /// <param name="map">The transformation.</param>
    /// <returns>The transformed result.</returns>
    public Result<TNew> Map<TNew>(Func<T, TNew> map)
        where TNew : notnull
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? new Result<TNew>(map(_value)) : new Result<TNew>(State);
    }

    /// <summary>Continues with an operation that can fail; a failure is passed on unchanged.</summary>
    /// <typeparam name="TNew">The new value type.</typeparam>
    /// <param name="bind">The next operation.</param>
    /// <returns>Its result, or this failure.</returns>
    public Result<TNew> Bind<TNew>(Func<T, Result<TNew>> bind)
        where TNew : notnull
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(_value) : new Result<TNew>(State);
    }

    /// <summary>Continues with an operation without a value that can fail; a failure is passed on unchanged.</summary>
    /// <param name="bind">The next operation.</param>
    /// <returns>Its result, or this failure.</returns>
    public Result Bind(Func<T, Result> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(_value) : new Result(State);
    }

    /// <summary>Turns a success into <paramref name="violation"/> when <paramref name="predicate"/> is false.</summary>
    /// <param name="predicate">The condition the value must meet.</param>
    /// <param name="violation">The violation when it does not.</param>
    /// <returns>This result, or the failure.</returns>
    public Result<T> Ensure(Func<T, bool> predicate, Violation violation)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(violation);
        return IsSuccess && !predicate(_value) ? new Result<T>(violation) : this;
    }

    /// <summary>Runs <paramref name="action"/> on success and returns this result unchanged.</summary>
    /// <param name="action">The side effect.</param>
    /// <returns>This result.</returns>
    public Result<T> Tap(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsSuccess)
        {
            action(_value);
        }

        return this;
    }

    /// <summary>Runs <paramref name="action"/> on failure and returns this result unchanged.</summary>
    /// <param name="action">The side effect.</param>
    /// <returns>This result.</returns>
    public Result<T> TapFailure(Action<Violation> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsSuccess)
        {
            action(State);
        }

        return this;
    }

    /// <summary>
    /// Folds the result into one value. Prefer a <c>switch</c> expression: a capturing lambda here allocates a
    /// closure and a delegate on every call; <c>static</c> lambdas do not.
    /// </summary>
    /// <typeparam name="TOut">The output type.</typeparam>
    /// <param name="success">Called with the value.</param>
    /// <param name="failure">Called with the violation.</param>
    /// <returns>The output of the called function.</returns>
    public TOut Match<TOut>(Func<T, TOut> success, Func<Violation, TOut> failure)
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentNullException.ThrowIfNull(failure);
        return IsSuccess ? success(_value) : failure(State);
    }

    /// <summary>Transforms the value asynchronously; a failure is passed on without calling <paramref name="map"/>.</summary>
    /// <typeparam name="TNew">The new value type.</typeparam>
    /// <param name="map">The transformation; receives <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="map"/>.</param>
    /// <returns>The transformed result.</returns>
    public async ValueTask<Result<TNew>> MapAsync<TNew>(Func<T, CancellationToken, ValueTask<TNew>> map, CancellationToken cancellationToken = default)
        where TNew : notnull
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? new Result<TNew>(await map(_value, cancellationToken).ConfigureAwait(false)) : new Result<TNew>(State);
    }

    /// <summary>Continues asynchronously with an operation that can fail; a failure is passed on.</summary>
    /// <typeparam name="TNew">The new value type.</typeparam>
    /// <param name="bind">The next operation; receives <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="bind"/>.</param>
    /// <returns>Its result, or this failure.</returns>
    public async ValueTask<Result<TNew>> BindAsync<TNew>(Func<T, CancellationToken, ValueTask<Result<TNew>>> bind, CancellationToken cancellationToken = default)
        where TNew : notnull
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? await bind(_value, cancellationToken).ConfigureAwait(false) : new Result<TNew>(State);
    }

    /// <summary>Continues asynchronously with an operation without a value that can fail; a failure is passed on.</summary>
    /// <param name="bind">The next operation; receives <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="bind"/>.</param>
    /// <returns>Its result, or this failure.</returns>
    public async ValueTask<Result> BindAsync(Func<T, CancellationToken, ValueTask<Result>> bind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? await bind(_value, cancellationToken).ConfigureAwait(false) : new Result(State);
    }

    /// <summary>Asynchronous <see cref="Ensure"/>.</summary>
    /// <param name="predicate">The condition; receives <paramref name="cancellationToken"/>.</param>
    /// <param name="violation">The violation when the condition is false.</param>
    /// <param name="cancellationToken">Passed to <paramref name="predicate"/>.</param>
    /// <returns>This result, or the failure.</returns>
    public async ValueTask<Result<T>> EnsureAsync(Func<T, CancellationToken, ValueTask<bool>> predicate, Violation violation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(violation);
        return IsSuccess && !await predicate(_value, cancellationToken).ConfigureAwait(false) ? new Result<T>(violation) : this;
    }

    /// <summary>Runs an asynchronous side effect on success and returns this result unchanged.</summary>
    /// <param name="action">The side effect; receives <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="action"/>.</param>
    /// <returns>This result.</returns>
    public async ValueTask<Result<T>> TapAsync(Func<T, CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsSuccess)
        {
            await action(_value, cancellationToken).ConfigureAwait(false);
        }

        return this;
    }

    /// <summary>Two successes are equal when their values are; two failures when their violations are. <c>default</c> equals <c>Failure(Error.Uninitialized)</c>.</summary>
    /// <param name="other">The other result.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(Result<T> other) => IsSuccess
        ? other.IsSuccess && EqualityComparer<T>.Default.Equals(_value, other._value)
        : !other.IsSuccess && State.Equals(other.State);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => IsSuccess ? HashCode.Combine(true, _value) : HashCode.Combine(false, State);

    /// <summary>Returns <c>Success(value)</c> or <c>Failure(violation)</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => IsSuccess ? $"Success({_value})" : $"Failure({State})";

    /// <summary>Equality, see <see cref="Equals(Result{T})"/>.</summary>
    /// <param name="left">The first result.</param>
    /// <param name="right">The second result.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(Result<T> left, Result<T> right) => left.Equals(right);

    /// <summary>Inequality, see <see cref="Equals(Result{T})"/>.</summary>
    /// <param name="left">The first result.</param>
    /// <param name="right">The second result.</param>
    /// <returns><see langword="true"/> when not equal.</returns>
    public static bool operator !=(Result<T> left, Result<T> right) => !left.Equals(right);

    [DoesNotReturn]
    private static void ThrowNullValue() => throw new ArgumentNullException("value", "A successful result needs a value; use a Violation for the absence of one.");
}
