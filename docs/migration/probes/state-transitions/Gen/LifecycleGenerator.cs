using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Lifecycle.Generators;

internal sealed record Edge(string From, string To);

internal sealed record DiagInfo(string Id, LocationInfo? Location, EquatableArray<string> Args);

/// <summary>One lifecycle enum: no symbols, no Locations (only LocationInfo), so the pipeline caches it.</summary>
internal sealed record LifecycleModel(
    string Namespace, string Name, string FullName, string Accessibility, string Initial,
    EquatableArray<string> States, EquatableArray<Edge> Edges, EquatableArray<DiagInfo> Diagnostics);

[Generator(LanguageNames.CSharp)]
public sealed class LifecycleGenerator : IIncrementalGenerator
{
    private const string LifecycleName = "Tarol.Primitives.LifecycleAttribute";
    private const string ToName = "Tarol.Primitives.ToAttribute";

    internal static readonly DiagnosticDescriptor NotAState = new("TPRM020", "Transition target is not a state",
        "'{0}' in [To] on '{1}' is not a member of '{2}'", "Tarol.Lifecycle", DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor Unreachable = new("TPRM021", "State is unreachable",
        "State '{0}' of '{1}' cannot be reached from the initial state '{2}'", "Tarol.Lifecycle", DiagnosticSeverity.Warning, true);
    internal static readonly DiagnosticDescriptor SelfOrDuplicate = new("TPRM022", "Redundant transition",
        "[To({0})] on '{1}' is {2}", "Tarol.Lifecycle", DiagnosticSeverity.Warning, true);
    internal static readonly DiagnosticDescriptor BadEnum = new("TPRM023", "Enum cannot be a lifecycle",
        "'{0}' cannot be a lifecycle: {1}", "Tarol.Lifecycle", DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor ToWithoutLifecycle = new("TPRM024", "[To] outside a lifecycle",
        "[To] on '{0}' has no effect: '{1}' has no [Lifecycle]", "Tarol.Lifecycle", DiagnosticSeverity.Error, true);

    private static readonly Dictionary<string, DiagnosticDescriptor> Descriptors = new()
    {
        [NotAState.Id] = NotAState, [Unreachable.Id] = Unreachable, [SelfOrDuplicate.Id] = SelfOrDuplicate,
        [BadEnum.Id] = BadEnum, [ToWithoutLifecycle.Id] = ToWithoutLifecycle,
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName(LifecycleName,
                static (node, _) => node is EnumDeclarationSyntax,
                static (ctx, ct) => Model((INamedTypeSymbol)ctx.TargetSymbol, ctx.Attributes[0], ct))
            .WithTrackingName("Lifecycle");

        context.RegisterSourceOutput(models, static (spc, model) =>
        {
            foreach (var d in model.Diagnostics)
                spc.ReportDiagnostic(Diagnostic.Create(Descriptors[d.Id], d.Location?.ToLocation(), d.Args.ToArray<object>()));
            if (model.Diagnostics.Any(static d => Descriptors[d.Id].DefaultSeverity == DiagnosticSeverity.Error))
                return;
            spc.AddSource(model.FullName + ".Lifecycle.g.cs", Emit(model));
        });

        // [To] on a member of an enum without [Lifecycle]: one diagnostic per member, no output.
        var orphans = context.SyntaxProvider.ForAttributeWithMetadataName(ToName,
                static (node, _) => node is EnumMemberDeclarationSyntax,
                static (ctx, _) => ctx.TargetSymbol.ContainingType.GetAttributes()
                        .Any(static a => a.AttributeClass?.ToDisplayString() == LifecycleName)
                    ? null
                    : new DiagInfo(ToWithoutLifecycle.Id, LocationInfo.From(ctx.TargetNode.GetLocation()),
                        new EquatableArray<string>(ImmutableArray.Create(ctx.TargetSymbol.Name, ctx.TargetSymbol.ContainingType.Name))))
            .Where(static d => d is not null);
        context.RegisterSourceOutput(orphans, static (spc, d) =>
            spc.ReportDiagnostic(Diagnostic.Create(ToWithoutLifecycle, d!.Location?.ToLocation(), d.Args.ToArray<object>())));
    }

    private static LifecycleModel Model(INamedTypeSymbol type, AttributeData lifecycle, CancellationToken ct)
    {
        var diags = new List<DiagInfo>();
        DiagInfo D(DiagnosticDescriptor d, Location? l, params string[] args)
            => new(d.Id, l is null ? null : LocationInfo.From(l), new EquatableArray<string>(args.ToImmutableArray()));
        var typeLocation = type.Locations.FirstOrDefault();

        var members = type.GetMembers().OfType<IFieldSymbol>().Where(static f => f.HasConstantValue).ToList();
        var byValue = new Dictionary<object, string>();
        foreach (var m in members)
        {
            if (byValue.TryGetValue(m.ConstantValue!, out var other))
                diags.Add(D(BadEnum, m.Locations.FirstOrDefault(), type.Name, $"'{m.Name}' and '{other}' have the same value"));
            else
                byValue[m.ConstantValue!] = m.Name;
        }
        if (type.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
            diags.Add(D(BadEnum, typeLocation, type.Name, "a [Flags] enum combines states"));
        if (members.Count == 0)
            diags.Add(D(BadEnum, typeLocation, type.Name, "it has no members"));

        var initialArg = lifecycle.NamedArguments.FirstOrDefault(static a => a.Key == "Initial").Value.Value as string;
        var initial = initialArg ?? members.FirstOrDefault(static m => System.Convert.ToInt64(m.ConstantValue) == 0)?.Name
                      ?? members.FirstOrDefault()?.Name ?? "";
        if (initialArg is not null && members.All(m => m.Name != initialArg))
            diags.Add(D(BadEnum, typeLocation, type.Name, $"Initial = \"{initialArg}\" names no member"));

        var edges = new List<Edge>();
        foreach (var m in members)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var attr in m.GetAttributes().Where(static a => a.AttributeClass?.ToDisplayString() == ToName))
            {
                var location = attr.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation();
                var args = attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Kind == TypedConstantKind.Array
                    ? attr.ConstructorArguments[0].Values
                    : attr.ConstructorArguments;
                foreach (var arg in args)
                {
                    if (!SymbolEqualityComparer.Default.Equals(arg.Type, type) || arg.Value is null || !byValue.TryGetValue(arg.Value, out var to))
                    {
                        diags.Add(D(NotAState, location, arg.ToCSharpString(), m.Name, type.Name));
                        continue;
                    }
                    var edge = new Edge(m.Name, to);
                    if (to == m.Name)
                        diags.Add(D(SelfOrDuplicate, location, to, m.Name, "a self-loop (a step to the same state is not a change)"));
                    else if (edges.Contains(edge))
                        diags.Add(D(SelfOrDuplicate, location, to, m.Name, "declared twice"));
                    else
                        edges.Add(edge);
                }
            }
        }

        // Reachability from the initial state (only for a valid declaration: one diagnostic per misuse).
        var hasErrors = diags.Any(static d => d.Id is "TPRM020" or "TPRM023");
        var reached = new HashSet<string> { initial };
        var queue = new Queue<string>(reached);
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            foreach (var e in edges.Where(e => e.From == s))
                if (reached.Add(e.To))
                    queue.Enqueue(e.To);
        }
        foreach (var m in members.Where(m => !hasErrors && !reached.Contains(m.Name)))
            diags.Add(D(Unreachable, m.Locations.FirstOrDefault(), m.Name, type.Name, initial));

        var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        return new LifecycleModel(ns, type.Name, type.ToDisplayString(),
            type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal", initial,
            new EquatableArray<string>(members.Select(static m => m.Name).ToImmutableArray()),
            new EquatableArray<Edge>(edges.ToImmutableArray()),
            new EquatableArray<DiagInfo>(diags.ToImmutableArray()));
    }

