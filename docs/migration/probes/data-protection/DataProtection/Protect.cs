using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Probe.DataProtection;

/// <summary>A subject's data key (AES-256-GCM), unwrapped in memory.</summary>
public sealed class SubjectKey(Guid tenant, Guid subject, int version, byte[] key) : IDisposable
{
    public Guid Tenant { get; } = tenant;
    public Guid Subject { get; } = subject;
    public int Version { get; } = version;
    public AesGcm Aes { get; } = new(key, Envelope.TagSize);
    public void Dispose() => Aes.Dispose();
}

/// <summary>The keys a codec call may use: found ones, and subjects known to have none (erased).</summary>
public sealed class KeyRing
{
    private readonly Dictionary<Guid, SubjectKey?> _keys = [];
    public void Add(Guid subject, SubjectKey? key) => _keys[subject] = key;
    public bool TryGet(Guid subject, out SubjectKey? key) => _keys.TryGetValue(subject, out key);
    public int Count => _keys.Count;
    public static readonly KeyRing Empty = new();
}

/// <summary>
/// The would-be field envelope: a JSON string <c>"tdp1." + base64url(subject 16 | key version 4 | nonce 12 | tag 16 | ciphertext)</c>.
/// The plaintext is the field's JSON value (any type); the associated data binds it to (tenant, subject).
/// </summary>
public static class Envelope
{
    public const int TagSize = 16, NonceSize = 12, Head = 16 + 4 + NonceSize + TagSize;
    private static ReadOnlySpan<byte> Prefix => "tdp1."u8;

    public static int MaxLength(int plain) => Prefix.Length + Base64Url.GetEncodedLength(Head + plain);

    /// <summary>Encrypts <paramref name="plain"/> (a JSON value) and writes the envelope's UTF-8 chars into <paramref name="dest"/>.</summary>
    public static int Seal(SubjectKey key, ReadOnlySpan<byte> plain, Span<byte> dest)
    {
        var raw = Head + plain.Length;
        byte[]? rented = null;
        var bin = raw <= 512 ? stackalloc byte[raw] : (rented = ArrayPool<byte>.Shared.Rent(raw)).AsSpan(0, raw);
        key.Subject.TryWriteBytes(bin[..16]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bin[16..20], key.Version);
        var nonce = bin.Slice(20, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        Span<byte> ad = stackalloc byte[32];
        key.Tenant.TryWriteBytes(ad[..16]); key.Subject.TryWriteBytes(ad[16..]);
        lock (key.Aes) key.Aes.Encrypt(nonce, plain, bin[Head..], bin.Slice(20 + NonceSize, TagSize), ad);   // AesGcm is not safe for concurrent use
        Prefix.CopyTo(dest);
        var n = Base64Url.EncodeToUtf8(bin, dest[Prefix.Length..]);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        return Prefix.Length + n;
    }

    public static bool IsEnvelope(ReadOnlySpan<byte> utf8) => utf8.StartsWith(Prefix);

    /// <summary>The subject an envelope is sealed for.</summary>
    public static Guid SubjectOf(ReadOnlySpan<byte> utf8)
    {
        Span<byte> head = stackalloc byte[24];   // 16 bytes need 22 chars; decode 24 chars → 18 bytes
        Base64Url.DecodeFromUtf8(utf8.Slice(Prefix.Length, 24), head, out _, out var written, isFinalBlock: false);
        return new Guid(head[..16]);
    }

    /// <summary>Decrypts an envelope into <paramref name="plain"/>; false when the key is missing (erased) or the data was tampered.</summary>
    public static bool Open(KeyRing ring, ReadOnlySpan<byte> utf8, ArrayBufferWriter<byte> plain, out Guid subject)
    {
        var chars = utf8[Prefix.Length..];
        var max = Base64Url.GetMaxDecodedLength(chars.Length);
        byte[]? rented = null;
        var bin = max <= 512 ? stackalloc byte[max] : (rented = ArrayPool<byte>.Shared.Rent(max)).AsSpan(0, max);
        try
        {
            if (Base64Url.DecodeFromUtf8(chars, bin, out _, out var n) != OperationStatus.Done || n < Head) { subject = Guid.Empty; return false; }
            bin = bin[..n];
            subject = new Guid(bin[..16]);
            if (!ring.TryGet(subject, out var key) || key is null) return false;
            Span<byte> ad = stackalloc byte[32];
            key.Tenant.TryWriteBytes(ad[..16]); key.Subject.TryWriteBytes(ad[16..]);
            var dest = plain.GetSpan(n - Head)[..(n - Head)];
            try { lock (key.Aes) key.Aes.Decrypt(bin.Slice(20, NonceSize), bin[Head..], bin.Slice(20 + NonceSize, TagSize), dest, ad); }
            catch (AuthenticationTagMismatchException) { return false; }
            plain.Advance(n - Head);
            return true;
        }
        finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented); }
    }
}

