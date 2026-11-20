using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.AspNetCore;

/// <summary>The headers of read-your-writes (QR-5) and the text form of a <see cref="GlobalPosition"/> in them.</summary>
public static class Positions
{
    /// <summary>The commit's position on a change answer, the projection's checkpoint on a list answer.</summary>
    public const string PositionHeader = "Tarol-Position";

    /// <summary>A list request waits until the projection passed this position (a change answer's <see cref="PositionHeader"/>).</summary>
    public const string ReadAfterHeader = "Tarol-Read-After";

    /// <summary><c>true</c> when the projection did not pass <see cref="ReadAfterHeader"/> within the timeout.</summary>
    public const string StaleHeader = "Tarol-Stale";

    /// <summary><c>&lt;transaction&gt;.&lt;position&gt;</c>, e.g. <c>7341.1000412</c>.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The text.</returns>
    public static string Format(GlobalPosition position)
        => position.Transaction.ToString(CultureInfo.InvariantCulture) + "." + position.Position.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses <see cref="Format"/>'s text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="position">The position.</param>
    /// <returns>Whether it was valid.</returns>
    public static bool TryParse(string? text, out GlobalPosition position)
    {
        position = default;
        if (string.IsNullOrEmpty(text) || text.Length > 41)
            return false;
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0
            || !ulong.TryParse(text.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var transaction)
            || !long.TryParse(text.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var at))
            return false;
        position = new GlobalPosition(transaction, at);
        return true;
    }
}

/// <summary>
/// The HTTP caching headers of list and view answers (CA-6): browsers may store an answer and must revalidate it
/// (<c>ETag</c>/<c>If-None-Match</c> → 304), shared caches must not store it, and a stored answer belongs to the
/// <c>Authorization</c> it was answered for. There is no output caching: the page cache of <c>Tarol.Caching</c> sits
/// behind <c>CheckList</c> and <c>Tarol-Read-After</c>.
/// </summary>
public static class CacheHeaders
{
    /// <summary>The <c>Cache-Control</c> value: <c>private, no-cache</c>.</summary>
    public const string CacheControl = "private, no-cache";

    /// <summary>The <c>Vary</c> value: <c>Authorization</c>.</summary>
    public const string Vary = "Authorization";

    /// <summary>Sets <see cref="CacheControl"/> and adds <see cref="Vary"/> to the response (kept when already there).</summary>
    /// <param name="http">The request.</param>
    public static void Apply(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var headers = http.Response.Headers;
        headers.CacheControl = CacheControl;
        foreach (var value in headers.Vary)
            if (value is not null && value.Split(',', StringSplitOptions.TrimEntries).Contains(Vary, StringComparer.OrdinalIgnoreCase))
                return;
        headers.Vary = StringValues.Concat(headers.Vary, Vary);
    }
}

public sealed partial class ChangeEndpoints
{
    /// <summary>The code of a 503 while the projection is building (a new version catching up, QR-3).</summary>
    public const string BuildingCode = "Projection.Building";

    /// <summary>
    /// The access of a list request: the edge (tenant from the claims), then <see cref="IChangeAuthorization.CheckList{T}"/>
    /// (the role map's <c>Read&lt;T&gt;</c>; row rules do not run on lists).
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate whose projection is listed.</typeparam>
    /// <param name="http">The request.</param>
    /// <returns>The tenant key of the list, or <see langword="null"/> when the refusal is written.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async ValueTask<Guid?> ListAccess<TAggregate>(HttpContext http)
        where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
    {
        ArgumentNullException.ThrowIfNull(http);
        CacheHeaders.Apply(http);                                        // every list answer: 200, 304 and the problems
        var (context, options, refused) = Edge(http);
        if (refused is not null)
        {
            await Problem(http, refused).ConfigureAwait(false);
            return null;
        }
        var allowed = await _authorization.CheckList<TAggregate>(new ChangeContext(context, _time.GetUtcNow()), options.Principal ?? NoOne, http.RequestAborted)
            .ConfigureAwait(false);
        if (allowed.TryGetValue(out Violation? denied))
        {
            await Problem(http, denied).ConfigureAwait(false);
            return null;
        }
        return context.Tenant.TryGetValue(out Id<ITenant> tenant) ? tenant.Value : Guid.Empty;
    }

