namespace Tarol.Options.Generators.Tests;

// The compiler behaviour Option<T> relies on for "absence must be handled" (docs/migration/options.md §3.1).
// Line numbers are 1-based relative to the test source.
public class OptionCompilerTests
{
    private const string Types = "record User(string Name);\n";

    [Fact]
    public void UsingAnOptionAsItsValue_Should_BeACompileError()
    {
        var diagnostics = AnalyzerTestHost.CompilerDiagnostics(Types + """
            class C
            {
                static void Take(User u) { }
                int A(Option<int> o) { int v = o; return v; }
                int B(Option<string> o) => o.Length;
                void D(Option<User> o) => Take(o);
            }
            """);

        diagnostics.ShouldBe(["CS0029 5", "CS1061 6", "CS1503 7"]);
    }

    [Fact]
    public void SwitchWithoutNone_Should_BeCS8509_ForValueAndReferenceTypes()
    {
        var diagnostics = AnalyzerTestHost.CompilerDiagnostics(Types + """
            class C
            {
                int A(Option<int> o) => o switch { int v => v };
                string B(Option<User> o) => o switch { User u => u.Name };
                int D(Option<int> o) => o switch { int v => v, None => 0 };
            }
            """);

        diagnostics.ShouldBe(["CS8509 4", "CS8509 5"]);
    }

    [Fact]
    public void Enforcement_Should_NotDependOnTheNullableContext()
    {
        var diagnostics = AnalyzerTestHost.CompilerDiagnostics(Types + """
            class C
            {
                int A(Option<int> o) => o switch { int v => v };
                void B(Option<string> o) { string s = o; }
            }
            """, nullable: false);

        diagnostics.ShouldBe(["CS8509 4", "CS0029 5"]);
    }

    [Fact]
    public void NullableTypeArgumentsAndNullValues_Should_Warn()
    {
        var diagnostics = AnalyzerTestHost.CompilerDiagnostics(Types + """
            class C
            {
                Option<string?> A() => default;
                Option<int?> B() => default;
                Option<string> D(string? s) => s;
                Option<string> E(string? s) => s.ToOption();
            }
            """);

        diagnostics.ShouldBe(["CS8714 4", "CS8714 5", "CS8604 6"]);
    }
}
