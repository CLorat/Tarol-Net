using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tarol.Results.Json;

/// <summary>
/// JSON for <see cref="Result{T}"/>: <c>{"value":…}</c> on success, <c>{"failure":{"kind":…}}</c> on failure
/// (<c>default</c> writes <see cref="Error.Uninitialized"/>). Applied through <c>[JsonConverter]</c> on the type;
/// you do not register it.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>
/// Native AOT: put <typeparamref name="T"/> (or the type that contains the result) in your <see cref="JsonSerializerContext"/>.
/// <see cref="Violation"/> resolves from your context when it lists it, otherwise from this package's own generated metadata.
/// The C# union default (writing the active case without a wrapper) is not used: it cannot tell a value object
/// from a violation when reading (SYSLIB1227).
/// </remarks>
public sealed class ResultJsonConverter<T> : JsonConverter<Result<T>>
    where T : notnull
{
    /// <inheritdoc />
    public override Result<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ResultJson.ReadStart(ref reader, typeof(Result<T>));
        Result<T>? result = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = ResultJson.ReadPropertyName(ref reader);
            if (name == ResultJson.Value)
            {
                var info = options.GetTypeInfo<T>();
                var value = JsonSerializer.Deserialize(ref reader, info) ?? throw new JsonException("\"value\" of a result must not be null.");
                result = ResultJson.Once(result, new Result<T>(value));
            }
            else if (name == ResultJson.Failure)
            {
                result = ResultJson.Once(result, new Result<T>(ResultJson.ReadViolation(ref reader, options)));
            }
            else
            {
                reader.Skip();
            }
        }

        return result ?? throw new JsonException($"A {typeof(Result<T>).Name} needs \"value\" or \"failure\".");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Result<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        writer.WriteStartObject();
        if (value.TryGetValue(out T? success))
        {
            writer.WritePropertyName(ResultJson.ValueName);
            JsonSerializer.Serialize(writer, success, options.GetTypeInfo<T>());
        }
        else if (value.TryGetValue(out Violation? violation))
        {
            writer.WritePropertyName(ResultJson.FailureName);
            ResultJson.WriteViolation(writer, violation, options);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// JSON for <see cref="Result"/>: <c>{"success":true}</c> or <c>{"failure":{"kind":…}}</c>. Applied through
/// <c>[JsonConverter]</c> on the type.
/// </summary>
public sealed class ResultJsonConverter : JsonConverter<Result>
{
    /// <inheritdoc />
    public override Result Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ResultJson.ReadStart(ref reader, typeof(Result));
        Result? result = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = ResultJson.ReadPropertyName(ref reader);
            if (name == ResultJson.Success)
            {
                if (reader.TokenType != JsonTokenType.True)
                {
                    throw new JsonException("\"success\" must be true.");
                }

                result = ResultJson.Once(result, Result.Success());
            }
            else if (name == ResultJson.Failure)
            {
                result = ResultJson.Once(result, new Result(ResultJson.ReadViolation(ref reader, options)));
            }
            else
            {
                reader.Skip();
            }
        }

        return result ?? throw new JsonException("A Result needs \"success\" or \"failure\".");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Result value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        writer.WriteStartObject();
        if (value.TryGetValue(out Violation? violation))
        {
            writer.WritePropertyName(ResultJson.FailureName);
            ResultJson.WriteViolation(writer, violation, options);
        }
        else
        {
            writer.WriteBoolean(ResultJson.SuccessName, true);
        }

        writer.WriteEndObject();
    }
}

internal static class ResultJson
{
    internal const string ValueName = "value";
    internal const string SuccessName = "success";
    internal const string FailureName = "failure";

    internal static void ReadStart(ref Utf8JsonReader reader, Type type)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"A {type.Name} is a JSON object, not {reader.TokenType}.");
        }
    }

    internal const int Other = 0;
    internal const int Value = 1;
    internal const int Success = 2;
    internal const int Failure = 3;

    /// <summary>Which member the reader is on (compared on the UTF-8 bytes: no string per name), and moves to its value.</summary>
    internal static int ReadPropertyName(ref Utf8JsonReader reader)
    {
        var name = reader.ValueTextEquals("value"u8) ? Value
            : reader.ValueTextEquals("failure"u8) ? Failure
            : reader.ValueTextEquals("success"u8) ? Success
            : Other;
        reader.Read();
        return name;
    }

    internal static TResult Once<TResult>(TResult? existing, TResult value)
        where TResult : struct
        => existing is null ? value : throw new JsonException("A result has exactly one of \"value\", \"success\" or \"failure\".");

    internal static Violation ReadViolation(ref Utf8JsonReader reader, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null
            ? throw new JsonException("\"failure\" must not be null.")
            : ViolationJsonConverter.ReadViolation(ref reader);

    internal static void WriteViolation(Utf8JsonWriter writer, Violation violation, JsonSerializerOptions options)
        => ViolationJsonConverter.WriteViolation(writer, violation);
}
