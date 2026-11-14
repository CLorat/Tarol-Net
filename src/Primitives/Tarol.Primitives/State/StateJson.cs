using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Tarol.Primitives;

/// <summary>
/// Reader steps and type-info lookups used by the generated <c>WriteState</c>/<c>ReadState</c>. Public because generated
/// code lives in your assembly; do not call it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class StateJson
{
    /// <summary>Moves to the state object and checks its <c>schema</c> member; skips the object when it differs or is missing.</summary>
    /// <param name="reader">The reader, on or before the object.</param>
    /// <param name="schema">The expected schema.</param>
    /// <param name="type">The aggregate type, for messages.</param>
    /// <returns><see langword="true"/> when the object has this schema; the reader is then on its start.</returns>
    public static bool Begin(ref Utf8JsonReader reader, int schema, string type)
    {
        if (reader.TokenType is JsonTokenType.None or JsonTokenType.PropertyName && !reader.Read())
            ThrowEnd(type);
        if (reader.TokenType != JsonTokenType.StartObject)
            ThrowToken(type, "an object", reader.TokenType);

        // jsonb reorders keys, so "schema" can be anywhere: scan a copy of the reader.
        var scan = reader;
        while (scan.Read() && scan.TokenType == JsonTokenType.PropertyName)
        {
            if (scan.ValueTextEquals("schema"u8))
            {
                scan.Read();
                if (scan.TokenType == JsonTokenType.Number && scan.TryGetInt32(out var found) && found == schema)
                    return true;
                break;
            }

            scan.Read();
            scan.Skip();
        }

        reader.Skip();
        return false;
    }

    /// <summary>Finds the <c>id</c> of the entity object the reader is on, without moving the reader.</summary>
    /// <param name="reader">A copy of the reader, on the object's start.</param>
    /// <param name="type">The entity type, for messages.</param>
    /// <returns>The id.</returns>
    public static Guid FindId(Utf8JsonReader reader, string type)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            ThrowToken(type, "an object", reader.TokenType);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                return reader.GetGuid();
            }

            reader.Read();
            reader.Skip();
        }

        throw new JsonException($"The stored state of a {type} has no \"id\".");
    }

    /// <summary>Moves to the next member name of the current object.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="type">The type, for messages.</param>
    /// <returns><see langword="true"/> on a member name; <see langword="false"/> at the end of the object.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Next(ref Utf8JsonReader reader, string type)
    {
        if (!reader.Read())
            ThrowEnd(type);
        return reader.TokenType == JsonTokenType.PropertyName;
    }

    /// <summary>Moves to a member's value.</summary>
    /// <param name="reader">The reader, on the member name.</param>
    /// <param name="type">The type, for messages.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Value(ref Utf8JsonReader reader, string type)
    {
        if (!reader.Read())
            ThrowEnd(type);
    }

    /// <summary>Moves into an array of children.</summary>
    /// <param name="reader">The reader, on the member name.</param>
    /// <param name="type">The owner type, for messages.</param>
    public static void StartArray(ref Utf8JsonReader reader, string type)
    {
        Value(ref reader, type);
        if (reader.TokenType != JsonTokenType.StartArray)
            ThrowToken(type, "an array", reader.TokenType);
    }

    /// <summary>Moves to the next child in an array.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="type">The owner type, for messages.</param>
    /// <returns><see langword="true"/> on a child object; <see langword="false"/> at the end of the array.</returns>
    public static bool NextItem(ref Utf8JsonReader reader, string type)
    {
        if (!reader.Read())
            ThrowEnd(type);
        return reader.TokenType != JsonTokenType.EndArray;
    }

    /// <summary>The state has a member this shape does not know.</summary>
    /// <param name="reader">The reader, on the member name.</param>
    /// <param name="type">The type.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void Unknown(ref Utf8JsonReader reader, string type)
        => throw new JsonException($"The stored state of {type} has an unknown member \"{reader.GetString()}\".");

    /// <summary>An enum member name this version of the enum does not have.</summary>
    /// <param name="reader">The reader, on the string.</param>
    /// <param name="type">The enum type.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void UnknownName(ref Utf8JsonReader reader, string type)
        => throw new JsonException($"The stored state holds {type} \"{reader.GetString()}\", which is not a member of {type}. Stored enum names are kept forever: rename with care.");

    /// <summary>The state lacks members.</summary>
    /// <param name="type">The type.</param>
    /// <param name="expected">The members of this shape.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void Missing(string type, string expected)
        => throw new JsonException($"The stored state of {type} lacks members; expected {expected}.");

    /// <summary>The metadata for a member type, from the options' resolver chain (cached per options instance).</summary>
    /// <typeparam name="T">The member type.</typeparam>
    /// <param name="options">The options.</param>
    /// <param name="member">The member, for the message when the metadata is missing.</param>
    /// <returns>The metadata.</returns>
    /// <exception cref="NotSupportedException">No resolver in the chain knows <typeparamref name="T"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JsonTypeInfo<T> Info<T>(JsonSerializerOptions options, string member)
    {
        var entry = InfoCache<T>.Last;
        return entry is not null && ReferenceEquals(entry.Options, options) ? entry.Info : InfoCache<T>.Resolve(options, member);
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEnd(string type) => throw new JsonException($"The stored state of {type} ends early.");

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowToken(string type, string expected, JsonTokenType actual)
        => throw new JsonException($"The stored state of {type}: expected {expected}, found {actual}.");

    private static class InfoCache<T>
    {
        public static Entry? Last;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static JsonTypeInfo<T> Resolve(JsonSerializerOptions options, string member)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (!options.TryGetTypeInfo(typeof(T), out var info) || info is not JsonTypeInfo<T> typed)
                throw new NotSupportedException(
                    $"The stored state member {member} has type '{typeof(T)}', and the JSON options know no metadata for it. Add [JsonSerializable(typeof({typeof(T).Name}))] to the JsonSerializerContext of the state options (value objects: put ValueObjectJson.Resolver first).");
            Last = new Entry(options, typed);
            return typed;
        }

        public sealed class Entry(JsonSerializerOptions options, JsonTypeInfo<T> info)
        {
            public JsonSerializerOptions Options { get; } = options;

            public JsonTypeInfo<T> Info { get; } = info;
        }
    }
}
