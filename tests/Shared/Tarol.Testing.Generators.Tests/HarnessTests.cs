using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tarol.Testing.Generators.Tests;

/// <summary>The shared snapshot and incrementality helpers behave as documented.</summary>
public sealed class HarnessTests : IDisposable
{
    private readonly string _name = "Harness_" + Guid.NewGuid().ToString("N");
    private readonly string _directory;

    public HarnessTests() => _directory = Path.Combine(SnapshotRoot(), _name);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Missing_Snapshot_Writes_Received_Files_And_Fails_Then_Passes_Once_Accepted()
    {
        var driver = Run("class A { }");

        Should.Throw<SnapshotMismatchException>(() => Snapshot.Match(_name, driver));
        var received = Directory.GetFiles(_directory, "*.received.*").Order(StringComparer.Ordinal).ToArray();
        received.Select(Path.GetFileName).ShouldBe(["Echo.g.received.cs", "diagnostics.received.txt"]);

        foreach (var file in received)
            File.Move(file, file.Replace(".received.", ".verified.", StringComparison.Ordinal));

        Snapshot.Match(_name, driver);
        Directory.GetFiles(_directory, "*.received.*").ShouldBeEmpty();
    }

    [Fact]
    public void Changed_Output_Fails_Against_The_Accepted_Snapshot()
    {
        Should.Throw<SnapshotMismatchException>(() => Snapshot.Match(_name, Run("class A { }")));
        foreach (var file in Directory.GetFiles(_directory, "*.received.*"))
            File.Move(file, file.Replace(".received.", ".verified.", StringComparison.Ordinal));

        var failure = Should.Throw<SnapshotMismatchException>(() => Snapshot.Match(_name, Run("class A { } class B { }")));

        failure.Message.ShouldContain("Echo.g.cs");
    }

    [Fact]
    public void Unrelated_Edit_Regenerates_Nothing_But_A_Relevant_Edit_Does()
    {
        var compilation = Compile("class A { }");
        var driver = CreateDriver().RunGenerators(compilation, TestContext.Current.CancellationToken);

        var unrelated = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("// comment only",
            cancellationToken: TestContext.Current.CancellationToken));
        Incrementality.RegeneratedOutputs(driver.RunGenerators(unrelated, TestContext.Current.CancellationToken))
            .ShouldBeEmpty();

        var relevant = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class B { }",
            cancellationToken: TestContext.Current.CancellationToken));
        Incrementality.RegeneratedOutputs(driver.RunGenerators(relevant, TestContext.Current.CancellationToken))
            .ShouldNotBeEmpty();
    }

    private static GeneratorDriver Run(string source)
        => CreateDriver().RunGenerators(Compile(source), TestContext.Current.CancellationToken);

    private static CSharpCompilation Compile(string source)
        => CSharpCompilation.Create("HarnessTest", [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

    private static CSharpGeneratorDriver CreateDriver()
        => CSharpGeneratorDriver.Create(
            [new EchoGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    private static string SnapshotRoot()
        => typeof(HarnessTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "TarolSnapshotRoot").Value!;

    /// <summary>Emits one file listing the class names of the compilation.</summary>
    private sealed class EchoGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var names = context.SyntaxProvider
                .CreateSyntaxProvider(
                    static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax,
                    static (ctx, _) => ((Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax)ctx.Node).Identifier.ValueText)
                .Collect()
                .Select(static (all, _) => string.Join(",", all.Order(StringComparer.Ordinal)));

            context.RegisterSourceOutput(names, static (spc, list) => spc.AddSource("Echo.g.cs", $"// {list}\n"));
        }
    }
}
