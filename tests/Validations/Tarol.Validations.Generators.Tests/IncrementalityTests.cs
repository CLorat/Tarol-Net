using Microsoft.CodeAnalysis.CSharp;
using Tarol.Testing.Generators;

namespace Tarol.Validations.Generators.Tests;

/// <summary>Finding 12: per-type models are cached; an unrelated edit regenerates nothing.</summary>
public class IncrementalityTests
{
    [Fact]
    public void An_Unrelated_Edit_Regenerates_Nothing()
    {
        var compilation = GeneratorTestHost.Compile(SnapshotTests.ValueObjects, SnapshotTests.Inputs, "public static class Other { public static int A => 1; }");
        var driver = GeneratorTestHost.Run(compilation);

        var edited = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Last(),
            CSharpSyntaxTree.ParseText("public static class Other { public static int A => 2; }", new CSharpParseOptions(LanguageVersion.Preview), path: "Source2.cs", cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);

        Incrementality.RegeneratedOutputs(driver).ShouldBeEmpty();
    }

    [Fact]
    public void Editing_One_Value_Object_Regenerates_Only_That_One()
    {
        var compilation = GeneratorTestHost.Compile(
            "public readonly partial record struct A([Tarol.Validations.Range(1, 2)] int Value);",
            "public readonly partial record struct B([Tarol.Validations.Range(1, 2)] int Value);");
        var driver = GeneratorTestHost.Run(compilation);

        var edited = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.First(),
            CSharpSyntaxTree.ParseText("public readonly partial record struct A([Tarol.Validations.Range(1, 3)] int Value);", new CSharpParseOptions(LanguageVersion.Preview), path: "Source0.cs", cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);

        Incrementality.RegeneratedOutputs(driver).Count.ShouldBe(1);
        driver.Generated("A").ShouldContain("value.Value <= 3");
    }
}
