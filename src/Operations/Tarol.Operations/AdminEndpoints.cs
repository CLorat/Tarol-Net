using System.Globalization;
using System.Security.Claims;
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
    /// <item><term>GET <c>dead/{kind}?after=&amp;limit=&amp;tenant=&amp;name=</c></term><description>one page and <c>next</c></description></item>
    /// <item><term>POST <c>dead/{kind}/{id}/redrive</c></term><description>204, 404</description></item>
    /// <item><term>POST <c>dead/{kind}/redrive?tenant=&amp;name=&amp;max=</c></term><description><c>{ redriven }</c>, at most 1,000</description></item>
    /// <item><term>DELETE <c>dead/{kind}/{id}</c></term><description>204, 404</description></item>
    /// <item><term>GET <c>projections</c></term><description>the versions</description></item>
    /// <item><term>DELETE <c>projections/{name}/{version}</c></term><description>204; 409 when it is the newest, in use, or still read</description></item>
    /// </list>
    /// The actor of every act is the principal's <c>Identity.Name</c> (else its <c>sub</c> claim).
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
        group.MapGet("/dead/{kind}", List);
        group.MapPost("/dead/{kind}/{id:long}/redrive", RedriveOne);
        group.MapPost("/dead/{kind}/redrive", RedriveMany);
        group.MapDelete("/dead/{kind}/{id:long}", Discard);
        group.MapGet("/projections", Projections);
        group.MapDelete("/projections/{name}/{version:int}", Drop);
        // step 16C (DP-6): a data subject's export and erasure, when data protection is registered
        if (endpoints.ServiceProvider.GetService<IDataSubjects>() is not null)
        {
            group.MapGet("/subjects/{id:guid}/export", ExportSubject);
            group.MapPost("/subjects/{id:guid}/erase", EraseSubject);
        }
        return group;
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

    private static Task Problem(HttpContext context, int status, string error) => Json(context, status, new AdminError(error), OperationsJson.Default.AdminError);

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
            await Problem(context, StatusCodes.Status400BadRequest, error!).ConfigureAwait(false);
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
            await Problem(context, StatusCodes.Status404NotFound, e.Message).ConfigureAwait(false);
        }
    }

    private static async Task RedriveOne(HttpContext context)
    {
        try
        {
            var done = await Admin(context).Redrive(Route(context, "kind"), long.Parse(Route(context, "id"), CultureInfo.InvariantCulture), Actor(context.User),
                context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = done ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
        }
        catch (KeyNotFoundException e)
        {
            await Problem(context, StatusCodes.Status404NotFound, e.Message).ConfigureAwait(false);
        }
    }

    private static async Task RedriveMany(HttpContext context)
    {
        if (Query(context, out var error) is not { } filter)
        {
            await Problem(context, StatusCodes.Status400BadRequest, error!).ConfigureAwait(false);
            return;
        }
        var max = IDeadLetterAdmin.MaxRedrive;
        if (context.Request.Query["max"] is [{ } text] && (!int.TryParse(text, CultureInfo.InvariantCulture, out max) || max < 1 || max > IDeadLetterAdmin.MaxRedrive))
        {
            await Problem(context, StatusCodes.Status400BadRequest, $"max must be 1 … {IDeadLetterAdmin.MaxRedrive}").ConfigureAwait(false);
            return;
        }
        try
        {
            var done = await Admin(context).Redrive(Route(context, "kind"), filter, max, Actor(context.User), context.RequestAborted).ConfigureAwait(false);
            await Json(context, StatusCodes.Status200OK, new RedriveView(done), OperationsJson.Default.RedriveView).ConfigureAwait(false);
        }
        catch (KeyNotFoundException e)
        {
            await Problem(context, StatusCodes.Status404NotFound, e.Message).ConfigureAwait(false);
        }
    }

    private static async Task Discard(HttpContext context)
    {
        try
        {
            var done = await Admin(context).Discard(Route(context, "kind"), long.Parse(Route(context, "id"), CultureInfo.InvariantCulture), Actor(context.User),
                context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = done ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
        }
        catch (KeyNotFoundException e)
        {
            await Problem(context, StatusCodes.Status404NotFound, e.Message).ConfigureAwait(false);
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
            await Problem(context, StatusCodes.Status400BadRequest, SubjectTenantRequired).ConfigureAwait(false);
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
            await Problem(context, StatusCodes.Status400BadRequest, SubjectTenantRequired).ConfigureAwait(false);
            return;
        }
        var subject = Guid.Parse(Route(context, "id"), CultureInfo.InvariantCulture);
        var erasure = await context.RequestServices.GetRequiredService<IDataSubjects>().EraseSubject(tenant, subject, Actor(context.User), context.RequestAborted).ConfigureAwait(false);
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
            await Problem(context, StatusCodes.Status409Conflict, e.Message).ConfigureAwait(false);
        }
    }
}
