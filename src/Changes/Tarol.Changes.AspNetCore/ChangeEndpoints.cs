using System.Buffers;
using System.Globalization;
using System.ComponentModel;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Security;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Tarol.Changes.AspNetCore;

/// <summary>
/// The runtime of the generated entries (one singleton): the operation context from the <see cref="ClaimsPrincipal"/>,
/// the member binder's helpers, the outcome → HTTP mapping (changes.md §6) and GET by id. Generated code calls it;
/// applications rarely do.
/// </summary>
public sealed partial class ChangeEndpoints
{
    private static readonly ClaimsPrincipal NoOne = new(new ClaimsIdentity());
    private readonly TimeProvider _time;
    private readonly IChangeAuthorization _authorization;

    /// <summary>Creates the runtime.</summary>
    /// <param name="options">The edge options.</param>
    /// <param name="pipeline">The change pipeline.</param>
    /// <param name="reader">Loads aggregates for views.</param>
    /// <param name="time">The clock of the operation contexts.</param>
    /// <param name="authorization">The change authorization; required: HTTP entries fail closed without one (C-11).</param>
    /// <exception cref="InvalidOperationException"><paramref name="authorization"/> is missing.</exception>
    public ChangeEndpoints(ChangeEndpointOptions options, ChangePipeline pipeline, IAggregateReader reader, TimeProvider time, IChangeAuthorization? authorization)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(time);
        options.Validate();
        Options = options;
        Pipeline = pipeline;
        Reader = reader;
        _time = time;
        _authorization = authorization
            ?? throw new InvalidOperationException("HTTP entries need the change authorization: call AddSecurity(…) (Tarol.Security). They fail closed without it.");
    }

    /// <summary>The edge options.</summary>
    public ChangeEndpointOptions Options { get; }

    /// <summary>The pipeline the entries run.</summary>
    public ChangePipeline Pipeline { get; }

    /// <summary>The reader of views.</summary>
    public IAggregateReader Reader { get; }

    /// <summary>The JSON options of inputs and views.</summary>
    public JsonSerializerOptions Json => Options.Json;

    /// <summary>The runtime of the request's application.</summary>
    /// <param name="http">The request.</param>
    /// <returns>The singleton.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ChangeEndpoints Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        StampRequestId(http);
        return http.RequestServices.GetRequiredService<ChangeEndpoints>();
    }

    /// <summary>
    /// The id a user can quote for this request (BC-11): the W3C trace id of the request's activity (what the traces are
    /// found by), else ASP.NET's <see cref="HttpContext.TraceIdentifier"/>. Every Tarol answer carries it as
    /// <see cref="TarolHeaders.RequestId"/> and every problem as <c>traceId</c>.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <returns>The id.</returns>
    public static string RequestId(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return System.Diagnostics.Activity.Current is { } activity && activity.TraceId != default
            ? activity.TraceId.ToHexString()
            : http.TraceIdentifier;
    }

    /// <summary>Sets <see cref="TarolHeaders.RequestId"/> on the answer (once).</summary>
    /// <param name="http">The request.</param>
    /// <returns>The id.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string StampRequestId(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var id = RequestId(http);
        if (!http.Response.HasStarted)
            http.Response.Headers[TarolHeaders.RequestId] = id;
        return id;
    }

    // ── context ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The operation context and options of a request: actor (rule or claims), tenant (claim, mapped; a <c>{tenant}</c>
    /// route value must equal it), a new correlation, the principal (always set, so the change is authorized),
    /// <c>Idempotency-Key</c> scoped to (tenant, caller, method and path) with the request's fingerprint, and <c>If-Match</c>.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="body">The request body read so far (part of the key's fingerprint).</param>
    /// <returns>The context and options, or the refusal (401 tenant, 404 tenant mismatch, 400 malformed header).</returns>
    public (OperationContext Context, ChangeOptions Options, Violation? Refused) Edge(HttpContext http, ReadOnlySpan<byte> body = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var user = http.User ?? NoOne;
        var actor = ActorOf(user);
        Option<Id<ITenant>> tenant = Option.None;
        if (user.Identity?.IsAuthenticated == true && user.FindFirst(Options.TenantClaim)?.Value is { } claimed)
        {
            if (!TenantOf(claimed).TryGetValue(out Id<ITenant> parsed))
                return (null!, default, Error.Unauthorized("Auth.Tenant", "the tenant claim is not a tenant"));
            tenant = parsed;
        }
        if (http.Request.RouteValues["tenant"] is string routed
            && !(tenant.TryGetValue(out Id<ITenant> own) && TenantOf(routed).TryGetValue(out Id<ITenant> asked) && own == asked))
            return (null!, default, Error.NotFound("Tenant.NotFound", routed));                 // never reveal another tenant (R-11)
        var context = OperationContext.Start(actor, tenant, _time);

        Option<IdempotencyKey> key = Option.None;
        if (http.Request.Headers[TarolHeaders.IdempotencyKey].ToString() is { Length: > 0 } k)
        {
            if (k.Length > 255)
                return (context, default, Invalid.Field("Idempotency-Key", "Length"));
            key = new IdempotencyKey(tenant, $"http:{actor.Kind}:{actor.Id}:{http.Request.Method} {http.Request.Path}", k)
            {
                Fingerprint = Fingerprint(http.Request.Method, http.Request.Path.Value ?? "", body),
            };
        }
        Option<long> ifMatch = Option.None;
        if (http.Request.Headers.IfMatch.ToString() is { Length: > 0 } m)
        {
            if (!(m.Length > 2 && m[0] == '"' && m[^1] == '"' && long.TryParse(m.AsSpan(1, m.Length - 2), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var version)))
                return (context, default, Invalid.Field("If-Match", "Format"));
            ifMatch = version;
        }
        return (context, new ChangeOptions(key, ifMatch, user), null);
    }

    private Actor ActorOf(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            return Actor.Anonymous;
        if (Options.Actor is { } rule)
            return rule(user);
        var client = user.FindFirst(Options.ClientClaim)?.Value;
        var subject = user.FindFirst(Options.SubjectClaim)?.Value;
        return subject is not null && subject != client ? Actor.User(subject)
             : client is not null ? Actor.Service(client)
             : Actor.Anonymous;
    }

    private Option<Id<ITenant>> TenantOf(string value)
        => Options.Tenant is { } map ? map(value)
         : Id<ITenant>.TryParse(value, null, out var id) ? id
         : Option.None;

    /// <summary>SHA-256 over method, path and body: what an <c>Idempotency-Key</c> was used for (R-9).</summary>
    private static string Fingerprint(string method, string path, ReadOnlySpan<byte> body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(method + " " + path + "\n"));
        hash.AppendData(body);
        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // ── body ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The body as bytes, at most <see cref="ChangeEndpointOptions.MaxBody"/> (else status 413).</summary>
    /// <param name="http">The request.</param>
    /// <returns>The bytes, or <see langword="null"/> and the status to answer.</returns>
    public async ValueTask<(byte[]? Bytes, int Status)> ReadBytes(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Request.ContentLength > Options.MaxBody)
            return (null, StatusCodes.Status413PayloadTooLarge);
        var buffer = new ArrayBufferWriter<byte>(Math.Min(4096, Options.MaxBody + 1));
        while (true)
        {
            var read = await http.Request.Body.ReadAsync(buffer.GetMemory(4096), http.RequestAborted).ConfigureAwait(false);
            if (read == 0)
                break;
            buffer.Advance(read);
            if (buffer.WrittenCount > Options.MaxBody)
                return (null, StatusCodes.Status413PayloadTooLarge);
        }
        return (buffer.WrittenSpan.ToArray(), 0);
    }

    /// <summary>
    /// The body of a change entry: 415 when a non-empty body is not JSON (<c>Content-Type</c> missing, or not
    /// <c>application/json</c> / <c>application/*+json</c>, or a charset other than UTF-8; BC-16), else <see cref="ReadBytes"/>.
    /// An empty body needs no content type (the binder answers what is missing).
    /// </summary>
    /// <param name="http">The request.</param>
    /// <returns>The bytes, or <see langword="null"/> and the status to answer (413, 415).</returns>
    public async ValueTask<(byte[]? Bytes, int Status)> ReadJson(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var (bytes, status) = await ReadBytes(http).ConfigureAwait(false);
        if (bytes is null)
            return (null, status);
        return bytes.Length == 0 || IsJson(http.Request.ContentType) ? (bytes, 0) : (null, StatusCodes.Status415UnsupportedMediaType);
    }

    /// <summary>Whether a <c>Content-Type</c> names JSON in UTF-8: <c>application/json</c> or <c>application/*+json</c>.</summary>
    /// <param name="contentType">The header value.</param>
    /// <returns>Whether it is JSON.</returns>
    public static bool IsJson(string? contentType)
    {
        if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var media))
            return false;
        if (media.Charset.HasValue && !media.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            return false;
        return media.Type.Equals("application", StringComparison.OrdinalIgnoreCase)
            && (media.SubType.Equals("json", StringComparison.OrdinalIgnoreCase) || media.SubType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The JSON names of members, from the naming policy unless the member names its own (<c>[JsonPropertyName]</c>).</summary>
    /// <param name="members">Pairs of (C# name, explicit JSON name or <see langword="null"/>).</param>
    /// <returns>The JSON names, in order.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string[] Names(params (string Member, string? Json)[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return [.. members.Select(m => m.Json ?? Json.PropertyNamingPolicy?.ConvertName(m.Member) ?? m.Member)];
    }

    /// <summary>
    /// Adds <c>Required</c> for every member missing in a nested record of the body (TN-23, Tatalu FB-F02.1-3, FB-F03.9-1):
    /// System.Text.Json reads a missing <c>DateOnly</c> as 0001-01-01 and a missing enum as its first value. The top-level
    /// members are the binder's own; called by generated code with the tree of the input's nested records.
    /// </summary>
    /// <param name="bytes">The body.</param>
    /// <param name="members">The top-level members that hold records, with their records' members.</param>
    /// <param name="errors">The errors so far; created when one is added.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Missing(byte[] bytes, RequiredMember[] members, ref List<FieldError>? errors)
    {
        ArgumentNullException.ThrowIfNull(members);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return;
        foreach (var member in members)
            if (Property(document.RootElement, member) is { } value && member.Members is { } inner)
                Nested(value, member.Name, member.List, inner, ref errors);
    }

    private void Nested(JsonElement value, string path, bool list, RequiredMember[] members, ref List<FieldError>? errors)
    {
        if (list)
        {
            if (value.ValueKind != JsonValueKind.Array)
                return;
            var i = 0;
            foreach (var item in value.EnumerateArray())
                Nested(item, string.Create(CultureInfo.InvariantCulture, $"{path}[{i++}]"), false, members, ref errors);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object)
            return;
        foreach (var member in members)
        {
            var field = path + "." + member.Name;
            if (Property(value, member) is not { } inner)
            {
                if (!member.Optional && errors?.Exists(f => f.Field == field) != true)
                    (errors ??= []).Add(new FieldError(field, ValidationCodes.Required));
                continue;
            }
            if (member.Members is { } next)
                Nested(inner, field, member.List, next, ref errors);
        }
    }

    private JsonElement? Property(JsonElement value, RequiredMember member)
    {
        var name = member.Json ?? Json.PropertyNamingPolicy?.ConvertName(member.Name) ?? member.Name;
        foreach (var property in value.EnumerateObject())
            if (string.Equals(property.Name, name, Json.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return property.Value;
        return null;
    }

    /// <summary>Whether the reader's property name is <paramref name="name"/> (case-insensitive when the options say so).</summary>
    /// <param name="reader">A reader on a property name.</param>
    /// <param name="name">The JSON name.</param>
    /// <returns>Whether it matches.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool Is(ref Utf8JsonReader reader, string name)
        => Json.PropertyNameCaseInsensitive
            ? string.Equals(reader.GetString(), name, StringComparison.OrdinalIgnoreCase)
            : reader.ValueTextEquals(name);

    /// <summary>Reads one member value through the application's JSON context on a copy of the reader: on success the reader
    /// moves past the value; on failure it stays on the value's first token (the caller skips it).</summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="reader">A reader on the value's first token.</param>
    /// <param name="value">The value.</param>
    /// <returns>Whether the value is valid JSON for <typeparamref name="TMember"/>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool TryValue<TMember>(ref Utf8JsonReader reader, out TMember? value)
    {
        var copy = reader;
        try
        {
            value = JsonSerializer.Deserialize(ref copy, Json.GetTypeInfo<TMember>());
            reader = copy;
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
        catch (InvalidOperationException)                                     // a token the converter refused
        {
            value = default;
            return false;
        }
    }

    /// <summary>A webhook payload through the application's JSON context; <see langword="null"/> when the data does not match.</summary>
    /// <typeparam name="TPayload">The sender's contract record.</typeparam>
    /// <param name="data">The envelope's <c>data</c>.</param>
    /// <returns>The payload, or <see langword="null"/>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TPayload? Payload<TPayload>(byte[] data)
        where TPayload : class
    {
        try
        {
            return JsonSerializer.Deserialize(data, Json.GetTypeInfo<TPayload>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── outcome → HTTP ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes a pipeline result: 204 (change) or 201 + <c>Location</c> + <c>{id, version}</c> (creation), both with
    /// <c>ETag</c>; a replayed key: the same status (200 for a creation) and <c>Idempotent-Replayed: true</c>; a failure:
    /// <see cref="Problem"/>.</summary>
    /// <param name="http">The request.</param>
    /// <param name="result">The result.</param>
    /// <param name="created">Whether the entry is a creation.</param>
    /// <returns>The write.</returns>
    public async Task Write(HttpContext http, Result<ChangeOutcome> result, bool created = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (result.TryGetValue(out Violation? violation))
        {
            await Problem(http, violation).ConfigureAwait(false);
            return;
        }
        var outcome = result.GetValueOrThrow();
        http.Response.Headers.ETag = $"\"{outcome.Version}\"";
        if (outcome.Position != GlobalPosition.Start)
            http.Response.Headers[Positions.PositionHeader] = Positions.Format(outcome.Position);
        if (outcome.Replayed)
            http.Response.Headers[TarolHeaders.IdempotentReplayed] = "true";
        if (!created)
        {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        http.Response.StatusCode = outcome.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created;
        http.Response.Headers.Location = $"{http.Request.PathBase}{http.Request.Path.Value?.TrimEnd('/')}/{outcome.StreamId}";
        http.Response.ContentType = "application/json";
        var writer = new Utf8JsonWriter(http.Response.Body);
        await using (writer.ConfigureAwait(false))
        {
            writer.WriteStartObject();
            writer.WriteString("id", outcome.StreamId);
            writer.WriteNumber("version", outcome.Version);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// RFC 9457 problem details: <c>Invalid</c> → 400 with <c>errors</c> keyed by JSON-cased input paths; an <c>Error</c> →
    /// <c>ToHttpStatusCode()</c> with <c>code</c> (and <c>detail</c>). 401 has no body (the scheme's challenge belongs there).
    /// Through <c>IProblemDetailsService</c> when registered, else written directly (no <c>ProblemDetails</c> metadata needed).
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="violation">The failure.</param>
    /// <returns>The write.</returns>
    public async Task Problem(HttpContext http, Violation violation)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(violation);
        var status = violation.ToHttpStatusCode();
        var requestId = StampRequestId(http);
        http.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized)
            return;
        var naming = Json.PropertyNamingPolicy ?? JsonNamingPolicy.CamelCase;
        if (Options.UseProblemDetailsService && http.RequestServices.GetService<IProblemDetailsService>() is { } service)
        {
            ProblemDetails problem = violation is Invalid invalid
                ? new HttpValidationProblemDetails(Errors(invalid, naming)) { Title = "One or more fields are invalid.", Extensions = { ["errorDetails"] = Details(invalid, naming) } }
                : new ProblemDetails { Title = ((Error)violation).Code, Detail = ((Error)violation).Detail };
            problem.Status = status;
            problem.Extensions["traceId"] = requestId;                         // the header's id, not ASP.NET's W3C traceparent
            if (violation is Error error)
            {
                problem.Extensions["code"] = error.Code;
                if (error.Reasons is { } reasons)
                    problem.Extensions["reasons"] = reasons;                // TN-36
            }
            if (await service.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem }).ConfigureAwait(false))
                return;
        }
        http.Response.ContentType = "application/problem+json";
        var writer = new Utf8JsonWriter(http.Response.Body);
        await using (writer.ConfigureAwait(false))
        {
            writer.WriteStartObject();
            writer.WriteString("type", $"https://tools.ietf.org/html/rfc9110#section-15.{(status >= 500 ? 6 : 5)}.{(status >= 500 ? status - 499 : status - 399)}");
            writer.WriteNumber("status", status);
            if (violation is Invalid invalid)
            {
                writer.WriteString("title", "One or more fields are invalid.");
                writer.WriteStartObject("errors");
                foreach (var (path, codes) in Errors(invalid, naming))
                {
                    writer.WriteStartArray(path);
                    foreach (var code in codes)
                        writer.WriteStringValue(code);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
                writer.WritePropertyName("errorDetails");
                WriteDetails(writer, invalid, naming);
            }
            else if (violation is Error error)
            {
                writer.WriteString("title", error.Code);
                writer.WriteString("code", error.Code);
                if (error.Detail is { } detail)
                    writer.WriteString("detail", detail);
                if (error.Reasons is { } reasons)
                {
                    writer.WriteStartArray("reasons");                         // TN-36 (Tatalu FB-F03.4-5)
                    foreach (var reason in reasons)
                        writer.WriteStringValue(reason);
                    writer.WriteEndArray();
                }
            }
            writer.WriteString("traceId", requestId);
            writer.WriteEndObject();
        }
    }

    /// <summary><c>errorDetails</c> (BC-5): per JSON path the errors with their code and, when the rule gave one, its detail
    /// (e.g. the bounds); <c>errors</c> keeps the codes only.</summary>
    private static void WriteDetails(Utf8JsonWriter writer, Invalid invalid, JsonNamingPolicy naming)
    {
        writer.WriteStartObject();
        foreach (var group in invalid.Errors.GroupBy(f => JsonPath(f.Field, naming), StringComparer.Ordinal))
        {
            writer.WriteStartArray(group.Key);
            foreach (var error in group)
            {
                writer.WriteStartObject();
                writer.WriteString("code", error.Code);
                if (error.Detail is { } detail)
                    writer.WriteString("detail", detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary><see cref="WriteDetails"/> as a <see cref="JsonElement"/> for an <see cref="IProblemDetailsService"/>
    /// (its JSON metadata covers <see cref="JsonElement"/> also under Native AOT).</summary>
    private static JsonElement Details(Invalid invalid, JsonNamingPolicy naming)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
            WriteDetails(writer, invalid, naming);
        return JsonElement.Parse(buffer.WrittenSpan);
    }

    private static Dictionary<string, string[]> Errors(Invalid invalid, JsonNamingPolicy naming)
        => invalid.Errors.GroupBy(f => JsonPath(f.Field, naming), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Code).ToArray(), StringComparer.Ordinal);

    /// <summary>Input paths are C# member names (<c>Lines[1].Sku</c>); each segment is converted with the naming policy;
    /// header names (<c>If-Match</c>) stay as they are.</summary>
    /// <param name="path">The input path.</param>
    /// <param name="naming">The naming policy.</param>
    /// <returns>The JSON path.</returns>
    public static string JsonPath(string path, JsonNamingPolicy naming)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(naming);
        return string.Join('.', path.Split('.').Select(segment =>
        {
            if (segment.Length == 0 || segment.Contains('-', StringComparison.Ordinal))
                return segment;
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            return bracket < 0 ? naming.ConvertName(segment) : naming.ConvertName(segment[..bracket]) + segment[bracket..];
        }));
    }

    // ── views (GET by id) ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads the aggregate (tenant-scoped: another tenant's is 404), authorizes <c>Read&lt;T&gt;</c>, sets
    /// <c>ETag</c> and the <see cref="CacheHeaders"/>, and answers 304 for a matching <c>If-None-Match</c>; <see langword="null"/> when the response is written.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="http">The request.</param>
    /// <param name="id">The id from the route.</param>
    /// <returns>The aggregate to build the view from, or <see langword="null"/>.</returns>
    public async ValueTask<T?> Load<T>(HttpContext http, Id<T> id)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(http);
        var (context, options, refused) = Edge(http);
        if (refused is not null)
        {
            await Problem(http, refused).ConfigureAwait(false);
            return null;
        }
        if (!(await Reader.Load(id, context, http.RequestAborted).ConfigureAwait(false)).TryGetValue(out T? aggregate))
        {
            await Problem(http, Error.NotFound($"{typeof(T).Name}.NotFound", id.ToString())).ConfigureAwait(false);
            return null;
        }
        var allowed = await _authorization.Check(Read<T>.Instance, aggregate, new ChangeContext(context, _time.GetUtcNow()),
            options.Principal ?? NoOne, http.RequestAborted).ConfigureAwait(false);
        if (allowed.TryGetValue(out Violation? denied))
        {
            await Problem(http, denied).ConfigureAwait(false);
            return null;
        }
        var etag = $"\"{aggregate.Version}\"";
        http.Response.Headers.ETag = etag;
        CacheHeaders.Apply(http);                                        // the view's 200 and 304
        if (EntityTags.Matches(http.Request.Headers.IfNoneMatch, etag))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return null;
        }
        return aggregate;
    }

    /// <summary>
    /// GET of a <see cref="PerTenantAttribute"/> aggregate's view (TN-2): the caller's tenant's one, by the tenant's id; then as
    /// <see cref="Load{T}"/>. Without a tenant: 403 <see cref="TenantAggregates.TenantRequiredCode"/>.
    /// </summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <param name="http">The request.</param>
    /// <returns>The aggregate, or <see langword="null"/> when the answer is written.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async ValueTask<T?> LoadTenant<T>(HttpContext http)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(http);
        var (context, _, refused) = Edge(http);
        if (refused is not null)
        {
            await Problem(http, refused).ConfigureAwait(false);
            return null;
        }
        if (!TenantAggregates.TryIdOf<T>(context, out var id))
        {
            await Problem(http, Error.Forbidden(TenantAggregates.TenantRequiredCode, typeof(T).Name)).ConfigureAwait(false);
            return null;
        }
        return await Load(http, id).ConfigureAwait(false);
    }

    /// <summary>Writes a view (200, the application's JSON context).</summary>
    /// <typeparam name="TView">The view type.</typeparam>
    /// <param name="http">The request.</param>
    /// <param name="view">The view.</param>
    /// <returns>The write.</returns>
    public Task WriteView<TView>(HttpContext http, TView view)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(http.Response.Body, view, Json.GetTypeInfo<TView>(), http.RequestAborted);
    }
}
