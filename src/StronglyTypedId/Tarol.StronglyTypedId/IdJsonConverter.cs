using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tarol.StronglyTypedId;

/// <summary>
///     Serializes <see cref="Id{T}" /> as a plain JSON string (<c>"xxxxxxxx-xxxx-..."</c>)
///     instead of an object wrapper (<c>{"value":"..."}</c>).
///     <para>
///         Also implements <see cref="JsonConverter{T}.ReadAsPropertyName" /> /
///         <see cref="JsonConverter{T}.WriteAsPropertyName" /> so that
///         <c>Dictionary&lt;Id&lt;T&gt;, TValue&gt;</c> round-trips.
///     </para>
///     <para>
///         Values and property names accept exactly the canonical <c>"D"</c> format
///         (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>), which is also what is written. Both paths
///         parse directly from the UTF-8 buffer and allocate nothing.
///     </para>
///     <para>
///         Applied to <see cref="Id{T}" /> as the open generic <c>[JsonConverter(typeof(IdJsonConverter&lt;&gt;))]</c>;
///         there is nothing to register.
///     </para>
/// </summary>
/// <typeparam name="T">The phantom entity type.</typeparam>
public sealed class IdJsonConverter<T> : JsonConverter<Id<T>>
{
    // 32 hex digits + 4 hyphens ("D" format).
    private const int GuidLength = 36;

    // Escaped input can be up to six times longer ("0" per character).
    private const int MaxEscapedGuidLength = GuidLength * 6;

    /// <inheritdoc />
    public override Id<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected a string token for Id<{typeof(T).Name}>, got {reader.TokenType}.");

        if (!TryReadGuid(ref reader, out var guid))
            throw new JsonException($"The value is not a valid Guid in \"D\" format for Id<{typeof(T).Name}>.");

        return new Id<T>(guid);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Id<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value); // dedicated Guid overload, no string allocation
    }

    /// <inheritdoc />
    public override Id<T> ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (!TryReadGuid(ref reader, out var guid))
            throw new JsonException(
                $"The property name is not a valid Guid in \"D\" format for Id<{typeof(T).Name}>.");

        return new Id<T>(guid);
    }

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, Id<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // No Guid overload exists for WritePropertyName: format as UTF-8 onto the stack.
        Span<byte> buffer = stackalloc byte[GuidLength];
        value.Value.TryFormat(buffer, out var written);
        writer.WritePropertyName(buffer[..written]);
    }

    /// <summary>Parses the current string or property-name token as a "D"-format Guid without allocating.</summary>
    private static bool TryReadGuid(ref Utf8JsonReader reader, out Guid guid)
    {
        if (!reader.HasValueSequence && !reader.ValueIsEscaped)
            return TryParseD(reader.ValueSpan, out guid);

        var length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (length > MaxEscapedGuidLength)
        {
            guid = default;
            return false;
        }

        Span<byte> unescaped = stackalloc byte[MaxEscapedGuidLength];
        var written = reader.CopyString(unescaped);
        return TryParseD(unescaped[..written], out guid);
    }

    private static bool TryParseD(ReadOnlySpan<byte> utf8, out Guid guid)
        => Utf8Parser.TryParse(utf8, out guid, out var consumed, 'D') && consumed == utf8.Length;
}