/// <summary>One personal field of a JSON object as a generator would emit it: its JSON name and its subject.</summary>
public readonly record struct PersonalField(string JsonName, Guid Subject);

/// <summary>
/// Candidate A, the would-be transform: after the serializer wrote the object, rewrite the personal top-level properties
/// into envelopes (one pass over the UTF-8 JSON, raw value slices, no DOM); before it reads, rewrite envelopes back into
/// their JSON values, or <c>null</c> when the key is gone. Type-agnostic: the generator only supplies names and subjects.
/// </summary>
public static class FieldTransform
{
    public static void Seal(ReadOnlySpan<byte> json, ReadOnlySpan<PersonalField> fields, Func<Guid, SubjectKey> keyOf, IBufferWriter<byte> output)
    {
        var reader = new Utf8JsonReader(json);
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true });
        reader.Read();
        writer.WriteStartObject();
        Span<byte> env = stackalloc byte[0];
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.ValueSpan;   // names written by the serializer are not escaped for plain identifiers
            var match = -1;
            for (var i = 0; i < fields.Length; i++)
                if (Encoding.UTF8.GetByteCount(fields[i].JsonName) == name.Length && name.SequenceEqual(Encoding.UTF8.GetBytes(fields[i].JsonName))) { match = i; break; }
            writer.WritePropertyName(name);
            reader.Read();
            var start = (int)reader.TokenStartIndex;
            var isNull = reader.TokenType == JsonTokenType.Null;
            reader.Skip();
            var value = json[start..(int)reader.BytesConsumed];
            if (match < 0 || isNull) { writer.WriteRawValue(value, skipInputValidation: true); continue; }
            var key = keyOf(fields[match].Subject);
            var buf = ArrayPool<byte>.Shared.Rent(Envelope.MaxLength(value.Length) + 2);
            buf[0] = (byte)'"';
            var n = Envelope.Seal(key, value, buf.AsSpan(1));
            buf[n + 1] = (byte)'"';
            writer.WriteRawValue(buf.AsSpan(0, n + 2), skipInputValidation: true);
            ArrayPool<byte>.Shared.Return(buf);
        }
        writer.WriteEndObject();
    }

    /// <summary>Opens every envelope of the top level; returns how many could not be opened (erased → null).</summary>
    public static int Open(ReadOnlySpan<byte> json, KeyRing ring, IBufferWriter<byte> output)
    {
        var reader = new Utf8JsonReader(json);
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true });
        var plain = new ArrayBufferWriter<byte>(256);
        var lost = 0;
        reader.Read();
        writer.WriteStartObject();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            writer.WritePropertyName(reader.ValueSpan);
            reader.Read();
            if (reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped && Envelope.IsEnvelope(reader.ValueSpan))
            {
                plain.ResetWrittenCount();
                if (Envelope.Open(ring, reader.ValueSpan, plain, out _)) writer.WriteRawValue(plain.WrittenSpan, skipInputValidation: true);
                else { writer.WriteNullValue(); lost++; }
                continue;
            }
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            writer.WriteRawValue(json[start..(int)reader.BytesConsumed], skipInputValidation: true);
        }
        writer.WriteEndObject();
        return lost;
    }

    /// <summary>Whether a payload holds an envelope at the top level, and of which subjects (the store's pre-scan without a subjects column).</summary>
    public static void Subjects(ReadOnlySpan<byte> json, HashSet<Guid> into)
    {
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.String && Envelope.IsEnvelope(reader.ValueSpan))
                into.Add(Envelope.SubjectOf(reader.ValueSpan));
    }
}

