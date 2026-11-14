using Microsoft.CodeAnalysis;

namespace Tarol.Validations.Generators.Tests;

/// <summary>One test per diagnostic (and its negative), with the severity agreed in V-5.</summary>
public class DiagnosticTests
{
    private const string Usings = "using System.Collections.Generic; using Tarol.Options; using Tarol.StronglyTypedId; using Tarol.Validations;\n";

    private static Diagnostic[] Of(string id, params string[] sources)
        => [.. GeneratorTestHost.Diagnose([.. sources.Select(s => Usings + s)]).Where(d => d.Id == id)];

    private static string Message(Diagnostic d) => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void TVAL001_A_Value_Object_Or_Input_Must_Be_Partial()
    {
        var found = Of("TVAL001",
            "public readonly record struct Zip([Length(5, 5)] string Value);",
            "public sealed record Cmd(string Name) : IValidatable { public void Validate(ref ValidationErrors e) { } }",
            "public static class Outer { public readonly partial record struct Inner([Range(1, 2)] int Value); }");

        found.Length.ShouldBe(2);                                           // a hand-written Validate needs no partial
        found.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Error);
        found.Select(Message).ShouldContain(m => m.Contains("'Zip' must be declared partial", StringComparison.Ordinal));
        found.Select(Message).ShouldContain(m => m.Contains("'Outer'", StringComparison.Ordinal) && m.Contains("'Inner'", StringComparison.Ordinal));
    }

    [Fact]
    public void TVAL002_Generic_File_Local_And_Derived_Inputs_Are_Unsupported()
    {
        var found = Of("TVAL002",
            "public readonly partial record struct Box<T>([Range(1, 2)] int Value);",
            "file readonly partial record struct Local([Range(1, 2)] int Value);",
            "public partial record BaseCmd(string A) : IValidatable; public sealed partial record Derived(string A, string B) : BaseCmd(A), IValidatable;");

        found.Length.ShouldBe(3);
        found.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void TVAL003_Rules_Outside_A_Record_Primary_Constructor()
    {
        var found = Of("TVAL003",
            "public static class Api { public static void Search([Length(1, 5)] string text) { } }",
            "public sealed class Holder([Range(1, 2)] int value) { public int Value => value; }");

        found.Length.ShouldBe(2);
        found.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Error);
        Of("TVAL003", "public readonly partial record struct Ok([Range(1, 2)] int Value);").ShouldBeEmpty();
    }

    [Fact]
    public void TVAL004_A_Rule_That_Does_Not_Fit_The_Type()
    {
        var found = Of("TVAL004",
            "public readonly partial record struct A([Length(1, 2)] int Value);",
            "public readonly partial record struct B([Range(1, 2)] string Value);",
            "public readonly partial record struct C([Email] System.Guid Value);",
            "public readonly partial record struct Q([Range(1, 2)] int Value); public sealed partial record Cmd([Range(1, 2)] Q Q, [Length(1, 2)] List<string> Tags) : IValidatable;");

        found.Length.ShouldBe(5);
        found.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Error);
        found.Select(Message).ShouldContain(m => m.Contains("'Q' is a value object", StringComparison.Ordinal));
    }

    [Fact]
    public void TVAL005_Invalid_Arguments()
    {
        var found = Of("TVAL005",
            "public readonly partial record struct A([Range(5, 1)] int Value);",
            "public readonly partial record struct B([Range(0, 300)] byte Value);",
            "public readonly partial record struct C([Range(0.5, 2)] int Value);",
            "public readonly partial record struct D([Range(\"x\", \"1\")] decimal Value);",
            "public readonly partial record struct E([Length(9, 3)] string Value);",
            "public readonly partial record struct F([Pattern(\"(unclosed\")] string Value);",
            "public readonly partial record struct G([Range] int Value);");

        found.Length.ShouldBe(7);
        found.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void TVAL006_Construction_Outside_The_Value_Object()
    {
        var found = Of("TVAL006", """
            public readonly partial record struct Q([Range(1, 2)] int Value)
            {
                public static Q One => new(1);                       // inside: fine
            }
            public readonly record struct HandMade(int Value)          // hand-written Check, no rule attribute: its own constructor
            {
                public static void Check(HandMade value, ref ValidationErrors errors) { }
            }
            public static class Use
            {
                public static Q Make() => new Q(500);
                public static Q Change(Q q) => q with { Value = 7 };
                public static HandMade Hand() => new HandMade(1);
            }
            """);

        found.Length.ShouldBe(2);
        found.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Warning);
        found.Select(Message).ShouldContain(m => m.Contains("'new Q(…)'", StringComparison.Ordinal));
        found.Select(Message).ShouldContain(m => m.Contains("'with' expression", StringComparison.Ordinal));
    }

    [Fact]
    public void TVAL007_DataAnnotations_Attributes_Have_No_Effect()
    {
        var found = Of("TVAL007",
            "public readonly partial record struct Name([Length(1, 9)][System.ComponentModel.DataAnnotations.MaxLength(5)] string Value);",
            "public sealed partial record Cmd([property: System.ComponentModel.DataAnnotations.Required] string Text) : IValidatable;",
            "public sealed record PlainDto([System.ComponentModel.DataAnnotations.Required] string Text);");

        found.Length.ShouldBe(2);                                            // the plain DTO is Microsoft.Extensions.Validation's
        found.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Warning);
        found.Select(Message).ShouldContain(m => m.Contains("Use Tarol.Validations.Length", StringComparison.Ordinal));
    }

    [Fact]
    public void TVAL008_An_Input_Member_The_Generator_Cannot_Validate()
    {
        var found = Of("TVAL008",
            "public sealed class Blob; public sealed partial record Cmd(Dictionary<string, int> Map, Blob Data, List<List<int>> Nested, int Count, string Text) : IValidatable;");

        found.Select(d => Message(d).Split('\'')[1]).ShouldBe(["Map", "Data", "Nested"], ignoreOrder: true);
        found.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void TVAL009_A_Same_Assembly_Context_Used_Directly()
    {
        var found = Of("TVAL009", """
            using System.Text.Json;
            using System.Text.Json.Serialization;
            public readonly partial record struct Q([Range(1, 2)] int Value);
            public sealed record Placed(Option<Q> Total);
            public sealed record Unrelated(int Count);
            // What the System.Text.Json generator would add (it does not run in this test compilation).
            [JsonSerializable(typeof(Placed))]
            public partial class PlacedJson() : JsonSerializerContext(null)
            {
                public static PlacedJson Default { get; } = new();
                public System.Text.Json.Serialization.Metadata.JsonTypeInfo<Placed> Placed => null!;
                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
                public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(System.Type type) => null;
            }
            [JsonSerializable(typeof(Unrelated))]
            public partial class UnrelatedJson() : JsonSerializerContext(null)
            {
                public static UnrelatedJson Default { get; } = new();
                public System.Text.Json.Serialization.Metadata.JsonTypeInfo<Unrelated> Unrelated => null!;
                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
                public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(System.Type type) => null;
            }
            public static class Use
            {
                public static string Bad(Placed p) => JsonSerializer.Serialize(p, PlacedJson.Default.Placed);
                public static string AlsoBad(Placed p) => JsonSerializer.Serialize(p, typeof(Placed), PlacedJson.Default);
                public static string Good(Placed p) => JsonSerializer.Serialize(p, ValueObjectJson.Options(PlacedJson.Default));
                public static JsonSerializerOptions Composed() => new JsonSerializerOptions { TypeInfoResolver = PlacedJson.Default }.AddValueObjects();
                public static string Fine(Unrelated u) => JsonSerializer.Serialize(u, UnrelatedJson.Default.Unrelated);
            }
            """);

        found.Length.ShouldBe(2);
        found.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Warning);
        found.Select(Message).ShouldAllBe(m => m.Contains("ValueObjectJson.Options(PlacedJson.Default)", StringComparison.Ordinal));
    }
}
