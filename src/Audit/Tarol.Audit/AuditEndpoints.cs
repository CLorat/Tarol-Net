using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes.AspNetCore;
using Tarol.Results;

namespace Tarol.Audit;

/// <summary>The list endpoint of the change log (TN-64, AU-4).</summary>
public static class AuditEndpoints
{
    /// <summary>
    /// Maps <c>GET {pattern}</c>: the caller's tenant's change log, newest first, with the optional filters <c>target</c>, <c>actor</c>,
    /// <c>streamType</c>, <c>field</c>, <c>kind</c> (<c>Created</c>, <c>Changed</c>, <c>Removed</c>, <c>Read</c>, <c>Denied</c>), <c>from</c>,
    /// <c>to</c> (RFC 3339) and the paging keys <c>after</c> and <c>limit</c>. The answer is <c>{ "items": […], "next": "…" | null }</c>;
    /// a bad value is 400 with the field. Who may read the log is the policy's: name one (it holds personal data).
    /// </summary>
    /// <param name="app">The routes.</param>
    /// <param name="pattern">The route, e.g. <c>/protokoll</c>.</param>
    /// <param name="policy">The authorization policy.</param>
    /// <returns>The endpoint, for more conventions.</returns>
    public static IEndpointConventionBuilder MapAudit(this IEndpointRouteBuilder app, string pattern, string policy)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        _ = app.ServiceProvider.GetRequiredService<IAuditLog>();                                // fail at startup without AddAudit
        return app.MapGet(pattern, (RequestDelegate)List).RequireAuthorization(policy).WithName("Tarol.Audit");
    }

    private static async Task List(HttpContext http)
    {
        var web = http.RequestServices.GetRequiredService<ChangeEndpoints>();
        var (context, _, refused) = web.Edge(http);
        if (refused is not null)
        {
            await web.Problem(http, refused).ConfigureAwait(false);
            return;
        }
        var (query, errors) = Parse(http.Request.Query);
        if (errors.Count > 0)
        {
            await web.Problem(http, new Invalid(errors)).ConfigureAwait(false);
            return;
        }
        var page = await http.RequestServices.GetRequiredService<IAuditLog>().Query(query, context, http.RequestAborted).ConfigureAwait(false);
        if (page.TryGetValue(out Violation? invalid))
        {
            await web.Problem(http, invalid).ConfigureAwait(false);
            return;
        }
        http.Response.ContentType = "application/json; charset=utf-8";
        await using var writer = new Utf8JsonWriter(http.Response.Body);
        Write(writer, page.GetValueOrThrow());
        await writer.FlushAsync(http.RequestAborted).ConfigureAwait(false);
    }

    private static (AuditQuery Query, List<FieldError> Errors) Parse(IQueryCollection q)
    {
        var errors = new List<FieldError>();
        var query = new AuditQuery();
        string? One(string name) => q.TryGetValue(name, out var v) && v.Count > 0 ? v[^1] : null;
        if (One("target") is { } target)
            query = Guid.TryParse(target, out var id) ? query with { Target = id } : Bad("target");
        if (One("actor") is { } actor)
            query = query with { Actor = actor };
        if (One("streamType") is { } streamType)
            query = query with { StreamType = streamType };
        if (One("field") is { } field)
            query = query with { Field = field };
        if (One("kind") is { } kind)
            query = Enum.TryParse<AuditKind>(kind, ignoreCase: true, out var k) && Enum.IsDefined(k) && !char.IsDigit(kind[0]) ? query with { Kind = k } : Bad("kind");
        if (One("from") is { } from)
            query = Time(from) is { } f ? query with { From = f } : Bad("from");
        if (One("to") is { } to)
            query = Time(to) is { } t ? query with { To = t } : Bad("to");
        if (One("after") is { } after)
            query = query with { After = after };
        if (One("limit") is { } limit)
            query = int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? query with { Limit = n } : Bad("limit");
        return (query, errors);

        AuditQuery Bad(string name)
        {
            errors.Add(new FieldError(name, "Format"));
            return query;
        }
    }

    private static DateTimeOffset? Time(string text)
        => text.Contains('T', StringComparison.Ordinal) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    private static void Write(Utf8JsonWriter writer, AuditPage page)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("items"u8);
        foreach (var e in page.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("id"u8, e.Id);
            writer.WriteString("at"u8, e.At);
            writer.WriteString("kind"u8, e.Kind.ToString());
            writer.WriteStartObject("actor"u8);
            writer.WriteString("kind"u8, e.Actor.Kind.ToString());
            writer.WriteString("id"u8, e.Actor.Id);
            if (e.Actor.OnBehalfOf.TryGetValue(out string? onBehalfOf))
                writer.WriteString("onBehalfOf"u8, onBehalfOf);
            writer.WriteEndObject();
            writer.WriteString("streamType"u8, e.StreamType);
            writer.WriteString("target"u8, e.Target);
            writer.WriteString("stream"u8, e.Stream);
            Optional(writer, "event"u8, e.Event);
            Optional(writer, "field"u8, e.Field);
            Value(writer, "before"u8, e.Before);
            Value(writer, "after"u8, e.After);
            if (e.CapturedAt is { } captured)
                writer.WriteString("capturedAt"u8, captured);
            Optional(writer, "device"u8, e.Device);
            Optional(writer, "view"u8, e.View);
            Optional(writer, "code"u8, e.Code);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (page.Next is { } next)
            writer.WriteString("next"u8, next);
        else
            writer.WriteNull("next"u8);
        writer.WriteEndObject();
    }

    private static void Optional(Utf8JsonWriter writer, ReadOnlySpan<byte> name, string? value)
    {
        if (value is not null)
            writer.WriteString(name, value);
    }

    // before and after are JSON values (the member as the event stored it)
    private static void Value(Utf8JsonWriter writer, ReadOnlySpan<byte> name, string? json)
    {
        writer.WritePropertyName(name);
        if (json is null)
            writer.WriteNullValue();
        else
            writer.WriteRawValue(json, skipInputValidation: false);
    }
}
