using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tarol.Options.Generators.Tests;

internal static class AnalyzerTestHost
{
    private static readonly IReadOnlyList<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(static path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) == "netstandard.dll")
        .Append(typeof(Option).Assembly.Location)
        .Distinct()
        .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    /// <summary>Compiles <paramref name="source"/> against Tarol.Options and returns the compiler's own diagnostics (no analyzer) as "ID line" strings.</summary>
    public static IReadOnlyList<string> CompilerDiagnostics(string source, bool nullable = true)
        => Compile(source, nullable).GetDiagnostics()
            .Where(static d => d.Severity >= DiagnosticSeverity.Warning)
            .OrderBy(static d => d.Location.SourceSpan.Start)
            .Select(static d => $"{d.Id} {d.Location.GetLineSpan().StartLinePosition.Line}")
            .ToArray();

    private static CSharpCompilation Compile(string source, bool nullable = true)
        => CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            [CSharpSyntaxTree.ParseText("using System; using System.Threading; using System.Threading.Tasks; using Tarol.Options;\n" + source,
                // Roslyn 5.9 (the newest NuGet package) still flags unions as preview; the SDK 11 compiler (5.12) ships them.
                new CSharpParseOptions(LanguageVersion.Preview))],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: nullable ? NullableContextOptions.Enable : NullableContextOptions.Disable));

    /// <summary>Compiles <paramref name="source"/> against Tarol.Options and returns the analyzer's diagnostics as "ID line" strings.</summary>
    public static async Task<IReadOnlyList<string>> Analyze(string source)
    {
        var compilation = Compile(source);
        var compilerErrors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error && !d.Id.StartsWith("TOPT", StringComparison.Ordinal)).ToArray();
        compilerErrors.ShouldBeEmpty(string.Join("\n", compilerErrors.Select(static d => d.ToString())));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new OptionAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return diagnostics
            .OrderBy(static d => d.Location.SourceSpan.Start)
            .Select(static d => $"{d.Id} {d.Location.GetLineSpan().StartLinePosition.Line}")
            .ToArray();
    }
}
