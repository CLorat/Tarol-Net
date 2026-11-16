using Npgsql;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Probe.Jobs;

/// <summary>What to do with the occurrences a scheduler finds in the past (downtime, a run longer than the interval).</summary>
public enum Missed { All, Latest, Skip }

/// <summary>A claimed schedule row: the job is due at <see cref="NextAt"/> (the oldest occurrence not yet run).</summary>
public sealed record JobRow(Guid Tenant, string Name, DateTimeOffset NextAt, Guid ClaimId, int Attempts);

/// <summary>One occurrence handed to a job body.</summary>
public sealed record JobRun(Option<Id<ITenant>> Tenant, string Name, DateTimeOffset Occurrence, OperationContext Context, Option<IdempotencyKey> Key);

public sealed record JobSpec(string Name, ISchedule Schedule, TimeZoneInfo Zone, Missed Policy, Func<JobRun, CancellationToken, Task<Result<ChangeOutcome>>> Body)
{
    public int CatchUpCap { get; init; } = 100;
    public TimeSpan Grace { get; init; } = TimeSpan.FromMinutes(1);
    public bool UseKey { get; init; } = true;
    public int MaxAttempts { get; init; } = 10;
}

/// <summary>The schedule rows: one per (tenant, job), the cursor of the job (variant A: leased claims; B: the leader's reads).</summary>
public interface IJobRows
{
    Task<IReadOnlyList<JobRow>> Claim(int max, TimeSpan lease, CancellationToken ct);
    Task<bool> Advance(JobRow row, DateTimeOffset next, CancellationToken ct);
    /// <summary>Moves the cursor past an occurrence that ran, keeping the claim (a crash later in a catch-up loses no progress).</summary>
    Task<bool> Move(JobRow row, DateTimeOffset next, CancellationToken ct);
    Task<bool> Extend(JobRow row, TimeSpan lease, CancellationToken ct);
    Task<bool> Retry(JobRow row, DateTimeOffset at, string error, CancellationToken ct);
}

/// <summary>Variant A on PostgreSQL: <c>FOR UPDATE SKIP LOCKED</c> claims with a lease, advance by claim id.</summary>
public sealed class NpgsqlJobRows(NpgsqlDataSource db, string schema, TimeProvider time) : IJobRows
{
    public static string Script(string s) => $"""
        CREATE TABLE IF NOT EXISTS {s}.jobs (
            tenant      uuid        NOT NULL,
            name        text        NOT NULL,
            next_at     timestamptz NOT NULL,          -- the oldest occurrence not yet run (the cursor)
            claim_id    uuid        NULL,
            lease_until timestamptz NULL,
            attempts    integer     NOT NULL DEFAULT 0,
            last_error  text        NULL,
            PRIMARY KEY (tenant, name));
        CREATE INDEX IF NOT EXISTS jobs_due ON {s}.jobs (next_at);
        """;

    public async Task EnsureSchema() { await Lab.Exec(Script(schema)); }

    public Task Register(Guid tenant, string name, DateTimeOffset first)
        => Lab.Exec($"INSERT INTO {schema}.jobs (tenant, name, next_at) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING", tenant, name, first);

    public async Task<IReadOnlyList<JobRow>> Claim(int max, TimeSpan lease, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var c = db.CreateCommand($"""
            WITH c AS (SELECT tenant, name FROM {schema}.jobs
                       WHERE next_at <= $1 AND (lease_until IS NULL OR lease_until <= $1)
                       ORDER BY next_at LIMIT $2 FOR UPDATE SKIP LOCKED)
            UPDATE {schema}.jobs j SET claim_id = gen_random_uuid(), lease_until = $3, attempts = j.attempts + 1
            FROM c WHERE j.tenant = c.tenant AND j.name = c.name
            RETURNING j.tenant, j.name, j.next_at, j.claim_id, j.attempts
            """);
        c.Parameters.Add(new() { Value = now });
        c.Parameters.Add(new() { Value = max });
        c.Parameters.Add(new() { Value = now + lease });
        await using var r = await c.ExecuteReaderAsync(ct);
        var rows = new List<JobRow>();
        while (await r.ReadAsync(ct))
            rows.Add(new JobRow(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetGuid(3), r.GetInt32(4)));
        return rows;
    }

