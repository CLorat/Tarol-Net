using Tarol.Primitives;

namespace Tarol.Persistence;

/// <summary>A stored stream: its tenant (<see cref="Guid.Empty"/> without one), stream type and id.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="StreamType">The stream type.</param>
/// <param name="Id">The stream id.</param>
public readonly record struct StreamRef(Guid Tenant, string StreamType, Guid Id);

/// <summary>What an erasure did.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Subject">The subject; <see langword="null"/> for a whole tenant.</param>
/// <param name="AlreadyErased">Whether the key was a tombstone already (the forgetting ran again regardless).</param>
/// <param name="Streams">The streams that held the subject's data.</param>
public sealed record SubjectErasure(Guid Tenant, Guid? Subject, bool AlreadyErased, IReadOnlyList<StreamRef> Streams);

/// <summary>A subject's data: one UTF-8 JSON document (DP-6).</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Utf8Json">The document: every event holding the subject's data, opened with that subject's key only, and the state of its own streams.</param>
public sealed record SubjectExport(Guid Tenant, Guid Subject, ReadOnlyMemory<byte> Utf8Json);

/// <summary>
/// Erasure and export of a data subject (GDPR art. 15, 17, 20; DP-5, DP-6). <c>Tarol.DataProtection</c> implements it: an
/// erasure writes the tombstone, then every <see cref="ErasureParticipant"/> forgets (projections re-fold, page caches evict,
/// personal lookups are deleted), and the act is audited.
/// </summary>
public interface IDataSubjects
{
    /// <summary>Erases a subject everywhere Tarol wrote its data, and audits it.</summary>
    /// <param name="tenant">The tenant (<see cref="Guid.Empty"/> without one).</param>
    /// <param name="subject">The subject.</param>
    /// <param name="actor">Who erased it (the operator's principal), for the audit.</param>
    /// <param name="cancellationToken">Cancels the call (the tombstone, once written, stays).</param>
    /// <returns>What was erased.</returns>
    ValueTask<SubjectErasure> EraseSubject(Guid tenant, Guid subject, string actor, CancellationToken cancellationToken);

    /// <summary>Erases a whole tenant (offboarding): its tenant key is blanked, its projections and caches forget, it is audited.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="actor">Who erased it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was erased.</returns>
    ValueTask<SubjectErasure> EraseTenant(Guid tenant, string actor, CancellationToken cancellationToken);

    /// <summary>Exports a subject's data, and audits it.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="actor">Who exported it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document.</returns>
    ValueTask<SubjectExport> Export(Guid tenant, Guid subject, string actor, CancellationToken cancellationToken);
}

/// <summary>
/// Something that holds copies of personal data outside the event store and must forget them on an erasure (a projection, a
/// cache, a lookup table, the app's own SQL tables). <c>Tarol.DataProtection</c> runs every registered participant, in
/// registration order, after the tombstone.
/// </summary>
public abstract class ErasureParticipant
{
    /// <summary>The participant's name (logs and the audit).</summary>
    public abstract string Name { get; }

    /// <summary>Forgets a subject: the key is already a tombstone, so a re-fold of <paramref name="streams"/> reads its fields as <c>null</c>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The erased subject.</param>
    /// <param name="streams">The streams that held its data.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    public abstract ValueTask Forget(Guid tenant, Guid subject, IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken);

    /// <summary>Forgets a whole tenant (its tenant key is blanked). The default does nothing.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    public virtual ValueTask ForgetTenant(Guid tenant, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// A subject or tenant was erased, here or in another process (<see cref="IKeyStore.Erased"/>): drop what this process
    /// holds in memory (a page cache). The default does nothing.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject; <see langword="null"/> for the whole tenant.</param>
    public virtual void Evict(Guid tenant, Guid? subject)
    {
    }

    /// <summary>Retention deleted <paramref name="streams"/> (DP-7): drop what was derived from them. The default does nothing.</summary>
    /// <param name="streams">The deleted streams.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    public virtual ValueTask Deleted(IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>
/// How long each stream type is kept after its last event (DP-7): <c>[Retention(Days)]</c> on the aggregate, overridden per
/// stream type (configuration <c>Tarol:Retention:&lt;streamType&gt;:Days</c>). Stream types without days are kept forever.
/// </summary>
public sealed class RetentionPolicy
{
    private readonly Dictionary<string, int> _days = new(StringComparer.Ordinal);
    private readonly HashSet<string> _overridden = new(StringComparer.Ordinal);

    /// <summary>The days per stream type.</summary>
    public IReadOnlyDictionary<string, int> Days => _days;

    /// <summary>Takes <typeparamref name="T"/>'s <c>[Retention(Days)]</c>, unless the stream type is overridden.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <returns>This policy.</returns>
    public RetentionPolicy Add<T>()
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        if (!_overridden.Contains(T.StreamType) && T.EventTypes.RetentionDays is { } days)
            _days[T.StreamType] = days;
        return this;
    }

    /// <summary>Overrides a stream type's days (configuration wins over the attribute).</summary>
    /// <param name="streamType">The stream type.</param>
    /// <param name="days">The days (at least 1); <see langword="null"/> = kept forever.</param>
    /// <returns>This policy.</returns>
    public RetentionPolicy Set(string streamType, int? days)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamType);
        if (days is { } d)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(d, 1, nameof(days));
            _days[streamType] = d;
        }
        else
        {
            _days.Remove(streamType);
        }

        _overridden.Add(streamType);
        return this;
    }

    /// <summary>The days of a stream type.</summary>
    /// <param name="streamType">The stream type.</param>
    /// <param name="days">The days, when it has a retention.</param>
    /// <returns><see langword="true"/> when it has one.</returns>
    public bool TryGetDays(string streamType, out int days) => _days.TryGetValue(streamType, out days);
}
