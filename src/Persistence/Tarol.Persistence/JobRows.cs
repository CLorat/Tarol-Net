using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence;

/// <summary>
/// A job row as the application declares it, for <see cref="IJobRows.Sync"/>: one per (tenant, job).
/// </summary>
/// <param name="Name">The job's stable name (stored in the row and in every idempotency key of its occurrences).</param>
/// <param name="Tenant">The tenant, or <see cref="Option.None"/> for a tenant-less job.</param>
/// <param name="Schedule">The schedule's text (cron and zone); a different text than the stored one recomputes the cursor.</param>
/// <param name="Enabled">Whether the row is claimed at all (<c>Tarol:Jobs:&lt;name&gt;:Enabled</c>).</param>
/// <param name="First">The first occurrence after now (whole seconds, UTC): the cursor of a new, rescheduled or re-enabled row.</param>
public sealed record JobSchedule(string Name, Option<Id<ITenant>> Tenant, string Schedule, bool Enabled, DateTimeOffset First);

/// <summary>What <see cref="IJobRows.Sync"/> did to one row.</summary>
public enum JobSyncKind
{
    /// <summary>A new row, its cursor at <see cref="JobSchedule.First"/>.</summary>
    Added,

    /// <summary>The schedule text changed: the cursor moved to <see cref="JobSchedule.First"/>, a claim on it was dropped.</summary>
    Rescheduled,

    /// <summary>A disabled row was enabled again, its cursor at <see cref="JobSchedule.First"/> (no catch-up for the pause).</summary>
    Enabled,

    /// <summary>The row was disabled: by configuration, or because its job (or tenant) is no longer declared.</summary>
    Disabled,
}

/// <summary>One row <see cref="IJobRows.Sync"/> changed.</summary>
/// <param name="Name">The job.</param>
/// <param name="Tenant">The row's tenant.</param>
/// <param name="Kind">What changed.</param>
public sealed record JobSyncChange(string Name, Option<Id<ITenant>> Tenant, JobSyncKind Kind);

/// <summary>
/// A claimed job row: run <see cref="Occurrence"/> (and, by the job's missed-run policy, the later ones up to now), then
/// <see cref="IJobRows.Move"/> the cursor after each and <see cref="IJobRows.Advance"/> to release the claim.
/// </summary>
/// <param name="Tenant">The row's tenant.</param>
/// <param name="Name">The job.</param>
/// <param name="Schedule">The stored schedule text.</param>
/// <param name="NextAt">The cursor: the oldest occurrence not yet run.</param>
/// <param name="ClaimId">This claim; every later call must present it.</param>
/// <param name="Attempts">How many times the row was claimed since its last success, this claim included.</param>
/// <param name="LastError">The error of the previous attempt.</param>
/// <param name="Redrive">A re-driven dead job (<see cref="DeadJob.Id"/>): run only <see cref="Occurrence"/>, never the cursor.</param>
/// <param name="Occurrence">The occurrence to run first: <see cref="NextAt"/>, or the re-driven dead job's occurrence.</param>
public sealed record JobRow(Option<Id<ITenant>> Tenant, string Name, string Schedule, DateTimeOffset NextAt, Guid ClaimId, int Attempts,
    Option<string> LastError, Option<long> Redrive, DateTimeOffset Occurrence);

/// <summary>A job row as stored, for tests, health checks and admin pages.</summary>
/// <param name="Tenant">The row's tenant.</param>
/// <param name="Name">The job.</param>
/// <param name="Schedule">The stored schedule text.</param>
/// <param name="Enabled">Whether it is claimed.</param>
/// <param name="NextAt">The cursor.</param>
/// <param name="LeaseUntil">Until when a claim (or a retry's backoff) holds it.</param>
/// <param name="Attempts">Attempts since the last success.</param>
/// <param name="LastError">The last error.</param>
public sealed record JobState(Option<Id<ITenant>> Tenant, string Name, string Schedule, bool Enabled, DateTimeOffset NextAt,
    Option<DateTimeOffset> LeaseUntil, int Attempts, Option<string> LastError);

/// <summary>An occurrence that kept failing: the cursor moved on without it. A human reads it and re-drives it.</summary>
/// <param name="Id">The dead job (re-drive it by this id).</param>
/// <param name="Tenant">The row's tenant.</param>
/// <param name="Name">The job.</param>
/// <param name="Occurrence">The occurrence that failed.</param>
/// <param name="Attempts">How many attempts it had.</param>
/// <param name="LastError">Why the last attempt failed.</param>
/// <param name="DeadAt">When it was buried.</param>
/// <param name="Redriven">Whether a re-drive is pending (it runs at the row's next claim).</param>
public sealed record DeadJob(long Id, Option<Id<ITenant>> Tenant, string Name, DateTimeOffset Occurrence, int Attempts, Option<string> LastError,
    DateTimeOffset DeadAt, bool Redriven);

