using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence;

/// <summary>
/// One number of the operations snapshot (docs/architecture/operations.md §2, O-1, O-7): a package registers it as a singleton,
/// as it registers a <see cref="MaintenanceTask"/>. <c>Tarol.Operations</c> calls <see cref="Read"/> once per poll (every 15 s
/// by default) and publishes the values as an observable gauge <see cref="Name"/> on the meter <see cref="Meter"/>; the health
/// checks read the same snapshot. Nothing reads the database per scrape.
/// </summary>
/// <param name="Meter">The meter of the gauge, e.g. <see cref="OperationsSignals.Meter"/>.</param>
/// <param name="Name">The instrument, <c>tarol.&lt;area&gt;.&lt;what&gt;</c> (e.g. <c>tarol.reactions.oldest_due</c>).</param>
/// <param name="Unit">The UCUM unit: <c>s</c>, <c>{row}</c>, …</param>
/// <param name="Description">What it measures.</param>
/// <param name="Read">
/// Reads the values: services and the poll's token in; one value per tag value out (an empty list = nothing to report). Cheap
/// index scans only; it runs on the poll, never on the exporter's thread.
/// </param>
public sealed record OperationsSignal(string Meter, string Name, string Unit, string Description,
    Func<IServiceProvider, CancellationToken, Task<IReadOnlyList<SignalValue>>> Read);

/// <summary>One value of an <see cref="OperationsSignal"/>, with at most one tag of low cardinality (never the tenant).</summary>
/// <param name="Value">The value.</param>
/// <param name="TagName">The tag's name, e.g. <c>tarol.projection</c>; <see langword="null"/> = untagged.</param>
/// <param name="TagValue">The tag's value.</param>
public readonly record struct SignalValue(double Value, string? TagName = null, string? TagValue = null);

/// <summary>The names of Tarol's snapshot signals (the gauges of <c>Tarol.Operations</c>).</summary>
public static class OperationsSignals
{
    /// <summary>The meter of the snapshot gauges.</summary>
    public const string Meter = "Tarol.Operations";

    /// <summary>Outbox rows, an estimate (<c>{row}</c>): a size, not an alert signal.</summary>
    public const string ReactionsBacklog = "tarol.reactions.backlog";

    /// <summary>The age of the oldest due reaction row in seconds; 0 when none is due: the alert signal.</summary>
    public const string ReactionsOldestDue = "tarol.reactions.oldest_due";

    /// <summary>Dead reaction rows (<c>{row}</c>).</summary>
    public const string ReactionsDead = "tarol.reactions.dead";

    /// <summary>How late the most overdue job cursor is, in seconds; 0 when none.</summary>
    public const string JobsOverdue = "tarol.jobs.overdue";

    /// <summary>Dead job occurrences (<c>{occurrence}</c>).</summary>
    public const string JobsDead = "tarol.jobs.dead";

    /// <summary>A projection's lag in seconds (feed head time − checkpoint time), tagged <c>tarol.projection</c>.</summary>
    public const string ProjectionsLag = "tarol.projections.lag";

    /// <summary>Dead messages (<c>{message}</c>).</summary>
    public const string MessagesDead = "tarol.messages.dead";

    /// <summary>Deliveries waiting on the PostgreSQL bus, tagged <c>tarol.subscription</c> (<c>{message}</c>).</summary>
    public const string MessagesBacklog = "tarol.messages.backlog";
}

/// <summary>
/// A page request over dead letters (O-3): keyset paging by id (<see cref="After"/>), at most <see cref="MaxLimit"/> rows, and
/// optional filters. The next page starts after the last id of this one.
/// </summary>
public sealed record DeadLetterQuery
{
    /// <summary>The largest page.</summary>
    public const int MaxLimit = 200;

    /// <summary>Only ids after this one (default 0: from the start).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Negative.</exception>
    public long After
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    /// <summary>The page size, 1 … <see cref="MaxLimit"/> (default 50).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 1 … <see cref="MaxLimit"/>.</exception>
    public int Limit
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxLimit);
            field = value;
        }
    } = 50;

    /// <summary>Only this tenant's rows; <see cref="Option.None"/> = every tenant.</summary>
    public Option<Id<ITenant>> Tenant { get; init; }

    /// <summary>Only rows of this reaction, job or subscription; <see langword="null"/> = all.</summary>
    public string? Name { get; init; }
}

/// <summary>One dead letter of any kind, as the admin entry lists it.</summary>
/// <param name="Id">Its id within its kind (re-drive or discard it by this id).</param>
/// <param name="Kind">The kind (<see cref="DeadLetterKinds"/>).</param>
/// <param name="Name">The reaction, job or subscription.</param>
/// <param name="Tenant">The tenant, when the kind has one.</param>
/// <param name="Subject">What failed: <c>&lt;stream type&gt;/&lt;stream&gt;/&lt;event&gt;</c>, the occurrence, or <c>&lt;topic&gt;/&lt;message&gt;</c>.</param>
/// <param name="Attempts">How often it was tried.</param>
/// <param name="Error">Why the last attempt failed.</param>
/// <param name="DeadAt">When it was given up.</param>
public sealed record DeadLetterItem(long Id, string Kind, string Name, Option<Id<ITenant>> Tenant, string Subject, int Attempts, Option<string> Error,
    DateTimeOffset DeadAt);

