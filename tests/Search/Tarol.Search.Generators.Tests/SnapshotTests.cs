using Tarol.Testing.Generators;

namespace Tarol.Search.Generators.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void Searchable_aggregates_get_their_shape_and_documents()
        => Snapshot.Match("searchables", GeneratorTestHost.Run(GeneratorTestHost.Compile(Sources.Gallery)));

    [Fact]
    public void The_generated_code_compiles_without_warnings()
        => string.Join("\n", GeneratorTestHost.Diagnose(Sources.Gallery).Select(d => d.ToString())).ShouldBeEmpty();

    [Fact]
    public void An_aggregate_without_the_class_attribute_gets_nothing()
        => GeneratorTestHost.Run(GeneratorTestHost.Compile(Sources.Gallery.Replace("[Searchable(\"Artwork\")]", "", StringComparison.Ordinal)
                .Replace("[Searchable]\n    public partial class Letter", "public partial class Letter", StringComparison.Ordinal)))
            .GetRunResult().GeneratedTrees.ShouldBeEmpty();

    [Fact]
    public void The_generated_code_compiles_under_csharp_12()
    {
        var parse = new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp12);
        var run = GeneratorTestHost.Run(GeneratorTestHost.Compile(Sources.Gallery)).GetRunResult();
        foreach (var tree in run.GeneratedTrees)
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(tree.GetText(TestContext.Current.CancellationToken), parse, cancellationToken: TestContext.Current.CancellationToken)
                .GetDiagnostics(TestContext.Current.CancellationToken).ShouldBeEmpty();
    }
}
