using Tarol.Primitives;

namespace Tarol.Persistence;

/// <summary>
/// The unique lookups of a store: a key function per (aggregate, name), evaluated on every commit (C-16, P-8). Define them
/// once and pass the same instance to the in-memory and the Npgsql store, so tests and production refuse the same keys.
/// <code>
/// var lookups = new StoreLookups().Define&lt;Auction&gt;("lot", a => a.Lot);
/// </code>
/// </summary>
/// <remarks>An empty or <see langword="null"/> key is not indexed. Define everything before the store is used.</remarks>
public sealed class StoreLookups
{
    private readonly Lock _gate = new();
    private volatile Dictionary<Type, (string Name, Func<object, string?> KeyOf)[]> _byType = [];
    private volatile HashSet<(Type, string)> _personal = [];
    private volatile HashSet<(string, string)> _personalByStream = [];

    /// <summary>Defines a unique lookup of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="name">The lookup's name, used by <see cref="IAggregateReader.Find{T}"/>.</param>
    /// <param name="key">The key of an aggregate after its events, or <see langword="null"/> for none.</param>
    /// <returns>This instance.</returns>
    /// <exception cref="InvalidOperationException">A lookup of that name is already defined for <typeparamref name="T"/>.</exception>
    public StoreLookups Define<T>(string name, Func<T, string?> key)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var existing = _byType.GetValueOrDefault(typeof(T)) ?? [];
            if (existing.Any(l => l.Name == name))
                throw new InvalidOperationException($"The lookup '{name}' of {typeof(T).Name} is already defined.");
            _byType = new Dictionary<Type, (string, Func<object, string?>)[]>(_byType)
            {
                [typeof(T)] = [.. existing, (name, aggregate => key((T)aggregate))],
            };
        }
        return this;
    }

    /// <summary>
    /// Defines a unique lookup over a personal value (an email, a phone number; DP-5): the store keeps a blind index, an
    /// HMAC-SHA256 of the key under the tenant's lookup key (<see cref="IKeyStore.BlindIndex"/>), never the value, and
    /// <c>Find</c> hashes the same way. Erasing the tenant makes it unfindable; erasing a subject deletes its rows.
    /// </summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="name">The lookup's name.</param>
    /// <param name="key">The key of an aggregate, normalized as callers of <c>Find</c> will pass it (e.g. lowercase), or <see langword="null"/>.</param>
    /// <returns>These lookups.</returns>
    /// <exception cref="InvalidOperationException">A lookup of that name is defined for <typeparamref name="T"/> already.</exception>
    public StoreLookups DefinePersonal<T>(string name, Func<T, string?> key)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        Define(name, key);
        lock (_gate)
        {
            _personal = [.. _personal, (typeof(T), name)];
            _personalByStream = [.. _personalByStream, (T.StreamType, name)];
        }
        return this;
    }

    /// <summary>Whether a lookup was defined with <see cref="DefinePersonal{T}"/> (its keys are blind indexes).</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="name">The lookup's name.</param>
    /// <returns><see langword="true"/> for a personal lookup.</returns>
    public bool IsPersonal<T>(string name)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => _personal.Contains((typeof(T), name));

    /// <summary>Whether a lookup of a stream type was defined with <see cref="DefinePersonal{T}"/>.</summary>
    /// <param name="streamType">The stream type.</param>
    /// <param name="name">The lookup's name.</param>
    /// <returns><see langword="true"/> for a personal lookup.</returns>
    public bool IsPersonal(string streamType, string name) => _personalByStream.Contains((streamType, name));

    /// <summary>Whether <typeparamref name="T"/> has a personal lookup.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <returns><see langword="true"/> when it has one.</returns>
    public bool HasPersonal<T>()
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => _personal.Any(static p => p.Item1 == typeof(T));

    /// <summary>Whether <paramref name="name"/> is defined for <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="name">The lookup's name.</param>
    /// <returns>The answer.</returns>
    public bool IsDefined<T>(string name)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => _byType.GetValueOrDefault(typeof(T)) is { } lookups && lookups.Any(l => l.Name == name);

    /// <summary>Every lookup key of <paramref name="aggregate"/>; an empty key is returned as <see langword="null"/>.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="aggregate">The aggregate after its uncommitted events.</param>
    /// <returns>(lookup name, key or <see langword="null"/>) per defined lookup.</returns>
    public IReadOnlyList<(string Lookup, string? Key)> KeysOf<T>(T aggregate)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (_byType.GetValueOrDefault(typeof(T)) is not { } lookups)
            return [];
        var keys = new (string, string?)[lookups.Length];
        for (var i = 0; i < lookups.Length; i++)
            keys[i] = (lookups[i].Name, lookups[i].KeyOf(aggregate) is { Length: > 0 } k ? k : null);
        return keys;
    }
}
