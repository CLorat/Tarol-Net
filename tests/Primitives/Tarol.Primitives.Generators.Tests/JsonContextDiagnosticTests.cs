using Microsoft.CodeAnalysis;

namespace Tarol.Primitives.Generators.Tests;

/// <summary>TPRM035 and TPRM036: what Tarol serializes against what the JSON contexts generate.</summary>
public class JsonContextDiagnosticTests
{
    // the analyzer knows Tarol's registration and endpoint types by name; the test compiles stand-ins of them
    internal const string Stubs = """
        using System; using System.Collections.Generic; using System.Text.Json.Serialization; using Tarol.Primitives;
        namespace Tarol.Persistence { public sealed class EventCodec { public EventCodec Add<T>() => this; } }
        namespace Tarol.Changes.AspNetCore { [AttributeUsage(AttributeTargets.Class)] public sealed class EndpointAttribute(string route) : Attribute { public string Route { get; } = route; } }
        """;

    internal const string Shop = """
        using System; using System.Collections.Generic; using System.Text.Json.Serialization; using Tarol.Primitives;
        namespace Shop
        {
            public sealed record Block(DateTimeOffset From, DateTimeOffset To);

            public partial class Month : AggregateRoot<Month>
            {
                private readonly List<Block> _blocks = [];
                public int Count { get; private set; }
                public sealed record Started(int Year) : Event;
                public sealed record Added(Block Block) : Event;
                private void On(Started e) => Count = 0;
                private void On(Added e) { _blocks.Add(e.Block); Count++; }
            }

            public partial class Settings : AggregateRoot<Settings>
            {
                public int Hours { get; private set; }
                public sealed record Started(int Hours) : Event;
                private void On(Started e) => Hours = e.Hours;
            }

            public static class Registration { public static void Codec(Tarol.Persistence.EventCodec c) => c.Add<Month>()REGISTER; }

            [Tarol.Changes.AspNetCore.Endpoint("POST /months/{month}/blocks")] public sealed record AddBlock(Guid Month, Block Block);
            [Tarol.Changes.AspNetCore.Endpoint("POST /months/{month}/close")] public sealed record CloseMonth(Guid Month);

            CONTEXT
            public sealed partial class ShopJson : JsonSerializerContext;
        }
        """;

    private static Diagnostic[] Run(string context, string register = "")
        => [.. GeneratorTestHost.Diagnose(Stubs, Shop.Replace("CONTEXT", context, StringComparison.Ordinal).Replace("REGISTER", register, StringComparison.Ordinal))
            .Where(static d => d.Id is "TPRM035" or "TPRM036").OrderBy(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal)];

    private static string[] Messages(string context, string register = "")
        => [.. Run(context, register).Select(static d => d.Id + " " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))];

    [Fact]
    public void A_registered_aggregates_events_and_json_state_and_an_endpoint_input_with_a_body_are_required()
        => Messages("[JsonSerializable(typeof(int))]").ShouldBe([
            "TPRM035 'AddBlock' is serialized by Tarol (an endpoint input), but no JsonSerializerContext of this assembly generates it; add [JsonSerializable(typeof(AddBlock))] to ShopJson",
            "TPRM035 'List<Block>' is serialized by Tarol (the stored state member Month._blocks), but no JsonSerializerContext of this assembly generates it; add [JsonSerializable(typeof(List<Block>))] to ShopJson",
            "TPRM035 'Month.Added' is serialized by Tarol (an event), but no JsonSerializerContext of this assembly generates it; add [JsonSerializable(typeof(Month.Added))] to ShopJson",
            "TPRM035 'Month.Started' is serialized by Tarol (an event), but no JsonSerializerContext of this assembly generates it; add [JsonSerializable(typeof(Month.Started))] to ShopJson",
        ]);                                                         // Settings is not registered; CloseMonth has no body

    [Fact]
    public void A_complete_context_reports_nothing_and_a_type_reached_through_an_entry_is_not_required()
        => Messages("""
            [JsonSerializable(typeof(Month.Started)), JsonSerializable(typeof(Month.Added)), JsonSerializable(typeof(List<Block>)), JsonSerializable(typeof(AddBlock))]
            """).ShouldBeEmpty();                                   // Block: through Added and AddBlock

    [Fact]
    public void An_entry_another_entry_reaches_is_reported_as_not_needed_unless_it_pins_a_name()
        => Messages("""
            [JsonSerializable(typeof(Month.Started), TypeInfoPropertyName = "MonthStarted"), JsonSerializable(typeof(Month.Added)), JsonSerializable(typeof(Block))]
            [JsonSerializable(typeof(List<Block>)), JsonSerializable(typeof(AddBlock)), JsonSerializable(typeof(Block[]), TypeInfoPropertyName = "Blocks")]
            """).ShouldBe(["TPRM036 [JsonSerializable(typeof(Block))] is not needed: the context generates it through Month.Added"]);

    [Fact]
    public void A_chain_names_the_entry_that_stays_and_entries_that_only_reach_each_other_are_kept()
        => GeneratorTestHost.Diagnose(Stubs, """
            using System.Collections.Generic; using System.Text.Json.Serialization;
            namespace Loop
            {
                public sealed record Leaf(int N);
                public sealed record Holder(Leaf[] Leaves);
                public sealed record Ping(Pong? Other);
                public sealed record Pong(Ping? Other);
                [JsonSerializable(typeof(Holder)), JsonSerializable(typeof(Leaf[])), JsonSerializable(typeof(Leaf))]
                [JsonSerializable(typeof(Ping)), JsonSerializable(typeof(Pong))]
                public sealed partial class LoopJson : JsonSerializerContext;
            }
            """).Where(static d => d.Id == "TPRM036").Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)
            .ShouldBe([
                "[JsonSerializable(typeof(Leaf))] is not needed: the context generates it through Holder",
                "[JsonSerializable(typeof(Leaf[]))] is not needed: the context generates it through Holder",
            ]);                                                     // Ping and Pong only reach each other: both stay

    [Fact]
    public void A_missing_type_whose_name_another_type_has_gets_a_pinned_name()
    {
        var missing = Run("""
            [JsonSerializable(typeof(Month.Started)), JsonSerializable(typeof(Month.Added)), JsonSerializable(typeof(List<Block>)), JsonSerializable(typeof(AddBlock))]
            """, ".Add<Settings>()").Single();

        missing.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("'Settings.Started' is serialized by Tarol (an event)");
        missing.Properties[JsonContextAnalyzer.NameKey].ShouldBe("SettingsStarted");
        missing.Properties[JsonContextAnalyzer.TypeKey].ShouldBe("global::Shop.Settings.Started");
        missing.Properties[JsonContextAnalyzer.ContextKey].ShouldBe("global::Shop.ShopJson");
        missing.AdditionalLocations.Single().SourceTree!.ToString().ShouldContain("class ShopJson");
    }

    [Fact]
    public void Without_a_context_nothing_is_reported()
        => GeneratorTestHost.Diagnose(Stubs, Shop.Replace("CONTEXT", "", StringComparison.Ordinal).Replace("REGISTER", "", StringComparison.Ordinal)
                .Replace("public sealed partial class ShopJson : JsonSerializerContext;", "", StringComparison.Ordinal))
            .Where(static d => d.Id is "TPRM035" or "TPRM036").ShouldBeEmpty();
}
