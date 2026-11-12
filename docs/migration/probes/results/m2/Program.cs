using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using M2;
Result<int> ok = 1, inv = new Invalid([new FieldError("Name", "Name.Required")]), err = Error.NotFound("User.NotFound"), def = default;
foreach (var r in new[] { ok, inv, err, def }) Console.WriteLine($"{Split(r)} | {Propagate(r)}");
long a = GC.GetAllocatedBytesForCurrentThread(); int s = 0; for (int i = 0; i < 1000; i++) { Result<int> r = i; s += r switch { int v => v, Failure => 0 }; }
Console.WriteLine($"alloc per 1000 success+switch: {GC.GetAllocatedBytesForCurrentThread() - a} B; size Result<int>={Unsafe.SizeOf<Result<int>>()} Result<Guid>={Unsafe.SizeOf<Result<Guid>>()}");

static string Split(Result<int> r) => r switch { int v => $"ok {v}", Invalid i => $"invalid {i.Errors.Count}", Error e => $"error {e.Type} {e.Code}" };   // L6
static Result<string> Propagate(Result<int> r) => r switch { int v => v.ToString(), Failure f => f };                                                       // L7
static string Missing(Result<int> r) => r switch { int v => "ok", Invalid => "invalid" };                                                                    // L8

namespace M2
{
    public enum ErrorType : byte { NotFound, Conflict, Forbidden, Unauthorized, Unavailable, Internal }
    public sealed record FieldError(string Field, string Code, string? Detail = null);
    public closed record Failure;
    public sealed record Invalid(IReadOnlyList<FieldError> Errors) : Failure;
    public sealed record Error(ErrorType Type, string Code, string? Detail = null) : Failure
    {
        public static Error NotFound(string code) => new(ErrorType.NotFound, code);
        public static Error Uninitialized { get; } = new(ErrorType.Internal, "Result.Uninitialized");
    }
    [Union]
    public readonly struct Result<T> : IUnion
    {
        private static readonly Failure Ok = new Error(ErrorType.Internal, "<ok>");
        private readonly T _value; private readonly Failure? _state;
        public Result(T value) { _value = value; _state = Ok; }
        public Result(Failure failure) { _value = default!; _state = failure; }
        public bool IsSuccess => ReferenceEquals(_state, Ok);
        public object? Value => IsSuccess ? _value : _state ?? Error.Uninitialized;
        public bool HasValue => true;
        public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _value; return IsSuccess; }
        public bool TryGetValue([NotNullWhen(true)] out Failure? value) { value = IsSuccess ? null : _state ?? Error.Uninitialized; return value is not null; }
        public static implicit operator Result<T>(T v) => new(v);
        public static implicit operator Result<T>(Failure f) => new(f);
    }
}
