using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Tarol.StronglyTypedId;

/// <summary>
/// One part of the key of a derived id (<see cref="IdExtensions"/> <c>Id&lt;T&gt;.Derive</c>): a <see cref="Guid"/>, a
/// <see cref="string"/>, an <see cref="int"/>, a <see cref="long"/> or a <see cref="DateOnly"/>, converted implicitly.
/// </summary>
public readonly struct IdPart
{
    private readonly byte _kind;
    private readonly Guid _guid;
    private readonly long _number;
    private readonly string? _text;

    private IdPart(byte kind, Guid guid = default, long number = 0, string? text = null)
    {
        _kind = kind;
        _guid = guid;
        _number = number;
        _text = text;
    }

    /// <summary>A Guid part.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator IdPart(Guid value) => new(1, guid: value);

    /// <summary>A text part (ordinal, UTF-8).</summary>
    /// <param name="value">The value.</param>
    public static implicit operator IdPart(string value) => new(2, text: value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>An <see cref="int"/> part.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator IdPart(int value) => new(3, number: value);

    /// <summary>A <see cref="long"/> part.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator IdPart(long value) => new(4, number: value);

    /// <summary>A calendar day part.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator IdPart(DateOnly value) => new(5, number: value.DayNumber);

    /// <summary>Creates a Guid part (for languages without implicit conversions).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The part.</returns>
    public static IdPart FromGuid(Guid value) => value;

    /// <summary>Creates a text part.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The part.</returns>
    public static IdPart FromString(string value) => value;

    /// <summary>Creates an <see cref="int"/> part.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The part.</returns>
    public static IdPart FromInt32(int value) => value;

    /// <summary>Creates a <see cref="long"/> part.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The part.</returns>
    public static IdPart FromInt64(long value) => value;

    /// <summary>Creates a calendar day part.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The part.</returns>
    public static IdPart FromDateOnly(DateOnly value) => value;

    /// <summary>
    /// The derived Guid: a UUIDv8 over SHA-256 of a length-prefixed, typed encoding of <paramref name="scope"/> and
    /// <paramref name="parts"/>. Stable across releases and machines (pinned by a test vector).
    /// </summary>
    internal static Guid Derive(string scope, ReadOnlySpan<IdPart> parts)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[16];
        hash.AppendData("tarol-id/1"u8);
        AppendText(hash, scope);
        foreach (var part in parts)
        {
            buffer[0] = part._kind;
            hash.AppendData(buffer[..1]);
            switch (part._kind)
            {
                case 1:
                    part._guid.TryWriteBytes(buffer, bigEndian: true, out _);
                    hash.AppendData(buffer);
                    break;
                case 2:
                    AppendText(hash, part._text!);
                    break;
                case 3 or 5:
                    BinaryPrimitives.WriteInt32BigEndian(buffer, (int)part._number);
                    hash.AppendData(buffer[..4]);
                    break;
                case 4:
                    BinaryPrimitives.WriteInt64BigEndian(buffer, part._number);
                    hash.AppendData(buffer[..8]);
                    break;
                default:
                    throw new ArgumentException("default(IdPart) is no part: convert a Guid, string, int, long or DateOnly.", nameof(parts));
            }
        }
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        var bytes = digest[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);                 // version 8 (RFC 9562: custom)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);                 // variant 10xx
        return new Guid(bytes, bigEndian: true);
    }

    private static void AppendText(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
