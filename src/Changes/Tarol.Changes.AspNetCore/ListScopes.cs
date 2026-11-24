using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Kernel;
using Tarol.Queries;

namespace Tarol.Changes.AspNetCore;

/// <summary>
/// The server-side scope of a list query (TN-6): which rows the caller may see, as filters added to every request of
/// <typeparamref name="TQuery"/> (an <c>[Endpoint]</c> <c>IQuery&lt;TRow&gt;</c> record). Row rules do not run on lists and
/// <c>[FromClaim]</c> binds one claim value; a scope answers from domain data, e.g. "the children this parent may see"
/// (<see cref="ListScope.In{T}"/> over a column, an array column or the row id). Register it in the services
/// (<c>services.AddScoped&lt;IListScope&lt;DailyLogs&gt;, ChildScope&gt;()</c>); with <c>[Endpoint(…, RequiresScope = true)]</c> a
/// missing registration fails at startup. The scope's filters bind the cursor and the page cache key like the request's.
/// </summary>
/// <typeparam name="TQuery">The list query record.</typeparam>
public interface IListScope<TQuery>
    where TQuery : class
{
    /// <summary>The caller's scope, after the list's access check (<c>CheckList</c>) passed.</summary>
    /// <param name="context">The caller's operation context (tenant, actor) and the time.</param>
    /// <param name="principal">The caller.</param>
    /// <param name="cancellationToken">The request's abort.</param>
    /// <returns><see cref="ListScope.All"/>, <see cref="ListScope.None"/> (an empty page) or the filters.</returns>
    ValueTask<ListScope> Scope(ChangeContext context, ClaimsPrincipal principal, CancellationToken cancellationToken);
}

public sealed partial class ChangeEndpoints
{
    private static readonly object ListCallerKey = new();

    /// <summary>
    /// Whether a list query has a registered <see cref="IListScope{TQuery}"/> (the generated <c>MapChanges</c> asks once, for the
    /// endpoint metadata and the OpenAPI document's <c>x-tarol-scoped</c>).
    /// </summary>
    /// <typeparam name="TQuery">The list query record.</typeparam>
    /// <param name="services">The application's services.</param>
    /// <param name="required">Whether the entry declares <c>RequiresScope = true</c>.</param>
    /// <returns>Whether a scope is registered.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="required"/> and no scope is registered (fail closed at startup).</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool HasScope<TQuery>(IServiceProvider services, bool required)
        where TQuery : class
    {
        ArgumentNullException.ThrowIfNull(services);
        var registered = services.GetService<IServiceProviderIsService>()?.IsService(typeof(IListScope<TQuery>))
                         ?? services.GetService<IListScope<TQuery>>() is not null;
        if (required && !registered)
            throw new InvalidOperationException(
                $"The list {typeof(TQuery).Name} declares RequiresScope = true, but no IListScope<{typeof(TQuery).Name}> is registered; add services.AddScoped<IListScope<{typeof(TQuery).Name}>, …>().");
        return registered;
    }

    /// <summary>
    /// Restricts a list query by the caller's <see cref="IListScope{TQuery}"/>, if one is registered (<paramref name="required"/>:
    /// it must be). Called after <see cref="ListAccess{TAggregate}"/> and the request's own filters.
    /// </summary>
    /// <typeparam name="TQuery">The list query record.</typeparam>
    /// <param name="http">The request.</param>
    /// <param name="query">The query with the request's filters, sort and paging.</param>
    /// <param name="required">Whether the entry declares <c>RequiresScope = true</c>.</param>
    /// <returns>The query within the caller's scope.</returns>
    /// <exception cref="InvalidOperationException">The scope is required but missing, or answered <see langword="null"/>.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async ValueTask<ListQuery> Scope<TQuery>(HttpContext http, ListQuery query, bool required = false)
        where TQuery : class
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(query);
        var scope = required ? http.RequestServices.GetRequiredService<IListScope<TQuery>>() : http.RequestServices.GetService<IListScope<TQuery>>();
        if (scope is null)
            return query;
        if (http.Items[ListCallerKey] is not (OperationContext context, ClaimsPrincipal principal))
        {
            OperationContext edge;
            (edge, var options, _) = Edge(http);
            (context, principal) = (edge, options.Principal ?? NoOne);
        }
        var answer = await scope.Scope(new ChangeContext(context, _time.GetUtcNow()), principal, http.RequestAborted).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"The IListScope<{typeof(TQuery).Name}> answered null; answer ListScope.All or ListScope.None.");
        return query.Within(answer);
    }
}
