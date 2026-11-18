using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Tarol.Persistence;

/// <summary>
/// A sealed personal field: the JSON string <c>"tdp1." + base64url(subject 16 | key version 4 | nonce 12 | tag 16 | ciphertext)</c>.
/// The plaintext is the member's JSON value (any type), the associated data is (tenant, subject), so an envelope relabelled
/// to another subject or tenant does not open (data-protection.md §3).
/// </summary>
public static class Envelope
{
    /// <summary>The AES-GCM tag size in bytes.</summary>
    public const int TagSize = 16;

    /// <summary>The nonce size in bytes (random per seal).</summary>
    public const int NonceSize = 12;

    /// <summary>The binary head before the ciphertext: subject, key version, nonce and tag.</summary>
    public const int HeadSize = 16 + 4 + NonceSize + TagSize;

    private const int StackLimit = 512;

    /// <summary>The prefix of every envelope (UTF-8), the format's version.</summary>
    public static ReadOnlySpan<byte> Prefix => "tdp1."u8;

    /// <summary>The most UTF-8 bytes the envelope of a <paramref name="plaintextLength"/>-byte value takes (without quotes).</summary>
    /// <param name="plaintextLength">The JSON value's length in bytes.</param>
    /// <returns>The length.</returns>
    public static int MaxLength(int plaintextLength) => Prefix.Length + Base64Url.GetEncodedLength(HeadSize + plaintextLength);

    /// <summary>Whether a UTF-8 string value is an envelope (starts with <see cref="Prefix"/>).</summary>
    /// <param name="utf8">The unescaped string value.</param>
    /// <returns><see langword="true"/> for an envelope.</returns>
    public static bool IsEnvelope(ReadOnlySpan<byte> utf8) => utf8.Length > Prefix.Length && utf8.StartsWith(Prefix);

    /// <summary>The subject an envelope is sealed for, without opening it.</summary>
    /// <param name="utf8">The envelope (unescaped string value).</param>
    /// <param name="subject">The subject.</param>
    /// <returns><see langword="false"/> when <paramref name="utf8"/> is not a well-formed envelope.</returns>
    public static bool TryReadSubject(ReadOnlySpan<byte> utf8, out Guid subject)
    {
        subject = Guid.Empty;
        if (!IsEnvelope(utf8) || utf8.Length < Prefix.Length + 24)
            return false;
        Span<byte> head = stackalloc byte[18]; // 24 chars decode to 18 bytes: the subject's 16 and 2 more
        if (Base64Url.DecodeFromUtf8(utf8.Slice(Prefix.Length, 24), head, out _, out var written, isFinalBlock: false) != OperationStatus.Done || written < 16)
            return false;
        subject = new Guid(head[..16]);
        return true;
    }

    /// <summary>Seals <paramref name="plaintext"/> (a JSON value) with <paramref name="key"/> and writes the envelope's UTF-8 chars.</summary>
    /// <param name="key">The subject's data key.</param>
    /// <param name="plaintext">The member's JSON value.</param>
    /// <param name="destination">At least <see cref="MaxLength"/> bytes.</param>
    /// <returns>The bytes written.</returns>
    /// <exception cref="SubjectErasedException"><paramref name="key"/> is a tombstone.</exception>
    public static int Seal(SubjectKey key, ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.IsErased)
            throw new SubjectErasedException(key.Tenant, key.Subject);
        var size = HeadSize + plaintext.Length;
        byte[]? rented = null;
        var raw = size <= StackLimit ? stackalloc byte[size] : (rented = ArrayPool<byte>.Shared.Rent(size)).AsSpan(0, size);
        try
        {
            key.Subject.TryWriteBytes(raw[..16]);
            BinaryPrimitives.WriteInt32BigEndian(raw.Slice(16, 4), key.Version);
            var nonce = raw.Slice(20, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            Span<byte> ad = stackalloc byte[32];
            WriteAssociatedData(key, ad);
            key.Encrypt(nonce, plaintext, raw[HeadSize..], raw.Slice(20 + NonceSize, TagSize), ad);
            Prefix.CopyTo(destination);
            return Prefix.Length + Base64Url.EncodeToUtf8(raw, destination[Prefix.Length..]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>Opens an envelope into its JSON value.</summary>
    /// <param name="keys">The keys; must hold the envelope's subject (a key or a tombstone).</param>
    /// <param name="utf8">The envelope (unescaped string value).</param>
    /// <param name="plaintext">Receives the JSON value.</param>
    /// <returns><see langword="false"/> when the subject is erased (the value reads as <c>null</c>).</returns>
    /// <exception cref="CryptographicException">The envelope is malformed, was sealed with another key version, or was tampered with or relabelled.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="keys"/> does not hold the subject.</exception>
    public static bool TryOpen(KeyRing keys, ReadOnlySpan<byte> utf8, IBufferWriter<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!IsEnvelope(utf8))
            throw new CryptographicException("The value is not a sealed field.");
        var chars = utf8[Prefix.Length..];
        var max = Base64Url.GetMaxDecodedLength(chars.Length);
        byte[]? rented = null;
        var raw = max <= StackLimit ? stackalloc byte[max] : (rented = ArrayPool<byte>.Shared.Rent(max)).AsSpan(0, max);
        try
        {
            if (Base64Url.DecodeFromUtf8(chars, raw, out _, out var length) != OperationStatus.Done || length < HeadSize)
                throw new CryptographicException("The sealed field is malformed.");
            raw = raw[..length];
            var key = keys.Get(new Guid(raw[..16]));
            if (key.IsErased)
                return false;
            var version = BinaryPrimitives.ReadInt32BigEndian(raw.Slice(16, 4));
            if (version != key.Version)
                throw new CryptographicException($"The field was sealed with key version {version} of subject {key.Subject}; the ring holds version {key.Version}.");
            Span<byte> ad = stackalloc byte[32];
            WriteAssociatedData(key, ad);
            var size = length - HeadSize;
            key.Decrypt(raw.Slice(20, NonceSize), raw[HeadSize..], raw.Slice(20 + NonceSize, TagSize), plaintext.GetSpan(size)[..size], ad);
            plaintext.Advance(size);
            return true;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static void WriteAssociatedData(SubjectKey key, Span<byte> ad)
    {
        key.Tenant.TryWriteBytes(ad[..16]);
        key.Subject.TryWriteBytes(ad[16..]);
    }
}
