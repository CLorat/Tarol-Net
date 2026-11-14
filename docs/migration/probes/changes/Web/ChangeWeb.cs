using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.Web;

// Library part of the would-be Tarol.Changes.AspNetCore (step 10): context from the ClaimsPrincipal, body reading,
// outcome -> HTTP, problem details with JSON-cased input paths, webhook signature + delivery id.

/// <summary>The JSON options entries use: the app's source-generated context with ValueObjectJson.Resolver first.</summary>
public sealed record ChangeJson(JsonSerializerOptions Options);

public readonly record struct Body<T>(T? Value, Violation? Failure);

public static class ChangeHttp
{
    public static async ValueTask<Body<T>> ReadBody<T>(HttpContext http, ChangeJson json)
    {
        if (!(http.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return new(default, Error.UnprocessableEntity("Request.ContentType", "application/json expected"));
        try
        {
            var value = await JsonSerializer.DeserializeAsync(http.Request.Body, (JsonTypeInfo<T>)json.Options.GetTypeInfo(typeof(T)), http.RequestAborted);
            return value is null ? new(default, Invalid.Field("", "Required")) : new(value, null);
        }
        catch (JsonException e)
        {
            // the path STJ reports is already in JSON names: $.reserve
            return new(default, Invalid.Field(e.Path is { Length: > 2 } p ? p[2..] : "", "Json"));
        }
    }

    /// <summary>The operation context at the HTTP edge: actor from the principal, tenant from a claim, new correlation.</summary>
    public static (OperationContext Context, ChangeOptions Options) Context(HttpContext http)
    {
        var services = http.RequestServices;
        var user = http.User;
        var actor = user.Identity?.IsAuthenticated == true && user.FindFirst("sub")?.Value is { } sub
            ? user.FindFirst("client_id") is null ? Actor.User(sub) : Actor.Service(sub)
            : Actor.Anonymous;
        Option<Id<ITenant>> tenant = user.FindFirst("tenant")?.Value is { } t && Id<ITenant>.TryParse(t, null, out var id) ? id : Option.None;
        var context = OperationContext.Start(actor, tenant, services.GetRequiredService<TimeProvider>());
        Option<IdempotencyKey> key = http.Request.Headers["Idempotency-Key"].ToString() is { Length: > 0 } k
            ? new IdempotencyKey(tenant, $"{actor.Id}:{http.Request.Path}", k) : Option.None;   // scoped to the caller and the route
        Option<long> ifMatch = http.Request.Headers.IfMatch.ToString().Trim('"') is { Length: > 0 } v && long.TryParse(v, out var n) ? n : Option.None;
        return (context, new ChangeOptions(key, ifMatch, user));
    }

    public static Task Write(HttpContext http, Violation violation, ChangeJson json) => Problem(http, violation, json);

    public static async Task Write(HttpContext http, Result<ChangeOutcome> result, ChangeJson json, bool created = false)
    {
        if (result.TryGetValue(out Violation? violation))
        {
            await Problem(http, violation, json);
            return;
        }
        result.TryGetValue(out ChangeOutcome outcome);
        http.Response.Headers.ETag = $"\"{outcome.Version}\"";
        if (!created)
        {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        http.Response.StatusCode = outcome.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created;
        http.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(http.Response.Body);
        writer.WriteStartObject();
        writer.WriteString("id", outcome.StreamId);
        writer.WriteNumber("version", outcome.Version);
        writer.WriteEndObject();
    }

    // RFC 9457 problem details, written without ProblemDetails metadata (security.md insight 6)
    private static async Task Problem(HttpContext http, Violation violation, ChangeJson json)
    {
        var status = violation is Error e ? e.Type.ToHttpStatusCode() : StatusCodes.Status400BadRequest;
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json";
        var naming = json.Options.PropertyNamingPolicy ?? JsonNamingPolicy.CamelCase;
        await using var writer = new Utf8JsonWriter(http.Response.Body);
        writer.WriteStartObject();
        writer.WriteString("type", $"https://tarol.dev/problems/{status}");
        writer.WriteNumber("status", status);
        switch (violation)
        {
            case Invalid invalid:
                writer.WriteString("title", "One or more fields are invalid.");
                writer.WriteStartObject("errors");
                foreach (var group in invalid.Errors.GroupBy(f => JsonPath(f.Field, naming)))
                {
                    writer.WriteStartArray(group.Key);
                    foreach (var f in group) writer.WriteStringValue(f.Code);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
                break;
            case Error error:
                writer.WriteString("title", error.Code);
                writer.WriteString("code", error.Code);
                if (error.Detail is { } d) writer.WriteString("detail", d);
                break;
        }
        writer.WriteEndObject();
    }

    /// <summary>Input paths are C# member names ("Lines[1].Sku"); convert per segment with the JSON naming policy.</summary>
    public static string JsonPath(string path, JsonNamingPolicy naming)
        => string.Join('.', path.Split('.').Select(segment =>
        {
            var bracket = segment.IndexOf('[');
            return bracket < 0 ? naming.ConvertName(segment) : naming.ConvertName(segment[..bracket]) + segment[bracket..];
        }));
}

public readonly record struct WebhookDelivery(string Id, string Type, byte[] Data, int? Rejected);

/// <summary>Webhook edge: raw body (signature over the exact bytes), HMAC-SHA256, timestamp window, delivery id.</summary>
public static class Webhooks
{
    public const int MaxBody = 64 * 1024;

    public static async ValueTask<WebhookDelivery> Read(HttpContext http, string sender)
    {
        var secrets = http.RequestServices.GetRequiredService<WebhookSecrets>();
        var time = http.RequestServices.GetRequiredService<TimeProvider>();
        if (!secrets.TryGet(sender, out var secret))
            return new("", "", [], StatusCodes.Status404NotFound);
        var buffer = new ArrayBufferWriter<byte>(4096);
        var read = 0;
        while (true)
        {
            var mem = buffer.GetMemory(4096);
            var n = await http.Request.Body.ReadAsync(mem, http.RequestAborted);
            if (n == 0) break;
            buffer.Advance(n);
            if ((read += n) > MaxBody) return new("", "", [], StatusCodes.Status413PayloadTooLarge);
        }
        var body = buffer.WrittenSpan;
        var timestamp = http.Request.Headers["Webhook-Timestamp"].ToString();
        if (!long.TryParse(timestamp, out var unix) || Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - unix) > 300)
            return new("", "", [], StatusCodes.Status401Unauthorized);           // replay window
        var signed = new byte[timestamp.Length + 1 + body.Length];
        Encoding.ASCII.GetBytes(timestamp, signed);
        signed[timestamp.Length] = (byte)'.';
        body.CopyTo(signed.AsSpan(timestamp.Length + 1));
        var expected = HMACSHA256.HashData(secret, signed);
        var given = http.Request.Headers["Webhook-Signature"].ToString();
        if (!given.StartsWith("sha256=", StringComparison.Ordinal) || !TryHex(given[7..], out var actual)
            || !CryptographicOperations.FixedTimeEquals(expected, actual))
            return new("", "", [], StatusCodes.Status401Unauthorized);
        // envelope { "id": "...", "type": "...", "data": { ... } } read without a DOM
        var reader = new Utf8JsonReader(body);
        string? id = null, type = null; byte[]? data = null;
        try
        {
            reader.Read();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id": id = reader.GetString(); break;
                    case "type": type = reader.GetString(); break;
                    case "data":
                        var start = (int)reader.TokenStartIndex;
                        reader.Skip();
                        data = body[start..((int)reader.BytesConsumed)].ToArray();
                        break;
                    default: reader.Skip(); break;
                }
            }
        }
        catch (JsonException)
        {
            return new("", "", [], StatusCodes.Status400BadRequest);
        }
        return id is null || type is null || data is null ? new("", "", [], StatusCodes.Status400BadRequest) : new(id, type, data, null);
    }

    /// <summary>The sender retries on 5xx (and timeouts), stops on 2xx; 4xx means "we will never accept this".</summary>
    public static Task Answer(HttpContext http, Result<ChangeOutcome> result)
    {
        http.Response.StatusCode = DeliveryPolicy.Decide(result, notFoundIsTransient: true).Decision switch
        {
            Delivery.Acknowledge => StatusCodes.Status200OK,
            Delivery.Retry => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        return Task.CompletedTask;
    }

    private static bool TryHex(string s, out byte[] bytes)
    {
        try { bytes = Convert.FromHexString(s); return true; }
        catch (FormatException) { bytes = []; return false; }
    }
}

public sealed class WebhookSecrets(IReadOnlyDictionary<string, byte[]> secrets)
{
    public bool TryGet(string sender, out byte[] secret) => secrets.TryGetValue(sender, out secret!);
}