    public async Task<bool> Advance(JobRow row, DateTimeOffset next, CancellationToken ct)
    {
        await using var c = db.CreateCommand($"UPDATE {schema}.jobs SET next_at = $4, claim_id = NULL, lease_until = NULL, attempts = 0, last_error = NULL WHERE tenant = $1 AND name = $2 AND claim_id = $3");
        foreach (var v in new object[] { row.Tenant, row.Name, row.ClaimId, next }) c.Parameters.Add(new() { Value = v });
        return await c.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> Move(JobRow row, DateTimeOffset next, CancellationToken ct)
    {
        await using var c = db.CreateCommand($"UPDATE {schema}.jobs SET next_at = $4 WHERE tenant = $1 AND name = $2 AND claim_id = $3");
        foreach (var v in new object[] { row.Tenant, row.Name, row.ClaimId, next }) c.Parameters.Add(new() { Value = v });
        return await c.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> Extend(JobRow row, TimeSpan lease, CancellationToken ct)
    {
        await using var c = db.CreateCommand($"UPDATE {schema}.jobs SET lease_until = $4 WHERE tenant = $1 AND name = $2 AND claim_id = $3");
        foreach (var v in new object[] { row.Tenant, row.Name, row.ClaimId, time.GetUtcNow() + lease }) c.Parameters.Add(new() { Value = v });
        return await c.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> Retry(JobRow row, DateTimeOffset at, string error, CancellationToken ct)
    {
        await using var c = db.CreateCommand($"UPDATE {schema}.jobs SET claim_id = NULL, lease_until = $4, last_error = $5 WHERE tenant = $1 AND name = $2 AND claim_id = $3");
        foreach (var v in new object[] { row.Tenant, row.Name, row.ClaimId, at, error }) c.Parameters.Add(new() { Value = v });
        return await c.ExecuteNonQueryAsync(ct) == 1;
    }
}

/// <summary>Variant B's rows: the one leader reads due rows without a lease and advances by the cursor it read.</summary>
public sealed class LeaderJobRows(NpgsqlDataSource db, string schema, TimeProvider time) : IJobRows
{
    public async Task<IReadOnlyList<JobRow>> Claim(int max, TimeSpan lease, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var c = db.CreateCommand($"SELECT tenant, name, next_at, attempts FROM {schema}.jobs WHERE next_at <= $1 AND (lease_until IS NULL OR lease_until <= $1) ORDER BY next_at LIMIT $2");
        c.Parameters.Add(new() { Value = now });
        c.Parameters.Add(new() { Value = max });
        await using var r = await c.ExecuteReaderAsync(ct);
        var rows = new List<JobRow>();
        while (await r.ReadAsync(ct))
            rows.Add(new JobRow(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), Guid.Empty, r.GetInt32(3) + 1));
        return rows;
    }

    public async Task<bool> Advance(JobRow row, DateTimeOffset next, CancellationToken ct)
    {
        await using var c = db.CreateCommand($"UPDATE {schema}.jobs SET next_at = $4, lease_until = NULL, attempts = 0 WHERE tenant = $1 AND name = $2 AND next_at = $3");
        foreach (var v in new object[] { row.Tenant, row.Name, row.NextAt, next }) c.Parameters.Add(new() { Value = v });
        return await c.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> Move(JobRow row, DateTimeOffset next, CancellationToken ct)
    {
        await using var c = db.CreateCommand($"UPDATE {schema}.jobs SET next_at = $4 WHERE tenant = $1 AND name = $2 AND next_at = $3");
        foreach (var v in new object[] { row.Tenant, row.Name, row.NextAt, next }) c.Parameters.Add(new() { Value = v });
        return await c.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<bool> Extend(JobRow row, TimeSpan lease, CancellationToken ct) => Task.FromResult(true);

    public async Task<bool> Retry(JobRow row, DateTimeOffset at, string error, CancellationToken ct)
    {
        await using var c = db.CreateCommand($"UPDATE {schema}.jobs SET lease_until = $4, attempts = attempts + 1, last_error = $5 WHERE tenant = $1 AND name = $2 AND next_at = $3");
        foreach (var v in new object[] { row.Tenant, row.Name, row.NextAt, at, error }) c.Parameters.Add(new() { Value = v });
        return await c.ExecuteNonQueryAsync(ct) == 1;
    }
}

/// <summary>The rows in memory, for the test helper (a fake clock decides what is due).</summary>
public sealed class InMemoryJobRows(TimeProvider time) : IJobRows
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid, string), (DateTimeOffset Next, Guid Claim, DateTimeOffset? Lease, int Attempts)> _rows = [];

    public void Register(Guid tenant, string name, DateTimeOffset first) { lock (_gate) _rows.TryAdd((tenant, name), (first, Guid.Empty, null, 0)); }
    public DateTimeOffset NextOf(Guid tenant, string name) { lock (_gate) return _rows[(tenant, name)].Next; }

    public Task<IReadOnlyList<JobRow>> Claim(int max, TimeSpan lease, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var due = _rows.Where(kv => kv.Value.Next <= now && (kv.Value.Lease is null || kv.Value.Lease <= now)).OrderBy(kv => kv.Value.Next).Take(max).ToList();
            var rows = new List<JobRow>();
            foreach (var (key, v) in due)
            {
                var claim = Guid.NewGuid();
                _rows[key] = (v.Next, claim, now + lease, v.Attempts + 1);
                rows.Add(new JobRow(key.Item1, key.Item2, v.Next, claim, v.Attempts + 1));
            }
            return Task.FromResult<IReadOnlyList<JobRow>>(rows);
        }
    }

    public Task<bool> Advance(JobRow row, DateTimeOffset next, CancellationToken ct) => Update(row, v => (next, Guid.Empty, null, 0));
    public Task<bool> Move(JobRow row, DateTimeOffset next, CancellationToken ct) => Update(row, v => (next, v.Claim, v.Lease, v.Attempts));
    public Task<bool> Extend(JobRow row, TimeSpan lease, CancellationToken ct) => Update(row, v => (v.Next, v.Claim, time.GetUtcNow() + lease, v.Attempts));
    public Task<bool> Retry(JobRow row, DateTimeOffset at, string error, CancellationToken ct) => Update(row, v => (v.Next, Guid.Empty, at, v.Attempts));

    private Task<bool> Update(JobRow row, Func<(DateTimeOffset Next, Guid Claim, DateTimeOffset? Lease, int Attempts), (DateTimeOffset, Guid, DateTimeOffset?, int)> f)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue((row.Tenant, row.Name), out var v) || v.Claim != row.ClaimId) return Task.FromResult(false);
            _rows[(row.Tenant, row.Name)] = f(v);
            return Task.FromResult(true);
        }
    }
}

