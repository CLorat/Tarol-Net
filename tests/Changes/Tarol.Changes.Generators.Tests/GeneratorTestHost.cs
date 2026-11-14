using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tarol.Primitives.Generators;

namespace Tarol.Changes.Generators.Tests;

/// <summary>Compiles test sources against the real runtime packages and runs the changes generator.</summary>
internal static class GeneratorTestHost
{
    // Roslyn 5.9 treats unions (Tarol.Results, Tarol.Options) as preview (results.md §8).
    public static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    private static readonly IReadOnlyList<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat([
                typeof(ChangePipeline).Assembly.Location,
                typeof(Tarol.Persistence.IAggregateStore).Assembly.Location,
                typeof(Tarol.Primitives.AggregateChanges).Assembly.Location,
                typeof(Tarol.Validations.IValidatable).Assembly.Location,
                typeof(Tarol.Results.Result).Assembly.Location,
                typeof(Tarol.Options.None).Assembly.Location,
                typeof(Tarol.Kernel.Actor).Assembly.Location,
                typeof(Tarol.StronglyTypedId.IStronglyTypedId).Assembly.Location])
            .Where(static path => !Path.GetFileName(path).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                                  && !Path.GetFileName(path).Contains(".Generators", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
    ];

    public const string Usings = """
        using System.Threading;
        using System.Threading.Tasks;
        using Tarol.Changes;
        using Tarol.Persistence;
        using Tarol.Primitives;
        using Tarol.Results;
        using Tarol.StronglyTypedId;
        using Tarol.Validations;

        """;

    /// <summary>An aggregate and a second one to read, shared by the test sources.</summary>
    public const string Domain = """
        using Tarol.Primitives;
        using Tarol.Results;
        using Tarol.StronglyTypedId;

        namespace Shop;

        public partial class Order : AggregateRoot<Order>
        {
            public bool Closed { get; private set; }
            public static Result<Order> Open() { var o = new Order(Id<Order>.New()); o.Raise(new Opened()); return o; }
            public Result Close() => Closed ? Error.Conflict("Order.Closed") : Raise(new WasClosed());
            public sealed record Opened : Event;
            public sealed record WasClosed : Event;
            private void On(Opened e) => Closed = false;
            private void On(WasClosed e) => Closed = true;
        }

        public partial class Customer : AggregateRoot<Customer>
        {
            public bool Blocked { get; private set; }
            public sealed record Registered : Event;
            private void On(Registered e) => Blocked = false;
        }
        """;

    public static CSharpCompilation Compile(params string[] sources)
        => CSharpCompilation.Create(
            "Shop.Domain",
            sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, Parse, path: $"Source{i}.cs")),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>The changes generator alone (what the snapshots and incrementality tests look at).</summary>
    public static GeneratorDriver Run(CSharpCompilation compilation)
        => CSharpGeneratorDriver
            .Create(
                [new ChangesGenerator().AsSourceGenerator()],
                parseOptions: Parse,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true))
            .RunGenerators(compilation);

    /// <summary>Generator diagnostics plus the compiler's errors and warnings with the Primitives and Changes generators applied.</summary>
    public static ImmutableArray<Diagnostic> Diagnose(params string[] sources)
    {
        var compilation = Compile([Domain, .. sources]);
        _ = CSharpGeneratorDriver
            .Create([new AggregateGenerator().AsSourceGenerator(), new ChangesGenerator().AsSourceGenerator()], parseOptions: Parse)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        var compiler = output.GetDiagnostics().Where(static d => d.Severity >= DiagnosticSeverity.Warning);
        return [.. generatorDiagnostics, .. compiler];
    }

    public static string[] Ids(this ImmutableArray<Diagnostic> diagnostics)
        => [.. diagnostics.Select(static d => d.Id).Order(StringComparer.Ordinal)];
}
