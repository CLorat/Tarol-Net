using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Primitives.Generators;

/// <summary>Turns one class or record declaration into a <see cref="TypeModel"/> (or nothing, for unrelated types).</summary>
internal static class Discovery
{
    public const string Namespace = "Tarol.Primitives";

    public static bool IsCandidate(SyntaxNode node)
        => node is ClassDeclarationSyntax { BaseList: not null } or RecordDeclarationSyntax { BaseList: not null };

    public static TypeModel? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var node = (TypeDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(node, ct) is not INamedTypeSymbol type)
            return null;

        // One model per type: the first declaration that has a base list.
        var first = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(static d => d.BaseList is not null);
        if (first != node)
            return null;

        if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class && !type.IsRecord)
        {
            if (Symbols.RootOf(type) is { } root)
                return Owner(TypeKind.Root, type, root, ct);
            if (Symbols.EntityRootOf(type) is { } entityRoot)
                return Owner(TypeKind.Entity, type, entityRoot, ct);
            return null;
        }

        return type.IsRecord ? Event(type, ct) : null;
    }

    private static TypeModel Owner(TypeKind kind, INamedTypeSymbol type, INamedTypeSymbol root, CancellationToken ct)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var location = LocationOf(type);
        var canEmit = true;

        var notPartial = NotPartial(type, ct);
        if (notPartial is not null)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.NotPartial, location, type.Name, notPartial));
            canEmit = false;
        }

        if (type.IsGenericType || type.ContainingType?.IsGenericType == true)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Unsupported, location, type.Name, "generic aggregates and entities (and entities nested in generic types) are not supported"));
            canEmit = false;
        }
        else if (type.IsFileLocal)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Unsupported, location, type.Name, "file-local aggregates and entities are not supported"));
            canEmit = false;
        }

        var members = new List<ContainerMember>();
        var handlers = new List<HandlerModel>();
        var invariants = new List<string>();
        var hasIdConstructor = false;
        var stored = !Symbols.HasAttribute(root, "LoadByReplayAttribute");
        var state = new List<StateField>();

        foreach (var member in type.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (stored)
                StateDiscovery.Capture(member, state, diagnostics);
            if (member.IsImplicitlyDeclared)
                continue;

            switch (member)
            {
                case IFieldSymbol { IsStatic: false } field when Symbols.ContainerChild(field.Type) is var (child, isRef) && child is not null:
                    if (!field.IsReadOnly)
                        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.AssignableContainer, LocationOf(field), field.Name));
                    members.Add(new ContainerMember(field.Name, Symbols.FullName(child), isRef, true, StateDiscovery.NameOf(field.Name, field), LocationOf(field)));
                    break;

                case IPropertySymbol { IsStatic: false } property when Symbols.ContainerChild(property.Type) is var (child, isRef) && child is not null:
                    if (property.SetMethod is not null)
                        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.AssignableContainer, LocationOf(property), property.Name));
                    if (property.GetMethod is not null)
                        members.Add(new ContainerMember(property.Name, Symbols.FullName(child), isRef, false, StateDiscovery.NameOf(property.Name, property), LocationOf(property)));
                    break;

                case IMethodSymbol { MethodKind: MethodKind.Ordinary } method when Symbols.IsHandler(method):
                    handlers.Add(new HandlerModel(Symbols.FullName(method.Parameters[0].Type), LocationOf(method)));
                    break;

                case IMethodSymbol { MethodKind: MethodKind.Ordinary } method when method.GetAttributes().Any(static a => Symbols.Is(a.AttributeClass, "InvariantAttribute")):
                    if (Symbols.IsInvariantShape(method))
                        invariants.Add(method.Name);
                    else
                        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.InvariantShape, LocationOf(method), method.Name));
                    break;

                case IMethodSymbol { MethodKind: MethodKind.Constructor, IsStatic: false } constructor:
                    hasIdConstructor |= kind == TypeKind.Root
                        ? constructor.Parameters.Length == 1 && Symbols.IsIdOf(constructor.Parameters[0].Type, type)
                        : constructor.Parameters.Length == 2 && Symbols.IsIdOf(constructor.Parameters[0].Type, type)
                          && SymbolEqualityComparer.Default.Equals(constructor.Parameters[1].Type, root);
                    break;
            }
        }

        if (stored)
            StateDiscovery.CheckNames(kind, type, state, members, diagnostics);
        else if (kind == TypeKind.Root)
            StateDiscovery.CheckUnusedNames(type, diagnostics);
        if (kind == TypeKind.Entity && Symbols.HasAttribute(type, "LoadByReplayAttribute"))
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.StateAttributeIgnored, location, "[LoadByReplay]", type.Name, "it belongs on the aggregate root; an entity is stored or replayed with its root"));

        return new TypeModel(
            kind, Symbols.FullName(root), Symbols.FullName(type), type.Name, HintName(type), NamespaceOf(type),
            Containers(type, ct), canEmit,
            Array(members), Array(handlers), Array(invariants), hasIdConstructor,
            Array(state), !stored,
            EventKind.None, null, null, type.IsAbstract, default, "", 0, location, Array(diagnostics));
    }

    private static TypeModel? Event(INamedTypeSymbol type, CancellationToken ct)
    {
        var bases = new List<string>();
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            ct.ThrowIfCancellationRequested();
            if (Symbols.LibraryEventBase(current) is var (kind, root, target, parent) && kind != EventKind.None)
            {
                var (alias, version) = Alias(type, root!);
                return new TypeModel(
                    TypeKind.Event, Symbols.FullName(root!), Symbols.FullName(type), type.Name, HintName(type), NamespaceOf(type),
                    default, true, default, default, default, false, default, false,
                    kind, Symbols.FullName(target!), parent is null ? null : Symbols.FullName(parent), type.IsAbstract,
                    new EquatableArray<string>(bases.ToImmutableArray()), alias, version, LocationOf(type), default);
            }

            if (Symbols.Is(current, "DomainEvent"))
            {
                return new TypeModel(
                    TypeKind.Event, "", Symbols.FullName(type), type.Name, HintName(type), NamespaceOf(type),
                    default, false, default, default, default, false, default, false, EventKind.None, null, null, type.IsAbstract, default, "", 0,
                    LocationOf(type),
                    Array(new List<DiagnosticInfo> { DiagnosticInfo.Create(Diagnostics.Unsupported, LocationOf(type), type.Name, "an event derives from the Event, Created, CreatedIn<TParent> or Deleted base of the type it targets, not from DomainEvent") }));
            }

            bases.Add(Symbols.FullName(current));
        }

        return null;
    }

    private static (string Alias, int Version) Alias(INamedTypeSymbol type, INamedTypeSymbol root)
    {
        var attribute = type.GetAttributes().FirstOrDefault(static a => Symbols.Is(a.AttributeClass, "EventTypeAttribute"));
        if (attribute is { ConstructorArguments.Length: >= 1 } && attribute.ConstructorArguments[0].Value is string pinned)
        {
            var version = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is int v ? v : 1;
            return (pinned, version);
        }

        var path = new List<string>();
        for (var container = type.ContainingType; container is not null; container = container.ContainingType)
            path.Insert(0, container.Name);
        // Events nested in the root are named from the root: gallery.opened, not gallery.gallery.opened.
        if (path.Count > 0 && path[0] == root.Name)
            path.RemoveAt(0);

        var sb = new StringBuilder(Snake(root.Name));
        foreach (var segment in path)
            sb.Append('.').Append(Snake(segment));
        sb.Append('.').Append(Snake(type.Name));
        return (sb.ToString(), 1);
    }

    public static string Snake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string? NotPartial(INamedTypeSymbol type, CancellationToken ct)
    {
        if (!IsPartial(type, ct))
            return "";
        for (var container = type.ContainingType; container is not null; container = container.ContainingType)
            if (!IsPartial(container, ct))
                return $" (its containing type '{container.Name}' is not partial)";
        return null;
    }

    private static bool IsPartial(INamedTypeSymbol type, CancellationToken ct)
        => type.DeclaringSyntaxReferences.Any(r => r.GetSyntax(ct) is TypeDeclarationSyntax d && d.Modifiers.Any(SyntaxKind.PartialKeyword));

    private static EquatableArray<ContainerModel> Containers(INamedTypeSymbol type, CancellationToken ct)
    {
        var list = new List<ContainerModel>();
        for (var container = type.ContainingType; container is not null; container = container.ContainingType)
        {
            var syntax = container.DeclaringSyntaxReferences.Select(r => r.GetSyntax(ct)).OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var keyword = syntax switch
            {
                RecordDeclarationSyntax { ClassOrStructKeyword.RawKind: (int)SyntaxKind.StructKeyword } => "record struct",
                RecordDeclarationSyntax => "record",
                StructDeclarationSyntax => "struct",
                InterfaceDeclarationSyntax => "interface",
                _ => "class",
            };
            list.Insert(0, new ContainerModel(keyword, container.Name));
        }

        return Array(list);
    }

    private static string? NamespaceOf(INamedTypeSymbol type)
        => type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

    private static string HintName(INamedTypeSymbol type)
    {
        var name = Symbols.FullName(type).Replace("global::", string.Empty);
        var sb = new StringBuilder(name.Length + 16);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '.' ? ch : '_');
        return sb.Append(".Aggregate.g.cs").ToString();
    }

    internal static LocationInfo? LocationOf(ISymbol symbol)
        => symbol.Locations.FirstOrDefault(static l => l.IsInSource) is { } location ? LocationInfo.From(location) : null;

    private static EquatableArray<T> Array<T>(List<T> items) where T : System.IEquatable<T>
        => new(items.ToImmutableArray());
}
