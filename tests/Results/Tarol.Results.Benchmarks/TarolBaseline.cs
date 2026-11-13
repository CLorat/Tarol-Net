namespace Tarol.Results.Benchmarks;

// The layout of Tarol.Results at CLorat/Tarol@6a62f81 (record struct: value, 40-byte Violation struct, flag),
// reduced to the members the benchmarks call. Baseline only; not the old API in full.
public enum BaselineViolationType : byte
{
    Validation = 0,
    NotFound = 3,
}

public readonly record struct BaselineViolation
{
    public required string Code { get; init; }

    public required BaselineViolationType Type { get; init; }

    public string? Detail { get; init; }

    public string? PropertyName { get; init; }

    public IReadOnlyList<BaselineViolation>? InnerErrors { get; init; }

    public static readonly BaselineViolation None = new() { Code = string.Empty, Type = default };
}

public readonly record struct BaselineResult<T>
{
    private readonly T? _value;
    private readonly BaselineViolation _error;
    private readonly bool _ok;

    private BaselineResult(T value)
    {
        _value = value;
        _error = BaselineViolation.None;
        _ok = true;
    }

    private BaselineResult(BaselineViolation error)
    {
        _value = default;
        _error = error;
        _ok = false;
    }

    public bool IsSuccess => _ok;

    public T Value => _ok ? _value! : throw new InvalidOperationException(_error.Code);

    public BaselineViolation Violation => _ok ? BaselineViolation.None : _error;

    public static implicit operator BaselineResult<T>(T value) => new(value);

    public static implicit operator BaselineResult<T>(BaselineViolation error) => new(error);

    public BaselineResult<TNew> Map<TNew>(Func<T, TNew> map) => _ok ? map(_value!) : _error;

    public BaselineResult<TNew> Bind<TNew>(Func<T, BaselineResult<TNew>> bind) => _ok ? bind(_value!) : _error;

    public TOut Match<TOut>(Func<T, TOut> success, Func<BaselineViolation, TOut> failure) => _ok ? success(_value!) : failure(_error);

    public async ValueTask<BaselineResult<TNew>> BindAsync<TNew>(Func<T, ValueTask<BaselineResult<TNew>>> bind) => _ok ? await bind(_value!).ConfigureAwait(false) : _error;
}
