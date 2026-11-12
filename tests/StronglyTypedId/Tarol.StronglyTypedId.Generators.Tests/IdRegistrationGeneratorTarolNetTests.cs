using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tarol.Testing.Generators;

namespace Tarol.StronglyTypedId.Generators.Tests;

/// <summary>Generator behaviour added in Tarol-Net (inherited ids, TSID001, snapshots).</summary>
public class IdRegistrationGeneratorTarolNetTests
{
    private const string DomainWithInheritedIds = """
        using System.Collections.Generic;
        using Tarol.StronglyTypedId;

        namespace Shop
        {
            public abstract class AggregateRoot<TSelf>
            {
                public Id<TSelf> Id { get; protected set; }
                protected Id<TSelf>? Previous;
            }

            public abstract class Entity<TRoot, TSelf>
            {
                public Id<TRoot> RootId { get; }
                public List<Id<TSelf>> Siblings { get; } = new();
            }

            public sealed class Order : AggregateRoot<Order> { }
            public sealed class OrderLine : Entity<Order, OrderLine> { }
            public sealed class Customer { public Id<Customer> Id { get; set; } }
        }
        """;

    [Fact]
    public void Registers_Ids_Closed_Through_Generic_Bases_Fields_Nullables_And_Collections()
    {
        var generated = GeneratorTestHost.Run(GeneratorTestHost.CreateCompilation(DomainWithInheritedIds))
            .SingleGeneratedSource();

        generated.ShouldContain("Register<global::Shop.Order>();");     // AggregateRoot<Order>.Id and .Previous
        generated.ShouldContain("Register<global::Shop.OrderLine>();"); // Entity<Order, OrderLine>.Siblings
        generated.ShouldContain("Register<global::Shop.Customer>();");  // spelled
    }

    [Fact]
    public void Snapshot_Of_Generated_Registrations()
    {
        var driver = GeneratorTestHost.Run(GeneratorTestHost.CreateCompilation(DomainWithInheritedIds, "Shop.Domain"));

        Snapshot.Match("InheritedIds", driver);
    }

    [Fact]
    public void Reports_TSID001_For_Private_Nested_And_File_Local_Markers()
    {
        var compilation = GeneratorTestHost.CreateCompilation("""
            using Tarol.StronglyTypedId;

            namespace App
            {
                public class Outer
                {
                    private sealed class Hidden { }
                    public object Make() => default(Id<Hidden>);
                }

                file sealed class Local { }
                file static class Uses { public static object Make() => default(Id<Local>); }
            }
            """);

        var driver = GeneratorTestHost.Run(compilation);
        var diagnostics = driver.GetRunResult().Diagnostics;

        diagnostics.Select(d => d.Id).ShouldBe(["TSID001", "TSID001"]);
        diagnostics.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Info);
        diagnostics.Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ShouldContain(m => m.Contains("'Hidden' is private", StringComparison.Ordinal));
        diagnostics.Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ShouldContain(m => m.Contains("'Local' is a file-local type", StringComparison.Ordinal));
        driver.GetRunResult().Results.Single().GeneratedSources.ShouldBeEmpty();
    }

    [Fact]
    public void Editing_An_Unrelated_Type_With_A_Base_List_Regenerates_Nothing()
    {
        var compilation = GeneratorTestHost.CreateCompilation(DomainWithInheritedIds);
        var driver = GeneratorTestHost.Run(compilation);

        var edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "namespace Shop { public sealed class Note : System.Exception { } }",
            new CSharpParseOptions(LanguageVersion.Latest),
            cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);

        Incrementality.RegeneratedOutputs(driver).ShouldBeEmpty();
    }
}
