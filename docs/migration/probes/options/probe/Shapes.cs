using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Tarol.Results;
namespace Probe
{

public readonly record struct Id<T>(Guid Value);
public sealed record User(string Name);

// ---------------- B: the old extensions, fixed: class and struct overloads, both unwrap
namespace B
{
    public static class OptionClassExtensions
    {
        extension<T>([NotNullWhen(true)] T? option) where T : class
        {
            public bool IsSome => option is not null;
            public T Or(T fallback) => option ?? fallback;
            public TOut Match<TOut>(Func<T, TOut> some, Func<TOut> none) => option is not null ? some(option) : none();
            public TOut? Map<TOut>(Func<T, TOut> map) where TOut : class => option is not null ? map(option) : null;
            public TOut? MapStruct<TOut>(Func<T, TOut> map) where TOut : struct => option is not null ? map(option) : null;
        }
    }
    public static class OptionStructExtensions
    {
        extension<T>([NotNullWhen(true)] T? option) where T : struct
        {
            public bool IsSome => option.HasValue;
            public T Or(T fallback) => option ?? fallback;
            public TOut Match<TOut>(Func<T, TOut> some, Func<TOut> none) => option is { } v ? some(v) : none();
            public TOut? Map<TOut>(Func<T, TOut> map) where TOut : struct => option is { } v ? map(v) : null;
            public TOut? MapClass<TOut>(Func<T, TOut> map) where TOut : class => option is { } v ? map(v) : null;
        }
    }
}

// ---------------- C: hand-written [Union] struct, cases T and None; default = None
public readonly struct None : IEquatable<None>
{
    public bool Equals(None other) => true;
    public override bool Equals(object? obj) => obj is None;
    public override int GetHashCode() => 0;
    public override string ToString() => "None";
}

public static class Option
{
    public static None None => default;
    public static Option<T> Some<T>(T value) where T : notnull => new(value);
    public static Option<T> FromNullable<T>(T? value) where T : class => value is null ? default : new(value);
    public static Option<T> FromNullable<T>(T? value) where T : struct => value is { } v ? new(v) : default;
}

[Union]
[System.Text.Json.Serialization.JsonConverter(typeof(OptionNullConverter<>))]
public readonly struct Option<T> : IUnion, IEquatable<Option<T>> where T : notnull
{
    public bool Equals(Option<T> other) => _isSome ? other._isSome && EqualityComparer<T>.Default.Equals(_value, other._value) : !other._isSome;
    public override bool Equals(object? obj) => obj is Option<T> o && Equals(o);
    public override int GetHashCode() => _isSome ? HashCode.Combine(_value) : 0;
    public static bool operator ==(Option<T> a, Option<T> b) => a.Equals(b);
    public static bool operator !=(Option<T> a, Option<T> b) => !a.Equals(b);
    private readonly T _value;
    private readonly bool _isSome;
    public Option(T value)
    {
        if (!typeof(T).IsValueType && value is null) throw new ArgumentNullException(nameof(value));
        _value = value; _isSome = true;
    }
    public Option(None none) { _value = default!; _isSome = false; }
    public bool IsSome => _isSome;
    public bool IsNone => !_isSome;
    public object Value => _isSome ? _value : default(None);
    public bool HasValue => true;
    public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _value; return _isSome; }
    public bool TryGetValue(out None none) { none = default; return !_isSome; }
    public T GetValueOrThrow() => _isSome ? _value : throw new InvalidOperationException("None");
    public T GetValueOrDefault(T fallback) => _isSome ? _value : fallback;
    public static implicit operator Option<T>(T value) => new(value);
    public static implicit operator Option<T>(None none) => default;
    public Option<TOut> Map<TOut>(Func<T, TOut> map) where TOut : notnull => _isSome ? new(map(_value)) : default;
    public Option<TOut> Bind<TOut>(Func<T, Option<TOut>> bind) where TOut : notnull => _isSome ? bind(_value) : default;
    public Option<T> Filter(Func<T, bool> predicate) => _isSome && predicate(_value) ? this : default;
    public TOut Match<TOut>(Func<T, TOut> some, Func<TOut> none) => _isSome ? some(_value) : none();
    public Result<T> ToResult(Violation violation) => _isSome ? new Result<T>(_value) : new Result<T>(violation);
}

public static class ResultToOption
{
    extension<T>(Result<T> result) where T : notnull
    {
        public Option<T> ToOption() => result.TryGetValue(out T? v) ? new Option<T>(v) : default;
    }
}

// ---------------- D: language union
public union DOption<T>(T, None);

}
