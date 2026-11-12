using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
namespace Probe;

// ---------------- A: today's record struct (copied layout from Tarol 6a62f81)
public enum ViolationType : byte { Validation = 0, Unauthorized, Forbidden, NotFound, Conflict, Gone, UnprocessableEntity, TooManyRequests, Internal, Unavailable, Timeout, Custom = 255 }
public readonly record struct Violation
{
    public required string Code { get; init; }
    public required ViolationType Type { get; init; }
    public string? Detail { get; init; }
    public string? PropertyName { get; init; }
    public IReadOnlyList<Violation>? InnerErrors { get; init; }
    public static readonly Violation None = new() { Code = string.Empty, Type = default };
}
public readonly record struct AResult<T>
{
    private readonly T? _value; private readonly Violation _error; private readonly bool _ok;
    private AResult(T value) { _value = value; _error = Violation.None; _ok = true; }
    private AResult(Violation e) { _value = default; _error = e; _ok = false; }
    [MemberNotNullWhen(true, nameof(Value))] public bool IsSuccess => _ok;
    public T Value => _ok ? _value! : throw new InvalidOperationException("failed: " + _error.Code);
    public Violation Violation => _ok ? Violation.None : _error;
    public static AResult<T> Success(T v) => new(v);
    public static AResult<T> Failure(Violation e) => new(e);
    public static implicit operator AResult<T>(T v) => new(v);
    public static implicit operator AResult<T>(Violation e) => new(e);
    public AResult<TN> Map<TN>(Func<T, TN> f) => _ok ? AResult<TN>.Success(f(_value!)) : AResult<TN>.Failure(_error);
    public AResult<TN> Bind<TN>(Func<T, AResult<TN>> f) => _ok ? f(_value!) : AResult<TN>.Failure(_error);
    public TR Match<TR>(Func<T, TR> ok, Func<Violation, TR> fail) => _ok ? ok(_value!) : fail(_error);
    public async ValueTask<AResult<TN>> BindAsync<TN>(Func<T, ValueTask<AResult<TN>>> f) => _ok ? await f(_value!) : AResult<TN>.Failure(_error);
}
public readonly record struct AResult
{
    private readonly Violation _error; private readonly bool _ok;
    private AResult(bool ok, Violation e) { _ok = ok; _error = e; }
    public bool IsSuccess => _ok;
    public Violation Violation => _ok ? Violation.None : _error;
    public static AResult Success() => new(true, Violation.None);
    public static implicit operator AResult(Violation e) => new(false, e);
}

// ---------------- Failure as a reference type (B, C)
public enum FailureKind : byte { Uninitialized = 0, Validation, Unauthorized, Forbidden, NotFound, Conflict, Gone, Unprocessable, TooManyRequests, Unavailable, Timeout, Internal }
public sealed record Failure(FailureKind Kind, string Code, string? Detail = null, string? Target = null)
{
    public IReadOnlyList<Failure> Inner { get; init; } = [];
    public static Failure Uninitialized { get; } = new(FailureKind.Uninitialized, "Result.Uninitialized", "default(Result) was used; it was never assigned a success or a failure.");
    public static Failure NotFound(string code) => new(FailureKind.NotFound, code);
    public static Failure Conflict(string code) => new(FailureKind.Conflict, code);
    public static Failure Validation(string code, string? target = null) => new(FailureKind.Validation, code, Target: target);
}

// ---------------- B: language union
public union BResult<T>(T, Failure);
public union BUnitResult(Ok, Failure);
public readonly record struct Ok;

// ---------------- C1: hand-written [Union], T + Failure? + bool (review sketch)
[Union]
public readonly struct C1Result<T> : IUnion
{
    private readonly T _value; private readonly Failure? _failure; private readonly bool _ok;
    public C1Result(T value) { ArgumentNullException.ThrowIfNull(value); _value = value; _ok = true; }
    public C1Result(Failure failure) { ArgumentNullException.ThrowIfNull(failure); _value = default!; _failure = failure; }
    public object? Value => _ok ? _value : _failure ?? Failure.Uninitialized;
    public bool HasValue => true;
    public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _value; return _ok; }
    public bool TryGetValue([NotNullWhen(true)] out Failure? value) { value = _ok ? null : _failure ?? Failure.Uninitialized; return !_ok; }
}

