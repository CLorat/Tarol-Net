using System.Buffers;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.Search;

namespace Tarol.Changes.AspNetCore;

/// <summary>
/// The global search entry (GS-9): <c>GET {pattern}?q=…</c> over every searchable type the caller may read, in the caller's
/// tenant, with the same edge as the list entries (tenant from the claim, cache headers, problems, <c>Tarol-Read-After</c>,
/// <c>Tarol-Position</c>, a weak <c>ETag</c>).
/// </summary>
public static class SearchEndpoints
{
    /// <summary>The problem code while no index version is current yet (503).</summary>
    public const string BuildingCode = "Search.Building";

    /// <summary>
    /// Maps <c>GET {pattern}</c>: <c>q</c> (web-search syntax), <c>types</c> (comma-separated; default: every readable type),
    /// <c>prefix</c>, <c>counts</c>, <c>snippets</c> (true/false), <c>limit</c> (≤ 50), <c>after</c> (the previous page's
    /// <c>next</c>), <c>lang</c> (else <c>Accept-Language</c>). Answers <c>{ "items": [{type, id, aggregate, title, score,
    /// snippet}], "counts", "countsCapped", "next" }</c>; 403 for a requested type the caller may not read (<c>Read&lt;T&gt;</c>);
    /// 503 while no index version is current. The caller's <c>sub</c> and roles are its audience keys (GS-7).
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route (default <c>/search</c>).</param>
    /// <param name="policy">An authorization policy on top of the per-type grants, or <see langword="null"/>.</param>
    /// <returns>The endpoint's builder.</returns>
    public static IEndpointConventionBuilder MapTarolSearch(this IEndpointRouteBuilder endpoints, string pattern = "/search", string? policy = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var builder = endpoints.MapGet(pattern, Search).WithName("TarolSearch").WithMetadata(
            ((RequestDelegate)Search).Method,                                   // OpenAPI describes endpoints that carry a MethodInfo (BC-3)
            new ProducesResponseTypeMetadata(StatusCodes.Status200OK),
            new TarolEndpointMetadata("search", typeof(SearchHit), [], [], policy,
            [
                new("q", "string", [], "The text: words (all must match), \"a phrase\", -word, a OR b; 1 to 256 characters"),
                new("types", "string", [], "Comma-separated document types; default: every type the caller may read"),
                new("prefix", "boolean", [], "The last word is a prefix"),
                new("counts", "boolean", [], "Also count the matches per type"),
                new("snippets", "boolean", [], "Return a plain-text snippet and its highlights"),
                new("limit", "int32", [], $"The page size (1 to {SearchLimits.MaxLimit})"),
                new("after", "string", [], "The cursor of the previous page (its next)"),
                new("lang", "string", [], "The query's language (ISO 639-1); default: Accept-Language"),
            ])
            { Errors = [BuildingCode, "Auth.Forbidden", "Tenant.NotFound"] });
        if (policy is not null)
            builder.RequireAuthorization(policy);
        return builder;
    }