    /// <summary>The value of a claim of the caller (a <c>[FromClaim]</c> member), or <see langword="null"/>.</summary>
    /// <param name="http">The request.</param>
    /// <param name="type">The claim type.</param>
    /// <returns>The value.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string? Claim(HttpContext http, string type)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.User?.FindFirst(type)?.Value;
    }

    /// <summary>The one value of a query-string key: <see langword="null"/> when absent; refused when given twice.</summary>
    /// <param name="http">The request.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns>Whether at most one value was given.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool One(HttpContext http, string key, out string? value)
    {
        ArgumentNullException.ThrowIfNull(http);
        var values = http.Request.Query[key];
        value = values.Count == 1 ? values[0] : null;
        return values.Count <= 1;
    }

    /// <summary>Parses a filter value from the query string (invariant culture; enums by name, case-insensitive).</summary>
    /// <typeparam name="T">The member's (non-nullable) type.</typeparam>
    /// <param name="text">The text.</param>
    /// <param name="value">The value.</param>
    /// <returns>Whether it parsed.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool TryFilter<T>(string text, out T value)
        where T : struct, ISpanParsable<T>
        => T.TryParse(text, CultureInfo.InvariantCulture, out value);

    /// <summary>Parses an enum filter value by name (case-insensitive; numbers are refused).</summary>
    /// <typeparam name="TEnum">The enum.</typeparam>
    /// <param name="text">The text.</param>
    /// <param name="value">The value.</param>
    /// <returns>Whether it named a member.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool TryEnum<TEnum>(string text, out TEnum value)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(text);
        value = default;
        return text.Length > 0 && !char.IsDigit(text[0]) && text[0] != '-' && Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    /// <summary>The field code of a query key a list does not know (BC-14).</summary>
    public const string UnknownKeyCode = "Unknown";

    /// <summary>
    /// Adds the paging keys of the query string to <paramref name="query"/>: <c>limit</c> (1 … <see cref="QueryEndpointOptions.MaxLimit"/>),
    /// <c>after</c> (the cursor), <c>offset</c> (0 … <see cref="QueryEndpointOptions.MaxOffset"/>, not with <c>after</c>) and <c>count</c>.
    /// A key that is none of the entry's query parameters (its <see cref="TarolEndpointMetadata.Query"/>: filters, <c>sort</c>
    /// and the paging keys) is a field error <c>Unknown</c> (BC-14): a typo would otherwise list unfiltered rows.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="query">The query with tenant, filters and sort.</param>
    /// <param name="errors">Collects the field errors.</param>
    /// <returns>The query with paging.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ListQuery Paging(HttpContext http, ListQuery query, ref List<FieldError>? errors)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(query);
        var limits = Options.Queries;
        var limit = limits.DefaultLimit;
        if (!One(http, "limit", out var limitText))
            (errors ??= []).Add(new FieldError("limit", "Multiple"));
        else if (limitText is not null && !(int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit >= 1 && limit <= limits.MaxLimit))
            (errors ??= []).Add(new FieldError("limit", "Range", $"between 1 and {limits.MaxLimit}"));
        int? offset = null;
        if (!One(http, "offset", out var offsetText))
            (errors ??= []).Add(new FieldError("offset", "Multiple"));
        else if (offsetText is not null)
        {
            if (int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var o) && o <= limits.MaxOffset)
                offset = o;
            else
                (errors ??= []).Add(new FieldError("offset", "Range", $"between 0 and {limits.MaxOffset}; page deeper with 'after'"));
        }
        if (!One(http, "after", out var after))
            (errors ??= []).Add(new FieldError("after", "Multiple"));
        if (after is not null && offset is not null)
            (errors ??= []).Add(new FieldError("offset", "WithCursor", "'offset' and 'after' exclude each other"));
        var count = false;
        if (!One(http, "count", out var countText) || (countText is not null && !bool.TryParse(countText, out count)))
            (errors ??= []).Add(new FieldError("count", "Format"));
        if (http.GetEndpoint()?.Metadata.GetMetadata<TarolEndpointMetadata>()?.Query is { } known)
            foreach (var key in http.Request.Query.Keys)
                if (!known.Any(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)))
                    (errors ??= []).Add(new FieldError(key, UnknownKeyCode, "not a filter, sort or paging key of this list"));
        return query with { Limit = limit, Offset = offset, After = after, Count = count };
    }

    /// <summary>
    /// Answers a list request (QR-4, QR-5): waits for <c>Tarol-Read-After</c> (then <c>Tarol-Stale: true</c>), 503 with
    /// <c>Retry-After</c> while the projection builds, the page as <c>{ "items", "next", "total" }</c> with its weak
    /// <c>ETag</c> (304 for a matching <c>If-None-Match</c>) and the projection's <c>Tarol-Position</c>; 400 for a bad cursor.
    /// </summary>
    /// <typeparam name="TRow">The projection's row record (in the application's JSON context).</typeparam>
    /// <param name="http">The request.</param>
    /// <param name="query">The query, tenant set from <see cref="ListAccess{TAggregate}"/>.</param>
    /// <returns>The write.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async Task List<TRow>(HttpContext http, ListQuery query)
        where TRow : class, IProjection<TRow>
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(query);
        var positions = http.RequestServices.GetRequiredService<IProjectionPositions>();
        var store = http.RequestServices.GetRequiredService<IReadModels>().Store<TRow>();
        var limits = Options.Queries;

        if (http.Request.Headers[Positions.ReadAfterHeader].ToString() is { Length: > 0 } readAfter)
        {
            if (!Positions.TryParse(readAfter, out var position))
            {
                await Problem(http, Invalid.Field(Positions.ReadAfterHeader, "Format")).ConfigureAwait(false);
                return;
            }
            if (!await positions.WaitFor<TRow>(position, limits.ReadAfterTimeout, http.RequestAborted).ConfigureAwait(false))
                http.Response.Headers[Positions.StaleHeader] = "true";
        }
        var checkpoint = await positions.Checkpoint<TRow>(http.RequestAborted).ConfigureAwait(false);
        if (checkpoint.State == ProjectionState.Building)
        {
            await Unavailable(http, Error.Unavailable(BuildingCode, $"The projection '{TRow.Name}' v{TRow.Version} is being built.")).ConfigureAwait(false);
            return;
        }
        var result = await store.Search(query, http.RequestAborted).ConfigureAwait(false);
        if (!result.TryGetValue(out Page<TRow>? page))
        {
            var failure = result.TryGetValue(out Violation? violation) ? violation : Error.Internal("Query.Failed");
            if (failure is Error { Type: ErrorType.Unavailable } unavailable)
                await Unavailable(http, unavailable).ConfigureAwait(false);
            else
                await Problem(http, failure).ConfigureAwait(false);
            return;
        }
        http.Response.Headers[Positions.PositionHeader] = Positions.Format(page.Position);
        http.Response.Headers.ETag = page.ETag;
        if (EntityTags.Matches(http.Request.Headers.IfNoneMatch, page.ETag))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        var item = Json.GetTypeInfo<TRow>();
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer))                 // in memory: serializing a row flushes, the response allows no sync writes
        {
            writer.WriteStartObject();
            writer.WriteStartArray("items");
            foreach (var row in page.Items)
                JsonSerializer.Serialize(writer, row, item);
            writer.WriteEndArray();
            if (page.Next is { } next)
                writer.WriteString("next", next);
            else
                writer.WriteNull("next");
            if (page.Total is { } total)
                writer.WriteNumber("total", total);
            writer.WriteEndObject();
        }
        http.Response.ContentLength = buffer.WrittenCount;
        await http.Response.Body.WriteAsync(buffer.WrittenMemory, http.RequestAborted).ConfigureAwait(false);
    }

    private async Task Unavailable(HttpContext http, Error error)
    {
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(Options.Queries.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        await Problem(http, error).ConfigureAwait(false);
    }
}
