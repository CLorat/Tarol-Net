using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tarol.Testing.Generators;

namespace Tarol.Search.Generators.Tests;

public sealed class IncrementalityTests
{
    private static (GeneratorDriver Driver, CSharpCompilation Compilation) First()
    {
        var compilation = GeneratorTestHost.Compile(Sources.Gallery);
        return (GeneratorTestHost.Run(compilation), compilation);
    }

    private static CSharpCompilation Replace(CSharpCompilation compilation, string text)
        => compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(t => t.FilePath == "Source0.cs"),
            CSharpSyntaxTree.ParseText(text, GeneratorTestHost.Parse, "Source0.cs", cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void An_unrelated_file_regenerates_nothing()
    {
        var (driver, compilation) = First();
        var next = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Gallery; public sealed class Unrelated;", GeneratorTestHost.Parse, "Other.cs",
            cancellationToken: TestContext.Current.CancellationToken));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void Editing_an_apply_body_regenerates_nothing()
    {
        var (driver, compilation) = First();
        var next = Replace(compilation, Sources.Gallery.Replace("private void On(Added e) => Label = e.Label;", "private void On(Added e) => Label = e.Label.Trim();", StringComparison.Ordinal));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void A_new_weight_regenerates_the_aggregate()
    {
        var (driver, compilation) = First();
        var next = Replace(compilation, Sources.Gallery.Replace("[Searchable] public string Description", "[Searchable(SearchWeight.C)] public string Description", StringComparison.Ordinal));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldNotBeEmpty();
    }
}
