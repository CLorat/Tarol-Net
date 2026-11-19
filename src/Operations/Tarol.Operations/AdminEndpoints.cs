using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace Tarol.Operations;

/// <summary>The admin endpoints over <see cref="IDeadLetterAdmin"/> (O-3), behind a required authorization policy.</summary>
public static class TarolAdminEndpoints
{
    /// <summary>
    /// Maps the admin entry under <paramref name="prefix"/>, every endpoint behind <paramref name="policy"/> (fail closed: no
    /// policy, no endpoints):
    /// <list type="table">
    /// <item><term>GET <c>dead</c></term><description>the dead-letter kinds</description></item>
    /// <item><term>GET <c>dead/{kind}?after=&amp;limit=&amp;tenant=&amp;name=</c></term><description>one page and <c>next</c></description></item>
    /// <item><term>POST <c>dead/{kind}/{id}/redrive</c></term><description>204, 404</description></item>
    /// <item><term>POST <c>dead/{kind}/redrive?tenant=&amp;name=&amp;max=</c></term><description><c>{ redriven }</c>, at most 1,000</description></item>
    /// <item><term>DELETE <c>dead/{kind}/{id}</c></term><description>204, 404</description></item>
    /// <item><term>GET <c>projections</c></term><description>the versions</description></item>
    /// <item><term>DELETE <c>projections/{name}/{version}</c></term><description>204; 409 when it is the newest, in use, or still read</description></item>
    /// </list>
    /// The actor of every act is the principal's <c>Identity.Name</c> (else its <c>sub</c> claim). Every error is an RFC 9457
    /// <c>application/problem+json</c> with a <c>code</c> (<see cref="AdminProblems"/>), like the change endpoints.
    /// </summary>
    /// <param name="endpoints">The routes.</param>
    /// <param name="prefix">The path prefix, e.g. <c>/tarol/admin</c>.</param>
    /// <param name="policy">The authorization policy every endpoint requires (e.g. a role <c>tarol-admin</c>).</param>
    /// <returns>The group, for more conventions.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="policy"/> is empty: the endpoints would be open.</exception>
    public static RouteGroupBuilder MapTarolAdmin(this IEndpointRouteBuilder endpoints, string prefix, string policy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (string.IsNullOrWhiteSpace(policy))
            throw new InvalidOperationException("MapTarolAdmin needs an authorization policy (e.g. \"tarol-admin\"): without one the admin entry would be open.");
        var group = endpoints.MapGroup(prefix.TrimEnd('/'));
        group.RequireAuthorization(policy);
        group.WithTags(OpenApiTag);
        // BC-3: each endpoint carries its handler's MethodInfo (OpenAPI describes endpoints that carry one), its answers and an
        // operation id; the generated transformers of Tarol.Changes.AspNetCore add the parameters and codes by that id
        Describe(group.MapGet("/dead", Kinds), "TarolAdminKinds", Kinds, typeof(IReadOnlyList<string>));
        Describe(group.MapGet("/dead/{kind}", List), "TarolAdminDeadLetters", List, typeof(DeadLetterPageView));
        Describe(group.MapPost("/dead/{kind}/{id:long}/redrive", RedriveOne), "TarolAdminRedrive", RedriveOne, null);
        Describe(group.MapPost("/dead/{kind}/redrive", RedriveMany), "TarolAdminRedriveMany", RedriveMany, typeof(RedriveView));
        Describe(group.MapDelete("/dead/{kind}/{id:long}", Discard), "TarolAdminDiscard", Discard, null);
        Describe(group.MapGet("/projections", Projections), "TarolAdminProjections", Projections, typeof(IReadOnlyList<ProjectionVersion>));
        Describe(group.MapDelete("/projections/{name}/{version:int}", Drop), "TarolAdminDropProjection", Drop, null);
        // step 16C (DP-6): a data subject's export and erasure, when data protection is registered
        if (endpoints.ServiceProvider.GetService<IDataSubjects>() is not null)
        {
            Describe(group.MapGet("/subjects/{id:guid}/export", ExportSubject), "TarolAdminExportSubject", ExportSubject, null);
            Describe(group.MapPost("/subjects/{id:guid}/erase", EraseSubject), "TarolAdminEraseSubject", EraseSubject, typeof(ErasureView));
        }
        return group;
    }

