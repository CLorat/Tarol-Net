using Microsoft.CodeAnalysis.CSharp;
using Tarol.Testing.Generators;

namespace Tarol.Primitives.Generators.Tests;

/// <summary>Per-type models are cached and grouped per aggregate: unrelated edits regenerate nothing (old F-15).</summary>
public class IncrementalityTests
{
    private const string Other = "namespace Unrelated; public static class Other { public static int A => 1; }";

    [Fact]
    public void An_Unrelated_Edit_Regenerates_Nothing()
    {
        var compilation = GeneratorTestHost.Compile(Domains.Gallery, Domains.Orders, Other);
        var driver = GeneratorTestHost.Run(compilation);

        var edited = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Last(),
            CSharpSyntaxTree.ParseText(Other.Replace("=> 1", "=> 2", StringComparison.Ordinal), GeneratorTestHost.Parse, path: "Source2.cs", cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);

        Incrementality.RegeneratedOutputs(driver).ShouldBeEmpty();
    }

    [Fact]
    public void Editing_One_Aggregate_Regenerates_Only_That_Aggregate()
    {
        var compilation = GeneratorTestHost.Compile(Domains.Gallery, Domains.Orders);
        var driver = GeneratorTestHost.Run(compilation);

        var edited = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.First(),
            CSharpSyntaxTree.ParseText(Domains.Gallery.Replace("public decimal Price { get; private set; }", "public decimal Price { get; private set; }\n    public sealed record Viewed : Event;\n    private void On(Viewed e) { }", StringComparison.Ordinal),
                GeneratorTestHost.Parse, path: "Source0.cs", cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);

        // a new event re-renders the Gallery aggregate; the Order aggregate stays cached
        Incrementality.RegeneratedOutputs(driver).Count.ShouldBe(1);
    }

    [Fact]
    public void Editing_A_Method_Body_Regenerates_Nothing()
    {
        var compilation = GeneratorTestHost.Compile(Domains.Gallery, Domains.Orders);
        var driver = GeneratorTestHost.Run(compilation);

        var edited = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.First(),
            CSharpSyntaxTree.ParseText(Domains.Gallery.Replace("Error.Conflict(\"Gallery.NoName\")", "Error.Conflict(\"Gallery.Unnamed\")", StringComparison.Ordinal),
                GeneratorTestHost.Parse, path: "Source0.cs", cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);

        Incrementality.RegeneratedOutputs(driver).ShouldBeEmpty();
    }
}
