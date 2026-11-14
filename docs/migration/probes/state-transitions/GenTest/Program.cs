using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tarol.Lifecycle.Generators;

var refs = AppDomain.CurrentDomain.GetAssemblies()
    .Concat([typeof(Tarol.Primitives.LifecycleAttribute).Assembly, typeof(Tarol.Results.Result).Assembly])
    .Where(a => !a.IsDynamic && a.Location.Length > 0).Select(a => MetadataReference.CreateFromFile(a.Location)).ToList();
var parse = new CSharpParseOptions(LanguageVersion.Preview);

(GeneratorDriver, Compilation) Run(string name, string source, bool print)
{
    var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, parse, "Domain.cs")], refs,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    GeneratorDriver driver = CSharpGeneratorDriver.Create([new LifecycleGenerator().AsSourceGenerator()], parseOptions: parse,
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
    driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
    Console.WriteLine($"--- {name}");
    foreach (var d in diagnostics.OrderBy(d => d.Location.SourceSpan.Start))
        Console.WriteLine($"  {d.Id} {d.Severity} {d.Location.GetLineSpan().StartLinePosition}: {d.GetMessage()}");
    var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    Console.WriteLine($"  generated files: {driver.GetRunResult().GeneratedTrees.Length}, compile errors after generation: {errors.Count}");
    foreach (var e in errors.Take(3)) Console.WriteLine("    " + e);
    if (print) foreach (var t in driver.GetRunResult().GeneratedTrees) Console.WriteLine(t.GetText());
    return (driver, compilation);
}

const string good = """
    using Tarol.Primitives;
    namespace Shop;
    [Lifecycle]
    public enum OrderStatus
    {
        [To(Paid, Cancelled)] Placed,
        [To(Shipped, Refunded)] Paid,
        [To(Delivered)] Shipped,
        Delivered,
        Cancelled,
        Refunded,
    }
    public static class Use
    {
        public static bool Ok(OrderStatus s) => s.CanTransitionTo(OrderStatus.Paid) && OrderStatusLifecycle.IsEdge(s, OrderStatus.Paid);
    }
    """;
var (driver, compilation) = Run("good", good, print: true);

Run("misuse", """
    using System;
    using Tarol.Primitives;
    namespace Shop;
    public enum Other { X }
    [Lifecycle]
    public enum Ticket
    {
        [To(Other.X, 3, Closed)] Open,      // TPRM020 x2
        [To(Closed, Closed)] Review,        // TPRM022 duplicate; Review unreachable (TPRM021)
        [To(Closed)] Closed,                // TPRM022 self-loop
        Archived,                           // TPRM021 unreachable
    }
    [Lifecycle, Flags] public enum Bits { A = 1, B = 2 }          // TPRM023 flags (+ B unreachable)
    [Lifecycle(Initial = "Nope")] public enum Bad { [To(Y)] X, Y } // TPRM023 initial
    [Lifecycle] public enum Alias { A = 0, B = 0 }                 // TPRM023 alias
    public enum Plain { [To(Two)] One, Two }                       // TPRM024
    [Lifecycle] public enum Lonely { [To(B)] A, B, Orphan }        // TPRM021 Orphan (ArtFlow's Settled)
    """, print: false);

// Incrementality: an unrelated edit, then an edit of the enum.
void Rerun(string label, string newSource)
{
    var tree = compilation.SyntaxTrees.First();
    var next = compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(newSource, parse, "Domain.cs"));
    driver = driver.RunGenerators(next);
    var steps = driver.GetRunResult().Results[0].TrackedSteps["Lifecycle"];
    Console.WriteLine($"--- incremental, {label}: " + string.Join(", ", steps.SelectMany(s => s.Outputs).Select(o => o.Reason)));
    compilation = next;
}
Rerun("comment added elsewhere", good + "\n// unrelated\n");
Rerun("unrelated class added", good + "\npublic class Unrelated { }\n");
Rerun("edge added", good.Replace("[To(Delivered)] Shipped", "[To(Delivered, Refunded)] Shipped") + "\npublic class Unrelated { }\n");
