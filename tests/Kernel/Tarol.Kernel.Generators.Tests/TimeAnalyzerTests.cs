namespace Tarol.Kernel.Generators.Tests;

// Line numbers are 0-based relative to the test source (the host prepends one line of usings).
public class TimeAnalyzerTests
{
    [Fact]
    public async Task TKRN001_Should_Report_EverySystemClockRead()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                DateTime A() => DateTime.Now;
                DateTime B() => DateTime.UtcNow;
                DateTime D() => DateTime.Today;
                DateTimeOffset E() => DateTimeOffset.Now;
                DateTimeOffset F() => DateTimeOffset.UtcNow;
                bool G(DateTimeOffset endsAt) => System.DateTimeOffset.UtcNow >= endsAt;
                System.Func<DateTimeOffset> H() => () => DateTimeOffset.UtcNow;
            }
            """);

        diagnostics.ShouldBe(["TKRN001 3", "TKRN001 4", "TKRN001 5", "TKRN001 6", "TKRN001 7", "TKRN001 8", "TKRN001 9"]);
    }

    [Fact]
    public async Task TKRN001_Should_NotReport_TimeProviderOrInstanceMembers()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C(TimeProvider clock)
            {
                DateTimeOffset A() => clock.GetUtcNow();
                TimeProvider B() => TimeProvider.System;
                DateTime D(DateTime d) => d.Date;
                DateTimeOffset E(DateTimeOffset d) => d.ToUniversalTime();
                DateTime F() => DateTime.MinValue;
                DateTimeOffset G() => DateTimeOffset.UnixEpoch;
                long H(DateTimeOffset now) => now.Ticks;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Analyzer_Should_DeclareExactlyTKRN001_AsWarning()
    {
        var descriptor = new TimeAnalyzer().SupportedDiagnostics.ShouldHaveSingleItem();
        descriptor.Id.ShouldBe("TKRN001");
        descriptor.DefaultSeverity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }
}