/// <summary>Candidate B: the same through <see cref="JsonNode"/> (parse, replace, write).</summary>
public static class NodeTransform
{
    public static void Seal(ReadOnlySpan<byte> json, ReadOnlySpan<PersonalField> fields, Func<Guid, SubjectKey> keyOf, IBufferWriter<byte> output)
    {
        var o = (JsonObject)JsonNode.Parse(json)!;
        foreach (var f in fields)
        {
            if (o[f.JsonName] is not { } v) continue;
            var plain = Encoding.UTF8.GetBytes(v.ToJsonString());
            var buf = new byte[Envelope.MaxLength(plain.Length)];
            var n = Envelope.Seal(keyOf(f.Subject), plain, buf);
            o[f.JsonName] = Encoding.UTF8.GetString(buf, 0, n);
        }
        using var w = new Utf8JsonWriter(output);
        o.WriteTo(w);
    }

    public static int Open(ReadOnlySpan<byte> json, KeyRing ring, IBufferWriter<byte> output)
    {
        var o = (JsonObject)JsonNode.Parse(json)!;
        var plain = new ArrayBufferWriter<byte>(256);
        var lost = 0;
        foreach (var (name, v) in o.ToArray())
        {
            if (v is not JsonValue jv || !jv.TryGetValue(out string? s) || !s.StartsWith("tdp1.", StringComparison.Ordinal)) continue;
            plain.ResetWrittenCount();
            if (Envelope.Open(ring, Encoding.UTF8.GetBytes(s), plain, out _)) o[name] = JsonNode.Parse(plain.WrittenSpan);
            else { o[name] = null; lost++; }
        }
        using var w = new Utf8JsonWriter(output);
        o.WriteTo(w);
        return lost;
    }
}

/// <summary>Candidate C: the whole payload in one envelope, <c>{"$tdp":"tdp1…"}</c> (one subject per event).</summary>
public static class WholeTransform
{
    public static void Seal(ReadOnlySpan<byte> json, SubjectKey key, IBufferWriter<byte> output)
    {
        var buf = ArrayPool<byte>.Shared.Rent(Envelope.MaxLength(json.Length) + 12);
        "{\"$tdp\":\""u8.CopyTo(buf);
        var n = Envelope.Seal(key, json, buf.AsSpan(9));
        buf[9 + n] = (byte)'"'; buf[10 + n] = (byte)'}';
        output.Write(buf.AsSpan(0, 11 + n));
        ArrayPool<byte>.Shared.Return(buf);
    }

    public static bool Open(ReadOnlySpan<byte> json, KeyRing ring, ArrayBufferWriter<byte> output)
    {
        var reader = new Utf8JsonReader(json);
        reader.Read(); reader.Read(); reader.Read();
        return Envelope.Open(ring, reader.ValueSpan, output, out _);
    }
}

