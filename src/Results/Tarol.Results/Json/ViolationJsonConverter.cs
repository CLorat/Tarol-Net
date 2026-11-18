using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tarol.Results.Json;

/// <summary>
/// Writes and reads a <see cref="Violation"/> in its fixed shape: <c>{"kind":"invalid","errors":[{"field":…,"code":…,"detail":…}]}</c>
/// or <c>{"kind":"error","type":"NotFound","code":…,"detail":…}</c>. The names do not follow the serializer's options (a naming
/// policy, an enum converter with its own policy), and the properties are read in any order: PostgreSQL's <c>jsonb</c> stores
/// keys sorted by length, so a stored violation comes back with <c>kind</c> after <c>code</c>.
/// </summary>
public sealed class ViolationJsonConverter : JsonConverter<Violation>
{
    /// <inheritdoc />
    public override Violation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadViolation(ref reader);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Violation value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        WriteViolation(writer, value);
    }

    internal static void WriteViolation(Utf8JsonWriter writer, Violation value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case Invalid invalid:
                writer.WriteString("kind"u8, "invalid"u8);
                writer.WriteStartArray("errors"u8);
                foreach (var error in invalid.Errors)
                {
                    writer.WriteStartObject();
                    writer.WriteString("field"u8, error.Field);
                    writer.WriteString("code"u8, error.Code);
                    if (error.Detail is { } fieldDetail)
                        writer.WriteString("detail"u8, fieldDetail);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                break;
            case Error error:
                writer.WriteString("kind"u8, "error"u8);
                writer.WriteString("type"u8, error.Type.ToString());
                writer.WriteString("code"u8, error.Code);
                if (error.Detail is { } detail)
                    writer.WriteString("detail"u8, detail);
                break;
        }
        writer.WriteEndObject();
    }

    internal static Violation ReadViolation(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A violation is a JSON object.");
        string? kind = null, code = null, detail = null;
        ErrorType? type = null;
        List<FieldError>? errors = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected a property name.");
            if (reader.ValueTextEquals("kind"u8))
            {
                reader.Read();
                kind = reader.ValueTextEquals("invalid"u8) ? "invalid"
                    : reader.ValueTextEquals("error"u8) ? "error"
                    : throw new JsonException("\"kind\" is \"invalid\" or \"error\".");
            }
            else if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                type = TypeOf(ref reader);
            }
            else if (reader.ValueTextEquals("code"u8))
            {
                reader.Read();
                code = reader.GetString();
            }
            else if (reader.ValueTextEquals("detail"u8))
            {
                reader.Read();
                detail = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else if (reader.ValueTextEquals("errors"u8))
            {
                reader.Read();
                errors = ReadErrors(ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();                                                      // an unknown member, as the serializer ignores it
            }
        }

        try
        {
            return kind switch
            {
                "invalid" => new Invalid(errors ?? throw new JsonException("An invalid violation has \"errors\".")),
                "error" => new Error(type ?? throw new JsonException("An error has a \"type\"."), code ?? throw new JsonException("An error has a \"code\"."), detail),
                _ => throw new JsonException("A violation has a \"kind\"."),
            };
        }
        catch (ArgumentException exception)
        {
            throw new JsonException($"Invalid violation: {exception.Message}", exception);
        }
    }

    // the names only (not a number, not "NotFound, Conflict"), whatever the caller's enum converters say
    private static ErrorType TypeOf(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
            foreach (var candidate in Enum.GetValues<ErrorType>())
                if (reader.ValueTextEquals(candidate.ToString()))
                    return candidate;
        throw new JsonException("\"type\" is the name of an ErrorType.");
    }

    private static List<FieldError> ReadErrors(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("\"errors\" is an array.");
        var errors = new List<FieldError>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("A field error is a JSON object.");
            string? field = null, code = null, detail = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.ValueTextEquals("field"u8) ? 1 : reader.ValueTextEquals("code"u8) ? 2 : reader.ValueTextEquals("detail"u8) ? 3 : 0;
                reader.Read();
                switch (name)
                {
                    case 1: field = reader.GetString(); break;
                    case 2: code = reader.GetString(); break;
                    case 3: detail = reader.TokenType == JsonTokenType.Null ? null : reader.GetString(); break;
                    default: reader.Skip(); break;
                }
            }
            try
            {
                errors.Add(new FieldError(field ?? throw new JsonException("A field error has a \"field\"."), code ?? throw new JsonException("A field error has a \"code\"."), detail));
            }
            catch (ArgumentException exception)
            {
                throw new JsonException($"Invalid field error: {exception.Message}", exception);
            }
        }
        return errors;
    }
}
