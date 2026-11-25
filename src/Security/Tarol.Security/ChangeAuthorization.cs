using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Primitives;
using Tarol.Results;

namespace Tarol.Security;

/// <summary>
/// <see cref="IChangeAuthorization"/> over ASP.NET Core's <see cref="IAuthorizationService"/>: the change is the
/// <see cref="ChangeRequirement"/>, the aggregate the resource. Allowed when a handler (the role map, a row rule) succeeded
/// and none failed; otherwise <c>Error.Unauthorized("Auth.Required")</c> for an anonymous caller, else
/// <c>Error.Forbidden</c> with the first denial's code (<see cref="ChangeHandler{TChange, TAggregate}.Deny"/>), or
/// <c>"Auth.Forbidden"</c> when nothing granted it. Every denial is logged (event 1001, a warning) and counted
/// (<c>tarol.authorization.denied</c> on the meter <see cref="MeterName"/>); neither carries personal data.
/// </summary>
/// <remarks>The pipeline asks only for runs that carry a principal (jobs and reactions are not checked). It is a singleton
/// over the root <see cref="IAuthorizationService"/>: register handlers as singletons.</remarks>
public sealed partial class ChangeAuthorization : IChangeAuthorization
{
    /// <summary>The meter of the denial counter.</summary>
    public const string MeterName = "Tarol.Security";

    /// <summary>The code of a refusal for an anonymous caller.</summary>
    public const string RequiredCode = "Auth.Required";

    /// <summary>The code of a refusal no rule explained (nothing granted the change).</summary>
    public const string ForbiddenCode = "Auth.Forbidden";

    private static readonly Meter SharedMeter = new(MeterName);
    private readonly Counter<long> _denied;

    private readonly IAuthorizationService _authorization;
    private readonly ILogger _log;
    private readonly ChangeRoles? _roles;
    private readonly IAuthorizationHandler[] _handlers = [];
    private readonly AccessObservers? _observers;