/// <summary>
/// Candidate D: a converter inside the serializer (<c>[JsonConverter(typeof(PersonalString))]</c> on the property), with the
/// key ring and the subject handed over in a thread-static scope for the length of one synchronous serializer call.
/// </summary>
public sealed class PersonalString : System.Text.Json.Serialization.JsonConverter<string>
{
    [ThreadStatic] public static SubjectKey? SealWith;
    [ThreadStatic] public static KeyRing? OpenWith;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (!Envelope.IsEnvelope(reader.ValueSpan)) return reader.GetString();
        var plain = new ArrayBufferWriter<byte>(128);
        if (OpenWith is null || !Envelope.Open(OpenWith, reader.ValueSpan, plain, out _)) return null;
        var r = new Utf8JsonReader(plain.WrittenSpan);
        r.Read();
        return r.GetString();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        var key = SealWith ?? throw new InvalidOperationException("no key in scope");
        Span<byte> plain = stackalloc byte[Encoding.UTF8.GetMaxByteCount(value.Length) + 2 + value.Length * 5];
        var w = 0;
        plain[w++] = (byte)'"';
        w += Encoding.UTF8.GetBytes(value, plain[w..]);   // the probe's values need no escaping
        plain[w++] = (byte)'"';
        Span<byte> env = stackalloc byte[Envelope.MaxLength(w)];
        var n = Envelope.Seal(key, plain[..w], env);
        writer.WriteStringValue(env[..n]);
    }
}

/// <summary>
/// The would-be key store contract: data keys per (tenant, subject), wrapped by a key-encryption key (here a 256-bit master key
/// in memory, standing in for configuration, a KMS or Vault). Rows: <c>subject_keys(tenant, subject, version, wrapped, created_at,
/// erased_at)</c>; an erased subject keeps a tombstone (no key) so new personal data for it is refused.
/// </summary>
public static class Kek
{
    private static readonly byte[] Master = RandomNumberGenerator.GetBytes(32);
    private static readonly AesGcm Aes = new(Master, 16);

    public static byte[] Wrap(Guid tenant, Guid subject, ReadOnlySpan<byte> dataKey)
    {
        var o = new byte[12 + 16 + dataKey.Length];
        RandomNumberGenerator.Fill(o.AsSpan(0, 12));
        Span<byte> ad = stackalloc byte[32]; tenant.TryWriteBytes(ad[..16]); subject.TryWriteBytes(ad[16..]);
        lock (Aes) Aes.Encrypt(o.AsSpan(0, 12), dataKey, o.AsSpan(28), o.AsSpan(12, 16), ad);
        return o;
    }

    public static byte[] Unwrap(Guid tenant, Guid subject, ReadOnlySpan<byte> wrapped)
    {
        var k = new byte[wrapped.Length - 28];
        Span<byte> ad = stackalloc byte[32]; tenant.TryWriteBytes(ad[..16]); subject.TryWriteBytes(ad[16..]);
        lock (Aes) Aes.Decrypt(wrapped[..12], wrapped[28..], wrapped.Slice(12, 16), k, ad);
        return k;
    }
}

/// <summary>A per-process cache of unwrapped keys with a time bound (the erasure's reach into other processes).</summary>
public sealed class KeyCache(TimeSpan ttl)
{
    private readonly ConcurrentDictionary<(Guid, Guid), (SubjectKey? Key, long At)> _keys = new();
    public bool TryGet(Guid tenant, Guid subject, out SubjectKey? key)
    {
        if (_keys.TryGetValue((tenant, subject), out var e) && Environment.TickCount64 - e.At < ttl.TotalMilliseconds) { key = e.Key; return true; }
        key = null; return false;
    }
    public void Put(Guid tenant, Guid subject, SubjectKey? key) => _keys[(tenant, subject)] = (key, Environment.TickCount64);
    public void Evict(Guid tenant, Guid subject) => _keys.TryRemove((tenant, subject), out _);
    public void Clear() => _keys.Clear();
    public int Count => _keys.Count;
}

/// <summary>A blind index for a personal lookup key: HMAC-SHA256 under a per-tenant lookup key, base64url (equality only).</summary>
public static class BlindIndex
{
    public static string Of(ReadOnlySpan<byte> tenantKey, string value)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(tenantKey, Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()), mac);
        return Base64Url.EncodeToString(mac);
    }
}
