using System.Security.Cryptography;

namespace Tarol.Persistence;

/// <summary>
/// A subject's data key (AES-256-GCM, unwrapped, in memory), or its tombstone once the subject is erased. Envelopes sealed
/// with it are bound to (tenant, subject): relabelled to another subject they fail to open.
/// </summary>
/// <remarks>
/// <see cref="AesGcm"/> is not safe for concurrent use (data-protection.md §5 item 1): this key uses its instance under a
/// lock, so one cached key serves every thread. A key store caches these and shares them between rings; they are not
/// disposed on eviction (the runtime releases the native handle).
/// </remarks>
public sealed class SubjectKey : IDisposable
{
    /// <summary>The size of a data key in bytes (AES-256).</summary>
    public const int KeySize = 32;

    private readonly Lock _gate = new();
    private readonly AesGcm? _aes;

    /// <summary>Creates an unwrapped key.</summary>
    /// <param name="tenant">The tenant (<see cref="Guid.Empty"/> without one).</param>
    /// <param name="subject">The subject.</param>
    /// <param name="version">The key's version (1 for the first key of the subject).</param>
    /// <param name="key">The 32 key bytes (copied; clear your copy).</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not 32 bytes.</exception>
    public SubjectKey(Guid tenant, Guid subject, int version, ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"A data key has {KeySize} bytes, not {key.Length}.", nameof(key));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        Tenant = tenant;
        Subject = subject;
        Version = version;
        _aes = new AesGcm(key, Envelope.TagSize);
    }

    private SubjectKey(Guid tenant, Guid subject)
    {
        Tenant = tenant;
        Subject = subject;
    }

    /// <summary>The tenant.</summary>
    public Guid Tenant { get; }

    /// <summary>The subject.</summary>
    public Guid Subject { get; }

    /// <summary>The key's version (0 for a tombstone).</summary>
    public int Version { get; }

    /// <summary>Whether the subject is erased: there is no key, its fields read as <see langword="null"/> and new data is refused.</summary>
    public bool IsErased => _aes is null;

    /// <summary>The tombstone of an erased subject.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <returns>A key without key material.</returns>
    public static SubjectKey Erased(Guid tenant, Guid subject) => new(tenant, subject);

    /// <summary>A new random data key.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="material">Receives the 32 key bytes, to be wrapped and stored.</param>
    /// <returns>The key.</returns>
    public static SubjectKey Create(Guid tenant, Guid subject, Span<byte> material)
    {
        RandomNumberGenerator.Fill(material[..KeySize]);
        return new SubjectKey(tenant, subject, 1, material[..KeySize]);
    }

    internal void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData)
    {
        var aes = _aes ?? throw new SubjectErasedException(Tenant, Subject);
        lock (_gate)
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
    }

    internal void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var aes = _aes ?? throw new SubjectErasedException(Tenant, Subject);
        lock (_gate)
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
    }

    /// <inheritdoc />
    public void Dispose() => _aes?.Dispose();
}

/// <summary>New personal data was sealed for an erased subject; a store returns <see cref="DataProtectionErrors.SubjectErased"/> instead.</summary>
public sealed class SubjectErasedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The erased subject.</param>
    public SubjectErasedException(Guid tenant, Guid subject)
        : base($"Subject {subject} is erased; new personal data for it is refused (a returning person is a new subject).")
    {
        Tenant = tenant;
        Subject = subject;
    }

    /// <summary>Creates the exception.</summary>
    public SubjectErasedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    public SubjectErasedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SubjectErasedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The tenant.</summary>
    public Guid Tenant { get; }

    /// <summary>The erased subject.</summary>
    public Guid Subject { get; }
}
