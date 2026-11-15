using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tarol.Changes.Generators;
using Tarol.Primitives.Generators;
using Tarol.Validations.Generators;

namespace Tarol.Changes.AspNetCore.Generators.Tests;

/// <summary>Compiles test sources against the real runtime packages (and ASP.NET Core) and runs the endpoint generator.</summary>
internal static class GeneratorTestHost
{
    public static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    private static IReadOnlyList<MetadataReference> References(bool openApi)
        => [
            .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Concat([
                    typeof(ChangeEndpoints).Assembly.Location,
                    typeof(Tarol.Security.ChangeAuthorization).Assembly.Location,
                    typeof(ChangePipeline).Assembly.Location,
                    typeof(Tarol.Persistence.IAggregateStore).Assembly.Location,
                    typeof(Tarol.Primitives.AggregateChanges).Assembly.Location,
                    typeof(Tarol.Validations.IValidatable).Assembly.Location,
                    typeof(Tarol.Results.Result).Assembly.Location,
                    typeof(Tarol.Options.None).Assembly.Location,
                    typeof(Tarol.Kernel.Actor).Assembly.Location,
                    typeof(Tarol.StronglyTypedId.IStronglyTypedId).Assembly.Location])
                .Concat(openApi ? [typeof(Microsoft.AspNetCore.OpenApi.OpenApiOptions).Assembly.Location, typeof(Microsoft.OpenApi.OpenApiSchema).Assembly.Location] : [])
                .Where(static path => !Path.GetFileName(path).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                                      && !Path.GetFileName(path).Contains(".Generators", StringComparison.Ordinal)
                                      && !Path.GetFileName(path).StartsWith("Microsoft.AspNetCore.OpenApi", StringComparison.Ordinal)
                                      && !Path.GetFileName(path).StartsWith("Microsoft.OpenApi", StringComparison.Ordinal))
                .Concat(openApi ? [typeof(Microsoft.AspNetCore.OpenApi.OpenApiOptions).Assembly.Location, typeof(Microsoft.OpenApi.OpenApiSchema).Assembly.Location] : [])
                .Distinct(StringComparer.Ordinal)
                .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
        ];

    public const string Usings = """
        using System;
        using System.Text.Json.Serialization;
        using Tarol.Changes;
        using Tarol.Changes.AspNetCore;
        using Tarol.Primitives;
        using Tarol.Results;
        using Tarol.StronglyTypedId;
        using Tarol.Validations;

        """;

    public const string Domain = """
        using Tarol.Primitives;
        using Tarol.Results;
        using Tarol.StronglyTypedId;
        using Tarol.Validations;

        namespace Shop;

        public readonly partial record struct Quantity([Range(1, 10)] int Value);
        public readonly partial record struct Note([Length(1, 80)] string Value);

        public partial class Order : AggregateRoot<Order>
        {
            public int Items { get; private set; }
            public static Result<Order> Open() { var o = new Order(Id<Order>.New()); o.Raise(new Opened()); return o; }
            public Result Add(int items) => Raise(new Added(items));
            public sealed record Opened : Event;
            public sealed record Added(int Items) : Event;
            private void On(Opened e) => Items = 0;
            private void On(Added e) => Items += e.Items;
        }
        """;

    public static CSharpCompilation Compile(bool openApi, params string[] sources)
        => CSharpCompilation.Create(
            "Shop.Api",
            sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, Parse, path: $"Source{i}.cs")),
            References(openApi),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static GeneratorDriver Run(CSharpCompilation compilation)
        => CSharpGeneratorDriver
            .Create([new EndpointGenerator().AsSourceGenerator()], parseOptions: Parse,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true))
            .RunGenerators(compilation);

    /// <summary>Generator diagnostics plus the compiler's warnings and errors with the Primitives, Changes and endpoint generators.</summary>
    public static ImmutableArray<Diagnostic> Diagnose(bool openApi, params string[] sources)
    {
        var compilation = Compile(openApi, [Domain, .. sources]);
        _ = CSharpGeneratorDriver
            .Create([new AggregateGenerator().AsSourceGenerator(), new ChangesGenerator().AsSourceGenerator(), new ValidationGenerator().AsSourceGenerator(),
                new EndpointGenerator().AsSourceGenerator()], parseOptions: Parse)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        return [.. generatorDiagnostics, .. output.GetDiagnostics().Where(static d => d.Severity >= DiagnosticSeverity.Warning)];
    }

    public static string[] Ids(this ImmutableArray<Diagnostic> diagnostics) => [.. diagnostics.Select(static d => d.Id).Order(StringComparer.Ordinal)];
}

internal static class Sources
{
    public const string Entries = GeneratorTestHost.Usings + """
        namespace Shop;

        [Endpoint("POST /orders", Policy = "orders:write")]
        public sealed partial record OpenOrder(string Customer) : ICreation<Order>
        {
            public Result<Order> Execute(ChangeContext context) => Order.Open();
        }

        [Endpoint("POST /orders/{order}/items")]
        public sealed partial record AddItems(Id<Order> Order, Quantity Quantity, [property: JsonPropertyName("note_text")] Note? Note = null, int Priority = 2) : IChange<Order>, IValidatable
        {
            public Result Execute(Order order, ChangeContext context) => order.Add(Quantity.Value);
        }

        [Endpoint("DELETE /orders/{order}/lines/{line}", Anonymous = true)]
        public sealed partial record RemoveLine(Id<Order> Order, int Line) : IChange<Order>
        {
            public Result Execute(Order order, ChangeContext context) => order.Add(-Line);
        }

        [Endpoint("GET /orders/{order}")]
        public sealed record OrderView(Guid Id, int Items) : IView<Order, OrderView>
        {
            public static OrderView From(Order order) => new(order.Id.Value, order.Items);
        }

        public sealed record ShippedV1(Guid Order, int Count);

        public sealed partial record ShipItems(Id<Order> Order, int Count) : IChange<Order>
        {
            [Webhook("carrier", "shipment.done", Version = "1")]
            public static ShipItems From(ShippedV1 s) => new((Id<Order>)s.Order, s.Count);

            public Result Execute(Order order, ChangeContext context) => order.Add(Count);
        }
        """;
}
