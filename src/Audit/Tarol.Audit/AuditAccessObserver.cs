using System.Diagnostics.Metrics;
using Tarol.Kernel;
using Tarol.Security;

namespace Tarol.Audit;

/// <summary>
/// Writes sensitive reads and denials into the change log (TN-64 AU-5, TN-59 VA-4): one list shows changes, reads and refusals. A
/// refusal of an anonymous caller is only counted (<c>tarol.audit.anonymous_denials</c>): probes would fill the log.
/// </summary>
internal sealed class AuditAccessObserver : IAccessObserver
{
    private static readonly Meter SharedMeter = new(AuditHost.MeterName);
    private readonly IAuditStore _store;
    private readonly Counter<long> _anonymous;

    public AuditAccessObserver(IAuditStore store, IMeterFactory? meters)
    {
        _store = store;
        _anonymous = (meters?.Create(AuditHost.MeterName) ?? SharedMeter).CreateCounter<long>("tarol.audit.anonymous_denials", "{request}", "Refusals of anonymous callers (not written to the log).");
    }

    public async ValueTask Observed(AccessRecord access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.Actor.Kind == ActorKind.Anonymous)
        {
            if (access.Decision == AccessDecision.Denied)
                _anonymous.Add(1);
            return;
        }
        var tenant = access.Tenant.TryGetValue(out Tarol.StronglyTypedId.Id<ITenant> t) ? t.Value : Guid.Empty;
        var entry = new AuditEntry(Guid.CreateVersion7(access.At), tenant, access.At, access.Decision == AccessDecision.Allowed ? AuditKind.Read : AuditKind.Denied,
            access.Actor, access.Aggregate, access.Id ?? Guid.Empty, null, null, null)
        {
            Stream = access.Id ?? Guid.Empty,
            Event = access.Intent,
            View = access.View,
            Code = access.Code,
        };
        await _store.Record(entry, cancellationToken).ConfigureAwait(false);
    }
}
