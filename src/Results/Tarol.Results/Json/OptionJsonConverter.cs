using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tarol.Results.Json;

/// <summary>
/// JSON for <see cref="Option{T}"/>: the value itself, or <c>null</c> for <see cref="None"/>: the same wire format as
/// <c>T?</c>. A missing property reads as <see cref="None"/>, and <c>JsonIgnoreCondition.WhenWritingDefault</c> on the
/// property omits <see cref="None"/>. Applied through <c>[JsonConverter]</c> on the type; you do not register it.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>
/// <para>
/// Native AOT: put <typeparamref name="T"/> in your <see cref="JsonSerializerContext"/> (or the type that contains the option).
/// <see cref="string"/>, <see cref="bool"/>, <see cref="int"/>, <see cref="long"/>, <see cref="decimal"/>, <see cref="double"/>,
/// <see cref="Guid"/>, <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>, <see cref="TimeOnly"/> and
/// <see cref="TimeSpan"/> resolve from this package's own metadata when your context does not list them.
/// </para>
/// <para>
/// Because <see cref="None"/> is <c>null</c>, <c>Option&lt;Option&lt;T&gt;&gt;</c> holding an inner <see cref="None"/>
/// reads back as the outer <see cref="None"/>. The C# union default (writing the active case) is not used: it cannot
/// read an option of an object type (SYSLIB1227).
/// </para>
/// </remarks>
public sealed class OptionJsonConverter<T> : JsonConverter<Option<T>>
    where T : notnull
{
    /// <summary><see langword="true"/>: a JSON <c>null</c> is read by this converter (as <see cref="None"/>).</summary>
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override Option<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        var value = JsonSerializer.Deserialize(ref reader, ValueInfo(options));
        return value is null ? default : new Option<T>(value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Option<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        if (value.TryGetValue(out T? some))
        {
            JsonSerializer.Serialize(writer, some, ValueInfo(options));
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    // The caller's options win (their context, encoder, converters); otherwise this package's metadata for primitives.
    private static JsonTypeInfo<T> ValueInfo(JsonSerializerOptions options)
    {
        if (options.TryGetTypeInfo(typeof(T), out var info))
        {
            return (JsonTypeInfo<T>)info;
        }

        return OptionsJsonContext.Default.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> fallback
            ? fallback
            : throw new NotSupportedException(
                $"JSON metadata for '{typeof(T)}' (the value of an Option) was not found. Add [JsonSerializable(typeof({typeof(T).Name}))] to your JsonSerializerContext.");
    }
}

/// <summary>Source-generated metadata for the primitives an <see cref="Option{T}"/> commonly holds, used when the caller's options do not provide it.</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(TimeSpan))]
internal sealed partial class OptionsJsonContext : JsonSerializerContext;
