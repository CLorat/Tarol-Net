namespace Tarol.Results;

/// <summary>Collections of results, nullable conversions and the <see cref="ValueTask{TResult}"/> pipeline.</summary>
public static class ResultExtensions
{
    /// <summary>
    /// Turns many results into one: all values in order when every result succeeds; otherwise the first
    /// <see cref="Error"/>, or one <see cref="Invalid"/> with all field errors.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="results">The results.</param>
    /// <returns>The values, or the combined violation.</returns>
    public static Result<IReadOnlyList<T>> Sequence<T>(this IEnumerable<Result<T>> results)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(results);
        var values = new List<T>();
        var failures = default(ResultState.Accumulator);
        var failed = false;
        foreach (var result in results)
        {
            if (result.TryGetValue(out T? value))
            {
                values.Add(value);
            }
            else
            {
                failed = true;
                Result.AddFailure(ref failures, result);
            }
        }

        return failed ? new Result<IReadOnlyList<T>>(failures.Result!) : new Result<IReadOnlyList<T>>(values);
    }

    /// <summary>Wraps a reference that may be <see langword="null"/>: the value, or <paramref name="whenNull"/>.</summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="whenNull">The violation when <paramref name="value"/> is <see langword="null"/>.</param>
    /// <returns>The result.</returns>
    public static Result<T> ToResult<T>(this T? value, Violation whenNull)
        where T : class => value is not null ? new Result<T>(value) : new Result<T>(whenNull);

    /// <summary>Wraps a nullable value type: the value, or <paramref name="whenNull"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="whenNull">The violation when <paramref name="value"/> has no value.</param>
    /// <returns>The result.</returns>
    public static Result<T> ToResult<T>(this T? value, Violation whenNull)
        where T : struct => value.HasValue ? new Result<T>(value.Value) : new Result<T>(whenNull);

    /// <summary>Awaits the result, then <see cref="Result{T}.Map{TNew}(Func{T, TNew})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TNew">The new value type.</typeparam>
    /// <param name="result">The pending result.</param>
    /// <param name="map">The transformation.</param>
    /// <returns>The transformed result.</returns>
    public static async ValueTask<Result<TNew>> Map<T, TNew>(this ValueTask<Result<T>> result, Func<T, TNew> map)
        where T : notnull
        where TNew : notnull => (await result.ConfigureAwait(false)).Map(map);

    /// <summary>Awaits the result, then <see cref="Result{T}.Bind{TNew}(Func{T, Result{TNew}})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TNew">The new value type.</typeparam>
    /// <param name="result">The pending result.</param>
    /// <param name="bind">The next operation.</param>
    /// <returns>Its result, or the failure.</returns>
    public static async ValueTask<Result<TNew>> Bind<T, TNew>(this ValueTask<Result<T>> result, Func<T, Result<TNew>> bind)
        where T : notnull
        where TNew : notnull => (await result.ConfigureAwait(false)).Bind(bind);

    /// <summary>Awaits the result, then <see cref="Result{T}.BindAsync{TNew}"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TNew">The new value type.</typeparam>
    /// <param name="result">The pending result.</param>
    /// <param name="bind">The next operation; receives <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="bind"/>.</param>
    /// <returns>Its result, or the failure.</returns>
    public static async ValueTask<Result<TNew>> BindAsync<T, TNew>(this ValueTask<Result<T>> result, Func<T, CancellationToken, ValueTask<Result<TNew>>> bind, CancellationToken cancellationToken = default)
        where T : notnull
        where TNew : notnull => await (await result.ConfigureAwait(false)).BindAsync(bind, cancellationToken).ConfigureAwait(false);

    /// <summary>Awaits the result, then <see cref="Result{T}.Ensure"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The pending result.</param>
    /// <param name="predicate">The condition.</param>
    /// <param name="violation">The violation when it is false.</param>
    /// <returns>The result, or the failure.</returns>
    public static async ValueTask<Result<T>> Ensure<T>(this ValueTask<Result<T>> result, Func<T, bool> predicate, Violation violation)
        where T : notnull => (await result.ConfigureAwait(false)).Ensure(predicate, violation);

    /// <summary>Awaits the result, then <see cref="Result{T}.Tap"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The pending result.</param>
    /// <param name="action">The side effect.</param>
    /// <returns>The result.</returns>
    public static async ValueTask<Result<T>> Tap<T>(this ValueTask<Result<T>> result, Action<T> action)
        where T : notnull => (await result.ConfigureAwait(false)).Tap(action);

    /// <summary>Awaits the result, then <see cref="Result{T}.TapFailure"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The pending result.</param>
    /// <param name="action">The side effect.</param>
    /// <returns>The result.</returns>
    public static async ValueTask<Result<T>> TapFailure<T>(this ValueTask<Result<T>> result, Action<Violation> action)
        where T : notnull => (await result.ConfigureAwait(false)).TapFailure(action);

    /// <summary>Awaits the result, then <see cref="Result{T}.Match{TOut}"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The output type.</typeparam>
    /// <param name="result">The pending result.</param>
    /// <param name="success">Called with the value.</param>
    /// <param name="failure">Called with the violation.</param>
    /// <returns>The output.</returns>
    public static async ValueTask<TOut> Match<T, TOut>(this ValueTask<Result<T>> result, Func<T, TOut> success, Func<Violation, TOut> failure)
        where T : notnull => (await result.ConfigureAwait(false)).Match(success, failure);

    /// <summary>Awaits the result, then <see cref="Result.Bind(Func{Result})"/>.</summary>
    /// <param name="result">The pending result.</param>
    /// <param name="bind">The next operation.</param>
    /// <returns>Its result, or the failure.</returns>
    public static async ValueTask<Result> Bind(this ValueTask<Result> result, Func<Result> bind) => (await result.ConfigureAwait(false)).Bind(bind);

    /// <summary>Awaits the result, then <see cref="Result.Match{TOut}"/>.</summary>
    /// <typeparam name="TOut">The output type.</typeparam>
    /// <param name="result">The pending result.</param>
    /// <param name="success">Called on success.</param>
    /// <param name="failure">Called with the violation.</param>
    /// <returns>The output.</returns>
    public static async ValueTask<TOut> Match<TOut>(this ValueTask<Result> result, Func<TOut> success, Func<Violation, TOut> failure)
        => (await result.ConfigureAwait(false)).Match(success, failure);
}
