using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence.Contract;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql.IntegrationTests;

/// <summary>The job rows contract on PostgreSQL, a schema per test.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlJobRowsContractTests(Postgres postgres) : JobRowsContract
{
    protected override async Task<IJobRows> Create(TimeProvider time) => (await postgres.IsolatedStore(time)).Jobs;
}

/// <summary>The job-rows contract on upgraded version 1 schemas.</summary>
[Trait("Category", "Integration")]
public sealed class UpgradedJobRowsContractTests(Postgres postgres) : JobRowsContract
{
    protected override async Task<IJobRows> Create(TimeProvider time) => (await postgres.UpgradedIsolatedStore(time)).Jobs;
}

/// <summary>What only the PostgreSQL rows can show: racing claimers, the stored precision, crashes, concurrent syncs.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlJobRowsTests(Postgres postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 1, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_script_creates_the_job_tables_once()
    {
        var store = await postgres.IsolatedStore(TimeProvider.System);

        await store.EnsureSchema(Ct);                                                  // idempotent

        (await postgres.Scalar($"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{store.Schema}' AND table_name IN ('jobs', 'jobs_dead')"))
            .ShouldBe(2L);
        (await postgres.Scalar($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{store.Schema}' AND indexname IN ('jobs_due', 'jobs_dead_redrive')"))
            .ShouldBe(2L);
    }

    [Fact]
    public async Task Racing_claimers_get_one_row_each_and_never_the_same()
    {
        var time = new FakeTimeProvider(Start);
        var rows = (await postgres.IsolatedStore(time)).Jobs;
        _ = await rows.Sync([.. Enumerable.Range(0, 5).Select(i => new JobSchedule("Job" + i, Option.None, "s", true, Start))], Ct);

        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => rows.Claim(Lease, Ct), Ct)));

        var won = claims.Where(c => c.TryGetValue(out JobRow? _)).Select(c => c.GetValueOrThrow().Name).ToList();
        won.Count.ShouldBe(5);
        won.Distinct(StringComparer.Ordinal).Count().ShouldBe(5);
    }

    [Fact]
    public async Task A_whole_second_cursor_reads_back_unchanged_and_a_finer_one_is_refused()
    {
        var time = new FakeTimeProvider(Start);
        var store = await postgres.IsolatedStore(time);
        var at = new DateTimeOffset(2026, 11, 16, 0, 59, 59, TimeSpan.Zero);
        _ = await store.Jobs.Sync([new JobSchedule("Nightly", Option.None, "s", true, at)], Ct);
        var row = (await store.Jobs.Claim(Lease, Ct)).GetValueOrThrow();

        row.NextAt.UtcTicks.ShouldBe(at.UtcTicks);
        var key = row.NextAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        key.ShouldBe(at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));       // the key text of the cursor read back is the occurrence's
        (await postgres.Scalar($"SELECT next_at = '2026-11-16T00:59:59Z'::timestamptz FROM {store.Schema}.jobs")).ShouldBe(true);
        // the probe's failure (jobs.md §5 item 1): 100 ns ticks are lost by timestamptz, so the store refuses them
        await Should.ThrowAsync<ArgumentException>(() => store.Jobs.Move(row, at.AddTicks(1793033), Ct));
    }

    [Fact]
    public async Task A_crash_between_the_commit_and_the_move_replays_the_key()
    {
        var time = new FakeTimeProvider(Start);
        var store = await postgres.IsolatedStore(time);
        _ = await store.Jobs.Sync([new JobSchedule("Nightly", Option.None, "s", true, Start)], Ct);
        var first = (await store.Jobs.Claim(Lease, Ct)).GetValueOrThrow();
        var key = new IdempotencyKey(Option.None, "job:Nightly", first.Occurrence.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        var context = OperationContext.Start(Actor.System("Jobs.Nightly"), Option.None, time);
        (await store.Append(Member.Join(Id<Member>.New(), "a"), 0, context, key, Ct)).IsSuccess.ShouldBeTrue();
        // the worker dies here: no Move, no Advance

        time.Advance(Lease);
        var second = (await store.Jobs.Claim(Lease, Ct)).GetValueOrThrow();

        second.Occurrence.ShouldBe(first.Occurrence);                                  // the cursor did not move
        (await store.Seen(key, Ct)).TryGetValue(out StoredOutcome _).ShouldBeTrue();   // the runner replays instead of committing
        (await store.Append(Member.Join(Id<Member>.New(), "b"), 0, context, key, Ct)).IsSuccess.ShouldBeFalse();
        (await postgres.Scalar($"SELECT count(*) FROM {store.Schema}.streams")).ShouldBe(1L);
        (await store.Jobs.Advance(first, Start.AddDays(1), Ct)).ShouldBeFalse();
        (await store.Jobs.Advance(second, Start.AddDays(1), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_syncs_of_starting_hosts_add_each_row_once()
    {
        var store = await postgres.IsolatedStore(TimeProvider.System);
        JobSchedule[] declared = [.. Enumerable.Range(0, 50).Select(i => new JobSchedule("Job" + i, Option.None, "s", true, Start))];

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => store.Jobs.Sync(declared, Ct), Ct)));

        results.Sum(r => r.Count(c => c.Kind == JobSyncKind.Added)).ShouldBe(50);    // one host added them, the others saw them
        (await store.Jobs.Rows(Ct)).Count.ShouldBe(50);
    }
}

