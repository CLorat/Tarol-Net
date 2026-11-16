using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tarol.Testing.Generators;

namespace Tarol.Queries.Generators.Tests;

public sealed class IncrementalityTests
{
    private static (GeneratorDriver Driver, CSharpCompilation Compilation) First()
    {
        var compilation = GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Projections);
        return (GeneratorTestHost.Run(compilation), compilation);
    }

    private static CSharpCompilation Replace(CSharpCompilation compilation, string path, string text)
        => compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(t => t.FilePath == path),
            CSharpSyntaxTree.ParseText(text, GeneratorTestHost.Parse, path, cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void An_unrelated_file_regenerates_nothing()
    {
        var (driver, compilation) = First();
        var next = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Shop; public sealed class Unrelated;", GeneratorTestHost.Parse, "Other.cs",
            cancellationToken: TestContext.Current.CancellationToken));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void Editing_a_When_body_regenerates_nothing()
    {
        var (driver, compilation) = First();
        var edited = Sources.Projections.Replace("Lines = Lines + e.Quantity", "Lines = e.Quantity + Lines", StringComparison.Ordinal);
        var next = Replace(compilation, "Source1.cs", edited);

        next.SyntaxTrees.Single(t => t.FilePath == "Source1.cs").ToString().ShouldNotBe(Sources.Projections);
        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void Adding_a_column_regenerates_the_projection()
    {
        var (driver, compilation) = First();
        var next = Replace(compilation, "Source1.cs", Sources.Projections.Replace("[Sort] Guid Since)", "[Sort] Guid Since, int Visits = 0)", StringComparison.Ordinal));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldNotBeEmpty();
    }
}
