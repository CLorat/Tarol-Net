using System.Buffers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.AspNetCore;

/// <summary>How the edge maps HTTP to changes. One instance per app (singleton).</summary>
public sealed record ChangeEndpointOptions
{
    /// <summary>The app's JSON options: its source-generated context with ValueObjectJson.Resolver first.</summary>
    public JsonSerializerOptions Json { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>The options the store writes the stored state with (GET by id, candidate G1).</summary>
    public JsonSerializerOptions? StateJson { get; set; }

    public string SubjectClaim { get; set; } = "sub";
    public string ClientClaim { get; set; } = "client_id";
    public string TenantClaim { get; set; } = "tenant";

    /// <summary>The app's actor rule (an identity provider's machine users are services); null = the sub/client_id default.</summary>
    public Func<ClaimsPrincipal, Actor>? Actor { get; set; }

    /// <summary>Largest accepted body (bytes) for member binding and webhooks.</summary>
    public int MaxBody { get; set; } = 64 * 1024;

    /// <summary>Write problem details through IProblemDetailsService when the app registered one (AddProblemDetails).</summary>
    public bool UseProblemDetailsService { get; set; } = true;
}

public readonly record struct Body<T>(T? Value, Violation? Failure);

/// <summary>Everything a generated RequestDelegate needs, resolved once (singleton): no per-request service lookups but this one.</summary>
public sealed class ChangeEndpoints(ChangeEndpointOptions options, ChangePipeline pipeline, IAggregateReader reader, TimeProvider time,
    IChangeAuthorization? authorization = null)
{
    public ChangeEndpointOptions Options { get; } = options;
    public ChangePipeline Pipeline { get; } = pipeline;
    public IAggregateReader Reader { get; } = reader;
    public JsonSerializerOptions Json => Options.Json;

    public static ChangeEndpoints Of(HttpContext http) => http.RequestServices.GetRequiredService<ChangeEndpoints>();

    // ── context at the edge ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Actor from the principal, tenant from a claim, a new correlation; Idempotency-Key and If-Match to options.</summary>
    public (OperationContext Context, ChangeOptions Options, Violation? Bad) Edge(HttpContext http)
    {
        var user = http.User;
        var actor = Tarol.Kernel.Actor.Anonymous;
        if (user.Identity?.IsAuthenticated == true && Options.Actor is { } rule)
            actor = rule(user);
        else if (user.Identity?.IsAuthenticated == true)
        {
            var client = user.FindFirst(Options.ClientClaim)?.Value;
            var sub = user.FindFirst(Options.SubjectClaim)?.Value;
            actor = sub is not null && sub != client ? Tarol.Kernel.Actor.User(sub) : client is not null ? Tarol.Kernel.Actor.Service(client) : Tarol.Kernel.Actor.Anonymous;
        }
        Option<Id<ITenant>> tenant = Option.None;
        if (user.FindFirst(Options.TenantClaim)?.Value is { } t)
        {
            if (!Id<ITenant>.TryParse(t, null, out var parsed))
                return (null!, default, Error.Unauthorized("Auth.Tenant", "the tenant claim is not a tenant id"));
            tenant = parsed;
        }
        var context = OperationContext.Start(actor, tenant, time);
        Option<IdempotencyKey> key = Option.None;
        if (http.Request.Headers["Idempotency-Key"].ToString() is { Length: > 0 } k)
        {
            if (k.Length > 255)
                return (context, default, Invalid.Field("Idempotency-Key", "Length"));
            // scoped to (tenant, caller, method + path): another caller or another resource never sees this outcome
            key = new IdempotencyKey(tenant, $"http:{actor.Kind}:{actor.Id}:{http.Request.Method} {http.Request.Path}", k);
        }
        Option<long> ifMatch = Option.None;
        if (http.Request.Headers.IfMatch.ToString() is { Length: > 0 } m)
        {
            if (!(m.Length > 2 && m[0] == '"' && m[^1] == '"' && long.TryParse(m.AsSpan(1, m.Length - 2), out var v)))
                return (context, default, Invalid.Field("If-Match", "Format"));
            ifMatch = v;
        }
        return (context, new ChangeOptions(key, ifMatch, user), null);
    }

    // ── body binding ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Candidate B1: the whole input record through the app's JSON context; route members are set afterwards.</summary>
    public async ValueTask<Body<T>> ReadRecord<T>(HttpContext http)
    {
        if (!http.Request.HasJsonContentType())
            return new(default, Error.UnprocessableEntity("Request.ContentType", "application/json expected"));   // 415 normally comes from routing
        try
        {
            var info = (JsonTypeInfo<T>)Json.GetTypeInfo(typeof(T));
            var value = await JsonSerializer.DeserializeAsync(http.Request.Body, info, http.RequestAborted);
            return value is null ? new(default, Invalid.Field("", "Required")) : new(value, null);
        }
        catch (JsonException e)
        {
            return new(default, Invalid.Field(JsonPathToMember(e.Path), "Json"));
        }
    }

    /// <summary>The body as bytes, size-limited (candidate B2 and webhooks).</summary>
    public async ValueTask<(byte[]? Bytes, int Status)> ReadBytes(HttpContext http)
    {
        if (http.Request.ContentLength > Options.MaxBody)
            return (null, StatusCodes.Status413PayloadTooLarge);
        var buffer = new ArrayBufferWriter<byte>(Math.Min(4096, Options.MaxBody + 1));
        while (true)
        {
            var memory = buffer.GetMemory(4096);
            var n = await http.Request.Body.ReadAsync(memory, http.RequestAborted);
            if (n == 0)
                break;
            buffer.Advance(n);
            if (buffer.WrittenCount > Options.MaxBody)
                return (null, StatusCodes.Status413PayloadTooLarge);
        }
        return (buffer.WrittenSpan.ToArray(), 0);
    }

    /// <summary>JSON names of the members, converted once with the app's naming policy (candidate B2).</summary>
    public string[] Names(params string[] members)
        => [.. members.Select(m => Json.PropertyNamingPolicy?.ConvertName(m) ?? m)];

    public bool Is(ref Utf8JsonReader reader, string name)
        => Json.PropertyNameCaseInsensitive
            ? string.Equals(reader.GetString(), name, StringComparison.OrdinalIgnoreCase)
            : reader.ValueTextEquals(name);

    /// <summary>Reads one member value through the app's context; false (and the reader skipped) when it is not valid JSON for TMember.</summary>
    public bool TryValue<TMember>(ref Utf8JsonReader reader, out TMember? value)
    {
        try
        {
            value = JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<TMember>)Json.GetTypeInfo(typeof(TMember)));
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    // ── outcome → HTTP (changes.md §6) ───────────────────────────────────────────────────────────────────────────

    public async Task Write(HttpContext http, Result<ChangeOutcome> result, bool created = false)
    {
        if (result.TryGetValue(out Violation? violation))
        {
            await Problem(http, violation);
            return;
        }
        result.TryGetValue(out ChangeOutcome outcome);
        http.Response.Headers.ETag = $"\"{outcome.Version}\"";
        if (outcome.Replayed)
            http.Response.Headers["Idempotent-Replayed"] = "true";
        if (!created)
        {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        http.Response.StatusCode = outcome.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created;
        http.Response.Headers.Location = $"{http.Request.PathBase}{http.Request.Path.Value!.TrimEnd('/')}/{outcome.StreamId}";
        http.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(http.Response.Body);
        writer.WriteStartObject();
        writer.WriteString("id", outcome.StreamId);
        writer.WriteNumber("version", outcome.Version);
        writer.WriteEndObject();
    }

    /// <summary>RFC 9457 problem details. Through IProblemDetailsService (customizable by the app) when registered,
    /// else written by hand (no ProblemDetails metadata needed, security.md insight 6).</summary>
    public async Task Problem(HttpContext http, Violation violation)
    {
        var status = violation.ToHttpStatusCode();
        http.Response.StatusCode = status;
        if (status is StatusCodes.Status401Unauthorized)
            return;                                                     // the challenge belongs to the authentication scheme
        var naming = Json.PropertyNamingPolicy ?? JsonNamingPolicy.CamelCase;
        if (Options.UseProblemDetailsService && http.RequestServices.GetService<IProblemDetailsService>() is { } service)
        {
            ProblemDetails problem = violation switch
            {
                Invalid invalid => new HttpValidationProblemDetails(invalid.Errors.GroupBy(f => JsonPath(f.Field, naming))
                    .ToDictionary(g => g.Key, g => g.Select(f => f.Code).ToArray())) { Title = "One or more fields are invalid." },
                Error error => new ProblemDetails { Title = error.Code, Detail = error.Detail },
                _ => new ProblemDetails(),
            };
            problem.Status = status;
            if (violation is Error e)
                problem.Extensions["code"] = e.Code;
            if (await service.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem }))
                return;
        }
        http.Response.ContentType = "application/problem+json";
        await using var writer = new Utf8JsonWriter(http.Response.Body);
        writer.WriteStartObject();
        writer.WriteString("type", $"https://tools.ietf.org/html/rfc9110#section-15.5.{status - 399}");
        writer.WriteNumber("status", status);
        switch (violation)
        {
            case Invalid invalid:
                writer.WriteString("title", "One or more fields are invalid.");
                writer.WriteStartObject("errors");
                foreach (var group in invalid.Errors.GroupBy(f => JsonPath(f.Field, naming)))
                {
                    writer.WriteStartArray(group.Key);
                    foreach (var f in group)
                        writer.WriteStringValue(f.Code);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
                break;
            case Error error:
                writer.WriteString("title", error.Code);
                writer.WriteString("code", error.Code);
                if (error.Detail is { } d)
                    writer.WriteString("detail", d);
                break;
        }
        writer.WriteEndObject();
    }

    // ── reads (GET by id) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Load (tenant-scoped), authorize the Read intent, 304 on If-None-Match; null when the response is already written.</summary>
    public async ValueTask<T?> Load<T>(HttpContext http, Id<T> id)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var (context, options, bad) = Edge(http);
        if (bad is not null)
        {
            await Problem(http, bad);
            return null;
        }
        if (!(await Reader.Load(id, context, http.RequestAborted)).TryGetValue(out T? aggregate))
        {
            await Problem(http, Error.NotFound($"{typeof(T).Name}.NotFound", id.ToString()));
            return null;
        }
        if (authorization is not null)
        {
            var allowed = await authorization.Check(Read<T>.Instance, aggregate, context, options.Principal ?? new ClaimsPrincipal(), http.RequestAborted);
            if (allowed.TryGetValue(out Violation? denied))
            {
                await Problem(http, denied);
                return null;
            }
        }
        var etag = $"\"{aggregate.Version}\"";
        http.Response.Headers.ETag = etag;
        if (http.Request.Headers.IfNoneMatch.ToString() == etag)
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return null;
        }
        return aggregate;
    }

    /// <summary>Candidate G1: the stored state JSON as the store writes it.</summary>
    public async Task WriteState<T>(HttpContext http, T aggregate)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(http.Response.Body);
        writer.WriteStartObject();
        writer.WriteString("id", aggregate.Id.Value);
        writer.WriteNumber("version", aggregate.Version);
        writer.WritePropertyName("state");
        T.WriteState(writer, aggregate, Options.StateJson ?? Json);
        writer.WriteEndObject();
    }

    /// <summary>Candidate G2: a view record through the app's JSON context.</summary>
    public async Task WriteView<TView>(HttpContext http, TView view)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(http.Response.Body, view, (JsonTypeInfo<TView>)Json.GetTypeInfo(typeof(TView)), http.RequestAborted);
    }

    // ── paths ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Input paths are C# member names ("Lines[1].Sku"); convert per segment with the JSON naming policy.</summary>
    public static string JsonPath(string path, JsonNamingPolicy naming)
        => string.Join('.', path.Split('.').Select(segment =>
        {
            var bracket = segment.IndexOf('[');
            if (segment.Contains('-'))
                return segment;                                         // a header name (If-Match, Idempotency-Key), not a member
            return bracket < 0 ? naming.ConvertName(segment) : naming.ConvertName(segment[..bracket]) + segment[bracket..];
        }));

    /// <summary>STJ reports "$.reserve" (JSON names); problem details want JSON names too, so only the root is cut.</summary>
    private static string JsonPathToMember(string? path) => path is { Length: > 2 } p ? p[2..] : "";
}