/// <summary>
/// Chaos (jobs.md §11, 14B): two worker processes claim 3 jobs that fire every second; the test kills a random worker with
/// -9 every 0.5–1.5 s, and each worker crashes right after every 7th commit, before its cursor moves. Bounded by
/// occurrences, never by a wall-clock count: it runs until every job's cursor passed 25 occurrences.
/// Then every occurrence before the cursor committed exactly once, and nothing after it (but the one a kill interrupted).
/// </summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlJobRowsChaosTests(Postgres postgres)
{
    private const int Jobs = 3;
    private const int Occurrences = 25;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Kills_and_crashes_cost_no_gap_and_no_second_commit()
    {
        var store = await postgres.IsolatedStore(TimeProvider.System);
        var first = DateTimeOffset.FromUnixTimeSeconds(TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() + 1);
        _ = await store.Jobs.Sync([.. Enumerable.Range(0, Jobs).Select(i => new JobSchedule("Chaos" + i, Option.None, "every second", true, first))], Ct);
        var workers = new Process?[2];
        var random = new Random(14);
        var kills = 0;
        var restarts = 0;
        var deadline = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                for (var i = 0; i < workers.Length; i++)
                    if (workers[i] is not { HasExited: false })
                    {
                        workers[i]?.Dispose();
                        workers[i] = StartWorker(store.Schema);
                        restarts++;
                    }
                var cursors = await store.Jobs.Rows(Ct);
                if (cursors.All(r => r.NextAt >= first.AddSeconds(Occurrences)))
                    break;
                deadline.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(3), $"stuck at {string.Join(", ", cursors.Select(c => c.NextAt))}");
                await Task.Delay(TimeSpan.FromMilliseconds(500 + random.Next(1000)), Ct);
                var victim = workers[random.Next(workers.Length)];
                if (victim is { HasExited: false })
                {
                    victim.Kill();                                                    // SIGKILL
                    kills++;
                }
            }
        }
        finally
        {
            foreach (var worker in workers)
            {
                if (worker is { HasExited: false })
                    worker.Kill();
                worker?.WaitForExit();
                worker?.Dispose();
            }
        }

        var rows = await store.Jobs.Rows(Ct);
        for (var job = 0; job < Jobs; job++)
        {
            var name = "Chaos" + job;
            var cursor = rows.Single(r => r.Name == name).NextAt;
            var committed = await Committed(store.Schema, name);
            var expected = Enumerable.Range(0, (int)(cursor - first).TotalSeconds).Select(s => Text(first.AddSeconds(s))).ToList();
            committed.Where(k => string.CompareOrdinal(k, Text(cursor)) < 0).ShouldBe(expected, $"{name}: a gap or an extra commit before the cursor");
            committed.Count(k => string.CompareOrdinal(k, Text(cursor)) >= 0).ShouldBeLessThanOrEqualTo(1);   // committed, killed before its Move
        }
        (await postgres.Scalar($"SELECT count(*) FROM {store.Schema}.streams")).ShouldBe(
            await postgres.Scalar($"SELECT count(*) FROM {store.Schema}.idempotency_keys"));                  // one commit per key
        TestContext.Current.TestOutputHelper?.WriteLine($"chaos: {kills} kills, {restarts - 2 - kills} crashes after a commit, {Jobs * Occurrences} occurrences checked, {deadline.Elapsed.TotalSeconds:F0} s");
        kills.ShouldBeGreaterThan(0);
        restarts.ShouldBeGreaterThan(kills + 2);                                       // the workers crashed after commits too
    }

    private static string Text(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private async Task<List<string>> Committed(string schema, string name)
    {
        await using var command = postgres.DataSource.CreateCommand($"SELECT key FROM {schema}.idempotency_keys WHERE scope = $1 ORDER BY key");
        command.Parameters.Add(new global::Npgsql.NpgsqlParameter { Value = "job:" + name });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var keys = new List<string>();
        while (await reader.ReadAsync(Ct))
            keys.Add(reader.GetString(0));
        return keys;
    }

    private Process StartWorker(string schema)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;          // artifacts/bin/<project>/<configuration>/
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Tarol.Persistence.Npgsql.ChaosWorker", configuration,
            "Tarol.Persistence.Npgsql.ChaosWorker.dll"));
        File.Exists(dll).ShouldBeTrue(dll);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { dll, postgres.ConnectionString, schema, "7" })
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        var process = Process.Start(start)!;
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();
        process.StandardOutput.ReadLine().ShouldBe("ready");
        return process;
    }
}
