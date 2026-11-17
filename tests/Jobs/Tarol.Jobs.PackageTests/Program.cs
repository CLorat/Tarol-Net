using Tarol.Jobs;
using Tarol.Jobs.PackageTests;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
        failures++;
}

Check("generated job set from the package", GeneratedJobs.Set.Definitions is [{ Name: "CloseDay", Cron: "0 2 * * *", TimeZone: "Europe/Vienna" }]);
var test = JobTest.At(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), GeneratedJobs.Set, stateOptions: SmokeJson.Default.Options);
test.Add(new OpenBook(new BookCounter()), "0 * * * *");
var runs = await test.Advance(TimeSpan.FromHours(2));
Check("JobTest from the package", runs.Count(r => r.Job == "CloseDay") == 1 && runs.Count(r => r.Job == "OpenBook") == 2);
Check("cron from the package", Cron.Parse("0 2 * * *").Text == "0 2 * * *");

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
