using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tarol.Primitives.Generators.Tests;

/// <summary>Compiles test sources against the real runtime packages and runs the generator and the analyzer.</summary>
internal static class GeneratorTestHost
{
    // Roslyn 5.9 treats unions (Tarol.Results, Tarol.Options) as preview (results.md §8).
    public static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    private static readonly IReadOnlyList<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat([
                typeof(AggregateRoot<>).Assembly.Location,
                typeof(Tarol.Results.Result).Assembly.Location,
                typeof(Tarol.Options.None).Assembly.Location,
                typeof(Tarol.Kernel.Actor).Assembly.Location,
                typeof(Tarol.StronglyTypedId.IStronglyTypedId).Assembly.Location])
            .Where(static path => !Path.GetFileName(path).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                                  && !Path.GetFileName(path).StartsWith("Tarol.Primitives.Generators", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
    ];

    public static CSharpCompilation Compile(params string[] sources)
        => CSharpCompilation.Create(
            "GeneratorTestAssembly",
            sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, Parse, path: $"Source{i}.cs")),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static GeneratorDriver Run(CSharpCompilation compilation)
        => CSharpGeneratorDriver
            .Create(
                [new AggregateGenerator().AsSourceGenerator()],
                parseOptions: Parse,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true))
            .RunGenerators(compilation);

    /// <summary>Generator diagnostics, analyzer diagnostics and compiler errors and warnings of the generated compilation.</summary>
    public static ImmutableArray<Diagnostic> Diagnose(params string[] sources)
    {
        var compilation = Compile(sources);
        _ = Run(compilation).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        var analyzer = output.WithAnalyzers([new DomainAnalyzer()]).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        var compiler = output.GetDiagnostics().Where(static d => d.Severity >= DiagnosticSeverity.Warning);
        return [.. generatorDiagnostics, .. analyzer, .. compiler];
    }

    /// <summary>
    /// Compiler warnings of the generated compilation after the suppressors ran: each with whether TPRMSPR01 suppressed it.
    /// </summary>
    public static ImmutableArray<(string Id, bool Suppressed)> CompilerWarningsWithSuppressor(params string[] sources)
    {
        var compilation = Compile(sources);
        _ = Run(compilation).RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null, concurrentAnalysis: false, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true);
        var all = output.WithAnalyzers([new LifecycleSwitchSuppressor()], options).GetAllDiagnosticsAsync().GetAwaiter().GetResult();
        return [.. all.Where(static d => d.Id.StartsWith("CS", StringComparison.Ordinal) && d.Severity >= DiagnosticSeverity.Warning)
            .Select(static d => (d.Id, d.IsSuppressed)).OrderBy(static d => d.Id, StringComparer.Ordinal)];
    }

    public static string[] Ids(this ImmutableArray<Diagnostic> diagnostics)
        => [.. diagnostics.Select(static d => d.Id).Order(StringComparer.Ordinal)];

    public static string Generated(this GeneratorDriver driver, string hintSuffix)
        => driver.GetRunResult().Results.Single().GeneratedSources.Single(s => s.HintName.EndsWith(hintSuffix, StringComparison.Ordinal)).SourceText.ToString();
}
