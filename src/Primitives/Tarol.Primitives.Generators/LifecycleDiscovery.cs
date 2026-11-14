using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

namespace Tarol.Primitives.Generators;

/// <summary>A declared edge of a lifecycle (member names).</summary>
internal sealed record LifecycleEdge(string From, string To);

/// <summary>A <c>[Transition&lt;TEnum&gt;(from, to)]</c> on an event that labels a declared edge of a lifecycle in this compilation.</summary>
internal sealed record LifecycleLabel(string Enum, string From, string To, string Event);

/// <summary>
/// One <c>[Lifecycle]</c> enum: its states, edges and diagnostics, as strings only (no symbols), so the pipeline caches it.
/// <paramref name="FullName"/> is the enum's fully qualified name, <paramref name="Class"/> the generated class's simple
/// name (containing types prefixed for a nested enum).
/// </summary>
internal sealed record LifecycleModel(
    string FullName,
    string? Namespace,
    string Name,
    string Class,
    string HintName,
    string Accessibility,
    string Initial,
    EquatableArray<string> States,
    EquatableArray<LifecycleEdge> Edges,
    EquatableArray<LifecycleLabel> Labels,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    /// <summary>The generated class, fully qualified.</summary>
    public string ClassFullName => "global::" + (Namespace is null ? "" : Namespace + ".") + Class;

    /// <summary>True when the declaration has no error, so the class is generated and aggregates may use it.</summary>
    public bool IsValid => Diagnostics.All(static d => d.Id is not ("TPRM016" or "TPRM019"));

    public LifecycleModel WithoutLocations() => this with { Diagnostics = default };
}

/// <summary>
/// The lifecycle part of discovery: the model of a <c>[Lifecycle]</c> enum (TPRM016–019), <c>[To]</c> outside a lifecycle
/// (TPRM020), <c>[Transition]</c> labels (TPRM021), and which lifecycle fields an <c>On</c> handler assigns.
/// </summary>
internal static class LifecycleDiscovery
{
    public const string LifecycleAttribute = "Tarol.Primitives.LifecycleAttribute";
    public const string ToAttribute = "Tarol.Primitives.ToAttribute";
    public const string TransitionAttribute = "Tarol.Primitives.TransitionAttribute`1";

    public static bool IsLifecycle(ITypeSymbol? type)
        => type is INamedTypeSymbol { TypeKind: Microsoft.CodeAnalysis.TypeKind.Enum } e && Symbols.HasAttribute(e, "LifecycleAttribute");

    /// <summary>The model of a <c>[Lifecycle]</c> enum (also for an enum from a referenced assembly, without locations).</summary>
    public static LifecycleModel Model(INamedTypeSymbol type, CancellationToken ct)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var location = Discovery.LocationOf(type);
        var name = type.Name;

