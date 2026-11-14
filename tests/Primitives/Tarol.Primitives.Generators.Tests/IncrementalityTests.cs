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

    // ── lifecycles ──

    private const string Status = """
        using Tarol.Primitives;
        namespace Auctions;
        [Lifecycle]
        public enum Lot { [To(Sold)] Listed, Sold }
        """;

    private static string LifecycleDomain => Domains.Lifecycle.Replace("public decimal Price { get; private set; }", "public decimal Price { get; private set; }\n    public Lot Lot { get; private set; }\n    public sealed record LotSold : Event;\n    private void On(LotSold e) { _ = e; Lot = Lot.Sold; }", StringComparison.Ordinal);

    private static (Microsoft.CodeAnalysis.GeneratorDriver Driver, Microsoft.CodeAnalysis.CSharp.CSharpCompilation Compilation) Lifecycles()
    {
        var compilation = GeneratorTestHost.Compile(LifecycleDomain, Status, Other);
        return (GeneratorTestHost.Run(compilation), compilation);
    }

    private static Microsoft.CodeAnalysis.GeneratorDriver Edit(Microsoft.CodeAnalysis.GeneratorDriver driver, Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation, int tree, string text)
        => driver.RunGenerators(compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.ElementAt(tree),
            CSharpSyntaxTree.ParseText(text, GeneratorTestHost.Parse, path: $"Source{tree}.cs", cancellationToken: TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);

    [Fact]
    public void An_Unrelated_Edit_Regenerates_No_Lifecycle()
    {
        var (driver, compilation) = Lifecycles();
        Incrementality.RegeneratedOutputs(Edit(driver, compilation, 2, Other.Replace("=> 1", "=> 2", StringComparison.Ordinal))).ShouldBeEmpty();
    }

    [Fact]
    public void Moving_The_Enum_Text_Regenerates_Nothing()
    {
        var (driver, compilation) = Lifecycles();
        Incrementality.RegeneratedOutputs(Edit(driver, compilation, 1, "// a comment that moves every location\n" + Status)).ShouldBeEmpty();
    }

    [Fact]
    public void A_New_Edge_Regenerates_Only_The_Lifecycle_Class()
    {
        // the aggregate calls LotLifecycle.IsEdge, so its own code does not change
        var (driver, compilation) = Lifecycles();
        var edited = Edit(driver, compilation, 1, Status.Replace("Sold }", "[To(Listed)] Sold }", StringComparison.Ordinal));
        Incrementality.RegeneratedOutputs(edited).Count.ShouldBe(1);
        edited.Generated("LotLifecycle.g.cs").ShouldContain("Sold --> Listed");
    }

    [Fact]
    public void A_New_State_In_Another_File_Regenerates_Its_Class_And_Its_Aggregate()
    {
        // a new member changes the class, and the aggregate's stored-state schema (the enum's names are part of it)
        var (driver, compilation) = Lifecycles();
        var before = driver.Generated("Auction.Aggregate.g.cs");
        var edited = Edit(driver, compilation, 1, Status.Replace("[To(Sold)] Listed, Sold }", "[To(Sold, Withdrawn)] Listed, Sold, Withdrawn }", StringComparison.Ordinal));
        Incrementality.RegeneratedOutputs(edited).Count.ShouldBe(2);
        edited.Generated("Auction.Aggregate.g.cs").ShouldNotBe(before);
    }

    [Fact]
    public void Renaming_The_Initial_State_Regenerates_The_Aggregate()
    {
        var (driver, compilation) = Lifecycles();
        var edited = Edit(driver, compilation, 1, Status.Replace("[Lifecycle]", "[Lifecycle(Initial = nameof(Lot.Sold))]", StringComparison.Ordinal).Replace("[To(Sold)] Listed, Sold", "Listed, [To(Listed)] Sold", StringComparison.Ordinal));
        Incrementality.RegeneratedOutputs(edited).Count.ShouldBe(2);
        edited.Generated("Auction.Aggregate.g.cs").ShouldContain("global::Auctions.Lot.Sold)");
    }

    [Fact]
    public void A_New_Transition_Label_Regenerates_Only_Its_Lifecycle_Class()
    {
        var (driver, compilation) = Lifecycles();
        var edited = Edit(driver, compilation, 0, LifecycleDomain.Replace("public sealed record LotSold : Event;", "[Transition<Lot>(Lot.Listed, Lot.Sold)] public sealed record LotSold : Event;", StringComparison.Ordinal));
        Incrementality.RegeneratedOutputs(edited).Count.ShouldBe(1);
        edited.Generated("LotLifecycle.g.cs").ShouldContain("Listed --> Sold : LotSold");
    }
}