/// <summary>A page of dead letters.</summary>
/// <param name="Items">The rows, by id.</param>
/// <param name="Next">The <see cref="DeadLetterQuery.After"/> of the next page; <see langword="null"/> = this is the last.</param>
public sealed record DeadLetterPage(IReadOnlyList<DeadLetterItem> Items, long? Next);

/// <summary>The kinds of dead letters Tarol's packages register.</summary>
public static class DeadLetterKinds
{
    /// <summary>Dead reaction rows (<c>outbox_dead</c>).</summary>
    public const string Reactions = "reactions";

    /// <summary>Dead job occurrences (<c>jobs_dead</c>).</summary>
    public const string Jobs = "jobs";

    /// <summary>Dead consumer messages (<c>messages_dead</c>).</summary>
    public const string Messages = "messages";
}

/// <summary>
/// A kind of dead letters the admin entry pages, re-drives and discards (O-3, O-7): a package registers one per kind as a
/// singleton. A database implementation writes the audit row (<c>operations_audit</c>) in the same statement as the act.
/// </summary>
public interface IDeadLetterSource
{
    /// <summary>The kind: <see cref="DeadLetterKinds.Reactions"/>, <see cref="DeadLetterKinds.Jobs"/>, <see cref="DeadLetterKinds.Messages"/>, ….</summary>
    string Kind { get; }

    /// <summary>One page.</summary>
    /// <param name="query">Paging and filters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    Task<DeadLetterPage> List(DeadLetterQuery query, CancellationToken cancellationToken);

    /// <summary>Runs one dead letter again (its kind decides how); audited with <paramref name="actor"/>.</summary>
    /// <param name="id">The dead letter.</param>
    /// <param name="actor">Who asked (the principal's name).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when there is no such dead letter (or it is re-driven already).</returns>
    Task<bool> Redrive(long id, string actor, CancellationToken cancellationToken);

    /// <summary>Deletes one dead letter for good; audited with <paramref name="actor"/>.</summary>
    /// <param name="id">The dead letter.</param>
    /// <param name="actor">Who asked.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when there is no such dead letter.</returns>
    Task<bool> Discard(long id, string actor, CancellationToken cancellationToken);
}

/// <summary>
/// A condition of readiness (step 15, O-2): a package registers one as a singleton, and <c>Tarol.Operations</c>' <c>ready</c>
/// check fails while it reports a problem (e.g. the schema is not ensured yet, the checkpoint <c>LISTEN</c> is down).
/// </summary>
/// <param name="Name">What is checked, e.g. <c>schema:tarol</c>.</param>
/// <param name="Problem">Services in, <see langword="null"/> when ready, else what is wrong. Cheap and synchronous: no query.</param>
public sealed record ReadinessCheck(string Name, Func<IServiceProvider, string?> Problem);

/// <summary>One version of a projection as the admin entry lists it (step 15, O-5).</summary>
/// <param name="Name">The projection.</param>
/// <param name="Version">Its version.</param>
/// <param name="State"><c>building</c> or <c>current</c>.</param>
/// <param name="Rows">Rows of its table, estimated (<c>reltuples</c>; −1 → 0 before the first <c>ANALYZE</c>).</param>
/// <param name="Bytes">The table's size with its indexes.</param>
/// <param name="Superseded">Whether a newer version of the same name exists (a candidate for <c>DropVersion</c>).</param>
public sealed record ProjectionVersion(string Name, int Version, string State, long Rows, long Bytes, bool Superseded);

/// <summary>The read models' versions for the admin entry (step 15): listed, and old ones dropped explicitly (QR-3, O-5).</summary>
public interface IReadModelAdmin
{
    /// <summary>Every stored projection version, by name then version.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The versions.</returns>
    Task<IReadOnlyList<ProjectionVersion>> Versions(CancellationToken cancellationToken);

    /// <summary>Drops an old version's table and checkpoint, audited with <paramref name="actor"/>.</summary>
    /// <param name="name">The projection.</param>
    /// <param name="version">The old version.</param>
    /// <param name="actor">Who asked.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">The version is the newest of its name, or this process uses it.</exception>
    /// <exception cref="TimeoutException">Another session still reads the table.</exception>
    Task DropVersion(string name, int version, string actor, CancellationToken cancellationToken);
}

/// <summary>
/// A dead-letter source whose rows can be purged by age (step 15, O-5: the optional <c>DeadRetention</c> of
/// <c>Tarol.Operations</c>, run as the system job <c>tarol.purge-dead</c>). Dead letters are kept unless it is configured.
/// </summary>
public interface IDeadLetterRetention
{
    /// <summary>The kind it purges (<see cref="IDeadLetterSource.Kind"/>).</summary>
    string Kind { get; }

    /// <summary>Deletes up to <paramref name="max"/> dead letters given up before <paramref name="olderThan"/>, audited as one row.</summary>
    /// <param name="olderThan">The cut-off.</param>
    /// <param name="max">The most rows of this call.</param>
    /// <param name="actor">Who purges (the system job's name).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many were deleted.</returns>
    Task<int> Purge(DateTimeOffset olderThan, int max, string actor, CancellationToken cancellationToken);
}
