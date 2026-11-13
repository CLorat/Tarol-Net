namespace Tarol.Results.Generators.Tests;

// Line numbers are 1-based relative to the test source (the host prepends one line of usings).
public class OptionAnalyzerTests
{
    [Fact]
    public async Task TRES003_Should_Report_OptionValue()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                object A(Option<int> o) => o.Value;
                bool B(Option<int> o) => o.IsSome;
            }
            """);

        diagnostics.ShouldBe(["TRES003 3"]);
    }

    [Fact]
    public async Task TRES004_Should_Report_NullAndEmptyPropertyPatterns()
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

        diagnostics.ShouldBe(["TRES004 4", "TRES004 5", "TRES004 6", "TRES004 7", "TRES004 8", "TRES004 9"]);
    }

    [Fact]
    public async Task TRES004_Should_NotReport_CasePatternsOrNullablePatterns()
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
    public async Task TRES005_Should_Report_IgnoredTryGetValue()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                int A(Option<int> o) { o.TryGetValue(out int v); return v; }
                int B(Result<int> r) { r.TryGetValue(out int v); return v; }
                int D(Option<int> o) => o.TryGetValue(out int v) ? v : 0;
                int E(Option<int> o) { _ = o.TryGetValue(out int v); return v; }
            }
            """);

        diagnostics.ShouldBe(["TRES005 3", "TRES005 4"]);
    }

    [Fact]
    public async Task TRES006_Should_Report_DefaultThatBecomesAValue()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                Option<int> A(bool b, int v) => b ? v : default;
                Result<int> B(bool b, int v) => b ? v : default;
                Option<int> D(int? v) => v ?? default;
                Option<int> E() => default(int);
                Option<int> F(bool b, int v) => b ? default : v;
            }
            """);

        diagnostics.ShouldBe(["TRES006 3", "TRES006 4", "TRES006 5", "TRES006 6", "TRES006 7"]);
    }

    [Fact]
    public async Task TRES006_Should_NotReport_TargetTypedAbsenceOrDefaultOption()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                Option<int> A(bool b, int v) => b ? v : Option.None;
                Option<int> B() => default;
                Option<int> D(bool b, Option<int> v) => b ? v : default;
                Result<int> E(bool b, int v) => b ? v : Error.NotFound("x");
                Option<int> F(int v) => v;
                int G(bool b, int v) => b ? v : default;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }
}
