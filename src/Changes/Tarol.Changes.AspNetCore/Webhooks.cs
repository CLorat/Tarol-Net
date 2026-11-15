using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.AspNetCore;

/// <summary>One sender's configuration: its secrets (two during a rotation) and the tenant it delivers for, if any.</summary>
public sealed class WebhookSender
{
    /// <summary>The HMAC secrets; a signature by any of them is accepted (the new one and the old one during a rotation).</summary>
    public IList<string> Secrets { get; } = [];

    /// <summary>The tenant this sender entry delivers for (a per-tenant URL <c>/webhooks/{sender}/{tenant}</c>); absent: none.</summary>
    public Guid? Tenant { get; set; }
}

/// <summary>The webhook entries' configuration (bind it from configuration: <c>Webhooks:Senders:payments:Secrets:0</c>).</summary>
public sealed class WebhookOptions
{
    /// <summary>Keyed by sender (<c>payments</c>), or by sender and tenant (<c>payments/0193…</c>) for per-tenant URLs.</summary>
    public IDictionary<string, WebhookSender> Senders { get; } = new Dictionary<string, WebhookSender>(StringComparer.Ordinal);

    /// <summary>How far <c>Webhook-Timestamp</c> may be from now (default ±5 minutes): the replay window.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The answer to a known type with a contract version no factory reads (R-10): 422 (default; the sender gives
    /// up, a human sees the contract change) or 202 (acknowledged and dropped).</summary>
    public int UnknownVersionStatus { get; set; } = StatusCodes.Status422UnprocessableEntity;
}

/// <summary>A verified delivery: its id, type, version, the raw <c>data</c>, and the operation context of the sender.</summary>
/// <param name="Id">The delivery id (the idempotency key).</param>
/// <param name="Type">The event type.</param>
/// <param name="Version">The contract version, if the envelope has one.</param>
/// <param name="Data">The bytes of <c>data</c>.</param>
/// <param name="Context"><c>Actor.Service("webhook:" + sender)</c>, the entry's tenant, a new correlation.</param>
/// <param name="Rejected">0, or the status that refused the delivery (401 signature/time, 400 envelope, 404 sender, 413 size).</param>
public readonly record struct WebhookDelivery(string Id, string Type, string? Version, byte[] Data, OperationContext? Context, int Rejected);

/// <summary>
/// The webhook edge (changes.md §8): raw body first (the signature covers the exact bytes), <c>HMAC-SHA256(secret,
/// timestamp + "." + body)</c> in <c>Webhook-Signature: sha256=&lt;hex&gt;</c> compared in fixed time with every configured
/// secret, <c>Webhook-Timestamp</c> (Unix seconds) within the window of the <see cref="TimeProvider"/>, then the envelope
/// <c>{id, type, version?, data}</c> without a DOM.
/// </summary>
public sealed class Webhooks
{
    private readonly IOptionsMonitor<WebhookOptions> _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the edge.</summary>
    /// <param name="options">The configuration.</param>
    /// <param name="time">The clock of the replay window.</param>
    public Webhooks(IOptionsMonitor<WebhookOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _time = time;
    }

    /// <summary>The configuration in force.</summary>
    public WebhookOptions Options => _options.CurrentValue;

