// Showcase types. Not the final API: just enough to show user code and JSON per shape.
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Show { public sealed record User(Guid Id, string Name); }

// ---------- A: today's shape (record struct + Violation struct), unchanged
namespace Show.A
{
    public enum ViolationType : byte { Validation = 0, NotFound = 3, Conflict = 4, Internal = 8 }
    public readonly record struct Violation
    {
        public required string Code { get; init; }
        public required ViolationType Type { get; init; }
        public string? Detail { get; init; }
        public string? PropertyName { get; init; }
        public IReadOnlyList<Violation>? InnerErrors { get; init; }
        public static Violation Validation(string code, string? propertyName = null) => new() { Code = code, Type = ViolationType.Validation, PropertyName = propertyName };
        public static Violation NotFound(string code) => new() { Code = code, Type = ViolationType.NotFound };
        public static Violation Aggregate(string code, IReadOnlyList<Violation> inner) => new() { Code = code, Type = ViolationType.Validation, InnerErrors = inner };
    }
    public readonly record struct Result<T>
    {
        private readonly T? _value; private readonly Violation _error; private readonly bool _ok;
        private Result(T v) { _value = v; _error = default; _ok = true; }
        private Result(Violation e) { _value = default; _error = e; _ok = false; }
        [MemberNotNullWhen(true, nameof(Value))] public bool IsSuccess => _ok;
        public bool IsFailure => !_ok;
        public T Value => _ok ? _value! : throw new InvalidOperationException($"Cannot access Value on failed Result. Violation: {_error.Code}");
        public Violation Violation => _error;
        public static implicit operator Result<T>(T v) => new(v);
        public static implicit operator Result<T>(Violation e) => new(e);
        public TOut Match<TOut>(Func<T, TOut> ok, Func<Violation, TOut> fail) => _ok ? ok(_value!) : fail(_error);
    }
}

// ---------- C with M1: [Union] struct + Violation as a sealed class (today's fields)
namespace Show.M1
{
    [JsonConverter(typeof(JsonStringEnumConverter<ViolationType>))]
    public enum ViolationType : byte { Validation, NotFound, Conflict, Internal }
    public sealed record Violation(ViolationType Type, string Code, string? Detail = null, string? PropertyName = null)
    {
        public IReadOnlyList<Violation> InnerErrors { get; init; } = [];
        public static Violation Validation(string code, string? propertyName = null) => new(ViolationType.Validation, code, PropertyName: propertyName);
        public static Violation NotFound(string code, string? detail = null) => new(ViolationType.NotFound, code, detail);
        public static Violation Aggregate(IReadOnlyList<Violation> inner) => new(ViolationType.Validation, "Validation.Multiple") { InnerErrors = inner };
        public static Violation Uninitialized { get; } = new(ViolationType.Internal, "Result.Uninitialized");
    }
    [Union, JsonConverter(typeof(ResultJsonConverter<>))]
    public readonly struct Result<T> : IUnion
    {
        internal static readonly Violation Ok = new(ViolationType.Internal, "<ok>");
        private readonly T _value; private readonly Violation? _state;
        public Result(T value) { ArgumentNullException.ThrowIfNull(value); _value = value; _state = Ok; }
        public Result(Violation violation) { ArgumentNullException.ThrowIfNull(violation); _value = default!; _state = violation; }
        public bool IsSuccess => ReferenceEquals(_state, Ok);
        public object? Value => IsSuccess ? _value : _state ?? Violation.Uninitialized;
        public bool HasValue => true;
        public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _value; return IsSuccess; }
        public bool TryGetValue([NotNullWhen(true)] out Violation? value) { value = IsSuccess ? null : _state ?? Violation.Uninitialized; return value is not null; }
        public static implicit operator Result<T>(T v) => new(v);
        public static implicit operator Result<T>(Violation v) => new(v);
    }
    public sealed class ResultJsonConverter<T> : JsonConverter<Result<T>>
    {
        public override Result<T> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            Result<T>? r = null; reader.Read();
            while (reader.TokenType == JsonTokenType.PropertyName)
            {
                var n = reader.GetString(); reader.Read();
                if (n == "value") r = new Result<T>(JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T)))!);
                else if (n == "failure") r = new Result<T>(JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<Violation>)o.GetTypeInfo(typeof(Violation)))!);
                else reader.Skip();
                reader.Read();
            }
            return r ?? throw new JsonException("expected 'value' or 'failure'");
        }
        public override void Write(Utf8JsonWriter w, Result<T> r, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            switch (r)
            {
                case T v: w.WritePropertyName("value"); JsonSerializer.Serialize(w, v, (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T))); break;
                case Violation f: w.WritePropertyName("failure"); JsonSerializer.Serialize(w, f, (JsonTypeInfo<Violation>)o.GetTypeInfo(typeof(Violation))); break;
            }
            w.WriteEndObject();
        }
    }
}

