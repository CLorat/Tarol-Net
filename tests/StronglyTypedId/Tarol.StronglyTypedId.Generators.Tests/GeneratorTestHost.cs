using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tarol.StronglyTypedId.Generators.Tests;

internal static class GeneratorTestHost
{
    /// <summary>
    /// Minimal stub of the core library. The generator identifies Id&lt;T&gt; purely by
    /// metadata name + namespace, so tests don't need to compile the real library —
    /// which also keeps these tests immune to unrelated core-lib changes.
    /// </summary>
    private const string CoreLibraryStub = """
                                           namespace Tarol.StronglyTypedId
                                           {
                                               public readonly struct Id<T> { }

                                               public static class IdConverterRegistry
                                               {
                                                   public static void Register<T>() { }
                                               }
                                           }
                                           """;

    private static readonly IReadOnlyList<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(static path =>
        {
            var file = Path.GetFileName(path);
            return file is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "netstandard.dll";
        })
        .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    public static CSharpCompilation CreateCompilation(string source, string assemblyName = "GeneratorTestAssembly")
        => CSharpCompilation.Create(
            assemblyName,
            syntaxTrees:
            [
                CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)),
                CSharpSyntaxTree.ParseText(CoreLibraryStub, new CSharpParseOptions(LanguageVersion.Latest))
            ],
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>
    /// Runs the generator with step tracking enabled (needed for cacheability tests)
    /// and returns the driver so callers can run subsequent incremental passes.
    /// </summary>
    public static GeneratorDriver Run(CSharpCompilation compilation)
        => CSharpGeneratorDriver
            .Create(
                generators: [new IdRegistrationGenerator().AsSourceGenerator()],
                driverOptions: new GeneratorDriverOptions(
                    IncrementalGeneratorOutputKind.None,
                    trackIncrementalGeneratorSteps: true))
            .RunGenerators(compilation);

    public static string SingleGeneratedSource(this GeneratorDriver driver)
    {
        var result = driver.GetRunResult().Results.Single();
        return result.GeneratedSources.Single().SourceText.ToString();
    }
}
