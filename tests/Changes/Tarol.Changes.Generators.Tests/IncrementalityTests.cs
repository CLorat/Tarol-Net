using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tarol.Testing.Generators;

namespace Tarol.Changes.Generators.Tests;

public sealed class IncrementalityTests
{
    private static (GeneratorDriver Driver, CSharpCompilation Compilation) First()
    {
        var compilation = GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Changes);
        return (GeneratorTestHost.Run(compilation), compilation);
    }

    private static CSharpCompilation Replace(CSharpCompilation compilation, string path, string text)
        => compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(t => t.FilePath == path), CSharpSyntaxTree.ParseText(text, GeneratorTestHost.Parse, path, cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void An_unrelated_file_regenerates_nothing()
    {
        var (driver, compilation) = First();
        var next = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Shop; public sealed class Unrelated;", GeneratorTestHost.Parse, "Other.cs", cancellationToken: TestContext.Current.CancellationToken));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void Editing_an_Execute_body_regenerates_nothing()
    {
        var (driver, compilation) = First();
        var next = Replace(compilation, "Source1.cs", Sources.Changes.Replace("=> order.Close();", "=> order.Close() ;", StringComparison.Ordinal));

        next.SyntaxTrees.Single(t => t.FilePath == "Source1.cs").ToString().ShouldNotBe(Sources.Changes);
        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void Moving_a_change_down_the_file_regenerates_nothing()
    {
        var (driver, compilation) = First();
        var next = Replace(compilation, "Source1.cs", Sources.Changes.Replace("namespace Shop;", "namespace Shop;\n\n\n// moved", StringComparison.Ordinal));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void Renaming_the_target_member_regenerates_only_its_change()
    {
        var (driver, compilation) = First();
        var next = Replace(compilation, "Source1.cs", Sources.Changes
            .Replace("record CloseOrder(Id<Order> Order)", "record CloseOrder(Id<Order> Id)", StringComparison.Ordinal));

        var regenerated = Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken));

        regenerated.Count.ShouldBe(1);
        driver.RunGenerators(next, TestContext.Current.CancellationToken).GetRunResult().Results.Single().GeneratedSources.Single(s => s.HintName == "Shop.CloseOrder.Change.g.cs")
            .SourceText.ToString().ShouldContain("=> this.Id;");
    }
}
