using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Tarol.Changes.Generators;

/// <summary>
///     Finds <c>[OnEvent&lt;T&gt;]</c> factories (<c>ForAttributeWithMetadataName</c>: only methods carrying the attribute are
///     visited) and turns each into a <see cref="ReactionModel"/> of names only. A factory, its change and its event are
///     user code; nothing here depends on another generator's output.
/// </summary>
internal static class ReactionDiscovery
{
    public const string AttributeName = "Tarol.Changes.OnEventAttribute`1";
    private const string ChangeName = "Tarol.Changes.IChange`1";
    private const string CreationName = "Tarol.Changes.ICreation`1";
    private const string RecordedName = "Tarol.Primitives.RecordedEvent";

    private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;

    public static ImmutableArray<DiscoveredReaction> Transform(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not IMethodSymbol method)
            return ImmutableArray<DiscoveredReaction>.Empty;
        var compilation = context.SemanticModel.Compilation;
        var builder = ImmutableArray.CreateBuilder<DiscoveredReaction>(context.Attributes.Length);
        foreach (var attribute in context.Attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(One(method, attribute, compilation));
        }
        return builder.MoveToImmutable();
    }

    private static DiscoveredReaction One(IMethodSymbol method, AttributeData attribute, Compilation compilation)
    {
        var location = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                                         ?? method.Locations.FirstOrDefault() ?? Location.None);
        if (attribute.AttributeClass is not { TypeArguments.Length: 1 } attributeClass || attributeClass.TypeArguments[0] is not INamedTypeSymbol domainEvent)
            return Fail(DiagnosticInfo.Create(Diagnostics.ReactionShape, location, "?", method.Name, "the event type is not a named type"));
        var change = method.ContainingType;
        var eventName = domainEvent.Name;
        var factory = change.Name + "." + method.Name;

        var why = Shape(method, change, domainEvent, compilation, out var returns);
        if (why is not null)
            return Fail(DiagnosticInfo.Create(Diagnostics.ReactionShape, location, eventName, factory, why));

        string? name = change.Name;
        string? dueAt = null;
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == "Name")
                name = argument.Value.Value as string;
            else if (argument.Key == "DueAt")
                dueAt = argument.Value.Value as string;
        }
        if (string.IsNullOrWhiteSpace(name))
            return Fail(DiagnosticInfo.Create(Diagnostics.ReactionShape, location, eventName, factory, "Name is empty"));
        if (dueAt is not null && !IsDateTimeOffsetProperty(domainEvent, dueAt))
            return Fail(DiagnosticInfo.Create(Diagnostics.ReactionDueAt, location, eventName, dueAt, factory));

        var changeName = change.ToDisplayString(Qualified);
        var eventFullName = domainEvent.ToDisplayString(Qualified);
        var model = new ReactionModel(name!, changeName, eventFullName, Depth(domainEvent), changeName + "." + method.Name,
            method.Parameters.Length == 2, dueAt, returns, TargetOf(change, compilation));
        return new DiscoveredReaction(model, new ReactionSite(name!, changeName, eventFullName, location),
            new EquatableArray<DiagnosticInfo>(ImmutableArray<DiagnosticInfo>.Empty));

        static DiscoveredReaction Fail(DiagnosticInfo diagnostic)
            => new(null, null, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(diagnostic)));
    }

    private static string? Shape(IMethodSymbol method, INamedTypeSymbol change, INamedTypeSymbol domainEvent, Compilation compilation, out int returns)
    {
        returns = 0;
        var changeInterface = compilation.GetTypeByMetadataName(ChangeName);
        var creationInterface = compilation.GetTypeByMetadataName(CreationName);
        var isChange = change.AllInterfaces.Any(i =>
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, changeInterface) ||
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, creationInterface));
        if (!isChange)
            return $"{change.Name} is not a change (IChange<T>, ICreation<T> or ILoads<T>)";
        if (change.IsGenericType || change.IsFileLocal)
            return $"{change.Name} is generic or file-local";
        if (!method.IsStatic)
            return "the factory must be static";
        if (method.IsGenericMethod)
            return "the factory cannot be generic";
        if (!compilation.IsSymbolAccessibleWithin(method, compilation.Assembly))
            return "the factory (or a type containing it) is private; generated code must call it";
        returns = Returns(method.ReturnType, change, compilation);
        if (returns < 0)
            return $"the factory must return {change.Name}, {change.Name}? (nothing to do) or IEnumerable<{change.Name}> (one run per element)";
        var parameters = method.Parameters;
        if (parameters.Length is not (1 or 2) || !SymbolEqualityComparer.Default.Equals(parameters[0].Type, domainEvent)
            || parameters.Any(p => p.RefKind != RefKind.None))
            return $"the parameters must be ({domainEvent.Name}) or ({domainEvent.Name}, RecordedEvent)";
        if (parameters.Length == 2 && parameters[1].Type.TypeKind == TypeKind.Error)
            return $"the type '{parameters[1].Type.ToDisplayString()}' of the second parameter is not found (missing 'using Tarol.Primitives;'?)"; // Tatalu FB-F15.1-1
        if (parameters.Length == 2 && parameters[1].Type.ToDisplayString() != RecordedName)
            return $"the second parameter must be Tarol.Primitives.RecordedEvent, not {parameters[1].Type.ToDisplayString()}";
        return null;
    }

    // 0: the change; 1: the change or null (R-2); 2: several changes (R-1, IEnumerable<T> or a type implementing it); -1: neither
    private static int Returns(ITypeSymbol type, INamedTypeSymbol change, Compilation compilation)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            return SymbolEqualityComparer.Default.Equals(nullable.TypeArguments[0], change) ? 1 : -1;
        if (SymbolEqualityComparer.Default.Equals(type, change))
            return type.NullableAnnotation == NullableAnnotation.Annotated ? 1 : 0;
        var enumerable = compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T);
        bool IsEnumerableOfChange(ITypeSymbol t) => t is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, enumerable)
            && SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], change);
        if (type.SpecialType == SpecialType.System_String)
            return -1;
        return IsEnumerableOfChange(type) || type.AllInterfaces.Any(IsEnumerableOfChange) ? 2 : -1;
    }

    // the aggregate T of IChange<T>, whose Target keys each element of a fan-out (R-1); null for a creation
    private static string? TargetOf(INamedTypeSymbol change, Compilation compilation)
    {
        var changeInterface = compilation.GetTypeByMetadataName(ChangeName);
        var target = change.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, changeInterface));
        return target?.TypeArguments[0].ToDisplayString(Qualified);
    }

    private static bool IsDateTimeOffsetProperty(INamedTypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
            foreach (var member in current.GetMembers(name))
                if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public } property
                    && property.Type.ToDisplayString() == "System.DateTimeOffset")
                    return true;
        return false;
    }

    private static int Depth(INamedTypeSymbol type)
    {
        var depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            depth++;
        return depth;
    }

    /// <summary>TCHG007: a name used by two changes, or one change with two factories for one event.</summary>
    public static IEnumerable<DiagnosticInfo> Duplicates(ImmutableArray<ReactionSite> sites)
    {
        foreach (var group in sites.GroupBy(s => s.Name, System.StringComparer.Ordinal))
        {
            var list = group.ToList();
            var clash = list.Select(s => s.Change).Distinct(System.StringComparer.Ordinal).Count() > 1
                        || list.GroupBy(s => s.Event, System.StringComparer.Ordinal).Any(g => g.Count() > 1);
            if (!clash)
                continue;
            var where = string.Join(", ", list.Select(s => Simple(s.Change) + " on " + Simple(s.Event)).OrderBy(x => x, System.StringComparer.Ordinal));
            foreach (var site in list.Skip(1))
                yield return DiagnosticInfo.Create(Diagnostics.ReactionName, site.Location, group.Key, where);
        }
    }

    private static string Simple(string qualified) => qualified.Substring(qualified.LastIndexOf('.') + 1);
}
