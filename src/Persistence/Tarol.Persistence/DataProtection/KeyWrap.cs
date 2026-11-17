using System.Security.Cryptography;

namespace Tarol.Persistence;

/// <summary>
/// Wraps 32-byte keys with an AES-256-GCM key: <c>nonce 12 | tag 16 | ciphertext 32</c> (60 bytes), bound to associated data.
/// The key encryption key wraps tenant keys with it, a tenant key wraps its subjects' data keys (DP-4).
/// </summary>
public sealed class KeyWrap : IDisposable
{
    /// <summary>The size of a wrapped 32-byte key.</summary>
    public const int WrappedSize = Envelope.NonceSize + Envelope.TagSize + SubjectKey.KeySize;

    private readonly Lock _gate = new();
    private readonly AesGcm _aes;

    /// <summary>Creates a wrap over a 32-byte key.</summary>
    /// <param name="key">The wrapping key (copied).</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not 32 bytes.</exception>
    public KeyWrap(ReadOnlySpan<byte> key)
    {
        if (key.Length != SubjectKey.KeySize)
            throw new ArgumentException($"A wrapping key has {SubjectKey.KeySize} bytes, not {key.Length}.", nameof(key));
        _aes = new AesGcm(key, Envelope.TagSize);
    }

    /// <summary>Wraps a 32-byte key.</summary>
    /// <param name="key">The key to wrap.</param>
    /// <param name="associatedData">What the wrapped key is bound to (its tenant, subject and version).</param>
    /// <returns>The wrapped key (<see cref="WrappedSize"/> bytes).</returns>
    public byte[] Wrap(ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData)
    {
        if (key.Length != SubjectKey.KeySize)
            throw new ArgumentException($"Only {SubjectKey.KeySize}-byte keys are wrapped.", nameof(key));
        var wrapped = new byte[WrappedSize];
        var nonce = wrapped.AsSpan(0, Envelope.NonceSize);
        RandomNumberGenerator.Fill(nonce);
        lock (_gate)
            _aes.Encrypt(nonce, key, wrapped.AsSpan(Envelope.NonceSize + Envelope.TagSize), wrapped.AsSpan(Envelope.NonceSize, Envelope.TagSize), associatedData);
        return wrapped;
    }

    /// <summary>Unwraps a key.</summary>
    /// <param name="wrapped">The wrapped key.</param>
    /// <param name="associatedData">The data it was wrapped with.</param>
    /// <param name="key">Receives the 32 key bytes.</param>
    /// <exception cref="CryptographicException">The wrapped key is malformed, tampered with, or bound to other data.</exception>
    public void Unwrap(ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> associatedData, Span<byte> key)
    {
        if (wrapped.Length != WrappedSize)
            throw new CryptographicException($"A wrapped key has {WrappedSize} bytes, not {wrapped.Length}.");
        lock (_gate)
            _aes.Decrypt(wrapped[..Envelope.NonceSize], wrapped[(Envelope.NonceSize + Envelope.TagSize)..], wrapped.Slice(Envelope.NonceSize, Envelope.TagSize), key[..SubjectKey.KeySize], associatedData);
    }

    /// <inheritdoc />
    public void Dispose() => _aes.Dispose();
}
