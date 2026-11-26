using Tarol.Primitives;
using Tarol.Queries;

namespace Tarol.Audit;

/// <summary>
/// Which aggregates the change log covers (TN-64, AU-2: every event of each, every member unless <c>[NotAudited]</c>):
/// <c>AddAudit(a => a.Add&lt;Child&gt;().Add&lt;Guardian&gt;())</c>. The aggregates must be registered with the event codec.
/// </summary>
public sealed class AuditRegistry
{
    private readonly HashSet<string> _streamTypes = new(StringComparer.Ordinal);

    /// <summary>Audits every event of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <returns>This registry.</returns>
    public AuditRegistry Add<T>()
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        _streamTypes.Add(T.StreamType);
        return this;
    }

    /// <summary>The audited stream types.</summary>
    public IReadOnlyCollection<string> StreamTypes => _streamTypes;

    /// <summary>
    /// The projection version (default 1): raise it to rebuild the log from the feed, e.g. after marking a member <c>[NotAudited]</c>.
    /// Reads and denials are kept across versions.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>How many days rows are kept (AU-4 (b): the log's own retention, configuration <c>Tarol:Audit:RetentionDays</c>); <see langword="null"/> = forever.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>The host's batch size and poll interval.</summary>
    public ProjectionOptions Projection { get; } = new();
}
