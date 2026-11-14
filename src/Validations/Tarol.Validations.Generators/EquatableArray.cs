using System;
using System.Collections.Immutable;

namespace Tarol.Validations.Generators;

/// <summary>
///     ImmutableArray wrapper with structural equality. Required because
///     ImmutableArray&lt;T&gt; compares by reference, which would defeat the
///     incremental pipeline's caching and re-emit source on every edit.
/// </summary>
internal readonly struct EquatableArray<T>(ImmutableArray<T> array) : IEquatable<EquatableArray<T>>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _array = array;

    public bool IsEmpty => _array.IsDefaultOrEmpty;

    public int Count => _array.IsDefault ? 0 : _array.Length;

    public ImmutableArray<T>.Enumerator GetEnumerator()
    {
        return (_array.IsDefault ? ImmutableArray<T>.Empty : _array).GetEnumerator();
    }

    public bool Equals(EquatableArray<T> other)
    {
        var a = _array.IsDefault ? ImmutableArray<T>.Empty : _array;
        var b = other._array.IsDefault ? ImmutableArray<T>.Empty : other._array;

        if (a.Length != b.Length)
            return false;

        for (var i = 0; i < a.Length; i++)
            if (!a[i].Equals(b[i]))
                return false;

        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is EquatableArray<T> other && Equals(other);
    }

    public override int GetHashCode()
    {
        if (_array.IsDefaultOrEmpty)
            return 0;

        var hash = 17;
        foreach (var item in _array)
            hash = unchecked(hash * 31 + item.GetHashCode());
        return hash;
    }
}
