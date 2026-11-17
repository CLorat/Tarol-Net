using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence;

/// <summary>
/// The job rows of an <see cref="InMemoryAggregateStore"/>: the same rules as the Npgsql rows (one claim per row, leases on
/// the store's clock, guarded by the claim id, dead jobs and re-drives), so <c>JobTest</c> and unit tests run the whole
/// schedule on a fake clock without a database.
/// </summary>
internal sealed class InMemoryJobRows(TimeProvider time) : IJobRows
{
    private sealed class Row
    {
        public required Guid Tenant { get; init; }
        public required string Name { get; init; }
        public required string Schedule { get; set; }
        public bool Enabled { get; set; } = true;
        public DateTimeOffset NextAt { get; set; }
        public Guid? Claim { get; set; }
        public DateTimeOffset? LeaseUntil { get; set; }
        public int Attempts { get; set; }
        public Option<string> LastError { get; set; }
        public long? ClaimedRedrive { get; set; }
    }

    private sealed class Dead
    {
        public required long Id { get; init; }
        public required Guid Tenant { get; init; }
        public required string Name { get; init; }
        public required DateTimeOffset Occurrence { get; init; }
        public int Attempts { get; set; }
        public Option<string> LastError { get; set; }
        public DateTimeOffset DeadAt { get; set; }
        public bool Redriven { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid Tenant, string Name), Row> _rows = [];
    private readonly List<Dead> _dead = [];
    private long _nextDead;

    public Task<IReadOnlyList<JobSyncChange>> Sync(IReadOnlyCollection<JobSchedule> jobs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        cancellationToken.ThrowIfCancellationRequested();
        var declared = new Dictionary<(Guid, string), JobSchedule>();
        foreach (var job in jobs)
        {
            ArgumentNullException.ThrowIfNull(job, nameof(jobs));
            ArgumentException.ThrowIfNullOrWhiteSpace(job.Name, nameof(jobs));
            ArgumentNullException.ThrowIfNull(job.Schedule, nameof(jobs));
            JobTimes.ThrowIfNotWholeSecond(job.First, nameof(jobs));
            if (!declared.TryAdd((TenantOf(job.Tenant), job.Name), job))
                throw new ArgumentException($"The job row ('{job.Name}', {job.Tenant}) is listed twice.", nameof(jobs));
        }
        var changes = new List<JobSyncChange>();
        lock (_gate)
        {
            foreach (var (key, job) in declared)
            {
                if (!_rows.TryGetValue(key, out var row))
                {
                    _rows[key] = new Row { Tenant = key.Item1, Name = job.Name, Schedule = job.Schedule, Enabled = job.Enabled, NextAt = job.First };
                    changes.Add(new(job.Name, job.Tenant, job.Enabled ? JobSyncKind.Added : JobSyncKind.Disabled));
                    continue;
                }
                if (!string.Equals(row.Schedule, job.Schedule, StringComparison.Ordinal))
                {
                    row.Schedule = job.Schedule;
                    Restart(row, job.First);
                    if (job.Enabled)
                        changes.Add(new(job.Name, job.Tenant, JobSyncKind.Rescheduled));
                }
                else if (job.Enabled && !row.Enabled)
                {
                    Restart(row, job.First);
                    changes.Add(new(job.Name, job.Tenant, JobSyncKind.Enabled));
                }
                if (row.Enabled && !job.Enabled)
                    changes.Add(new(job.Name, job.Tenant, JobSyncKind.Disabled));
                row.Enabled = job.Enabled;
            }
            foreach (var (key, row) in _rows)
            {
                if (!row.Enabled || declared.ContainsKey(key))
                    continue;
                row.Enabled = false;
                changes.Add(new(row.Name, Tenant(row.Tenant), JobSyncKind.Disabled));
            }
        }
        return Task.FromResult<IReadOnlyList<JobSyncChange>>(changes);

        static void Restart(Row row, DateTimeOffset first)
        {
            row.NextAt = first;
            row.Claim = null;
            row.LeaseUntil = null;
            row.ClaimedRedrive = null;
            row.Attempts = 0;
            row.LastError = Option.None;
        }
    }

