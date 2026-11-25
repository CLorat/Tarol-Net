using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tarol.Changes;
using Tarol.Jobs;
using Tarol.Jobs.AotSmoke;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
        failures++;
}

var vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
var spring = Cron.Parse("30 2 * * *").After(new DateTimeOffset(2027, 3, 28, 0, 0, 0, TimeSpan.FromHours(1)), vienna);
Check("cron: a skipped time runs at the end of the gap", spring == new DateTimeOffset(2027, 3, 28, 3, 0, 0, TimeSpan.FromHours(2)));
var fall = Cron.Parse("0 * * * *").After(new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(2)), vienna);
Check("cron: a repeated hour runs once", fall == new DateTimeOffset(2026, 10, 25, 3, 0, 0, TimeSpan.FromHours(1)));
Check("cron: day-of-month or day-of-week", Cron.Parse("0 2 13 * 5").After(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc).Day == 6);
Check("cron: invalid expressions are refused", !Cron.TryParse("0 0 2 * * *", out _, out _));
// FB-WIN-1: InvariantGlobalization (this app) has no ICU to convert ids; a Windows id still resolves on Linux and vice versa
Check("zones: a Windows id and an IANA id resolve without ICU",
    Tarol.Kernel.TimeZones.Find("W. Europe Standard Time").GetUtcOffset(new DateTime(2026, 7, 1)) == TimeSpan.FromHours(2)
    && Tarol.Kernel.TimeZones.Find("Europe/Vienna").GetUtcOffset(new DateTime(2026, 1, 1)) == TimeSpan.FromHours(1));

var test = JobTest.At(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), GeneratedJobs.Set, stateOptions: SmokeJson.Default.Options);
var counter = new BookCounter();
test.Add(new OpenBook(counter), "0 * * * *", o => o.Missed = Missed.All);
var runs = await test.Advance(TimeSpan.FromHours(2));
Check("JobTest: the generated entry and the class ran", runs.Count(r => r.Job == "CloseDay") == 1 && runs.Count(r => r.Job == "OpenBook") == 2);
var closed = test.Store.Recorded.Where(r => r.Event.Event is DayBook.Closed).ToList();
Check("JobTest: run as the system with the occurrence key", closed.Count == 3 && closed.All(r => r.Context.Actor.Id.StartsWith("Jobs.", StringComparison.Ordinal)));
Check("JobTest: All runs every missed hour", (await test.Down(TimeSpan.FromHours(5)).Advance(TimeSpan.Zero)).Count(r => r.Job == "OpenBook") == 5);

// The database check needs a PostgreSQL: TAROL_PG=<connection string> (the CI aot-smoke job provides one).
var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrEmpty(connectionString))
    Console.WriteLine("skip database checks: TAROL_PG is not set");
else
{
    var services = new ServiceCollection();
    services.AddSingleton<BookCounter>();
    var schema = "jobs_smoke_" + Environment.ProcessId;
    services.AddTarol(SmokeJson.Default, codec => codec.Add<DayBook>())
        .UseNpgsqlStore(connectionString, new NpgsqlStoreOptions { Schema = schema })
        .AddJobs(j => j.Add<OpenBook>(Schedule.Every(TimeSpan.FromSeconds(1))), o => o.PollInterval = TimeSpan.FromMilliseconds(100));
    await using var provider = services.BuildServiceProvider();
    var hosts = provider.GetServices<IHostedService>().ToList();
    foreach (var host in hosts)
        await host.StartAsync(default);
    var books = provider.GetRequiredService<BookCounter>();
    for (var i = 0; i < 100 && books.Runs < 3; i++)
        await Task.Delay(100);
    foreach (var host in hosts)
        await host.StopAsync(default);
    var rows = await provider.GetRequiredService<IJobRows>().Rows(default);
    Check("Npgsql: the host ran an occurrence every second", books.Runs >= 3);
    Check("Npgsql: the job's row and the key purge's system job", rows.Select(r => r.Name).Order(StringComparer.Ordinal).SequenceEqual(["OpenBook", "tarol.purge-keys"]));
    var store = provider.GetRequiredService<NpgsqlAggregateStore>();
    var keys = 0;
    await foreach (var e in store.ReadAll(default, 1000, default))
        if (e.Event.Event.Event is DayBook.Closed)
            keys++;
    Check("Npgsql: one commit per occurrence", keys == books.Runs);
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
