using System.Buffers.Binary;

namespace Tarol.Persistence;

/// <summary>The associated data wrapped keys are bound to, so a wrapped key copied to another row does not unwrap.</summary>
public static class KeyBinding
{
    /// <summary>The associated data of a tenant key: the tenant (16 bytes).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Tenant(Guid tenant) => tenant.ToByteArray();

    /// <summary>The blind index of <paramref name="value"/> in <paramref name="lookup"/> under a tenant key (see <see cref="IKeyStore.BlindIndex"/>).</summary>
    /// <param name="tenantKey">The unwrapped 32-byte tenant key.</param>
    /// <param name="lookup">The lookup's name.</param>
    /// <param name="value">The key.</param>
    /// <returns>The index, base64url (43 characters).</returns>
    public static string BlindIndex(ReadOnlySpan<byte> tenantKey, string lookup, string value)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(value);
        Span<byte> lookupKey = stackalloc byte[32];
        System.Security.Cryptography.HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, tenantKey, lookupKey, salt: [], info: "tarol.lookup.v1"u8);
        var input = System.Text.Encoding.UTF8.GetBytes(string.Concat(lookup, "\n", value));
        Span<byte> mac = stackalloc byte[32];
        System.Security.Cryptography.HMACSHA256.HashData(lookupKey, input, mac);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(lookupKey);
        return System.Buffers.Text.Base64Url.EncodeToString(mac);
    }

    /// <summary>The associated data of a data key: tenant, subject and version (36 bytes).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="version">The key's version.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Subject(Guid tenant, Guid subject, int version)
    {
        var bytes = new byte[36];
        tenant.TryWriteBytes(bytes.AsSpan(0, 16));
        subject.TryWriteBytes(bytes.AsSpan(16, 16));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(32), version);
        return bytes;
    }
}
