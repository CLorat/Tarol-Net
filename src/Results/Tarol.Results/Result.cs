using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Tarol.Results.Json;

namespace Tarol.Results;

/// <summary>
/// The outcome of an operation without a value: <see cref="Tarol.Results.Success"/>, or a <see cref="Violation"/>
/// (<see cref="Invalid"/> field errors or one <see cref="Error"/>). One reference, 8 bytes, no allocation on success.
/// </summary>
/// <remarks>
/// <code>
/// return await unitOfWork.Commit(ct) switch
/// {
///     Success =&gt; TypedResults.NoContent(),
///     Violation violation =&gt; TypedResults.Problem(statusCode: violation.ToHttpStatusCode()),
/// };
/// </code>
/// <c>default(Result)</c> is a failure whose violation is <see cref="Error.Uninitialized"/>, like <c>default(Result&lt;T&gt;)</c>.
/// JSON: <c>{"success":true}</c> or <c>{"failure":{…}}</c> (see <see cref="ResultJsonConverter"/>).
/// </remarks>
[Union]
[JsonConverter(typeof(ResultJsonConverter))]
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct Result : IUnion, IEquatable<Result>
{
    private static readonly Result Successful = new(default(Tarol.Results.Success));

    private readonly Violation? _state;

    /// <summary>Creates a successful result. <see cref="Success()"/> is shorter.</summary>
    /// <param name="success">The success marker.</param>
    public Result(Tarol.Results.Success success)
    {
        _ = success;
        _state = ResultState.Ok;
    }

    /// <summary>Creates a failed result.</summary>
    /// <param name="violation">Why the operation failed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="violation"/> is <see langword="null"/>.</exception>
    public Result(Violation violation)
    {
        ArgumentNullException.ThrowIfNull(violation);
        _state = violation;
    }

    /// <summary><see langword="true"/> when the operation succeeded.</summary>
    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ReferenceEquals(_state, ResultState.Ok);
    }

    /// <summary><see langword="true"/> when the result holds a <see cref="Violation"/> (including <c>default</c>).</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The active case as <see cref="object"/> (boxes <see cref="Tarol.Results.Success"/>). Required by the union protocol; use a pattern instead (TRES003).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public object Value => IsSuccess ? default(Tarol.Results.Success) : State;

    /// <summary>Always <see langword="true"/>. Part of the union protocol.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The C# union protocol requires an instance HasValue property.")]
    public bool HasValue => true;

    private Violation State => _state ?? Error.Uninitialized;

    /// <summary>Gets the success marker when the result is a success. Used by pattern matching.</summary>
    /// <param name="success">The marker.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public bool TryGetValue(out Tarol.Results.Success success)
    {
        success = default;
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

    /// <summary>Returns the cached successful result.</summary>
    /// <returns>A success.</returns>
    public static Result Success() => Successful;

    /// <summary>Creates a failed result.</summary>
    /// <param name="violation">The violation.</param>
    /// <returns>A failure.</returns>
    public static Result Failure(Violation violation) => new(violation);

    /// <summary>Creates a successful <see cref="Result{T}"/>; the type argument is inferred.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>A success.</returns>
    public static Result<T> Success<T>(T value)
        where T : notnull => new(value);

    /// <summary>Wraps a violation as a failure.</summary>
    /// <param name="violation">The violation.</param>
    public static implicit operator Result(Violation violation) => new(violation);

    /// <summary>Wraps the success marker as a success.</summary>
    /// <param name="success">The marker.</param>
    public static implicit operator Result(Tarol.Results.Success success) => new(success);

    /// <summary>
    /// Combines results: success when all succeed; otherwise the first <see cref="Error"/>, or, when every failure is
    /// <see cref="Invalid"/>, one <see cref="Invalid"/> with all field errors in order.
    /// </summary>
    /// <param name="results">The results.</param>
    /// <returns>The combined result.</returns>
    public static Result Combine(params ReadOnlySpan<Result> results)
    {
        var failures = default(ResultState.Accumulator);
        foreach (var result in results)
        {
            if (!result.IsSuccess)
            {
                failures.Add(result.State);
            }
        }

        return failures.Result is { } violation ? new Result(violation) : Successful;
    }

    /// <summary>Combines results; same rule as <see cref="Combine(ReadOnlySpan{Result})"/>.</summary>
    /// <param name="results">The results.</param>
    /// <returns>The combined result.</returns>
    public static Result Combine(IEnumerable<Result> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var failures = default(ResultState.Accumulator);
        foreach (var result in results)
        {
            if (!result.IsSuccess)
            {
                failures.Add(result.State);
            }
        }

        return failures.Result is { } violation ? new Result(violation) : Successful;
    }

    /// <summary>Combines two results into a tuple; failures combine as in <see cref="Combine(ReadOnlySpan{Result})"/>.</summary>
    /// <typeparam name="T1">The first value type.</typeparam>
    /// <typeparam name="T2">The second value type.</typeparam>
    /// <param name="first">The first result.</param>
    /// <param name="second">The second result.</param>
    /// <returns>Both values, or the combined violation.</returns>
    public static Result<(T1, T2)> Combine<T1, T2>(Result<T1> first, Result<T2> second)
        where T1 : notnull
        where T2 : notnull
    {
        if (first.TryGetValue(out T1? a) & second.TryGetValue(out T2? b))
        {
            return new Result<(T1, T2)>((a!, b!));
        }

        var failures = default(ResultState.Accumulator);
        AddFailure(ref failures, first);
        AddFailure(ref failures, second);
        return new Result<(T1, T2)>(failures.Result!);
    }

    /// <summary>Combines three results into a tuple; failures combine as in <see cref="Combine(ReadOnlySpan{Result})"/>.</summary>
    /// <typeparam name="T1">The first value type.</typeparam>
    /// <typeparam name="T2">The second value type.</typeparam>
    /// <typeparam name="T3">The third value type.</typeparam>
    /// <param name="first">The first result.</param>
    /// <param name="second">The second result.</param>
    /// <param name="third">The third result.</param>
    /// <returns>All values, or the combined violation.</returns>
    public static Result<(T1, T2, T3)> Combine<T1, T2, T3>(Result<T1> first, Result<T2> second, Result<T3> third)
        where T1 : notnull
        where T2 : notnull
        where T3 : notnull
    {
        if (first.TryGetValue(out T1? a) & second.TryGetValue(out T2? b) & third.TryGetValue(out T3? c))
        {
            return new Result<(T1, T2, T3)>((a!, b!, c!));
        }

        var failures = default(ResultState.Accumulator);
        AddFailure(ref failures, first);
        AddFailure(ref failures, second);
        AddFailure(ref failures, third);
        return new Result<(T1, T2, T3)>(failures.Result!);
    }

    internal static void AddFailure<T>(ref ResultState.Accumulator failures, Result<T> result)
        where T : notnull
    {
        if (result.TryGetValue(out Violation? violation))
        {
            failures.Add(violation);
        }
    }

    /// <summary>Produces a value on success; a failure is passed on.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="map">Produces the value.</param>
    /// <returns>The value, or this failure.</returns>
    public Result<T> Map<T>(Func<T> map)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? new Result<T>(map()) : new Result<T>(State);
    }

    /// <summary>Continues with an operation that can fail; a failure is passed on.</summary>
    /// <param name="bind">The next operation.</param>
    /// <returns>Its result, or this failure.</returns>
    public Result Bind(Func<Result> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind() : this;
    }

    /// <summary>Continues with an operation that produces a value and can fail; a failure is passed on.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="bind">The next operation.</param>
    /// <returns>Its result, or this failure.</returns>
    public Result<T> Bind<T>(Func<Result<T>> bind)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind() : new Result<T>(State);
    }

    /// <summary>Turns a success into <paramref name="violation"/> when <paramref name="predicate"/> is false.</summary>
    /// <param name="predicate">The condition.</param>
    /// <param name="violation">The violation when it is false.</param>
    /// <returns>This result, or the failure.</returns>
    public Result Ensure(Func<bool> predicate, Violation violation)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(violation);
        return IsSuccess && !predicate() ? new Result(violation) : this;
    }

    /// <summary>Runs <paramref name="action"/> on success and returns this result.</summary>
    /// <param name="action">The side effect.</param>
    /// <returns>This result.</returns>
    public Result Tap(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsSuccess)
        {
            action();
        }

        return this;
    }

    /// <summary>Runs <paramref name="action"/> on failure and returns this result.</summary>
    /// <param name="action">The side effect.</param>
    /// <returns>This result.</returns>
    public Result TapFailure(Action<Violation> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsSuccess)
        {
            action(State);
        }

        return this;
    }

    /// <summary>Folds the result into one value. Prefer a <c>switch</c> expression (no delegates).</summary>
    /// <typeparam name="TOut">The output type.</typeparam>
    /// <param name="success">Called on success.</param>
    /// <param name="failure">Called with the violation.</param>
    /// <returns>The output of the called function.</returns>
    public TOut Match<TOut>(Func<TOut> success, Func<Violation, TOut> failure)
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentNullException.ThrowIfNull(failure);
        return IsSuccess ? success() : failure(State);
    }

    /// <summary>Produces a value asynchronously on success; a failure is passed on.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="map">Produces the value; receives <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="map"/>.</param>
    /// <returns>The value, or this failure.</returns>
    public async ValueTask<Result<T>> MapAsync<T>(Func<CancellationToken, ValueTask<T>> map, CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? new Result<T>(await map(cancellationToken).ConfigureAwait(false)) : new Result<T>(State);
    }

    /// <summary>Continues asynchronously with an operation that can fail; a failure is passed on.</summary>
    /// <param name="bind">The next operation; receives <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="bind"/>.</param>
    /// <returns>Its result, or this failure.</returns>
    public async ValueTask<Result> BindAsync(Func<CancellationToken, ValueTask<Result>> bind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? await bind(cancellationToken).ConfigureAwait(false) : this;
    }

    /// <summary>Continues asynchronously with an operation that produces a value; a failure is passed on.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="bind">The next operation; receives <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="bind"/>.</param>
    /// <returns>Its result, or this failure.</returns>
    public async ValueTask<Result<T>> BindAsync<T>(Func<CancellationToken, ValueTask<Result<T>>> bind, CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? await bind(cancellationToken).ConfigureAwait(false) : new Result<T>(State);
    }

    /// <summary>Runs an asynchronous side effect on success and returns this result.</summary>
    /// <param name="action">The side effect; receives <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="action"/>.</param>
    /// <returns>This result.</returns>
    public async ValueTask<Result> TapAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsSuccess)
        {
            await action(cancellationToken).ConfigureAwait(false);
        }

        return this;
    }

    /// <summary>Two successes are equal; two failures when their violations are. <c>default</c> equals <c>Failure(Error.Uninitialized)</c>.</summary>
    /// <param name="other">The other result.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(Result other) => IsSuccess ? other.IsSuccess : !other.IsSuccess && State.Equals(other.State);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => IsSuccess ? 1 : State.GetHashCode();

    /// <summary>Returns <c>Success</c> or <c>Failure(violation)</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => IsSuccess ? "Success" : $"Failure({State})";

    /// <summary>Equality, see <see cref="Equals(Result)"/>.</summary>
    /// <param name="left">The first result.</param>
    /// <param name="right">The second result.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(Result left, Result right) => left.Equals(right);

    /// <summary>Inequality, see <see cref="Equals(Result)"/>.</summary>
    /// <param name="left">The first result.</param>
    /// <param name="right">The second result.</param>
    /// <returns><see langword="true"/> when not equal.</returns>
    public static bool operator !=(Result left, Result right) => !left.Equals(right);
}
