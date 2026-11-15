using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tarol.Changes.Generators;
using Tarol.Primitives.Generators;
using Tarol.Validations.Generators;

namespace Tarol.Messaging.Generators.Tests;

/// <summary>Compiles test sources against the real runtime packages and runs the consumer generator.</summary>
internal static class GeneratorTestHost
{
    public static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    private static readonly IReadOnlyList<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat([
                typeof(MessageConsumers).Assembly.Location,
                typeof(Tarol.Sagas.DeliveryPolicy).Assembly.Location,
                typeof(Tarol.Changes.ChangePipeline).Assembly.Location,
                typeof(Tarol.Persistence.IAggregateStore).Assembly.Location,
                typeof(Tarol.Primitives.AggregateChanges).Assembly.Location,
                typeof(Tarol.Validations.IValidatable).Assembly.Location,
                typeof(Tarol.Results.Result).Assembly.Location,
                typeof(Tarol.Options.None).Assembly.Location,
                typeof(Tarol.Kernel.Actor).Assembly.Location,
                typeof(Tarol.StronglyTypedId.IStronglyTypedId).Assembly.Location])
            .Where(static path => !Path.GetFileName(path).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                                  && !Path.GetFileName(path).Contains(".Generators", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)),
    ];

    public const string Usings = """
        using System;
        using System.Text.Json.Serialization;
        using Tarol.Changes;
        using Tarol.Messaging;
        using Tarol.Primitives;
        using Tarol.Results;
        using Tarol.StronglyTypedId;
        using Tarol.Validations;

        """;

    public const string Domain = """
        using Tarol.Primitives;
        using Tarol.Results;
        using Tarol.StronglyTypedId;

        namespace Shop;

        public partial class Order : AggregateRoot<Order>
        {
            public decimal Paid { get; private set; }
            public static Order Open() { var o = new Order(Id<Order>.New()); o.Raise(new Opened()); return o; }
            public Result Pay(decimal amount) => Raise(new PaymentRecorded(amount));
            public sealed record Opened : Event;
            public sealed record PaymentRecorded(decimal Amount) : Event;
            private void On(Opened e) => Paid = 0;
            private void On(PaymentRecorded e) => Paid += e.Amount;
        }
        """;

    public static CSharpCompilation Compile(params string[] sources)
        => CSharpCompilation.Create(
            "Shop.Billing",
            sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, Parse, path: $"Source{i}.cs")),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static GeneratorDriver Run(CSharpCompilation compilation)
        => CSharpGeneratorDriver
            .Create([new ConsumerGenerator().AsSourceGenerator()], parseOptions: Parse,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true))
            .RunGenerators(compilation);

    /// <summary>Generator diagnostics plus the compiler's warnings and errors with the Primitives, Changes, Validations and consumer generators.</summary>
    public static ImmutableArray<Diagnostic> Diagnose(params string[] sources)
    {
        var compilation = Compile([Domain, .. sources]);
        _ = CSharpGeneratorDriver
            .Create([new AggregateGenerator().AsSourceGenerator(), new ChangesGenerator().AsSourceGenerator(), new ValidationGenerator().AsSourceGenerator(),
                new ConsumerGenerator().AsSourceGenerator()], parseOptions: Parse)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        return [.. generatorDiagnostics, .. output.GetDiagnostics().Where(static d => d.Severity >= DiagnosticSeverity.Warning)];
    }

    public static string[] Ids(this ImmutableArray<Diagnostic> diagnostics) => [.. diagnostics.Select(static d => d.Id).Order(StringComparer.Ordinal)];
}

internal static class Sources
{
    public const string Consumers = GeneratorTestHost.Usings + """
        namespace Shop;

        public sealed record InvoicePaidV1(Guid Order, decimal Amount);
        public sealed record InvoicePaidV2(Guid Order, MoneyV2 Paid);
        public sealed record MoneyV2(decimal Amount, string Currency);

        public sealed partial record RecordPayment(Id<Order> Order, decimal Amount) : IChange<Order>
        {
            [Consumes("billing.invoice-paid", 1)]
            public static RecordPayment From(InvoicePaidV1 m) => new((Id<Order>)m.Order, m.Amount);

            [Consumes("billing.invoice-paid", 2)]
            public static RecordPayment From(InvoicePaidV2 m, Message envelope) => new((Id<Order>)m.Order, m.Paid.Amount);

            public Result Execute(Order order, ChangeContext context) => order.Pay(Amount);
        }

        public sealed partial record OpenForPayment(Guid Reference) : ICreation<Order>
        {
            [Consumes("billing.invoice-paid", 1, Name = "OpenOnPayment")]
            public static OpenForPayment From(InvoicePaidV1 m) => new(m.Order);

            public Result<Order> Execute(ChangeContext context) => Order.Open();
        }
        """;
}