// ---------------- C2: hand-written [Union], T + one state reference (null = default, sentinel = success)
[Union]
public readonly struct C2Result<T> : IUnion
{
    internal static readonly Failure OkSentinel = new(FailureKind.Uninitialized, "<ok>");
    private readonly T _value; private readonly Failure? _state;
    public C2Result(T value) { ArgumentNullException.ThrowIfNull(value); _value = value; _state = OkSentinel; }
    public C2Result(Failure failure) { ArgumentNullException.ThrowIfNull(failure); _value = default!; _state = failure; }
    public bool IsSuccess => ReferenceEquals(_state, OkSentinel);
    public object? Value => IsSuccess ? _value : _state ?? Failure.Uninitialized;
    public bool HasValue => true;
    public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _value; return IsSuccess; }
    public bool TryGetValue([NotNullWhen(true)] out Failure? value) { value = IsSuccess ? null : _state ?? Failure.Uninitialized; return value is not null; }
    public static implicit operator C2Result<T>(T v) => new(v);
    public static implicit operator C2Result<T>(Failure f) => new(f);
    public C2Result<TN> Map<TN>(Func<T, TN> f) => IsSuccess ? new C2Result<TN>(f(_value)) : new C2Result<TN>(_state ?? Failure.Uninitialized);
    public C2Result<TN> Bind<TN>(Func<T, C2Result<TN>> f) => IsSuccess ? f(_value) : new C2Result<TN>(_state ?? Failure.Uninitialized);
    public TR Match<TR>(Func<T, TR> ok, Func<Failure, TR> fail) => IsSuccess ? ok(_value) : fail(_state ?? Failure.Uninitialized);
    public async ValueTask<C2Result<TN>> BindAsync<TN>(Func<T, ValueTask<C2Result<TN>>> f) => IsSuccess ? await f(_value) : new C2Result<TN>(_state ?? Failure.Uninitialized);
}
// non-generic: one reference, 8 B
[Union]
public readonly struct C2Result : IUnion
{
    private readonly Failure? _state;
    public C2Result(Ok _) { _state = C2Result<Ok>.OkSentinel; }
    public C2Result(Failure failure) { ArgumentNullException.ThrowIfNull(failure); _state = failure; }
    public bool IsSuccess => ReferenceEquals(_state, C2Result<Ok>.OkSentinel);
    public object? Value => IsSuccess ? default(Ok) : _state ?? Failure.Uninitialized;
    public bool HasValue => true;
    public bool TryGetValue(out Ok value) { value = default; return IsSuccess; }
    public bool TryGetValue([NotNullWhen(true)] out Failure? value) { value = IsSuccess ? null : _state ?? Failure.Uninitialized; return value is not null; }
    public static C2Result Success { get; } = new(default(Ok));
    public static implicit operator C2Result(Failure f) => new(f);
}

// ---------------- D: C2 + closed failure hierarchy
[System.Text.Json.Serialization.JsonPolymorphic(InferClosedTypePolymorphism = true)]
public closed record Problem(string Code, string? Detail = null);
public sealed record NotFound(string Code, string? Detail = null) : Problem(Code, Detail);
public sealed record Conflict(string Code, string? Detail = null) : Problem(Code, Detail);
public sealed record Invalid(string Code, string? Target = null) : Problem(Code);
public sealed record Uninitialized() : Problem("Result.Uninitialized") { public static Uninitialized Instance { get; } = new(); }

[Union]
public readonly struct DResult<T> : IUnion
{
    private static readonly Problem OkSentinel = new NotFound("<ok>");
    private readonly T _value; private readonly Problem? _state;
    public DResult(T value) { ArgumentNullException.ThrowIfNull(value); _value = value; _state = OkSentinel; }
    public DResult(Problem failure) { ArgumentNullException.ThrowIfNull(failure); _value = default!; _state = failure; }
    public bool IsSuccess => ReferenceEquals(_state, OkSentinel);
    public object? Value => IsSuccess ? _value : _state ?? Uninitialized.Instance;
    public bool HasValue => true;
    public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _value; return IsSuccess; }
    public bool TryGetValue([NotNullWhen(true)] out Problem? value) { value = IsSuccess ? null : _state ?? Uninitialized.Instance; return value is not null; }
    public static implicit operator DResult<T>(T v) => new(v);
    public static implicit operator DResult<T>(Problem f) => new(f);
}

public readonly record struct Id<T>(Guid Value);
public sealed class User { public string Name { get; init; } = ""; }
