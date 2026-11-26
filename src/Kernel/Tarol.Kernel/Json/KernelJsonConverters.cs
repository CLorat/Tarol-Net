using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Kernel.Json;

/// <summary>
/// JSON for <see cref="Actor"/>: <c>{"kind":"System","id":"Sagas.X","onBehalfOf":"alice"}</c> (<c>onBehalfOf</c> is
/// <c>null</c> or missing when absent). A fixed contract, independent of the caller's naming policy, because it is stored
/// in event metadata. Applied through <c>[JsonConverter]</c> on the type; you do not register it.
/// </summary>
public sealed class ActorJsonConverter : JsonConverter<Actor>
{
    /// <inheritdoc />
    public override Actor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        KernelJson.Expect(ref reader, JsonTokenType.StartObject);
        ActorKind? kind = null;
        string? id = null;
        Option<string> onBehalfOf = default;
        while (KernelJson.NextProperty(ref reader))
        {
            if (reader.ValueTextEquals("kind"u8))
            {
                reader.Read();
                // the names only: Enum.TryParse would also take "1" or "User,Service" (1 | 2 = System)
                kind = reader.TokenType != JsonTokenType.String ? throw new JsonException("An actor's 'kind' is a string.")
                    : reader.ValueTextEquals("User"u8) ? ActorKind.User
                    : reader.ValueTextEquals("Service"u8) ? ActorKind.Service
                    : reader.ValueTextEquals("System"u8) ? ActorKind.System
                    : reader.ValueTextEquals("Anonymous"u8) ? ActorKind.Anonymous
                    : throw new JsonException($"Unknown actor kind '{reader.GetString()}'.");
            }
            else if (reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                id = reader.GetString();
            }
            else if (reader.ValueTextEquals("onBehalfOf"u8))
            {
                reader.Read();
                onBehalfOf = reader.GetString().ToOption();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        try
        {
            return new Actor(
                kind ?? throw new JsonException("An actor needs a 'kind'."),
                id ?? throw new JsonException("An actor needs an 'id'."),
                onBehalfOf);
        }
        catch (ArgumentException invalid)
        {
            throw new JsonException($"Invalid actor: {invalid.Message}", invalid);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Actor value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteString("kind"u8, KernelJson.KindName(value.Kind));
        writer.WriteString("id"u8, value.Id);
        if (value.OnBehalfOf.TryGetValue(out string? subject))
        {
            writer.WriteString("onBehalfOf"u8, subject);
        }
        else
        {
            writer.WriteNull("onBehalfOf"u8);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// JSON for <see cref="OperationContext"/>:
/// <c>{"actor":{…},"tenant":"&lt;guid&gt;"|null,"correlationId":"&lt;guid&gt;","causationId":"&lt;guid&gt;"|null}</c>. A fixed
/// contract that needs no entry in your <see cref="JsonSerializerContext"/> besides the type that holds the context.
/// Applied through <c>[JsonConverter]</c> on the type.
/// </summary>
public sealed class OperationContextJsonConverter : JsonConverter<OperationContext>
{
    private static readonly ActorJsonConverter ActorConverter = new();

    /// <inheritdoc />
    public override OperationContext Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        KernelJson.Expect(ref reader, JsonTokenType.StartObject);
        Actor? actor = null;
        Option<Id<ITenant>> tenant = default;
        Id<ICorrelation>? correlation = null;
        Option<Id<ICausation>> causation = default;
        Capture? capture = null;
        while (KernelJson.NextProperty(ref reader))
        {
            if (reader.ValueTextEquals("capture"u8))
            {
                reader.Read();
                capture = reader.TokenType == JsonTokenType.Null ? null : ReadCapture(ref reader);
            }
            else if (reader.ValueTextEquals("actor"u8))
            {
                reader.Read();
                actor = ActorConverter.Read(ref reader, typeof(Actor), options);
            }
            else if (reader.ValueTextEquals("tenant"u8))
            {
                reader.Read();
                tenant = reader.TokenType == JsonTokenType.Null ? Option.None : Id<ITenant>.From(reader.GetGuid());
            }
            else if (reader.ValueTextEquals("correlationId"u8))
            {
                reader.Read();
                correlation = Id<ICorrelation>.From(reader.GetGuid());
            }
            else if (reader.ValueTextEquals("causationId"u8))
            {
                reader.Read();
                causation = reader.TokenType == JsonTokenType.Null ? Option.None : Id<ICausation>.From(reader.GetGuid());
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return new OperationContext(
            actor ?? throw new JsonException("An operation context needs an 'actor'."),
            tenant,
            correlation ?? throw new JsonException("An operation context needs a 'correlationId'."),
            causation)
        { Capture = capture };
    }

    private static Capture ReadCapture(ref Utf8JsonReader reader)
    {
        KernelJson.Expect(ref reader, JsonTokenType.StartObject);
        string? device = null;
        DateTimeOffset? at = null;
        while (KernelJson.NextProperty(ref reader))
        {
            if (reader.ValueTextEquals("device"u8))
            {
                reader.Read();
                device = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else if (reader.ValueTextEquals("at"u8))
            {
                reader.Read();
                at = reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTimeOffset();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return new Capture(device, at);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, OperationContext value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WritePropertyName("actor"u8);
        ActorConverter.Write(writer, value.Actor, options);
        if (value.Tenant is Id<ITenant> tenant)
        {
            writer.WriteString("tenant"u8, tenant.Value);
        }
        else
        {
            writer.WriteNull("tenant"u8);
        }

        writer.WriteString("correlationId"u8, value.CorrelationId.Value);
        if (value.CausationId is Id<ICausation> causation)
        {
            writer.WriteString("causationId"u8, causation.Value);
        }
        else
        {
            writer.WriteNull("causationId"u8);
        }

        if (value.Capture is { } capture)                                          // TN-60; absent for a direct request
        {
            writer.WriteStartObject("capture"u8);
            if (capture.Device is { } device)
                writer.WriteString("device"u8, device);
            if (capture.At is { } at)
                writer.WriteString("at"u8, at);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}

internal static class KernelJson
{
    public static void Expect(ref Utf8JsonReader reader, JsonTokenType token)
    {
        if (reader.TokenType != token)
        {
            throw new JsonException($"Expected {token}, found {reader.TokenType}.");
        }
    }

    // Moves to the next property name; false at the end of the object.
    public static bool NextProperty(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new JsonException("Unexpected end of JSON.");
        }

        return reader.TokenType == JsonTokenType.PropertyName
            || (reader.TokenType == JsonTokenType.EndObject ? false : throw new JsonException($"Unexpected {reader.TokenType}."));
    }

    public static string KindName(ActorKind kind) => kind switch
    {
        ActorKind.Anonymous => nameof(ActorKind.Anonymous),
        ActorKind.User => nameof(ActorKind.User),
        ActorKind.Service => nameof(ActorKind.Service),
        ActorKind.System => nameof(ActorKind.System),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown actor kind."),
    };
}
