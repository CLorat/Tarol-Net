using Microsoft.CodeAnalysis;
using Tarol.Testing.Generators;

namespace Tarol.Validations.Generators.Tests;

public class SnapshotTests
{
    internal const string ValueObjects = """
        using Tarol.Validations;

        namespace Shop;

        public enum Currency { Eur, Usd }

        public readonly partial record struct Quantity([Range(1, 100)] int Value);
        public readonly partial record struct Title([NotEmpty, Length(3, 80)] string Value);
        public readonly partial record struct Zip([Pattern(@"^\d{5}$", TimeoutMilliseconds = 50)] string Value);
        public readonly partial record struct Price([Range("0.01", "9999.99")] decimal Value);
        public readonly partial record struct Money([Range(Min = 0)] decimal Amount, Currency Currency);
        public sealed partial record Iban([Length(15, 34)] string Value)
        {
            private static void Validate(string value, ref ValidationErrors errors)
            {
                if (!char.IsLetter(value[0]))
                    errors.Add("Iban.Country");
            }
        }
        """;

    internal const string Inputs = """
        using System.Collections.Generic;
        using Tarol.Options;
        using Tarol.StronglyTypedId;
        using Tarol.Validations;

        namespace Shop;

        public sealed class Order;
        public sealed class Product;

        public sealed partial record Line(Id<Product> Product, Quantity Quantity) : IValidatable;

        public sealed partial record PlaceOrder(
            Id<Order> OrderId,
            Title Name,
            [Count(1, 50)] IReadOnlyList<Line> Lines,
            Option<Title> Nickname,
            Money Total,
            [Length(Max = 200)] string? Comment,
            [Range(1, 10)] int? Priority,
            Id<Product>[] Related) : IValidatable;
        """;

    [Fact]
    public void Value_Objects()
        => Snapshot.Match("ValueObjects", GeneratorTestHost.Run(GeneratorTestHost.Compile(ValueObjects)));

    [Fact]
    public void Inputs_With_Every_Member_Kind()
        => Snapshot.Match("Inputs", GeneratorTestHost.Run(GeneratorTestHost.Compile(ValueObjects, Inputs)));

    [Fact]
    public void Nested_And_Internal_Types()
        => Snapshot.Match("Nested", GeneratorTestHost.Run(GeneratorTestHost.Compile("""
            using Tarol.Validations;

            namespace Catalog;

            internal static partial class Tags
            {
                public readonly partial record struct Tag([Length(1, 20)] string Value);

                private readonly partial record struct Hidden([Range(0, 9)] byte Digit);
            }
            """)));

    [Fact]
    public void The_Generated_Code_Compiles_Without_Diagnostics()
    {
        var diagnostics = GeneratorTestHost.Diagnose(ValueObjects, Inputs);
        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Generates_Nothing_For_Records_Without_Rules_Or_A_Hand_Written_Validate()
    {
        var driver = GeneratorTestHost.Run(GeneratorTestHost.Compile("""
            using Tarol.Validations;

            public sealed record Plain(string Name);
            public sealed partial record HandWritten(string Name) : IValidatable
            {
                public void Validate(ref ValidationErrors errors) { }
            }
            """));

        driver.GetRunResult().Results.Single().GeneratedSources.ShouldBeEmpty();
    }

    [Fact]
    public void Finding_3_Attributes_Are_Matched_By_Full_Name()
    {
        var driver = GeneratorTestHost.Run(GeneratorTestHost.Compile("""
            namespace Other
            {
                [System.AttributeUsage(System.AttributeTargets.Parameter)]
                public sealed class RangeAttribute(int min, int max) : System.Attribute;

                public readonly partial record struct NotTarol([Range(1, 2)] int Value);
            }
            """));

        driver.GetRunResult().Results.Single().GeneratedSources.ShouldBeEmpty();
    }

    [Fact]
    public void Finding_9_Equal_Names_In_Different_Namespaces_Get_Distinct_Hint_Names()
    {
        var driver = GeneratorTestHost.Run(GeneratorTestHost.Compile(
            "namespace A { public readonly partial record struct Name([Tarol.Validations.Length(1, 5)] string Value); }",
            "namespace B { public readonly partial record struct Name([Tarol.Validations.Length(1, 9)] string Value); }"));

        driver.GetRunResult().Results.Single().GeneratedSources.Select(s => s.HintName)
            .ShouldBe(["A.Name.Validation.g.cs", "B.Name.Validation.g.cs"], ignoreOrder: true);
    }
}