    /// <summary>Reads and verifies a delivery for <paramref name="sender"/>.</summary>
    /// <param name="http">The request.</param>
    /// <param name="endpoints">The runtime (body limit).</param>
    /// <param name="sender">The sender of the entry.</param>
    /// <returns>The delivery, or one with <see cref="WebhookDelivery.Rejected"/> set.</returns>
    public async ValueTask<WebhookDelivery> Read(HttpContext http, ChangeEndpoints endpoints, string sender)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoints);
        var config = _options.CurrentValue;
        var tenantRoute = http.Request.RouteValues["tenant"] as string;
        if (!config.Senders.TryGetValue(tenantRoute is null ? sender : $"{sender}/{tenantRoute}", out var known) || known.Secrets.Count == 0)
            return Reject(StatusCodes.Status404NotFound);
        var (body, status) = await endpoints.ReadBytes(http).ConfigureAwait(false);
        if (body is null)
            return Reject(status);
        var timestamp = http.Request.Headers["Webhook-Timestamp"].ToString();
        if (!long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var unix)
            || Math.Abs(_time.GetUtcNow().ToUnixTimeSeconds() - unix) > config.Window.TotalSeconds)
            return Reject(StatusCodes.Status401Unauthorized);
        if (!Signed(http.Request.Headers["Webhook-Signature"].ToString(), timestamp, body, known.Secrets))
            return Reject(StatusCodes.Status401Unauthorized);
        if (!Envelope(body, out var id, out var type, out var version, out var data))
            return Reject(StatusCodes.Status400BadRequest);
        Option<Id<ITenant>> tenant = known.Tenant is { } t ? (Id<ITenant>)t : Option.None;
        return new WebhookDelivery(id, type, version, data, OperationContext.Start(Actor.Service("webhook:" + sender), tenant, _time), 0);
    }

    /// <summary>The key of a delivery: (the entry's tenant, <c>webhook:&lt;sender&gt;</c>, delivery id).</summary>
    /// <param name="delivery">A verified delivery.</param>
    /// <param name="sender">The sender.</param>
    /// <returns>The options to run the change with (no principal: the signature authenticated the sender).</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ChangeOptions KeyOf(WebhookDelivery delivery, string sender)
    {
        ArgumentNullException.ThrowIfNull(delivery.Context);
        return new ChangeOptions(new IdempotencyKey(delivery.Context.Tenant, "webhook:" + sender, delivery.Id));
    }

    /// <summary>The answer table (changes.md §6): <see cref="Delivery.Classify"/> → 200 (done or replayed), 503 (retry: any
    /// Conflict, NotFound, an outage; <c>Webhook-Error</c> names the code), 422 (bury: Invalid, Forbidden, Gone).</summary>
    /// <param name="http">The request.</param>
    /// <param name="result">The pipeline's answer.</param>
    /// <returns>The write.</returns>
    public static Task Answer(HttpContext http, Result<ChangeOutcome> result)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.StatusCode = Delivery.Classify(result) switch
        {
            Verdict.Done => StatusCodes.Status200OK,
            Verdict.Retry => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        if (result.TryGetValue(out Violation? violation))
            http.Response.Headers["Webhook-Error"] = violation is Error error ? error.Code : "Invalid";
        return Task.CompletedTask;
    }

    private static bool Signed(string header, string timestamp, byte[] body, IEnumerable<string> secrets)
    {
        if (!header.StartsWith("sha256=", StringComparison.Ordinal) || header.Length != 7 + 64)
            return false;
        var given = new byte[32];
        if (Convert.FromHexString(header.AsSpan(7), given, out _, out var written) != OperationStatus.Done || written != 32)
            return false;
        var signed = new byte[timestamp.Length + 1 + body.Length];
        Encoding.ASCII.GetBytes(timestamp, signed);
        signed[timestamp.Length] = (byte)'.';
        body.CopyTo(signed.AsSpan(timestamp.Length + 1));
        var valid = false;
        foreach (var secret in secrets)                                         // every secret, so the time does not tell which
            valid |= CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed), given);
        return valid;
    }

    private static bool Envelope(byte[] body, out string id, out string type, out string? version, out byte[] data)
    {
        (id, type, version, data) = ("", "", null, []);
        string? foundId = null, foundType = null;
        byte[]? foundData = null;
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id" when reader.TokenType == JsonTokenType.String:
                        foundId = reader.GetString();
                        break;
                    case "type" when reader.TokenType == JsonTokenType.String:
                        foundType = reader.GetString();
                        break;
                    case "version" when reader.TokenType == JsonTokenType.Number:
                        version = reader.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "version" when reader.TokenType == JsonTokenType.String:
                        version = reader.GetString();
                        break;
                    case "data":
                        var start = (int)reader.TokenStartIndex;
                        reader.Skip();
                        foundData = body[start..(int)reader.BytesConsumed];
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }
        if (foundId is not { Length: > 0 and <= 255 } || foundType is null || foundData is null)
            return false;
        (id, type, data) = (foundId, foundType, foundData);
        return true;
    }

    private static WebhookDelivery Reject(int status) => new("", "", null, [], null, status);
}
