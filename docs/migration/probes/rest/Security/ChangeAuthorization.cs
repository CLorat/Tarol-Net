using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Primitives;
using Tarol.Results;

namespace Tarol.Security;

/// <summary>The requirement of a change: the intent itself (security.md §2: action = the change type). Candidate AZ-B.</summary>
public sealed class ChangeRequirement(object change) : IAuthorizationRequirement
{
    public object Change { get; } = change;
}

/// <summary>A resource handler for one change on its aggregate: sees the input and the loaded (or new) aggregate.</summary>
public abstract class ChangeHandler<TChange, TAggregate> : AuthorizationHandler<ChangeRequirement, TAggregate>
    where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
{
    protected sealed override Task HandleRequirementAsync(AuthorizationHandlerContext context, ChangeRequirement requirement, TAggregate resource)
        => requirement.Change is TChange change ? Handle(context, requirement, change, resource) : Task.CompletedTask;

    protected abstract Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, TChange change, TAggregate aggregate);

    /// <summary>Deny with a code the problem details carry (instead of the generic "Auth.Forbidden").</summary>
    protected void Deny(AuthorizationHandlerContext context, string code) => context.Fail(new AuthorizationFailureReason(this, code));
}

/// <summary>IChangeAuthorization via IAuthorizationService. No principal = an internal entry (job, reaction, saga): the intent
/// was authorized where it entered (security.md insight 4), nothing is checked. A principal (HTTP always passes one) = checked,
/// fail closed: no handler that succeeds = denied.</summary>
public sealed partial class ChangeAuthorization(IAuthorizationService authorization, ILogger<ChangeAuthorization> log) : IChangeAuthorization
{
    public const string MeterName = "Tarol.Security";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Denied = Meter.CreateCounter<long>("tarol.authorization.denied", description: "Changes and reads refused by authorization.");

    public async ValueTask<Result> Check<T>(object change, T aggregate, OperationContext context, ClaimsPrincipal? principal, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        if (principal is null)
            return Result.Success();
        var result = await authorization.AuthorizeAsync(principal, aggregate, new ChangeRequirement(change));
        if (result.Succeeded)
            return Result.Success();
        var name = change.GetType().Name;
        var reason = result.Failure?.FailureReasons.FirstOrDefault()?.Message;
        var anonymous = principal.Identity?.IsAuthenticated != true;
        Denied.Add(1, new("change", name), new("reason", reason ?? "none"), new("actor.kind", context.Actor.Kind.ToString()));
        Refused(log, name, typeof(T).Name, aggregate.Id.Value, context.Actor.Kind, context.Actor.Id, reason ?? "no handler succeeded");
        return anonymous ? Error.Unauthorized("Auth.Required") : Error.Forbidden(reason ?? "Auth.Forbidden");
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Refused {Change} on {Aggregate} {Id} for {ActorKind} {ActorId}: {Reason}")]
    private static partial void Refused(ILogger logger, string change, string aggregate, Guid id, ActorKind actorKind, string actorId, string reason);
}

public static class SecurityServiceCollectionExtensions
{
    /// <summary>ASP.NET Core authorization (core) plus the change authorization. Handlers are registered by the app.</summary>
    public static IServiceCollection AddTarolSecurity(this IServiceCollection services)
    {
        services.AddAuthorizationCore();
        services.AddSingleton<IChangeAuthorization, ChangeAuthorization>();
        return services;
    }
}
