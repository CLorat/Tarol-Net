using System.Linq;
using Microsoft.CodeAnalysis;

namespace Tarol.Primitives.Generators;

internal static class Symbols
{
    public static string FullName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>Whether <paramref name="type"/> is the Tarol.Primitives type named <paramref name="name"/> (metadata name, top level).</summary>
    public static bool Is(INamedTypeSymbol? type, string name)
        => type is { ContainingType: null } && type.MetadataName == name && IsLibraryNamespace(type.ContainingNamespace);

    private static bool IsLibraryNamespace(INamespaceSymbol? ns)
        => ns is { Name: "Primitives", ContainingNamespace: { Name: "Tarol", ContainingNamespace.IsGlobalNamespace: true } };

    /// <summary>The root of a class that derives directly from <c>AggregateRoot&lt;itself&gt;</c>.</summary>
    public static INamedTypeSymbol? RootOf(INamedTypeSymbol type)
        => type.BaseType is { } b && Is(b.OriginalDefinition, "AggregateRoot`1") && SymbolEqualityComparer.Default.Equals(b.TypeArguments[0], type)
            ? type
            : null;

    /// <summary>The root of a class that derives directly from <c>Entity&lt;TRoot, itself&gt;</c>.</summary>
    public static INamedTypeSymbol? EntityRootOf(INamedTypeSymbol type)
        => type.BaseType is { } b && Is(b.OriginalDefinition, "Entity`2") && SymbolEqualityComparer.Default.Equals(b.TypeArguments[1], type)
            ? b.TypeArguments[0] as INamedTypeSymbol
            : null;

    /// <summary>
    /// Whether a base type is one of the library's nested event bases (<c>AggregateRoot&lt;R&gt;.Event</c>,
    /// <c>Entity&lt;R, X&gt;.Event/Created/CreatedIn&lt;P&gt;/Deleted</c>), and what it says.
    /// </summary>
    public static (EventKind Kind, INamedTypeSymbol? Root, INamedTypeSymbol? Target, INamedTypeSymbol? Parent) LibraryEventBase(INamedTypeSymbol type)
    {
        if (type.ContainingType is not { } outer)
            return default;

        if (Is(outer.OriginalDefinition, "AggregateRoot`1") && type.Name == "Event" && outer.TypeArguments[0] is INamedTypeSymbol root)
            return (EventKind.RootEvent, root, root, null);

        if (!Is(outer.OriginalDefinition, "Entity`2") || outer.TypeArguments[0] is not INamedTypeSymbol entityRoot || outer.TypeArguments[1] is not INamedTypeSymbol target)
            return default;

        return type.MetadataName switch
        {
            "Event" => (EventKind.EntityEvent, entityRoot, target, null),
            "Created" => (EventKind.Created, entityRoot, target, null),
            "Deleted" => (EventKind.Deleted, entityRoot, target, null),
            "CreatedIn`1" => (EventKind.CreatedIn, entityRoot, target, type.TypeArguments[0] as INamedTypeSymbol),
            _ => default,
        };
    }

    /// <summary>The child type of an <c>EntitySet&lt;T&gt;</c> or <c>EntityRef&lt;T&gt;</c>, and whether it is a ref.</summary>
    public static (ITypeSymbol? Child, bool IsRef) ContainerChild(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true } named)
            return default;
        if (Is(named.OriginalDefinition, "EntitySet`1"))
            return (named.TypeArguments[0], false);
        if (Is(named.OriginalDefinition, "EntityRef`1"))
            return (named.TypeArguments[0], true);
        return default;
    }

    /// <summary><c>void On(TEvent e)</c>, instance, one by-value parameter whose type is a domain event.</summary>
    public static bool IsHandler(IMethodSymbol method)
        => method is { Name: "On", IsStatic: false, ReturnsVoid: true, IsGenericMethod: false, Parameters.Length: 1 }
           && method.Parameters[0].RefKind == RefKind.None
           && IsDomainEvent(method.Parameters[0].Type);

    public static bool IsDomainEvent(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (Is(current, "DomainEvent"))
                return true;
        return false;
    }

    public static bool IsInvariantShape(IMethodSymbol method)
        => method is { IsStatic: false, IsGenericMethod: false, Parameters.Length: 0 }
           && method.ReturnType is INamedTypeSymbol { Name: "Result", IsGenericType: false, ContainingNamespace: { Name: "Results", ContainingNamespace: { Name: "Tarol", ContainingNamespace.IsGlobalNamespace: true } } };

    /// <summary>Whether <paramref name="type"/> is <c>Tarol.StronglyTypedId.Id&lt;<paramref name="of"/>&gt;</c>.</summary>
    public static bool IsIdOf(ITypeSymbol type, ITypeSymbol of)
        => type is INamedTypeSymbol { Name: "Id", IsGenericType: true } id
           && id.ContainingNamespace is { Name: "StronglyTypedId", ContainingNamespace: { Name: "Tarol", ContainingNamespace.IsGlobalNamespace: true } }
           && SymbolEqualityComparer.Default.Equals(id.TypeArguments[0], of);

    public static bool HasAttribute(ISymbol symbol, string name) => symbol.GetAttributes().Any(a => Is(a.AttributeClass, name));
}
