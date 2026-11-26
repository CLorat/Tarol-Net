using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Security;

/// <summary>
/// Sees access decisions (TN-59, VA-4; Tatalu FB-F13.3-4, FB-F02.2-4): every denial of a change, view, list or row query, and
/// every granted read of a <c>Sensitive</c> view (a 304 included). It runs after the decision and never changes it; a failing
/// observer is logged (1004). Durable storage is the app's (or <c>Tarol.Audit</c>'s). Register it as a singleton:
/// <c>services.AddSingleton&lt;IAccessObserver, AccessLogWriter&gt;()</c>.
/// </summary>
public interface IAccessObserver
{
    /// <summary>One decision.</summary>
    /// <param name="access">What was decided: ids, codes and types, no personal data.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>A task.</returns>
    ValueTask Observed(AccessRecord access, CancellationToken cancellationToken);
}

/// <summary>The outcome of an access decision.</summary>
public enum AccessDecision
{
    /// <summary>Granted (reported for <c>Sensitive</c> reads only).</summary>
    Allowed,

    /// <summary>Refused (401 or 403).</summary>
    Denied,
}

/// <summary>One access decision for <see cref="IAccessObserver"/>.</summary>
/// <param name="Actor">Who asked.</param>
/// <param name="Tenant">The caller's tenant.</param>
/// <param name="Intent">The change type's name, <c>Read&lt;Child&gt;</c> or <c>List&lt;Child&gt;</c>.</param>
/// <param name="Aggregate">The aggregate type's name.</param>
/// <param name="Id">The aggregate's id; <see langword="null"/> for a list.</param>
/// <param name="View">The view, list or row query record's name of a read; <see langword="null"/> for a change.</param>
/// <param name="Decision">Allowed or denied.</param>
/// <param name="Code">The refusal's code (<c>Auth.Forbidden</c>, a rule's code); <see langword="null"/> when allowed.</param>
/// <param name="At">The decision's time (the run's <c>Now</c>).</param>
public sealed record AccessRecord(Actor Actor, Option<Id<ITenant>> Tenant, string Intent, string Aggregate, Guid? Id,
    string? View, AccessDecision Decision, string? Code, DateTimeOffset At);

/// <summary>Calls every <see cref="IAccessObserver"/> in order; a failure is logged (1004) and never changes the decision.</summary>
public sealed partial class AccessObservers
{
    private readonly IAccessObserver[] _observers;
    private readonly ILogger _log;

    /// <summary>Creates the fan-out.</summary>
    /// <param name="observers">The registered observers.</param>
    /// <param name="logger">The failure log.</param>
    public AccessObservers(IEnumerable<IAccessObserver> observers, ILogger<AccessObservers>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(observers);
        _observers = [.. observers];
        _log = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Whether any observer is registered.</summary>
    public bool Any => _observers.Length > 0;

    /// <summary>Reports <paramref name="access"/> to every observer.</summary>
    /// <param name="access">The decision.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>A task.</returns>
    public async ValueTask Report(AccessRecord access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        foreach (var observer in _observers)
        {
            try
            {
                await observer.Observed(access, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Failed(_log, e, observer.GetType().Name, access.Intent);
            }
        }
    }

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error, Message = "The access observer {Observer} failed for {Intent}; the decision stands")]
    private static partial void Failed(ILogger logger, Exception exception, string observer, string intent);
}