/// <summary>
/// The job rows of a store (docs/architecture/jobs.md §5, J-4): one row per (tenant, job) holding the cursor of the oldest
/// occurrence not yet run, claimed one at a time with a lease that a heartbeat extends. The runner keys every occurrence's
/// commit with (tenant, <c>job:&lt;name&gt;</c>, occurrence), so the commit is once; the cursor only moves forward with the
/// claim, so the occurrence is once.
/// </summary>
/// <remarks>
/// Every call after <see cref="Claim"/> presents the claim: a worker that lost its lease (frozen, or slower than its
/// heartbeat) gets <see langword="false"/> and changes nothing. Times are whole seconds in UTC (an occurrence must survive the
/// store's precision unchanged, jobs.md §5 item 1); other times throw <see cref="ArgumentException"/>.
/// </remarks>
public interface IJobRows
{
    /// <summary>
    /// Makes the rows match the declared jobs, in one transaction: adds new rows (cursor <see cref="JobSchedule.First"/>),
    /// recomputes the cursor of a row whose schedule text changed (and drops its claim), enables and disables rows, and
    /// disables every stored row that is not declared (a removed job, or a tenant the directory no longer lists).
    /// </summary>
    /// <param name="jobs">Every declared row, at most one per (tenant, name).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The rows that changed.</returns>
    /// <exception cref="ArgumentException">A (tenant, name) is listed twice, or a time is not a whole second.</exception>
    Task<IReadOnlyList<JobSyncChange>> Sync(IReadOnlyCollection<JobSchedule> jobs, CancellationToken cancellationToken);

    /// <summary>
    /// Claims one enabled row for <paramref name="lease"/> that is due (cursor ≤ now) or has a pending re-drive, and whose
    /// lease (or retry backoff) has passed. A pending re-drive is claimed first.
    /// </summary>
    /// <param name="lease">How long the row stays with this worker unless extended.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The claimed row, or <see cref="Option.None"/> when nothing is due.</returns>
    Task<Option<JobRow>> Claim(TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the cursor past an occurrence that ran and keeps the claim (a catch-up keeps its progress on a crash). The next
    /// occurrence's attempts start at this claim (1) and the error is cleared: earlier failures belong to the occurrence that ran.
    /// </summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="cursor">The new cursor.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the claim was lost.</returns>
    Task<bool> Move(JobRow row, DateTimeOffset cursor, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the cursor to <paramref name="cursor"/> and releases the claim after a success: attempts and the error are cleared.
    /// For a re-drive, the dead job is removed (pass the unchanged <see cref="JobRow.NextAt"/>).
    /// </summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="cursor">The new cursor.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the claim was lost.</returns>
    Task<bool> Advance(JobRow row, DateTimeOffset cursor, CancellationToken cancellationToken);

    /// <summary>Extends the lease of a running claim to now + <paramref name="lease"/> (the heartbeat, every lease/3).</summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="lease">The new lease from now.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the claim was lost: stop, another worker may have the row.</returns>
    Task<bool> Extend(JobRow row, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Releases the claim to be claimed again at <paramref name="retryAt"/>; the cursor stays, so the same occurrence runs again.</summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="retryAt">When it may be claimed again (the backoff).</param>
    /// <param name="reason">What went wrong (the next claim's <see cref="JobRow.LastError"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the claim was lost.</returns>
    Task<bool> Retry(JobRow row, DateTimeOffset retryAt, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Gives the occurrence up: <see cref="JobRow.Occurrence"/> goes to the dead jobs, the cursor to <paramref name="cursor"/>,
    /// the claim is released and attempts are cleared. For a re-drive, its dead job stays dead with the new attempts and error
    /// (pass the unchanged <see cref="JobRow.NextAt"/>).
    /// </summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="cursor">The new cursor.</param>
    /// <param name="reason">Why it is given up.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the claim was lost.</returns>
    Task<bool> Bury(JobRow row, DateTimeOffset cursor, string reason, CancellationToken cancellationToken);

    /// <summary>The oldest dead jobs.</summary>
    /// <param name="max">The most entries.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The dead jobs, oldest first.</returns>
    Task<IReadOnlyList<DeadJob>> DeadJobs(int max, CancellationToken cancellationToken);

    /// <summary>
    /// Re-drives a dead job: its row runs the dead occurrence once more at its next claim, with its key (a part that did
    /// commit is not repeated), never at the same time as the row's own occurrences.
    /// </summary>
    /// <param name="id">The dead job's <see cref="DeadJob.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when there is no such dead job, or its re-drive is pending already.</returns>
    Task<bool> Redrive(long id, CancellationToken cancellationToken);

    /// <summary>Every stored row, by name then tenant.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The rows.</returns>
    Task<IReadOnlyList<JobState>> Rows(CancellationToken cancellationToken);

    /// <summary>The oldest cursor of an enabled row that is due now (a health check: now minus it is how late jobs run).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The oldest due cursor, or <see cref="Option.None"/> when nothing is due.</returns>
    Task<Option<DateTimeOffset>> Overdue(CancellationToken cancellationToken);
}

/// <summary>Checks shared by the stores' job rows.</summary>
internal static class JobTimes
{
    /// <summary>Throws unless <paramref name="value"/> is a whole second (the cursor must round-trip through <c>timestamptz</c>).</summary>
    public static void ThrowIfNotWholeSecond(DateTimeOffset value, string name)
    {
        if (value.UtcTicks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentException($"A job time is a whole second; {value:O} is not.", name);
    }
}
