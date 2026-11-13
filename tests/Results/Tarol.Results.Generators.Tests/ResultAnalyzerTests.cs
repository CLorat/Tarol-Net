namespace Tarol.Results.Generators.Tests;

// Line numbers are 1-based relative to the test source (the host prepends one line of usings).
public class ResultAnalyzerTests
{
    [Fact]
    public async Task TRES001_Should_Report_SpelledAmbiguousTypes()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                Result<Violation> A() => default;
                Result<Error> B() => default;
                Result<object> D() => default;
                Result<Result<int>> E() => default;
                Result<Result> F() => default;
            }
            """);

        diagnostics.ShouldBe(["TRES001 3", "TRES001 4", "TRES001 5", "TRES001 6", "TRES001 7"]);
    }

    [Fact]
    public async Task TRES001_Should_Report_InferredTypeArguments()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                void M(Error error)
                {
                    var a = Result.Success(error);
                    var b = Result.Success(new object());
                    var ok = Result.Success(5);
                }
            }
            """);

        diagnostics.ShouldBe(["TRES001 5", "TRES001 6"]);
    }

    [Fact]
    public async Task TRES001_Should_NotReport_OrdinaryOrGenericValues()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            record User(string Name);
            class C
            {
                Result<int> A() => 1;
                Result<User> B() => new User("x");
                Result<(int, string)> D() => (1, "a");
                Result<T> E<T>(T value) where T : notnull => value;
                Result<FieldError> F() => new FieldError("a", "b");
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task TRES002_Should_Report_DiscardedResults()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                Result Save() => Result.Success();
                Result<int> Load() => 1;
                ValueTask<Result> Commit(CancellationToken ct) => ValueTask.FromResult(Result.Success());
                Task<Result<int>> Fetch() => Task.FromResult<Result<int>>(1);
                async Task M(CancellationToken ct)
                {
                    Save();
                    Load();
                    await Commit(ct);
                    await Commit(ct).ConfigureAwait(false);
                    await Fetch();
                    Commit(ct);
                    Load().Map(static v => v + 1);
                }
            }
            """);

        diagnostics.ShouldBe(["TRES002 9", "TRES002 10", "TRES002 11", "TRES002 12", "TRES002 13", "TRES002 14", "TRES002 15"]);
    }

    [Fact]
    public async Task TRES002_Should_NotReport_HandledResultsAndTaps()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                Result Save() => Result.Success();
                Result<int> Load() => 1;
                async Task<Result> M(CancellationToken ct)
                {
                    _ = Save();
                    var loaded = Load();
                    loaded.Tap(static v => Console.WriteLine(v));
                    loaded.TapFailure(static v => Console.WriteLine(v));
                    await loaded.TapAsync(static (v, _) => ValueTask.CompletedTask, ct);
                    if (Save() is Violation v) return v;
                    return Save();
                }
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task TRES003_Should_Report_TheObjectValue()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                object A(Result<int> r) => r.Value;
                object B(Result r) => r.Value;
                int D(Result<int> r) => r switch { int v => v, Violation => 0 };
                int E(Result<int> r) => r.GetValueOrThrow();
                int F(int? n) => n.Value;
            }
            """);

        diagnostics.ShouldBe(["TRES003 3", "TRES003 4"]);
    }

    [Fact]
    public async Task TRES003_Should_NotReport_OptionValue()
    {
        // Option<T>.Value is TOPT001 in Tarol.Options; the Result analyzer must not report it twice.
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                object A(Tarol.Options.Option<int> o) => o.Value;
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
                int A(Result<int> r) { r.TryGetValue(out int v); return v; }
                int B(Result<int> r) => r.TryGetValue(out int v) ? v : 0;
                int D(Tarol.Options.Option<int> o) { o.TryGetValue(out int v); return v; }
            }
            """);

        diagnostics.ShouldBe(["TRES005 3"]);
    }

    [Fact]
    public async Task TRES006_Should_Report_DefaultThatBecomesASuccess()
    {
        var diagnostics = await AnalyzerTestHost.Analyze("""
            class C
            {
                Result<int> A(bool b, int v) => b ? v : default;
                Result<int> B(int? v) => v ?? default;
                Result<int> D(bool b, int v) => b ? v : Error.NotFound("x");
                Tarol.Options.Option<int> E(bool b, int v) => b ? v : default;
            }
            """);

        diagnostics.ShouldBe(["TRES006 3", "TRES006 4"]);
    }

    [Fact]
    public async Task Analyzer_Should_DoNothing_WithoutTarolResults()
    {
        var analyzer = new ResultAnalyzer();
        analyzer.SupportedDiagnostics.Select(static d => d.Id).ShouldBe(["TRES001", "TRES002", "TRES003", "TRES005", "TRES006"]);
        (await AnalyzerTestHost.Analyze("class Plain { void M() { } }")).ShouldBeEmpty();
    }
}
