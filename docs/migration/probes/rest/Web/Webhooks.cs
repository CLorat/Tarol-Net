using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.AspNetCore;

/// <summary>One sender's configuration: one or two secrets (rotation), the tenant it delivers for (if any).</summary>
public sealed record WebhookSender
{
    public List<string> Secrets { get; set; } = [];
    public Guid? Tenant { get; set; }
}

public sealed record WebhookOptions
{
    /// <summary>Keyed by sender ("payments"), or by sender/tenant ("payments/0193…") for per-tenant URLs.</summary>
    public Dictionary<string, WebhookSender> Senders { get; set; } = new(StringComparer.Ordinal);
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A known type with an unknown contract version: 422 (the sender dead-letters, a human looks) or 202.</summary>
    public int UnknownVersionStatus { get; set; } = StatusCodes.Status422UnprocessableEntity;
}

public readonly record struct WebhookDelivery(string Id, string Type, string? Version, byte[] Data, OperationContext? Context, int Rejected);

/// <summary>Raw body first (the signature covers the exact bytes), HMAC-SHA256 over "timestamp.body" with FixedTimeEquals,
/// a ±window timestamp check from the TimeProvider, then the envelope {id, type, version?, data} without a DOM.</summary>
public sealed class Webhooks(IOptionsMonitor<WebhookOptions> options, TimeProvider time)
{
    public async ValueTask<WebhookDelivery> Read(HttpContext http, ChangeEndpoints endpoints, string sender)
    {
        var config = options.CurrentValue;
        var tenantRoute = http.Request.RouteValues["tenant"] as string;
        if (!config.Senders.TryGetValue(tenantRoute is null ? sender : $"{sender}/{tenantRoute}", out var known) || known.Secrets.Count == 0)
            return Reject(StatusCodes.Status404NotFound);
        var (body, status) = await endpoints.ReadBytes(http);
        if (body is null)
            return Reject(status);
        var timestamp = http.Request.Headers["Webhook-Timestamp"].ToString();
        if (!long.TryParse(timestamp, out var unix) || Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - unix) > config.Window.TotalSeconds)
            return Reject(StatusCodes.Status401Unauthorized);
        var given = http.Request.Headers["Webhook-Signature"].ToString();
        if (!given.StartsWith("sha256=", StringComparison.Ordinal) || !TryHex(given.AsSpan(7), out var actual))
            return Reject(StatusCodes.Status401Unauthorized);
        var signed = new byte[timestamp.Length + 1 + body.Length];
        Encoding.ASCII.GetBytes(timestamp, signed);
        signed[timestamp.Length] = (byte)'.';
        body.CopyTo(signed.AsSpan(timestamp.Length + 1));
        var valid = false;
        foreach (var secret in known.Secrets)                          // rotation: the new and the old secret, both checked
            valid |= CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed), actual);
        if (!valid)
            return Reject(StatusCodes.Status401Unauthorized);

        string? id = null, type = null, version = null;
        byte[]? data = null;
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Reject(StatusCodes.Status400BadRequest);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id": id = reader.GetString(); break;
                    case "type": type = reader.GetString(); break;
                    case "version": version = reader.TokenType == JsonTokenType.Number ? reader.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetString(); break;
                    case "data":
                        var start = (int)reader.TokenStartIndex;
                        reader.Skip();
                        data = body[start..(int)reader.BytesConsumed];
                        break;
                    default: reader.Skip(); break;
                }
            }
        }
        catch (JsonException)
        {
            return Reject(StatusCodes.Status400BadRequest);
        }
        if (id is not { Length: > 0 and <= 255 } || type is null || data is null)
            return Reject(StatusCodes.Status400BadRequest);
        Option<Id<ITenant>> tenant = known.Tenant is { } t ? (Id<ITenant>)t : Option.None;
        var context = OperationContext.Start(Actor.Service("webhook:" + sender), tenant, time);
        return new(id, type, version, data, context, 0);
    }

    public static ChangeOptions Options(WebhookDelivery delivery, string sender)
        => new(new IdempotencyKey(delivery.Context!.Tenant, "webhook:" + sender, delivery.Id));

    /// <summary>The answer table of changes.md §6, from the same classification the reaction worker uses (C-9, S-7).</summary>
    public static Task Answer(HttpContext http, Result<ChangeOutcome> result)
    {
        http.Response.StatusCode = DeliveryPolicy.Classify(result) switch
        {
            Verdict.Done => StatusCodes.Status200OK,
            Verdict.Retry => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        if (result.TryGetValue(out Violation? v) && v is Error e)
            http.Response.Headers["Webhook-Error"] = e.Code;
        return Task.CompletedTask;
    }

    private static WebhookDelivery Reject(int status) => new("", "", null, [], null, status);

    private static bool TryHex(ReadOnlySpan<char> s, out byte[] bytes)
    {
        bytes = new byte[s.Length / 2];
        return s.Length == 64 && Convert.FromHexString(s, bytes, out _, out var written) == System.Buffers.OperationStatus.Done && written == 32;
    }
}
