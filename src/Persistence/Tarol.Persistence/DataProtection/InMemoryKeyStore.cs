using System.Security.Cryptography;

namespace Tarol.Persistence;

/// <summary>
/// An <see cref="IKeyStore"/> in memory, for tests and samples: the same two levels as the database store (data keys wrapped
/// by tenant keys, wrapped by the master key), tombstones with their erasure time from the injected clock, and the
/// <see cref="Erased"/> signal. Thread-safe.
/// </summary>
public sealed class InMemoryKeyStore : IKeyStore
{
    private readonly IKeyEncryptionKey _master;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, TenantRow> _tenants = [];
    private readonly Dictionary<(Guid Tenant, Guid Subject), SubjectRow> _subjects = [];

    /// <summary>Creates a store.</summary>
    /// <param name="master">The master key.</param>
    /// <param name="time">The clock for the erasure times.</param>
    public InMemoryKeyStore(IKeyEncryptionKey master, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(time);
        _master = master;
        _time = time;
    }

    /// <summary>A store with a random master key (tests).</summary>
    /// <param name="time">The clock; <see langword="null"/> = the system clock.</param>
    /// <returns>The store.</returns>
    public static InMemoryKeyStore Create(TimeProvider? time = null)
    {
        Span<byte> key = stackalloc byte[SubjectKey.KeySize];
        RandomNumberGenerator.Fill(key);
        var store = new InMemoryKeyStore(new ConfiguredKeyEncryptionKey("test", key), time ?? TimeProvider.System);
        CryptographicOperations.ZeroMemory(key);
        return store;
    }

    /// <inheritdoc />
    public event EventHandler<KeyErasedEventArgs>? Erased;

    /// <summary>When a subject was erased; <see langword="null"/> when it is not.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <returns>The erasure time.</returns>
    public DateTimeOffset? ErasedAt(Guid tenant, Guid subject)
    {
        lock (_gate)
            return _subjects.TryGetValue((tenant, subject), out var row) ? row.ErasedAt : null;
    }

    /// <inheritdoc />
    public ValueTask<KeyRing> Keys(Guid tenant, IReadOnlyCollection<Guid> subjects, bool create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        cancellationToken.ThrowIfCancellationRequested();
        var ring = new KeyRing(tenant);
        Span<byte> material = stackalloc byte[SubjectKey.KeySize];
        Span<byte> tenantMaterial = stackalloc byte[SubjectKey.KeySize];
        lock (_gate)
        {
            var tenantKey = TenantKey(tenant, create && subjects.Count > 0, tenantMaterial);
            using var wrap = tenantKey ? new KeyWrap(tenantMaterial) : null;
            foreach (var subject in subjects)
            {
                if (ring.Contains(subject))
                    continue;
                if (wrap is null)
                {
                    ring.Add(SubjectKey.Erased(tenant, subject));
                    continue;
                }

                if (_subjects.TryGetValue((tenant, subject), out var row))
                {
                    if (row.Wrapped is null)
                    {
                        ring.Add(SubjectKey.Erased(tenant, subject));
                        continue;
                    }

                    wrap.Unwrap(row.Wrapped, KeyBinding.Subject(tenant, subject, row.Version), material);
                    ring.Add(new SubjectKey(tenant, subject, row.Version, material));
                }
                else if (create)
                {
                    var key = SubjectKey.Create(tenant, subject, material);
                    _subjects[(tenant, subject)] = new SubjectRow(1, wrap.Wrap(material, KeyBinding.Subject(tenant, subject, 1)), null);
                    ring.Add(key);
                }
                else
                {
                    ring.Add(SubjectKey.Erased(tenant, subject)); // never sealed here: nothing to open
                }
            }
        }

        CryptographicOperations.ZeroMemory(material);
        CryptographicOperations.ZeroMemory(tenantMaterial);
        return ValueTask.FromResult(ring);
    }

    /// <summary>Unwraps (or creates) the tenant key; <see langword="false"/> when the tenant is erased or has none.</summary>
    private bool TenantKey(Guid tenant, bool create, Span<byte> material)
    {
        if (_tenants.TryGetValue(tenant, out var row))
        {
            if (row.Wrapped is null)
                return false;
            _master.Unwrap(row.KeyId!, row.Wrapped, KeyBinding.Tenant(tenant), material);
            return true;
        }

        if (!create)
            return false;
        RandomNumberGenerator.Fill(material);
        _tenants[tenant] = new TenantRow(_master.KeyId, _master.Wrap(material, KeyBinding.Tenant(tenant)), null);
        return true;
    }

    /// <inheritdoc />
    public ValueTask<bool> EraseSubject(Guid tenant, Guid subject, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool erased;
        lock (_gate)
        {
            erased = !_subjects.TryGetValue((tenant, subject), out var row) || row.Wrapped is not null;
            if (erased)
                _subjects[(tenant, subject)] = new SubjectRow(row?.Version ?? 1, null, _time.GetUtcNow());
        }

        if (erased)
            Erased?.Invoke(this, new KeyErasedEventArgs(tenant, subject));
        return ValueTask.FromResult(erased);
    }

    /// <inheritdoc />
    public ValueTask<bool> EraseTenant(Guid tenant, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool erased;
        lock (_gate)
        {
            erased = !_tenants.TryGetValue(tenant, out var row) || row.Wrapped is not null;
            if (erased)
                _tenants[tenant] = new TenantRow(null, null, _time.GetUtcNow());
        }

        if (erased)
            Erased?.Invoke(this, new KeyErasedEventArgs(tenant, null));
        return ValueTask.FromResult(erased);
    }

    /// <inheritdoc />
    public ValueTask<string?> BlindIndex(Guid tenant, string lookup, string value, bool create, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> material = stackalloc byte[SubjectKey.KeySize];
        string? index = null;
        lock (_gate)
        {
            if (TenantKey(tenant, create, material))
                index = KeyBinding.BlindIndex(material, lookup, value);
        }

        CryptographicOperations.ZeroMemory(material);
        return ValueTask.FromResult(index);
    }

    /// <inheritdoc />
    public ValueTask<int> Rewrap(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = 0;
        Span<byte> material = stackalloc byte[SubjectKey.KeySize];
        lock (_gate)
        {
            foreach (var (tenant, row) in _tenants.ToArray())
            {
                if (row.Wrapped is null || row.KeyId == _master.KeyId)
                    continue;
                _master.Unwrap(row.KeyId!, row.Wrapped, KeyBinding.Tenant(tenant), material);
                _tenants[tenant] = row with { KeyId = _master.KeyId, Wrapped = _master.Wrap(material, KeyBinding.Tenant(tenant)) };
                count++;
            }
        }

        CryptographicOperations.ZeroMemory(material);
        return ValueTask.FromResult(count);
    }

    private sealed record TenantRow(string? KeyId, byte[]? Wrapped, DateTimeOffset? ErasedAt);

    private sealed record SubjectRow(int Version, byte[]? Wrapped, DateTimeOffset? ErasedAt);
}