        var members = type.GetMembers().OfType<IFieldSymbol>().Where(static f => f.HasConstantValue).ToList();
        var byValue = new Dictionary<object, string>();
        foreach (var member in members)
        {
            if (byValue.TryGetValue(member.ConstantValue!, out var other))
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnusableLifecycle, Discovery.LocationOf(member), name, $"'{member.Name}' and '{other}' have the same value, so they are one state"));
            else
                byValue[member.ConstantValue!] = member.Name;
        }

        if (type.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnusableLifecycle, location, name, "a [Flags] enum combines states"));
        if (members.Count == 0)
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnusableLifecycle, location, name, "it has no members"));
        if (type.IsFileLocal)
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnusableLifecycle, location, name, "a file-local enum cannot be used by its generated class"));

        var accessibility = "public";
        var prefix = "";
        for (var container = type.ContainingType; container is not null; container = container.ContainingType)
        {
            if (container.IsGenericType)
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnusableLifecycle, location, name, $"it is nested in the generic type '{container.Name}'"));
            prefix = container.Name + prefix;
        }

        for (ISymbol? s = type; s is INamedTypeSymbol t; s = t.ContainingType)
        {
            switch (t.DeclaredAccessibility)
            {
                case Accessibility.Public:
                    break;
                case Accessibility.Internal or Accessibility.ProtectedOrInternal:
                    accessibility = "internal";
                    break;
                default:
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnusableLifecycle, location, name, $"'{t.Name}' is {t.DeclaredAccessibility.ToString().ToLowerInvariant()}; its generated class is declared in the namespace and needs at least internal access"));
                    break;
            }
        }

        var lifecycle = type.GetAttributes().FirstOrDefault(static a => Symbols.Is(a.AttributeClass, "LifecycleAttribute"));
        var initialArgument = lifecycle?.NamedArguments.FirstOrDefault(static a => a.Key == "Initial").Value.Value as string;
        var initial = initialArgument
                      ?? members.FirstOrDefault(static m => System.Convert.ToDecimal(m.ConstantValue, System.Globalization.CultureInfo.InvariantCulture) == 0)?.Name
                      ?? members.FirstOrDefault()?.Name ?? "";
        if (initialArgument is not null && members.All(m => m.Name != initialArgument))
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnusableLifecycle, location, name, $"Initial = \"{initialArgument}\" names no member"));

        var edges = new List<LifecycleEdge>();
        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var attribute in member.GetAttributes().Where(static a => Symbols.Is(a.AttributeClass, "ToAttribute")))
            {
                var at = attribute.ApplicationSyntaxReference is { } r ? LocationInfo.From(r.GetSyntax(ct).GetLocation()) : null;
                var arguments = attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array
                    ? attribute.ConstructorArguments[0].Values
                    : attribute.ConstructorArguments;
                if (arguments.IsDefaultOrEmpty)
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.BadTarget, at, "(nothing)", member.Name, name));
                foreach (var argument in arguments)
                {
                    if (argument.Kind == TypedConstantKind.Error)
                        continue; // the compiler reports it
                    if (!SymbolEqualityComparer.Default.Equals(argument.Type, type) || argument.Value is null || !byValue.TryGetValue(argument.Value, out var to))
                    {
                        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.BadTarget, at, argument.ToCSharpString(), member.Name, name));
                        continue;
                    }

                    var edge = new LifecycleEdge(member.Name, to);
                    if (to == member.Name)
                        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RedundantEdge, at, to, member.Name, "a self-loop: a step to the same state is no change and is never checked"));
                    else if (edges.Contains(edge))
                        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RedundantEdge, at, to, member.Name, "declared twice"));
                    else
                        edges.Add(edge);
                }
            }
        }

        // Reachability from the initial state, only for a declaration without errors (one diagnostic per misuse).
        if (diagnostics.All(static d => d.Id is not ("TPRM016" or "TPRM019")))
        {
            var reached = new HashSet<string>(System.StringComparer.Ordinal) { initial };
            var queue = new Queue<string>(reached);
            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                foreach (var edge in edges.Where(e => e.From == state))
                    if (reached.Add(edge.To))
                        queue.Enqueue(edge.To);
            }

            foreach (var member in members.Where(m => byValue[m.ConstantValue!] == m.Name && !reached.Contains(m.Name)))
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Unreachable, Discovery.LocationOf(member), member.Name, name, initial));
        }

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
        var @class = prefix + name + "Lifecycle";
        return new LifecycleModel(
            Symbols.FullName(type), ns, name, @class, (ns is null ? "" : ns + ".") + @class + ".g.cs", accessibility, initial,
            new EquatableArray<string>(members.Where(m => byValue[m.ConstantValue!] == m.Name).Select(static m => m.Name).ToImmutableArray()),
            new EquatableArray<LifecycleEdge>(edges.ToImmutableArray()),
            default,
            new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutableArray()));
    }

    /// <summary>TPRM020: <c>[To]</c> on a member of an enum without <c>[Lifecycle]</c>.</summary>
    public static DiagnosticInfo? ToWithoutLifecycle(ISymbol member, SyntaxNode node)
        => IsLifecycle(member.ContainingType)
            ? null
            : DiagnosticInfo.Create(Diagnostics.ToWithoutLifecycle, LocationInfo.From(node.GetLocation()), member.Name, member.ContainingType.Name);

    /// <summary>The <c>[Transition]</c> labels on one event type, or TPRM021 for those that are not a declared edge.</summary>
    public static ImmutableArray<(LifecycleLabel? Label, DiagnosticInfo? Diagnostic)> Labels(INamedTypeSymbol type, ImmutableArray<AttributeData> attributes, CancellationToken ct)
    {
        var builder = ImmutableArray.CreateBuilder<(LifecycleLabel?, DiagnosticInfo?)>();
        foreach (var attribute in attributes)
        {
            ct.ThrowIfCancellationRequested();
            if (attribute.AttributeClass is not { TypeArguments.Length: 1 } attributeClass
                || attributeClass.TypeArguments[0] is not INamedTypeSymbol { TypeKind: Microsoft.CodeAnalysis.TypeKind.Enum } e
                || attribute.ConstructorArguments.Length != 2
                || attribute.ConstructorArguments[0].Kind != TypedConstantKind.Enum
                || attribute.ConstructorArguments[1].Kind != TypedConstantKind.Enum)
                continue;
            var fromArg = attribute.ConstructorArguments[0];
            var toArg = attribute.ConstructorArguments[1];
            var location = attribute.ApplicationSyntaxReference is { } r ? LocationInfo.From(r.GetSyntax(ct).GetLocation()) : null;
            var from = MemberName(e, fromArg.Value);
            var to = MemberName(e, toArg.Value);
            var text = $"{from ?? fromArg.ToCSharpString()} -> {to ?? toArg.ToCSharpString()}";
            string? why = null;
            if (!IsLifecycle(e))
                why = $"'{e.Name}' has no [Lifecycle]";
            else if (from is null || to is null)
                why = "it names a value that is not a member";
            else
            {
                var model = Model(e, ct);
                if (!model.IsValid)
                    continue; // the enum's own error is reported
                if (!model.Edges.Contains(new LifecycleEdge(from, to)))
                    why = $"'{from}' declares no [To({to})]";
            }

            builder.Add(why is null
                ? (new LifecycleLabel(Symbols.FullName(e), from!, to!, type.Name), null)
                : (null, DiagnosticInfo.Create(Diagnostics.TransitionNotAnEdge, location, type.Name, text, e.Name, why)));
        }

        return builder.ToImmutable();
    }

    private static string? MemberName(INamedTypeSymbol type, object? value)
        => value is null ? null : type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value))?.Name;

    /// <summary>The lifecycle fields of a root or entity: instance, non-readonly fields (auto-property backing fields included) of a valid <c>[Lifecycle]</c> enum type.</summary>
    public static List<(IFieldSymbol Field, ISymbol Member, LifecycleField Model)> Fields(INamedTypeSymbol type, CancellationToken ct)
    {
        var list = new List<(IFieldSymbol, ISymbol, LifecycleField)>();
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.IsReadOnly || field.Name.StartsWith("__", System.StringComparison.Ordinal) || !IsLifecycle(field.Type))
                continue;
            var e = (INamedTypeSymbol)field.Type;
            var model = Model(e, ct);
            if (!model.IsValid)
                continue;
            var member = (ISymbol?)field.AssociatedSymbol ?? field;
            list.Add((field, member, new LifecycleField(field.Name, member.Name, Symbols.FullName(e), model.ClassFullName, Symbols.FullName(e) + "." + model.Initial)));
        }

        return list;
    }

    /// <summary>
    /// The lifecycle members an <c>On</c> handler assigns (directly or through a private helper of the same type it calls),
    /// so the generator emits step checks only for the events that can change a state (L-6).
    /// </summary>
    public static EquatableArray<string> Assigns(IMethodSymbol handler, SemanticModel model, HashSet<ISymbol> members, CancellationToken ct)
    {
        var assigned = new SortedSet<string>(System.StringComparer.Ordinal);
        Walk(handler, 0, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default));
        return new EquatableArray<string>(assigned.ToImmutableArray());

        void Walk(IMethodSymbol method, int depth, HashSet<IMethodSymbol> seen)
        {
            if (depth > 8 || !seen.Add(method))
                return;
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax(ct);
                var semantic = syntax.SyntaxTree == model.SyntaxTree ? model : model.Compilation.GetSemanticModel(syntax.SyntaxTree);
                if (semantic.GetOperation(syntax, ct) is not { } body)
                    continue;
                foreach (var operation in body.DescendantsAndSelf())
                {
                    var target = operation switch
                    {
                        IAssignmentOperation a => a.Target,
                        IIncrementOrDecrementOperation i => i.Target,
                        _ => null,
                    };
                    var member = target switch
                    {
                        IFieldReferenceOperation { Instance: IInstanceReferenceOperation } f => (ISymbol)f.Field,
                        IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } p => p.Property,
                        _ => null,
                    };
                    if (member is not null && members.Contains(member.OriginalDefinition))
                        assigned.Add(member.Name);

                    if (operation is IInvocationOperation { Instance: null or IInstanceReferenceOperation } call
                        && SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, method.ContainingType))
                        Walk(call.TargetMethod.OriginalDefinition, depth + 1, seen);
                }
            }
        }
    }
}
