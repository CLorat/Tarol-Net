using Tarol.Changes;

namespace Tarol.Jobs.Tests;

/// <summary>The test example of skills/tarol-jobs/SKILL.md, verbatim (CloseDay and the domain are in Domain.cs, TickJob in JobTestTests.cs).</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_test_example_runs()
    {
        // test
        var test = JobTest.At(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), GeneratedJobs.Set, stateOptions: AppJson.Default.Options)
            .Given(Counter.Start(Counter.Main))
            .Add(new TickJob(), "0 * * * *", o => { o.Name = "Ticks"; o.Missed = Missed.All; });
        var runs = await test.Advance(TimeSpan.FromHours(2));      // CloseDay at 01:00Z (02:00 Vienna), Ticks at 01:00 and 02:00
        var missed = await test.Down(TimeSpan.FromDays(1)).Advance(TimeSpan.Zero);   // down a day: Ticks 24 (All), CloseDay 1 (Latest)

        runs.Select(r => r.Job).Order(StringComparer.Ordinal).ShouldBe(["CloseDay", "Ticks", "Ticks"]);
        missed.Count(r => r.Job == "Ticks").ShouldBe(24);
        missed.Count(r => r.Job == "CloseDay").ShouldBe(1);
        test.Load(Counter.Main).Ticks.Count.ShouldBe(26);
    }

    private static class AppJson
    {
        public static TestJson Default => TestJson.Default;
    }
}