    private sealed class Access(IChangeAuthorization authorization, ChangeContext context, ClaimsPrincipal principal) : ISearchAccess
    {
        public async ValueTask<bool> CanRead<T>(CancellationToken cancellationToken)
            where T : AggregateRoot<T>, IAggregateRoot<T>
            => (await authorization.CheckList<T>(context, principal, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }

    private static async Task Search(HttpContext http)
    {
        var entries = ChangeEndpoints.Of(http);
        CacheHeaders.Apply(http);
        var (context, _, refused) = entries.Edge(http);
        if (refused is not null)
        {
            await entries.Problem(http, refused).ConfigureAwait(false);
            return;
        }
        var principal = http.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        if (principal.Identity?.IsAuthenticated != true)
        {
            await entries.Problem(http, Error.Unauthorized("Auth.Required")).ConfigureAwait(false);
            return;
        }

        List<FieldError>? errors = null;
        var query = Read(http, ref errors);
        if (errors is not null)
        {
            await entries.Problem(http, new Invalid([.. errors])).ConfigureAwait(false);
            return;
        }

        var registry = http.RequestServices.GetRequiredService<SearchRegistry>();
        var time = http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        var authorization = http.RequestServices.GetRequiredService<IChangeAuthorization>();
        var readable = await registry.Readable(new Access(authorization, new ChangeContext(context, time.GetUtcNow()), principal), http.RequestAborted)
            .ConfigureAwait(false);
        if (query.Types is { } asked)
        {
            if (asked.FirstOrDefault(t => registry.Types.All(r => r.Type != t)) is { } unknown)
            {
                await entries.Problem(http, Invalid.Field("types", "Search.Type", $"'{unknown}' is not a searchable type.")).ConfigureAwait(false);
                return;
            }
            if (asked.Any(t => !readable.Contains(t)))
            {
                await entries.Problem(http, Error.Forbidden("Auth.Forbidden")).ConfigureAwait(false);
                return;
            }
        }
        else if (readable.Count == 0)
        {
            await entries.Problem(http, Error.Forbidden("Auth.Forbidden")).ConfigureAwait(false);
            return;
        }
        query = query with { Types = query.Types ?? readable, Audience = AudienceOf(principal, entries.Options.SubjectClaim) };

        if (http.Request.Headers[Positions.ReadAfterHeader].ToString() is { Length: > 0 } readAfter)
        {
            if (!Positions.TryParse(readAfter, out var position))
            {
                await entries.Problem(http, Invalid.Field(Positions.ReadAfterHeader, "Format")).ConfigureAwait(false);
                return;
            }
            if (!await Passed(http.RequestServices.GetRequiredService<SearchHost>().Index, position, entries.Options.Queries.ReadAfterTimeout, time,
                    http.RequestAborted).ConfigureAwait(false))
                http.Response.Headers[Positions.StaleHeader] = "true";
        }

        var result = await http.RequestServices.GetRequiredService<ISearch>().Search(query, context, http.RequestAborted).ConfigureAwait(false);
        if (!result.TryGetValue(out SearchPage? page))
        {
            await entries.Problem(http, result.TryGetValue(out Violation? violation) ? violation : Error.Internal("Search.Failed")).ConfigureAwait(false);
            return;
        }
        if (page.State == ProjectionState.Building)
        {
            http.Response.Headers.RetryAfter = "5";
            await entries.Problem(http, Error.Unavailable(BuildingCode, "The search index is being built.")).ConfigureAwait(false);
            return;
        }
        http.Response.Headers[Positions.PositionHeader] = Positions.Format(page.Position);
        var etag = ETag(page);
        http.Response.Headers.ETag = etag;
        if (EntityTags.Matches(http.Request.Headers.IfNoneMatch, etag))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, page);
        await http.Response.Body.WriteAsync(buffer.WrittenMemory, http.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the index's stored checkpoint passes <paramref name="position"/>, polling it every 5 ms (QR-5 measured p50 8.2 ms
    /// from an API process). The stored checkpoint, not this process's host: in an API process the host is not the runner.
    /// </summary>
    private static async Task<bool> Passed(ISearchIndex index, Tarol.Persistence.GlobalPosition position, TimeSpan timeout, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow() + timeout;
        while (true)
        {
            if ((await index.Checkpoint(cancellationToken).ConfigureAwait(false)).Position >= position)
                return true;
            if (time.GetUtcNow() >= until)
                return false;
            await Task.Delay(TimeSpan.FromMilliseconds(5), time, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SearchQuery Read(HttpContext http, ref List<FieldError>? errors)
    {
        var q = http.Request.Query;
        var text = q["q"].ToString();
        if (q["q"].Count != 1 || text.Length is 0 or > SearchLimits.MaxText)
            (errors ??= []).Add(new FieldError("q", "Search.Text", $"One query 'q' of 1 to {SearchLimits.MaxText} characters."));
        var query = new SearchQuery(text)
        {
            Prefix = Flag(q, "prefix", ref errors),
            Counts = Flag(q, "counts", ref errors),
            Snippets = Flag(q, "snippets", ref errors),
            After = q["after"].Count == 1 ? q["after"].ToString() : null,
            Language = q["lang"].Count == 1 ? q["lang"].ToString() : LanguageOf(http.Request.Headers.AcceptLanguage),
        };
        if (q["types"].ToString() is { Length: > 0 } types)
            query = query with { Types = [.. types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)] };
        if (q["limit"].ToString() is { Length: > 0 } limit)
        {
            if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > SearchLimits.MaxLimit)
                (errors ??= []).Add(new FieldError("limit", "Search.Limit", $"The limit must be between 1 and {SearchLimits.MaxLimit}."));
            else
                query = query with { Limit = n };
        }
        return query;
    }

    private static bool Flag(IQueryCollection q, string name, ref List<FieldError>? errors)
    {
        var value = q[name].ToString();
        if (value.Length == 0)
            return false;
        if (bool.TryParse(value, out var flag))
            return flag;
        (errors ??= []).Add(new FieldError(name, "Search.Flag", "true or false"));
        return false;
    }

    /// <summary>The first language of <c>Accept-Language</c> as an ISO 639-1 code ("de-CH" → "de").</summary>
    private static string? LanguageOf(StringValues header)
    {
        var first = header.ToString().Split(',', StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(first) || first == "*")
            return null;
        var code = first.Split(';')[0].Split('-')[0].Trim();
        return code.Length == 2 && code.All(char.IsAsciiLetter) ? code.ToLowerInvariant() : null;
    }

    /// <summary>The caller's audience keys: its subject and its roles.</summary>
    private static HashSet<string> AudienceOf(ClaimsPrincipal principal, string subjectClaim)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (principal.FindFirst(subjectClaim)?.Value is { Length: > 0 } sub)
            keys.Add(sub);
        foreach (var identity in principal.Identities)
            foreach (var role in identity.FindAll(identity.RoleClaimType))
                keys.Add(role.Value);
        return keys;
    }

    private static string ETag(SearchPage page)
    {
        var sb = new StringBuilder();
        sb.Append(page.Version).Append('|').Append(Positions.Format(page.Position)).Append('|').Append(page.Next);
        foreach (var hit in page.Items)
            sb.Append('|').Append(hit.Type).Append(':').Append(hit.Id.ToString("N"));
        if (page.Counts is { } counts)
            foreach (var (type, count) in counts.OrderBy(static c => c.Key, StringComparer.Ordinal))
                sb.Append('|').Append(type).Append('=').Append(count);
        return "W/\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())).AsSpan(0, 12)) + "\"";
    }

    private static void Write(Utf8JsonWriter writer, SearchPage page)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("items");
        foreach (var hit in page.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("type", hit.Type);
            writer.WriteString("id", hit.Id);
            writer.WriteString("aggregate", hit.Aggregate);
            writer.WriteString("title", hit.Title);
            writer.WriteNumber("score", Math.Round(hit.Score, 6));
            if (hit.Snippet is { } snippet)
            {
                writer.WriteString("snippet", snippet);                       // plain text; the matched words are offsets (BC-8)
                writer.WriteStartArray("highlights");
                foreach (var highlight in hit.Highlights)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("start", highlight.Start);
                    writer.WriteNumber("length", highlight.Length);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (page.Counts is { } counts)
        {
            writer.WriteStartObject("counts");
            foreach (var (type, count) in counts.OrderBy(static c => c.Key, StringComparer.Ordinal))
                writer.WriteNumber(type, count);
            writer.WriteEndObject();
            writer.WriteBoolean("countsCapped", page.CountsCapped);
        }
        if (page.Next is { } next)
            writer.WriteString("next", next);
        else
            writer.WriteNull("next");
        writer.WriteEndObject();
    }
}
