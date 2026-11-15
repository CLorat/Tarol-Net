using System.Diagnostics;
using ArtFlow;
using Tarol.Sagas;
using static Lab;

/// <summary>A real process dies between the reaction's commit and Complete; another worker redelivers after the lease.</summary>
internal static class Crash
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(3);

    public static async Task Parent()
    {
        await using var env = await Env.Create("crash", TimeProvider.System, DeadLetterDesign.Table);
        var alice = Alice(TimeProvider.System);
        var auction = await env.AuctionWithBid(alice);
        env.Pipeline.Run(new SettleAuction(auction), alice).GetValueOrThrow();

        var self = Environment.ProcessPath!;
        var start = Path.GetFileNameWithoutExtension(self) == "dotnet"
            ? new ProcessStartInfo(self, [Path.Combine(AppContext.BaseDirectory, "Probe.Sagas.App.dll"), "crash-child", env.Schema])
            : new ProcessStartInfo(self, ["crash-child", env.Schema]);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var child = Process.Start(start)!;
        var output = await child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync();
        Check("child ran the reaction and died before Complete", child.ExitCode != 0 && output.Contains("committed", StringComparison.Ordinal),
            $"exit code {child.ExitCode}, child said: {output.Trim().Split('\n').LastOrDefault()}");
        Check("after the crash: one payout committed, its row still claimed",
            await env.Scalar("SELECT count(*) FROM {s}.events WHERE type = 'payout'") == 1
            && await env.Scalar("SELECT count(*) FROM {s}.outbox WHERE reaction = 'MarkPayoutDue' AND attempts = 1 AND lease_until IS NOT NULL") == 1);

        var verdicts = new List<(string, int, Verdict)>();
        var worker = env.Worker(new WorkerOptions { Lease = Lease }, (r, v, _) => verdicts.Add((r.Reaction, r.Attempts, v)));
        var waited = Stopwatch.StartNew();
        while (verdicts.All(v => v.Item1 != "MarkPayoutDue") && waited.Elapsed < TimeSpan.FromSeconds(15))
        {
            _ = await worker.RunOnce(default);
            await Task.Delay(200);
        }
        Check("another worker claims it after the lease (attempt 2) and completes it", verdicts.Contains(("MarkPayoutDue", 2, Verdict.Done)),
            $"after {waited.Elapsed.TotalSeconds:0.0} s: {string.Join(",", verdicts)}");
        Check("still one payout: delivered twice, committed once", await env.Scalar("SELECT count(*) FROM {s}.events WHERE type = 'payout'") == 1);
    }

    public static async Task<int> Child(string schema)
    {
        var codec = Codec();
        var store = new Tarol.Persistence.Npgsql.NpgsqlAggregateStore(Db, codec, TimeProvider.System,
            new Tarol.Persistence.Npgsql.NpgsqlStoreOptions { Schema = schema, Reactions = GeneratedReactions.Set.Table });
        var outbox = new PgOutbox(Db, codec, TimeProvider.System, schema, DeadLetterDesign.Table);
        var row = (await outbox.Claim(10, Lease, default)).Single(r => r.Reaction == "MarkPayoutDue");
        var outcome = await GeneratedReactions.Set.Run(new Tarol.Changes.ChangePipeline(store, TimeProvider.System), row.Reaction, row.Event, default);
        Console.WriteLine($"committed {outcome.IsSuccess}");
        Console.Out.Flush();
        Environment.FailFast("probe: crash between run and Complete");
        return 0;
    }
}