    public Task<Option<JobRow>> Claim(TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var free = _rows.Values.Where(r => r.Enabled && (r.LeaseUntil is null || r.LeaseUntil <= now)).ToList();
            var redrive = _dead.Where(d => d.Redriven).OrderBy(d => d.Id)
                .Select(d => (Dead: d, Row: free.Find(r => r.Tenant == d.Tenant && r.Name == d.Name)))
                .FirstOrDefault(x => x.Row is not null);
            Row? row;
            Dead? dead = null;
            if (redrive.Row is not null)
                (row, dead) = (redrive.Row, redrive.Dead);
            else
                row = free.Where(r => r.NextAt <= now).OrderBy(r => r.NextAt).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Tenant).FirstOrDefault();
            if (row is null)
                return Task.FromResult<Option<JobRow>>(Option.None);
            row.Claim = Guid.NewGuid();
            row.LeaseUntil = now + lease;
            row.Attempts++;
            row.ClaimedRedrive = dead?.Id;
            Option<long> redriveId = dead is null ? Option.None : dead.Id;
            return Task.FromResult<Option<JobRow>>(new JobRow(Tenant(row.Tenant), row.Name, row.Schedule, row.NextAt, row.Claim.Value, row.Attempts,
                row.LastError, redriveId, dead?.Occurrence ?? row.NextAt));
        }
    }

    public Task<bool> Move(JobRow row, DateTimeOffset cursor, CancellationToken cancellationToken)
    {
        JobTimes.ThrowIfNotWholeSecond(cursor, nameof(cursor));
        return Owned(row, owned => owned.NextAt = cursor);
    }

    public Task<bool> Advance(JobRow row, DateTimeOffset cursor, CancellationToken cancellationToken)
    {
        JobTimes.ThrowIfNotWholeSecond(cursor, nameof(cursor));
        return Owned(row, owned =>
        {
            if (owned.ClaimedRedrive is { } id)
                _ = _dead.RemoveAll(d => d.Id == id);
            Release(owned, cursor);
        });
    }

    public Task<bool> Extend(JobRow row, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        return Owned(row, owned => owned.LeaseUntil = time.GetUtcNow() + lease);
    }

    public Task<bool> Retry(JobRow row, DateTimeOffset retryAt, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return Owned(row, owned =>
        {
            owned.Claim = null;
            owned.LeaseUntil = retryAt;
            owned.LastError = reason;
        });
    }

    public Task<bool> Bury(JobRow row, DateTimeOffset cursor, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reason);
        JobTimes.ThrowIfNotWholeSecond(cursor, nameof(cursor));
        return Owned(row, owned =>
        {
            var now = time.GetUtcNow();
            if (owned.ClaimedRedrive is { } id && _dead.Find(d => d.Id == id) is { } dead)
                (dead.Attempts, dead.LastError, dead.DeadAt, dead.Redriven) = (owned.Attempts, reason, now, false);
            else
                _dead.Add(new Dead
                {
                    Id = ++_nextDead,
                    Tenant = owned.Tenant,
                    Name = owned.Name,
                    Occurrence = row.Occurrence,
                    Attempts = owned.Attempts,
                    LastError = reason,
                    DeadAt = now,
                });
            Release(owned, cursor);
        });
    }

    public Task<IReadOnlyList<DeadJob>> DeadJobs(int max, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        lock (_gate)
            return Task.FromResult<IReadOnlyList<DeadJob>>([.. _dead.OrderBy(d => d.Id).Take(max)
                .Select(d => new DeadJob(d.Id, Tenant(d.Tenant), d.Name, d.Occurrence, d.Attempts, d.LastError, d.DeadAt, d.Redriven))]);
    }

    public Task<bool> Redrive(long id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_dead.Find(d => d.Id == id) is not { Redriven: false } dead)
                return Task.FromResult(false);
            dead.Redriven = true;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<JobState>> Rows(CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<JobState>>([.. _rows.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Tenant)
                .Select(r => new JobState(Tenant(r.Tenant), r.Name, r.Schedule, r.Enabled, r.NextAt,
                    r.LeaseUntil is { } lease ? lease : Option.None, r.Attempts, r.LastError))]);
    }

    public Task<Option<DateTimeOffset>> Overdue(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var due = _rows.Values.Where(r => r.Enabled && r.NextAt <= now).Select(r => r.NextAt).ToList();
            return Task.FromResult<Option<DateTimeOffset>>(due.Count == 0 ? Option.None : due.Min());
        }
    }

    private Task<bool> Owned(JobRow claimed, Action<Row> change)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        lock (_gate)
        {
            if (!_rows.TryGetValue((TenantOf(claimed.Tenant), claimed.Name), out var row) || row.Claim != claimed.ClaimId)
                return Task.FromResult(false);
            change(row);
            return Task.FromResult(true);
        }
    }

    private static void Release(Row row, DateTimeOffset next)
    {
        row.NextAt = next;
        row.Claim = null;
        row.LeaseUntil = null;
        row.ClaimedRedrive = null;
        row.Attempts = 0;
        row.LastError = Option.None;
    }

    private static Guid TenantOf(Option<Id<ITenant>> tenant) => tenant.TryGetValue(out Id<ITenant> id) ? id.Value : Guid.Empty;

    private static Option<Id<ITenant>> Tenant(Guid tenant) => tenant == Guid.Empty ? Option.None : (Id<ITenant>)tenant;
}