    private static string Emit(LifecycleModel m)
    {
        var t = "global::" + m.FullName;
        var outgoing = m.States.ToDictionary(s => s, s => m.Edges.Where(e => e.From == s).Select(e => e.To).ToList());
        var diagram = new StringBuilder("stateDiagram-v2\n    [*] --> " + m.Initial + "\n");
        foreach (var e in m.Edges)
            diagram.Append("    ").Append(e.From).Append(" --> ").Append(e.To).Append('\n');
        foreach (var s in m.States.Where(s => outgoing[s].Count == 0))
            diagram.Append("    ").Append(s).Append(" --> [*]\n");

        var sb = new StringBuilder("// <auto-generated/>\n#nullable enable\n\n");
        if (m.Namespace.Length > 0)
            sb.Append("namespace ").Append(m.Namespace).Append(";\n\n");
        sb.Append("/// <summary>The lifecycle of <see cref=\"").Append(m.Name).Append("\"/>: its edges, guards and diagram.</summary>\n");
        sb.Append("/// <remarks><code>\n");
        foreach (var line in diagram.ToString().TrimEnd('\n').Split('\n'))
            sb.Append("/// ").Append(line).Append('\n');
        sb.Append("/// </code></remarks>\n");
        sb.Append("[global::System.CodeDom.Compiler.GeneratedCode(\"Tarol.Primitives.Generators\", \"0.0.0\")]\n");
        sb.Append(m.Accessibility).Append(" static class ").Append(m.Name).Append("Lifecycle\n{\n");
        sb.Append("    /// <summary>The initial state.</summary>\n");
        sb.Append("    public const ").Append(t).Append(" Initial = ").Append(t).Append('.').Append(m.Initial).Append(";\n\n");
        sb.Append("    /// <summary>The lifecycle as a Mermaid state diagram.</summary>\n");
        sb.Append("    public const string Diagram = ").Append(SymbolDisplay.FormatLiteral(diagram.ToString(), true)).Append(";\n\n");
        sb.Append("    /// <summary>True when <paramref name=\"to\"/> is a declared edge from <paramref name=\"from\"/>.</summary>\n");
        sb.Append("    public static bool IsEdge(").Append(t).Append(" from, ").Append(t).Append(" to) => from switch\n    {\n");
        foreach (var s in m.States.Where(s => outgoing[s].Count > 0))
            sb.Append("        ").Append(t).Append('.').Append(s).Append(" => to is ")
              .Append(string.Join(" or ", outgoing[s].Select(x => t + "." + x))).Append(",\n");
        sb.Append("        _ => false,\n    };\n\n");
        sb.Append("    /// <summary>True when the state has no outgoing edge.</summary>\n");
        var terminals = m.States.Where(s => outgoing[s].Count == 0).Select(x => t + "." + x).ToList();
        sb.Append("    public static bool IsTerminal(").Append(t).Append(" state) => ")
          .Append(terminals.Count == 0 ? "false" : "state is " + string.Join(" or ", terminals)).Append(";\n\n");
        sb.Append("    /// <summary>The declared edges, in declaration order (for tests and documentation).</summary>\n");
        sb.Append("    public static global::System.ReadOnlySpan<(").Append(t).Append(" From, ").Append(t).Append(" To)> Edges => s_edges;\n\n");
        sb.Append("    private static readonly (").Append(t).Append(", ").Append(t).Append(")[] s_edges =\n    [\n");
        foreach (var e in m.Edges)
            sb.Append("        (").Append(t).Append('.').Append(e.From).Append(", ").Append(t).Append('.').Append(e.To).Append("),\n");
        sb.Append("    ];\n\n");
        sb.Append("    extension(").Append(t).Append(" state)\n    {\n");
        sb.Append("        /// <summary>True when this state may change to <paramref name=\"to\"/> (a declared edge).</summary>\n");
        sb.Append("        public bool CanTransitionTo(").Append(t).Append(" to) => IsEdge(state, to);\n\n");
        sb.Append("        /// <summary>Success when this state may change to <paramref name=\"to\"/>; otherwise <c>Error.Conflict(\"")
          .Append(m.Name).Append(".NotAllowed\")</c> with the step as detail.</summary>\n");
        sb.Append("        public global::Tarol.Results.Result RequireTransitionTo(").Append(t).Append(" to) => IsEdge(state, to)\n");
        sb.Append("            ? global::Tarol.Results.Result.Success()\n");
        sb.Append("            : global::Tarol.Results.Error.Conflict(\"").Append(m.Name).Append(".NotAllowed\", state.ToString() + \" -> \" + to.ToString());\n");
        sb.Append("    }\n}\n");
        return sb.ToString();
    }
}
