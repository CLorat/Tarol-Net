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

    [Fact]
    public void A_Change_To_A_Stored_Record_In_Another_File_Regenerates_The_Aggregate()
    {
        // the schema hash covers the members of stored records, so the aggregate must follow an edit elsewhere
        const string Record = "namespace Museum; public sealed record Plaque(string Text);";
        var gallery = Domains.Gallery.Replace("public string Name { get; private set; } = \"\";", "public string Name { get; private set; } = \"\";\n    public Plaque? Plaque { get; private set; }", StringComparison.Ordinal);
        var compilation = GeneratorTestHost.Compile(gallery, Domains.Orders, Record);
        var driver = GeneratorTestHost.Run(compilation);
        var before = driver.Generated("Gallery.Aggregate.g.cs");

        var edited = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Last(),
            CSharpSyntaxTree.ParseText(Record.Replace("string Text", "string Text, int Year", StringComparison.Ordinal), GeneratorTestHost.Parse, path: "Source2.cs", cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);

        Incrementality.RegeneratedOutputs(driver).Count.ShouldBe(1);
        driver.Generated("Gallery.Aggregate.g.cs").ShouldNotBe(before);
    }
}
