using System.Buffers;
using System.Text;
using System.Text.Json;
using Tarol.Primitives;

namespace Tarol.Persistence;

/// <summary>A <see cref="PersonalShape"/> with its JSON names resolved (UTF-8), ready for the transforms.</summary>
internal sealed class ResolvedShape
{
    private ResolvedShape(byte[][] names, byte[]?[] subjects, (byte[] Name, ResolvedShape Shape)[] children)
    {
        Names = names;
        Subjects = subjects;
        Children = children;
        HasSubjectMembers = subjects.Any(static s => s is not null);
    }

    public byte[][] Names { get; }

    /// <summary>Per member: the JSON name of its subject member, or <see langword="null"/> for the stream id.</summary>
    public byte[]?[] Subjects { get; }

    public (byte[] Name, ResolvedShape Shape)[] Children { get; }

    public bool HasSubjectMembers { get; }

    /// <summary>Resolves the names: pinned ones verbatim, the others through <paramref name="naming"/>.</summary>
    public static ResolvedShape From(PersonalShape shape, JsonNamingPolicy? naming)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var names = shape.Members.Select(m => Encoding.UTF8.GetBytes(m.JsonName ?? Name(m.Name, naming))).ToArray();
        var subjects = shape.Members.Select(m => m.Subject is null && m.SubjectJsonName is null
            ? null
            : Encoding.UTF8.GetBytes(m.SubjectJsonName ?? Name(m.Subject!, naming))).ToArray();
        var children = shape.Children.Select(c => (Encoding.UTF8.GetBytes(c.JsonName), From(c.Shape, naming))).ToArray();
        return new ResolvedShape(names, subjects, children);
    }

    private static string Name(string member, JsonNamingPolicy? naming) => naming?.ConvertName(member) ?? member;
}

/// <summary>
/// The sealing transform (DP-2, candidate A): one pass with <see cref="Utf8JsonReader"/> over the serializer's output that
/// replaces the raw value of each personal member with its envelope, and the reverse that replaces each envelope with its
/// JSON value (or <c>null</c> once the subject is erased). Everything else is copied byte for byte, for any value type.
/// </summary>
internal static class PersonalJson
{
    private const int MaxDepth = 64;

    /// <summary>Whether a payload holds an envelope (a quoted string starting with <c>tdp1.</c>); cheap, no parse.</summary>
    public static bool HasEnvelope(ReadOnlySpan<byte> json) => json.IndexOf("\"tdp1."u8) >= 0;

    /// <summary>The subjects whose keys sealing <paramref name="json"/> by <paramref name="shape"/> needs (non-null personal values only).</summary>
    public static void Subjects(ResolvedShape shape, ReadOnlySpan<byte> json, Guid stream, ICollection<Guid> subjects)
    {
        foreach (var field in Fields(shape, json, stream))
        {
            if (!subjects.Contains(field.Subject))
                subjects.Add(field.Subject);
        }
    }

    /// <summary>Writes <paramref name="json"/> with every non-null personal value of <paramref name="shape"/> sealed.</summary>
    /// <returns>Whether anything was sealed (<see langword="false"/>: <paramref name="json"/> was copied as it is).</returns>
    public static bool Seal(ResolvedShape shape, ReadOnlySpan<byte> json, Guid stream, KeyRing keys, IBufferWriter<byte> output, bool nullErased = false)
    {
        var fields = Fields(shape, json, stream);
        var copied = 0;
        foreach (var (start, end, subject) in fields)
        {
            Write(output, json[copied..start]);
            var key = keys.Get(subject);
            if (key.IsErased && nullErased)
            {
                Write(output, "null"u8);
                copied = end;
                continue;
            }

            if (key.IsErased)
                throw new SubjectErasedException(keys.Tenant, subject);
            var value = json[start..end];
            var span = output.GetSpan(Envelope.MaxLength(value.Length) + 2);
            span[0] = (byte)'"';
            var length = Envelope.Seal(key, value, span[1..]);
            span[length + 1] = (byte)'"';
            output.Advance(length + 2);
            copied = end;
        }

        Write(output, json[copied..]);
        return fields.Count > 0;
    }

    private static List<(int Start, int End, Guid Subject)> Fields(ResolvedShape shape, ReadOnlySpan<byte> json, Guid stream)
    {
        var fields = new List<(int, int, Guid)>();
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A payload with personal data must be a JSON object.");
        Walk(ref reader, shape, stream, fields, 0);
        return fields;
    }

