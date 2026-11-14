using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tarol.Validations.Generators.Tests;

/// <summary>Compiles test sources against the real runtime packages and runs the generator and the analyzer.</summary>
internal static class GeneratorTestHost
{
    // Roslyn 5.9 treats unions (Tarol.Results, Tarol.Options) as preview (results.md §8).
    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    private static readonly IReadOnlyList<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat([
                typeof(IValidatable).Assembly.Location,
                typeof(Tarol.Results.Invalid).Assembly.Location,
                typeof(Tarol.Options.None).Assembly.Location,
                typeof(Tarol.StronglyTypedId.IStronglyTypedId).Assembly.Location])
            .Where(static path => !Path.GetFileName(path).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                                  && !Path.GetFileName(path).StartsWith("Tarol.Validations.Generators", StringComparison.Ordinal))
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
                [new ValidationGenerator().AsSourceGenerator()],
                parseOptions: Parse,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true))
            .RunGenerators(compilation);

    /// <summary>Generator diagnostics, analyzer diagnostics and compiler errors of the generated compilation.</summary>
    public static ImmutableArray<Diagnostic> Diagnose(params string[] sources)
    {
        var compilation = Compile(sources);
        var driver = Run(compilation).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        _ = driver;
        var analyzed = output.WithAnalyzers([new ValidationAnalyzer()]);
        var analyzer = analyzed.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        var compiler = output.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error);
        return [.. generatorDiagnostics, .. analyzer, .. compiler];
    }

    public static string[] Ids(this ImmutableArray<Diagnostic> diagnostics)
        => [.. diagnostics.Select(static d => d.Id).Order(StringComparer.Ordinal)];

    public static string Generated(this GeneratorDriver driver, string typeName)
        => driver.GetRunResult().Results.Single().GeneratedSources
            .Single(s => s.HintName == typeName + ".Validation.g.cs" || s.HintName.EndsWith("." + typeName + ".Validation.g.cs", StringComparison.Ordinal)).SourceText.ToString();
}