    /// <summary>Creates the authorization.</summary>
    /// <param name="authorization">ASP.NET Core's authorization service (<c>AddAuthorizationCore</c>).</param>
    /// <param name="logger">The denial log.</param>
    /// <param name="meters">Creates the meter <see cref="MeterName"/> (the host's <see cref="IMeterFactory"/>, step 15); <see langword="null"/> = a shared meter.</param>
    public ChangeAuthorization(IAuthorizationService authorization, ILogger<ChangeAuthorization>? logger = null, IMeterFactory? meters = null)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _authorization = authorization;
        _log = (ILogger?)logger ?? NullLogger.Instance;
        _denied = (meters?.Create(MeterName) ?? SharedMeter).CreateCounter<long>("tarol.authorization.denied", "{request}", "Changes and reads refused by authorization.");
    }

    /// <summary>Creates the authorization that also answers <see cref="MayBeGranted"/> from the role map and the row rules (VA-3).</summary>
    /// <param name="authorization">ASP.NET Core's authorization service (<c>AddAuthorizationCore</c>).</param>
    /// <param name="roles">The role map.</param>
    /// <param name="handlers">The registered authorization handlers (the <see cref="ChangeHandler{TChange, TAggregate}"/> row rules among them).</param>
    /// <param name="observers">The access observers (TN-59): every denial, and every granted <c>Sensitive</c> read.</param>
    /// <param name="logger">The denial log.</param>
    /// <param name="meters">Creates the meter <see cref="MeterName"/>; <see langword="null"/> = a shared meter.</param>
    public ChangeAuthorization(IAuthorizationService authorization, ChangeRoles roles, IEnumerable<IAuthorizationHandler> handlers,
        AccessObservers observers, ILogger<ChangeAuthorization>? logger = null, IMeterFactory? meters = null)
        : this(authorization, logger, meters)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(observers);
        _roles = roles;
        _handlers = [.. handlers];
        _observers = observers.Any ? observers : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A role of the map, or a <see cref="ChangeHandler{TChange, TAggregate}"/> / <see cref="ViewHandler{TView, TAggregate}"/> for the
    /// intent (a rule that only denies counts too: the check cannot see inside it). Lists are granted by the role map only (row
    /// rules do not run on lists), so a rule does not cover a list. <see langword="null"/> when built without the role map.
    /// </remarks>
    public bool? MayBeGranted(Type intent, ReadScope? scope)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (_roles is null)
            return null;
        if (_roles.GrantsAnyone(intent, scope))
            return true;
        foreach (var handler in _handlers)
            if (handler is IChangeRule rule && rule.Covers(intent, scope))
                return true;
        return false;
    }

    /// <inheritdoc />
    public ValueTask<Result> Check<T>(object change, T aggregate, ChangeContext context, ClaimsPrincipal principal, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Check(change, aggregate, context, principal, null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result> Check<T>(object change, T aggregate, ChangeContext context, ClaimsPrincipal principal, ReadScope scope, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Check(change, aggregate, context, principal, (ReadScope?)scope, cancellationToken);

    private async ValueTask<Result> Check<T>(object change, T aggregate, ChangeContext context, ClaimsPrincipal principal, ReadScope? scope, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _authorization.AuthorizeAsync(principal, aggregate, new ChangeRequirement(change, context) { View = scope }).ConfigureAwait(false);
        if (result.Succeeded)
        {
            if (_observers is not null && scope is { Sensitive: true } sensitive)
                await _observers.Report(Record(context, Name(change), typeof(T).Name, aggregate.Id.Value, sensitive, AccessDecision.Allowed, null), cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        var reason = result.Failure?.FailureReasons.FirstOrDefault()?.Message;
        var anonymous = principal.Identity?.IsAuthenticated != true;
        var code = anonymous ? RequiredCode : reason ?? ForbiddenCode;
        var name = Name(change);
        var actor = context.Operation.Actor;
        _denied.Add(1, new KeyValuePair<string, object?>("change", name), new KeyValuePair<string, object?>("reason", code),
            new KeyValuePair<string, object?>("actor.kind", actor.Kind.ToString()));
        Refused(_log, name, typeof(T).Name, aggregate.Id.Value, actor.Kind, actor.Id, code);
        if (_observers is not null)
            await _observers.Report(Record(context, name, typeof(T).Name, aggregate.Id.Value, scope, AccessDecision.Denied, code), cancellationToken).ConfigureAwait(false);
        return anonymous ? Error.Unauthorized(RequiredCode) : Error.Forbidden(code);
    }

    /// <inheritdoc />
    public ValueTask<Result> CheckList<T>(ChangeContext context, ClaimsPrincipal principal, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => CheckList<T>(context, principal, null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result> CheckList<T>(ChangeContext context, ClaimsPrincipal principal, ReadScope scope, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => CheckList<T>(context, principal, (ReadScope?)scope, cancellationToken);

    private async ValueTask<Result> CheckList<T>(ChangeContext context, ClaimsPrincipal principal, ReadScope? scope, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();
        // no resource: resource handlers (row rules, ChangeHandler<TChange, TAggregate>) never run; the role map may grant Read<T>
        var result = await _authorization.AuthorizeAsync(principal, null, new ChangeRequirement(Read<T>.Instance, context) { View = scope }).ConfigureAwait(false);
        if (result.Succeeded)
        {
            if (_observers is not null && scope is { Sensitive: true } sensitive)
                await _observers.Report(Record(context, "List<" + typeof(T).Name + ">", typeof(T).Name, null, sensitive, AccessDecision.Allowed, null), cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        var anonymous = principal.Identity?.IsAuthenticated != true;
        var code = anonymous ? RequiredCode : result.Failure?.FailureReasons.FirstOrDefault()?.Message ?? ForbiddenCode;
        var actor = context.Operation.Actor;
        _denied.Add(1, new KeyValuePair<string, object?>("change", "List<" + typeof(T).Name + ">"), new KeyValuePair<string, object?>("reason", code),
            new KeyValuePair<string, object?>("actor.kind", actor.Kind.ToString()));
        ListRefused(_log, typeof(T).Name, actor.Kind, actor.Id, code);
        if (_observers is not null)
            await _observers.Report(Record(context, "List<" + typeof(T).Name + ">", typeof(T).Name, null, scope, AccessDecision.Denied, code), cancellationToken).ConfigureAwait(false);
        return anonymous ? Error.Unauthorized(RequiredCode) : Error.Forbidden(code);
    }

    private static AccessRecord Record(ChangeContext context, string intent, string aggregate, Guid? id, ReadScope? scope, AccessDecision decision, string? code)
        => new(context.Operation.Actor, context.Operation.Tenant, intent, aggregate, id, scope?.View.Name, decision, code, context.Now);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Refused a list of {Aggregate} for {ActorKind} {ActorId}: {Reason}")]
    private static partial void ListRefused(ILogger logger, string aggregate, ActorKind actorKind, string actorId, string reason);

    private static string Name(object change) => change.GetType() is { IsGenericType: true } generic && generic.GetGenericTypeDefinition() == typeof(Read<>)
        ? change.ToString()!
        : change.GetType().Name;

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Refused {Change} on {Aggregate} {Id} for {ActorKind} {ActorId}: {Reason}")]
    private static partial void Refused(ILogger logger, string change, string aggregate, Guid id, ActorKind actorKind, string actorId, string reason);
}