    /// <summary>Writes <paramref name="json"/> with every envelope opened (anywhere in it), <c>null</c> for erased subjects.</summary>
    /// <param name="json">The stored JSON.</param>
    /// <param name="keys">The keys; <see langword="null"/> = every envelope reads as <c>null</c>.</param>
    /// <param name="output">The opened JSON.</param>
    /// <returns>How many envelopes read as <c>null</c>.</returns>
    public static int Open(ReadOnlySpan<byte> json, KeyRing? keys, IBufferWriter<byte> output)
    {
        var reader = new Utf8JsonReader(json);
        var copied = 0;
        var erased = 0;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped || !Envelope.IsEnvelope(reader.ValueSpan))
                continue;
            var start = (int)reader.TokenStartIndex;
            Write(output, json[copied..start]);
            if (keys is null || !Envelope.TryOpen(keys, reader.ValueSpan, output))
            {
                Write(output, "null"u8);
                erased++;
            }

            copied = (int)reader.BytesConsumed;
        }

        Write(output, json[copied..]);
        return erased;
    }

    /// <summary>The subjects of every envelope in <paramref name="json"/> (what opening it needs).</summary>
    public static void SealedSubjects(ReadOnlySpan<byte> json, ICollection<Guid> subjects)
    {
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped && Envelope.TryReadSubject(reader.ValueSpan, out var subject) && !subjects.Contains(subject))
                subjects.Add(subject);
        }
    }

    /// <summary>
    /// Walks the object the reader is on (its start) and adds the span and subject of each non-null personal member to
    /// <paramref name="fields"/>, in document order; descends into children.
    /// </summary>
    private static void Walk(ref Utf8JsonReader reader, ResolvedShape shape, Guid stream, List<(int, int, Guid)> fields, int depth)
    {
        if (depth > MaxDepth)
            throw new JsonException("The payload nests deeper than the personal-data transform reads.");
        var found = shape.HasSubjectMembers ? SubjectValues(reader, shape) : null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var member = IndexOf(ref reader, shape.Names);
            var child = member < 0 ? ChildOf(ref reader, shape.Children) : null;
            reader.Read();
            if (member >= 0 && reader.TokenType != JsonTokenType.Null)
            {
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                var subject = found is null || shape.Subjects[member] is null ? stream : found[member];
                if (subject == Guid.Empty)
                    throw new JsonException(shape.Subjects[member] is { } name
                        ? $"The subject member '{Encoding.UTF8.GetString(name)}' of a personal field is missing or empty."
                        : "A personal field needs a stream id as its subject.");
                fields.Add((start, (int)reader.BytesConsumed, subject));
            }
            else if (child is not null && reader.TokenType == JsonTokenType.StartObject)
            {
                Walk(ref reader, child, stream, fields, depth + 1);
            }
            else if (child is not null && reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                        Walk(ref reader, child, stream, fields, depth + 1);
                    else
                        reader.Skip();
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    /// <summary>The subject values of an object (a copy of the reader scans it; members can come in any order, jsonb reorders).</summary>
    private static Guid[] SubjectValues(Utf8JsonReader scan, ResolvedShape shape)
    {
        var values = new Guid[shape.Names.Length];
        while (scan.Read() && scan.TokenType == JsonTokenType.PropertyName)
        {
            var matched = false;
            for (var i = 0; i < shape.Subjects.Length; i++)
            {
                if (shape.Subjects[i] is { } name && scan.ValueTextEquals(name))
                {
                    if (!matched)
                    {
                        scan.Read();
                        matched = true;
                    }

                    if (scan.TokenType == JsonTokenType.String && scan.TryGetGuid(out var guid))
                        values[i] = guid;
                }
            }

            if (!matched)
                scan.Read();
            scan.Skip();
        }

        return values;
    }

    private static int IndexOf(ref Utf8JsonReader reader, byte[][] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            if (reader.ValueTextEquals(names[i]))
                return i;
        }

        return -1;
    }

    private static ResolvedShape? ChildOf(ref Utf8JsonReader reader, (byte[] Name, ResolvedShape Shape)[] children)
    {
        foreach (var (name, shape) in children)
        {
            if (reader.ValueTextEquals(name))
                return shape;
        }

        return null;
    }

    private static void Write(IBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        bytes.CopyTo(output.GetSpan(bytes.Length));
        output.Advance(bytes.Length);
    }
}
