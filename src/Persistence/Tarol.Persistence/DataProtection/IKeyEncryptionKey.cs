using System.Security.Cryptography;

namespace Tarol.Persistence;

/// <summary>
/// The master key outside the database (DP-4): it wraps and unwraps tenant keys. The default reads a 256-bit key from
/// configuration (<see cref="ConfiguredKeyEncryptionKey"/>); a KMS-backed one (e.g. Infisical) follows as its own package.
/// </summary>
/// <remarks>
/// Rotation: a new <see cref="KeyId"/> becomes current while the old ids still unwrap; <see cref="IKeyStore.Rewrap"/> then
/// re-wraps every tenant key under the current id. No event and no data key is touched.
/// </remarks>
public interface IKeyEncryptionKey
{
    /// <summary>The id of the key that wraps new tenant keys (stored next to each wrapped key).</summary>
    string KeyId { get; }

    /// <summary>Wraps a tenant key under <see cref="KeyId"/>.</summary>
    /// <param name="tenantKey">The 32-byte tenant key.</param>
    /// <param name="associatedData">What it is bound to (the tenant).</param>
    /// <returns>The wrapped key.</returns>
    byte[] Wrap(ReadOnlySpan<byte> tenantKey, ReadOnlySpan<byte> associatedData);

    /// <summary>Unwraps a tenant key wrapped under <paramref name="keyId"/>.</summary>
    /// <param name="keyId">The id it was wrapped under.</param>
    /// <param name="wrapped">The wrapped key.</param>
    /// <param name="associatedData">The data it was wrapped with.</param>
    /// <param name="tenantKey">Receives the 32 key bytes.</param>
    /// <exception cref="CryptographicException">The key id is unknown, or the wrapped key does not open.</exception>
    void Unwrap(string keyId, ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> associatedData, Span<byte> tenantKey);
}

/// <summary>
/// A master key from configuration: <c>Tarol:DataProtection:MasterKey</c> (base64, 32 bytes) and
/// <c>Tarol:DataProtection:MasterKeyId</c>, plus earlier keys kept for unwrapping during a rotation. Keep the key in a
/// secret store, never in a repository's <c>appsettings.json</c>.
/// </summary>
public sealed class ConfiguredKeyEncryptionKey : IKeyEncryptionKey, IDisposable
{
    private readonly Dictionary<string, KeyWrap> _keys = new(StringComparer.Ordinal);

    /// <summary>Creates the key.</summary>
    /// <param name="keyId">The current key's id (stored with every wrapped tenant key).</param>
    /// <param name="key">The current 32-byte key.</param>
    /// <param name="previous">Earlier keys by id, still accepted for unwrapping.</param>
    /// <exception cref="ArgumentException">A key is not 32 bytes, or an id is empty or repeated.</exception>
    public ConfiguredKeyEncryptionKey(string keyId, ReadOnlySpan<byte> key, IEnumerable<KeyValuePair<string, byte[]>>? previous = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        KeyId = keyId;
        _keys[keyId] = new KeyWrap(key);
        foreach (var (id, bytes) in previous ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (!_keys.TryAdd(id, new KeyWrap(bytes)))
                throw new ArgumentException($"The master key id '{id}' is given twice.", nameof(previous));
        }
    }

    /// <inheritdoc />
    public string KeyId { get; }

    /// <summary>Creates the key from base64 (as configuration holds it).</summary>
    /// <param name="keyId">The key's id.</param>
    /// <param name="base64Key">The 32-byte key in base64.</param>
    /// <returns>The key.</returns>
    /// <exception cref="FormatException"><paramref name="base64Key"/> is not base64.</exception>
    public static ConfiguredKeyEncryptionKey FromBase64(string keyId, string base64Key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        var bytes = Convert.FromBase64String(base64Key);
        try
        {
            return new ConfiguredKeyEncryptionKey(keyId, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc />
    public byte[] Wrap(ReadOnlySpan<byte> tenantKey, ReadOnlySpan<byte> associatedData) => _keys[KeyId].Wrap(tenantKey, associatedData);

    /// <inheritdoc />
    public void Unwrap(string keyId, ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> associatedData, Span<byte> tenantKey)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        if (!_keys.TryGetValue(keyId, out var wrap))
            throw new CryptographicException($"The master key '{keyId}' is not configured; keep earlier keys until IKeyStore.Rewrap has run.");
        wrap.Unwrap(wrapped, associatedData, tenantKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var wrap in _keys.Values)
            wrap.Dispose();
    }
}