    /// <summary>The OpenAPI tag of the admin endpoints; the generated OpenAPI transformers of <c>Tarol.Changes.AspNetCore</c>
    /// give its operations <c>x-tarol-kind: admin</c>, the <c>TarolProblem</c> schema and their codes (BC-3).</summary>
    public const string OpenApiTag = "Tarol.Admin";

    private static void Describe(IEndpointConventionBuilder endpoint, string name, RequestDelegate handler, Type? answer)
    {
        endpoint.WithName(name);
        endpoint.WithMetadata(handler.Method);
        if (answer is not null)
            endpoint.WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status200OK, answer, ["application/json"]));
    }

    private static IDeadLetterAdmin Admin(HttpContext context) => context.RequestServices.GetRequiredService<IDeadLetterAdmin>();

    /// <summary>The actor: the principal's name, else its <c>sub</c> claim.</summary>
    internal static string Actor(ClaimsPrincipal user)
        => user.Identity?.Name is { Length: > 0 } name ? name : user.FindFirst("sub")?.Value ?? "unknown";

    private static string Route(HttpContext context, string key) => (string)context.Request.RouteValues[key]!;

    private static Task Json<T>(HttpContext context, int status, T value, JsonTypeInfo<T> info)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(value, info, cancellationToken: context.RequestAborted);
    }

    /// <summary>An RFC 9457 problem with a <c>code</c> (BC-10; the shape of the change endpoints' problems).</summary>
    private static async Task Problem(HttpContext context, int status, string code, string? detail, Action<Utf8JsonWriter>? extra = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var writer = new Utf8JsonWriter(context.Response.Body);
        await using (writer.ConfigureAwait(false))
        {
            writer.WriteStartObject();
            writer.WriteString("type", $"https://tools.ietf.org/html/rfc9110#section-15.{(status >= 500 ? 6 : 5)}.{(status >= 500 ? status - 499 : status - 399)}");
            writer.WriteNumber("status", status);
            writer.WriteString("title", code);
            writer.WriteString("code", code);
            if (detail is not null)
                writer.WriteString("detail", detail);
            extra?.Invoke(writer);
            writer.WriteEndObject();
        }
    }

    private static Task NotFound(HttpContext context, string detail) => Problem(context, StatusCodes.Status404NotFound, AdminProblems.NotFound, detail);

    private static Task UnknownKind(HttpContext context, KeyNotFoundException e) => Problem(context, StatusCodes.Status404NotFound, AdminProblems.Kind, e.Message);

    private static Task Kinds(HttpContext context)
        => Json(context, StatusCodes.Status200OK, Admin(context).Kinds, OperationsJson.Default.IReadOnlyListString);

    /// <summary>Reads <c>after</c>, <c>limit</c>, <c>tenant</c>, <c>name</c>; <see langword="null"/> with a 400 written.</summary>
    private static DeadLetterQuery? Query(HttpContext context, out string? error)
    {
        error = null;
        var q = context.Request.Query;
        var query = new DeadLetterQuery();
        try
        {
            if (q["after"] is [{ } after])
                query = query with { After = long.Parse(after, CultureInfo.InvariantCulture) };
            if (q["limit"] is [{ } limit])
                query = query with { Limit = int.Parse(limit, CultureInfo.InvariantCulture) };
            if (q["tenant"] is [{ } tenant])
                query = query with { Tenant = (Id<ITenant>)Guid.Parse(tenant, CultureInfo.InvariantCulture) };
            if (q["name"] is [{ Length: > 0 } name])
                query = query with { Name = name };
            return query;
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            error = $"bad query: {e.Message}";
            return null;
        }
    }

    private static async Task List(HttpContext context)
    {
        if (Query(context, out var error) is not { } query)
        {
            await Problem(context, StatusCodes.Status400BadRequest, AdminProblems.Query, error).ConfigureAwait(false);
            return;
        }
        try
        {
            var page = await Admin(context).List(Route(context, "kind"), query, context.RequestAborted).ConfigureAwait(false);
            await Json(context, StatusCodes.Status200OK, new DeadLetterPageView([.. page.Items.Select(DeadLetterView.Of)], page.Next),
                OperationsJson.Default.DeadLetterPageView).ConfigureAwait(false);
        }
        catch (KeyNotFoundException e)
        {
            await UnknownKind(context, e).ConfigureAwait(false);
        }
    }

    private static async Task RedriveOne(HttpContext context)
    {
        try
        {
            var done = await Admin(context).Redrive(Route(context, "kind"), long.Parse(Route(context, "id"), CultureInfo.InvariantCulture), Actor(context.User),
                context.RequestAborted).ConfigureAwait(false);
            if (done)
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            else
                await NotFound(context, $"no dead letter {Route(context, "id")} of kind {Route(context, "kind")}").ConfigureAwait(false);
        }
        catch (KeyNotFoundException e)
        {
            await UnknownKind(context, e).ConfigureAwait(false);
        }
    }

    private static async Task RedriveMany(HttpContext context)
    {
        if (Query(context, out var error) is not { } filter)
        {
            await Problem(context, StatusCodes.Status400BadRequest, AdminProblems.Query, error).ConfigureAwait(false);
            return;
        }
        var max = IDeadLetterAdmin.MaxRedrive;
        if (context.Request.Query["max"] is [{ } text] && (!int.TryParse(text, CultureInfo.InvariantCulture, out max) || max < 1 || max > IDeadLetterAdmin.MaxRedrive))
        {
            await Problem(context, StatusCodes.Status400BadRequest, AdminProblems.Query, $"max must be 1 … {IDeadLetterAdmin.MaxRedrive}").ConfigureAwait(false);
            return;
        }
        try
        {
            var done = await Admin(context).Redrive(Route(context, "kind"), filter, max, Actor(context.User), context.RequestAborted).ConfigureAwait(false);
            await Json(context, StatusCodes.Status200OK, new RedriveView(done), OperationsJson.Default.RedriveView).ConfigureAwait(false);
        }
        catch (KeyNotFoundException e)
        {
            await UnknownKind(context, e).ConfigureAwait(false);
        }
    }

    private static async Task Discard(HttpContext context)
    {
        try
        {
            var done = await Admin(context).Discard(Route(context, "kind"), long.Parse(Route(context, "id"), CultureInfo.InvariantCulture), Actor(context.User),
                context.RequestAborted).ConfigureAwait(false);
            if (done)
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            else
                await NotFound(context, $"no dead letter {Route(context, "id")} of kind {Route(context, "kind")}").ConfigureAwait(false);
        }
        catch (KeyNotFoundException e)
        {
            await UnknownKind(context, e).ConfigureAwait(false);
        }
    }

    /// <summary>The <c>tenant</c> query parameter (a GUID; absent = no tenant); <see langword="null"/> when malformed.</summary>
    private static Guid? TenantOf(HttpContext context)
        => context.Request.Query["tenant"].ToString() is { Length: > 0 } text
            ? Guid.TryParse(text, CultureInfo.InvariantCulture, out var tenant) ? tenant : null
            : Guid.Empty;

    /// <summary>
    /// The <c>tenant</c> of a data-subject call: required, a GUID (<see cref="Guid.Empty"/> names tenant-less data). An absent
    /// tenant would act on the tenant-less data only: an erasure would answer 200 and leave the person's keys readable.
    /// </summary>
    private static Guid? SubjectTenantOf(HttpContext context)
        => context.Request.Query.ContainsKey("tenant") ? TenantOf(context) : null;

    private const string SubjectTenantRequired =
        "tenant is required and must be a GUID (00000000-0000-0000-0000-000000000000 for tenant-less data)";

    private static async Task ExportSubject(HttpContext context)
    {
        if (SubjectTenantOf(context) is not { } tenant)
        {
            await Problem(context, StatusCodes.Status400BadRequest, AdminProblems.Tenant, SubjectTenantRequired).ConfigureAwait(false);
            return;
        }
        var subject = Guid.Parse(Route(context, "id"), CultureInfo.InvariantCulture);
        var export = await context.RequestServices.GetRequiredService<IDataSubjects>().Export(tenant, subject, Actor(context.User), context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"subject-{subject}.json\"";
        await context.Response.Body.WriteAsync(export.Utf8Json, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task EraseSubject(HttpContext context)
    {
        if (SubjectTenantOf(context) is not { } tenant)
        {
            await Problem(context, StatusCodes.Status400BadRequest, AdminProblems.Tenant, SubjectTenantRequired).ConfigureAwait(false);
            return;
        }
        var subject = Guid.Parse(Route(context, "id"), CultureInfo.InvariantCulture);
        SubjectErasure erasure;
        try
        {
            erasure = await context.RequestServices.GetRequiredService<IDataSubjects>().EraseSubject(tenant, subject, Actor(context.User), context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IIncompleteErasure incomplete)
        {
            // the tombstone is written and the act audited; the failed participants forget on a re-run (idempotent)
            await Problem(context, StatusCodes.Status409Conflict, AdminProblems.ErasureIncomplete,
                "The key is erased, but some participants failed to forget; run the erasure again.", w =>
                {
                    w.WriteString("tenant", incomplete.Tenant);
                    w.WriteString("subject", subject);
                    w.WriteStartArray("failed");
                    foreach (var name in incomplete.Failed)
                        w.WriteStringValue(name);
                    w.WriteEndArray();
                }).ConfigureAwait(false);
            return;
        }
        await Json(context, StatusCodes.Status200OK, new ErasureView(erasure.Tenant, subject, erasure.AlreadyErased, erasure.Streams.Count), OperationsJson.Default.ErasureView).ConfigureAwait(false);
    }

    private static async Task Projections(HttpContext context)
        => await Json(context, StatusCodes.Status200OK, await Admin(context).Projections(context.RequestAborted).ConfigureAwait(false),
            OperationsJson.Default.IReadOnlyListProjectionVersion).ConfigureAwait(false);

    private static async Task Drop(HttpContext context)
    {
        try
        {
            await Admin(context).DropVersion(Route(context, "name"), int.Parse(Route(context, "version"), CultureInfo.InvariantCulture), Actor(context.User),
                context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or ArgumentException)
        {
            await Problem(context, StatusCodes.Status409Conflict, AdminProblems.ProjectionDrop, e.Message).ConfigureAwait(false);
        }
    }
}

/// <summary>The <c>code</c>s of the admin entry's problems (BC-10).</summary>
public static class AdminProblems
{
    /// <summary>400: a malformed query parameter (<c>after</c>, <c>limit</c>, <c>tenant</c>, <c>max</c>).</summary>
    public const string Query = "Admin.Query";

    /// <summary>400: a data-subject call without a valid <c>tenant</c>.</summary>
    public const string Tenant = "Admin.Tenant";

    /// <summary>404: an unknown dead-letter kind.</summary>
    public const string Kind = "Admin.Kind";

    /// <summary>404: no dead letter with that id.</summary>
    public const string NotFound = "Admin.NotFound";

    /// <summary>409: the projection version cannot be dropped (the newest, in use, or still read).</summary>
    public const string ProjectionDrop = "Projection.Drop";

    /// <summary>409: the erasure wrote the tombstone, but participants failed (<c>failed</c> lists them); run it again.</summary>
    public const string ErasureIncomplete = "DataProtection.ErasureIncomplete";
}