/// <summary>What one claimed row did.</summary>
public sealed record JobAttempt(JobRow Row, IReadOnlyList<DateTimeOffset> Ran, bool Advanced, string? Error);

/// <summary>
/// The would-be job worker: claim due rows, pick the occurrences by the job's missed-run policy, run each with the
/// occurrence as its idempotency key (tenant, <c>job:&lt;name&gt;</c>, occurrence), extend the lease while a run lasts,
/// then advance the cursor (only with the claim: a worker that lost its lease advances nothing).
/// </summary>
public sealed class JobRunner(IJobRows rows, IReadOnlyDictionary<string, JobSpec> specs, TimeProvider time)
{
    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(30);
    public bool Heartbeat { get; init; } = true;
    public int Batch { get; init; } = 16;

    public async Task<IReadOnlyList<JobAttempt>> RunOnce(CancellationToken ct = default)
    {
        var claimed = await rows.Claim(Batch, Lease, ct);
        var done = new List<JobAttempt>(claimed.Count);
        foreach (var row in claimed)
            done.Add(await Handle(row, ct));
        return done;
    }

    public async Task Run(TimeSpan poll, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if ((await RunOnce(ct)).Count == 0)
                    await Task.Delay(poll, time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                try { await Task.Delay(poll * 5, time, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>The occurrences a claim runs: the cursor and every later one up to now, by policy.</summary>
    public static (List<DateTimeOffset> Run, DateTimeOffset Next) Pick(JobSpec spec, DateTimeOffset cursor, DateTimeOffset now)
    {
        var due = new List<DateTimeOffset> { cursor };
        due.AddRange(Schedules.Between(spec.Schedule, cursor, now, spec.Zone).Take(spec.CatchUpCap + 1));
        switch (spec.Policy)
        {
            case Missed.All:
                if (due.Count > spec.CatchUpCap)
                    return (due.Take(spec.CatchUpCap).ToList(), due[spec.CatchUpCap]);          // the rest in the next claim
                return (due, spec.Schedule.Next(due[^1], spec.Zone)!.Value);
            case Missed.Latest:
                return ([due[^1]], spec.Schedule.Next(now, spec.Zone)!.Value);
            default:
                var last = due[^1];
                return (now - last <= spec.Grace ? [last] : [], spec.Schedule.Next(now, spec.Zone)!.Value);
        }
    }

    private async Task<JobAttempt> Handle(JobRow row, CancellationToken ct)
    {
        var spec = specs[row.Name];
        var (run, next) = Pick(spec, row.NextAt, time.GetUtcNow());
        using var beat = new CancellationTokenSource();
        var heartbeat = Heartbeat ? Beat(row, beat.Token) : Task.CompletedTask;
        var ran = new List<DateTimeOffset>();
        string? error = null;
        var lost = false;
        try
        {
            for (var i = 0; i < run.Count && !lost; i++)
            {
                var occurrence = run[i];
                Option<Id<ITenant>> tenant = row.Tenant == Guid.Empty ? Option.None : (Id<ITenant>)row.Tenant;
                Option<IdempotencyKey> key = spec.UseKey
                    ? (Option<IdempotencyKey>)new IdempotencyKey(tenant, "job:" + spec.Name, occurrence.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                    : Option.None;
                var result = await spec.Body(new JobRun(tenant, spec.Name, occurrence, Lab.System(spec.Name, tenant, time), key), ct);
                if (result.TryGetValue(out Violation? v)) { error = v!.ToString(); break; }
                ran.Add(occurrence);
                if (run.Count > 1)                                           // catch-up: keep the progress of every occurrence
                {
                    var cursor = i + 1 < run.Count ? run[i + 1] : next;
                    lost = !await rows.Move(row, cursor, CancellationToken.None);
                    row = row with { NextAt = cursor };
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { error = e.GetType().Name + ": " + e.Message; }
        finally
        {
            await beat.CancelAsync();
            await heartbeat;
        }
        if (lost)
            return new JobAttempt(row, ran, false, "claim lost");
        if (error is null)
            return new JobAttempt(row, ran, await rows.Advance(row, next, CancellationToken.None), null);
        if (ran.Count > 0)                                                   // keep what ran: advance to the failed occurrence
            _ = await rows.Advance(row, run[ran.Count], CancellationToken.None);
        else if (row.Attempts >= spec.MaxAttempts)
            _ = await rows.Advance(row, next, CancellationToken.None);       // give the occurrence up (a dead letter in the design)
        else
            _ = await rows.Retry(row, time.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, row.Attempts - 1))), error, CancellationToken.None);
        return new JobAttempt(row, ran, false, error);
    }

    private async Task Beat(JobRow row, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(Lease / 3, time, stop);
                if (!await rows.Extend(row, Lease, CancellationToken.None)) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (NpgsqlException) { }
    }
}

/// <summary>Variant B: one leader per database by a session advisory lock; the leader runs a <see cref="JobRunner"/> over <see cref="LeaderJobRows"/>.</summary>
public sealed class Leader(NpgsqlDataSource db, string schema, IReadOnlyDictionary<string, JobSpec> specs, TimeProvider time)
{
    public bool IsLeader { get; private set; }

    public async Task Run(TimeSpan poll, CancellationToken ct)
    {
        var runner = new JobRunner(new LeaderJobRows(db, schema, time), specs, time) { Heartbeat = false };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var lockConnection = await db.OpenConnectionAsync(ct);
                await using (var tryLock = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext($1))", lockConnection))
                {
                    tryLock.Parameters.Add(new() { Value = schema + ".jobs" });
                    IsLeader = (bool)(await tryLock.ExecuteScalarAsync(ct))!;
                }
                if (!IsLeader) { await lockConnection.CloseAsync(); await Task.Delay(poll, time, ct); continue; }
                while (!ct.IsCancellationRequested)
                {
                    await using (var alive = new NpgsqlCommand("SELECT 1", lockConnection))
                        await alive.ExecuteScalarAsync(ct);                  // the lock lives as long as this session
                    if ((await runner.RunOnce(ct)).Count == 0)
                        await Task.Delay(poll, time, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                IsLeader = false;
                try { await Task.Delay(poll * 5, time, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
