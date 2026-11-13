namespace Tarol.Options.Generators.Tests;

// Line numbers are 1-based relative to the test source (the host prepends one line of usings).
public class OptionAnalyzerTests
{
    [Fact]
    public async Task TOPT001_Should_Report_OptionValue()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                object A(Option<int> o) => o.Value;
                bool B(Option<int> o) => o.IsSome;
            }
            """);

        diagnostics.ShouldBe(["TOPT001 3"]);
    }

    [Fact]
    public async Task TOPT002_Should_Report_NullAndEmptyPropertyPatterns()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            record User(string Name);
            class C
            {
                bool A(Option<User> o) => o is { };
                bool B(Option<User> o) => o is not null;
                bool D(Option<User> o) => o is null;
                int E(Option<User> o) => o is { } x ? 1 : 0;
                bool F(Option<int> o) => o != null;
                bool G(Option<int> o) => null == o;
            }
            """);

        diagnostics.ShouldBe(["TOPT002 4", "TOPT002 5", "TOPT002 6", "TOPT002 7", "TOPT002 8", "TOPT002 9"]);
    }

    [Fact]
    public async Task TOPT002_Should_NotReport_CasePatternsOrNullablePatterns()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            record User(string Name);
            class C
            {
                bool A(Option<User> o) => o is User;
                string B(Option<User> o) => o is User { Name: var n } ? n : "";
                bool D(Option<User> o) => o is None;
                int E(Option<int> o) => o switch { int v => v, None => 0 };
                bool F(User? u) => u is { };
                bool G(Option<int> a, Option<int> b) => a == b;
                bool H(Option<User> o) => o.IsSome;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task TOPT003_Should_Report_IgnoredTryGetValue()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                int A(Option<int> o) { o.TryGetValue(out int v); return v; }
                int B(Option<string> o) { o.TryGetValue(out string? v); return v?.Length ?? 0; }
                int D(Option<int> o) => o.TryGetValue(out int v) ? v : 0;
                int E(Option<int> o) { _ = o.TryGetValue(out int v); return v; }
            }
            """);

        diagnostics.ShouldBe(["TOPT003 3", "TOPT003 4"]);
    }

    [Fact]
    public async Task TOPT004_Should_Report_DefaultThatBecomesAValue()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                Option<int> A(bool b, int v) => b ? v : default;
                Option<long> B(bool b, long v) => b ? v : default;
                Option<int> D(int? v) => v ?? default;
                Option<int> E() => default(int);
                Option<int> F(bool b, int v) => b ? default : v;
            }
            """);

        diagnostics.ShouldBe(["TOPT004 3", "TOPT004 4", "TOPT004 5", "TOPT004 6", "TOPT004 7"]);
    }

    [Fact]
    public async Task TOPT004_Should_NotReport_TargetTypedAbsenceOrDefaultOption()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                Option<int> A(bool b, int v) => b ? v : Option.None;
                Option<int> B() => default;
                Option<int> D(bool b, Option<int> v) => b ? v : default;
                Option<int> E(bool b, int v) => b ? Option.Some(v) : Option.None;
                Option<int> F(int v) => v;
                int G(bool b, int v) => b ? v : default;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Analyzer_Should_DeclareExactlyTheOptionRules()
    {
        var analyzer = new OptionAnalyzer();
        analyzer.SupportedDiagnostics.Select(static d => d.Id).ShouldBe(["TOPT001", "TOPT002", "TOPT003", "TOPT004"]);
    }
}