// ---------- C with M2: [Union] struct + exactly two failure cases (the owner's model):
//            validation errors, or one light, non-fatal error
namespace Show.M2
{
    [JsonConverter(typeof(JsonStringEnumConverter<ErrorType>))]
    public enum ErrorType : byte { NotFound, Conflict, Forbidden, Unauthorized, Unavailable, Internal }
    public sealed record FieldError(string Field, string Code, string? Detail = null);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Invalid), "invalid")]
    [JsonDerivedType(typeof(Error), "error")]
    public closed record Failure;
    public sealed record Invalid(IReadOnlyList<FieldError> Errors) : Failure
    {
        public static Invalid Field(string field, string code, string? detail = null) => new([new FieldError(field, code, detail)]);
    }
    public sealed record Error(ErrorType Type, string Code, string? Detail = null) : Failure
    {
        public static Error NotFound(string code, string? detail = null) => new(ErrorType.NotFound, code, detail);
        public static Error Uninitialized { get; } = new(ErrorType.Internal, "Result.Uninitialized");
    }

    [Union, JsonConverter(typeof(ResultJsonConverter<>))]
    public readonly struct Result<T> : IUnion
    {
        internal static readonly Failure Ok = new Error(ErrorType.Internal, "<ok>");
        private readonly T _value; private readonly Failure? _state;
        public Result(T value) { ArgumentNullException.ThrowIfNull(value); _value = value; _state = Ok; }
        public Result(Failure failure) { ArgumentNullException.ThrowIfNull(failure); _value = default!; _state = failure; }
        public bool IsSuccess => ReferenceEquals(_state, Ok);
        public object? Value => IsSuccess ? _value : _state ?? Error.Uninitialized;
        public bool HasValue => true;
        public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _value; return IsSuccess; }
        public bool TryGetValue([NotNullWhen(true)] out Failure? value) { value = IsSuccess ? null : _state ?? Error.Uninitialized; return value is not null; }
        public static implicit operator Result<T>(T v) => new(v);
        public static implicit operator Result<T>(Failure f) => new(f);
        public Failure? Failure => IsSuccess ? null : _state ?? Error.Uninitialized;
    }
    public sealed class ResultJsonConverter<T> : JsonConverter<Result<T>>
    {
        public override Result<T> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            Result<T>? r = null; reader.Read();
            while (reader.TokenType == JsonTokenType.PropertyName)
            {
                var n = reader.GetString(); reader.Read();
                if (n == "value") r = new Result<T>(JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T)))!);
                else if (n == "failure") r = new Result<T>(JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<Failure>)o.GetTypeInfo(typeof(Failure)))!);
                else reader.Skip();
                reader.Read();
            }
            return r ?? throw new JsonException("expected 'value' or 'failure'");
        }
        public override void Write(Utf8JsonWriter w, Result<T> r, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            if (r is T v) { w.WritePropertyName("value"); JsonSerializer.Serialize(w, v, (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T))); }
            else { w.WritePropertyName("failure"); JsonSerializer.Serialize(w, r.Failure!, (JsonTypeInfo<Failure>)o.GetTypeInfo(typeof(Failure))); }
            w.WriteEndObject();
        }
    }
}

// ---------- D: [Union] struct + a closed hierarchy with one record per kind
namespace Show.D
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(InvalidField), "invalidField")]
    [JsonDerivedType(typeof(NotFound), "notFound")]
    [JsonDerivedType(typeof(Conflict), "conflict")]
    [JsonDerivedType(typeof(Forbidden), "forbidden")]
    [JsonDerivedType(typeof(Unexpected), "unexpected")]
    public closed record Failure(string Code);
    public sealed record InvalidField(string Code, string Field) : Failure(Code);
    public sealed record NotFound(string Code, string? Detail = null) : Failure(Code);
    public sealed record Conflict(string Code, string? Detail = null) : Failure(Code);
    public sealed record Forbidden(string Code) : Failure(Code);
    public sealed record Unexpected(string Code, string? Detail = null) : Failure(Code);
}

namespace Show.D
{
    [Union, JsonConverter(typeof(ResultJsonConverter<>))]
    public readonly struct Result<T> : IUnion
    {
        internal static readonly Failure Ok = new Unexpected("<ok>");
        internal static readonly Failure Uninit = new Unexpected("Result.Uninitialized");
        private readonly T _value; private readonly Failure? _state;
        public Result(T value) { ArgumentNullException.ThrowIfNull(value); _value = value; _state = Ok; }
        public Result(Failure failure) { ArgumentNullException.ThrowIfNull(failure); _value = default!; _state = failure; }
        public bool IsSuccess => ReferenceEquals(_state, Ok);
        public object? Value => IsSuccess ? _value : _state ?? Uninit;
        public bool HasValue => true;
        public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _value; return IsSuccess; }
        public bool TryGetValue([NotNullWhen(true)] out Failure? value) { value = IsSuccess ? null : _state ?? Uninit; return value is not null; }
        public static implicit operator Result<T>(T v) => new(v);
        public static implicit operator Result<T>(Failure f) => new(f);
    }
    public sealed class ResultJsonConverter<T> : JsonConverter<Result<T>>
    {
        public override Result<T> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, Result<T> r, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            switch (r)
            {
                case T v: w.WritePropertyName("value"); JsonSerializer.Serialize(w, v, (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T))); break;
                case Failure f: w.WritePropertyName("failure"); JsonSerializer.Serialize(w, f, (JsonTypeInfo<Failure>)o.GetTypeInfo(typeof(Failure))); break;
            }
            w.WriteEndObject();
        }
    }
}
namespace Show.B { public union Result<T>(T, Show.M1.Violation); }
