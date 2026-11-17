namespace Tarol.Persistence;

/// <summary>
/// The data keys one codec call may use, for one tenant: the keys of live subjects and the tombstones of erased ones. A key
/// store fills it (<see cref="IKeyStore.Keys"/>); the codec seals with it and opens with it. A subject the ring does not
/// hold is a bug of the caller: the codec throws rather than read its fields as erased.
/// </summary>
/// <remarks>Not thread-safe while it is filled; reading it from several threads afterwards is safe.</remarks>
public sealed class KeyRing
{
    private readonly Dictionary<Guid, SubjectKey> _keys = [];

    /// <summary>Creates an empty ring.</summary>
    /// <param name="tenant">The tenant (<see cref="Guid.Empty"/> without one).</param>
    public KeyRing(Guid tenant) => Tenant = tenant;

    /// <summary>The tenant every key of the ring belongs to.</summary>
    public Guid Tenant { get; }

    /// <summary>How many subjects the ring holds (tombstones included).</summary>
    public int Count => _keys.Count;

    /// <summary>The subjects the ring holds.</summary>
    public IEnumerable<Guid> Subjects => _keys.Keys;

    /// <summary>Adds (or replaces) a subject's key or tombstone.</summary>
    /// <param name="key">The key.</param>
    /// <returns>This ring.</returns>
    /// <exception cref="ArgumentException">The key belongs to another tenant.</exception>
    public KeyRing Add(SubjectKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Tenant != Tenant)
            throw new ArgumentException($"The key of subject {key.Subject} belongs to tenant {key.Tenant}, not to the ring's tenant {Tenant}.", nameof(key));
        _keys[key.Subject] = key;
        return this;
    }

    /// <summary>Whether the ring holds <paramref name="subject"/> (a key or a tombstone).</summary>
    /// <param name="subject">The subject.</param>
    /// <returns><see langword="true"/> when held.</returns>
    public bool Contains(Guid subject) => _keys.ContainsKey(subject);

    /// <summary>The key or tombstone of <paramref name="subject"/>.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="key">The key, when held.</param>
    /// <returns><see langword="true"/> when held.</returns>
    public bool TryGet(Guid subject, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SubjectKey? key) => _keys.TryGetValue(subject, out key);

    internal SubjectKey Get(Guid subject)
        => _keys.TryGetValue(subject, out var key) ? key
            : throw new InvalidOperationException($"The key ring of tenant {Tenant} holds no key for subject {subject}: fetch the keys of every subject first (IKeyStore.Keys).");
}
