using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Primitives.Generators;

/// <summary>
/// The stored-state part of discovery: which fields of a root or entity are captured, under which JSON names, how each is
/// written, which fields cannot be stored (TPRM024), and the shape descriptor that feeds the schema hash.
/// </summary>
internal static class StateDiscovery
{
    /// <summary>Field types with nullable reference annotations, so the generated <c>ref</c> accessors match the field.</summary>
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Adds <paramref name="member"/> to the captured state when it is an instance field (or a field-like event, reported).</summary>
    public static void Capture(ISymbol member, List<StateField> state, List<DiagnosticInfo> diagnostics)
    {
        if (member is IEventSymbol { IsStatic: false } ev && IsFieldLike(ev))
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Uncapturable, Discovery.LocationOf(ev), ev.Name, ev.ContainingType.Name, "a delegate (a field-like event)",
                "Raise a domain event instead, or mark the aggregate [LoadByReplay]"));
            return;
        }

        if (member is not IFieldSymbol { IsStatic: false, IsConst: false } field)
            return;
        if (Symbols.ContainerChild(field.Type).Child is not null)
            return; // an EntitySet/EntityRef: its children are nested, not a field

        var property = field.AssociatedSymbol as IPropertySymbol;
        if (field.IsImplicitlyDeclared && property is null)
            return; // compiler-generated (a captured primary-constructor parameter is reported by TPRM013's rules, not stored)
        if (field.Name.StartsWith("__", System.StringComparison.Ordinal))
            return; // generated plumbing (__map, __Parent)
        if (Symbols.HasAttribute(field, "TransientAttribute") || (property is not null && Symbols.HasAttribute(property, "TransientAttribute")))
            return;

        var memberName = property?.Name ?? field.Name;
        var location = Discovery.LocationOf((ISymbol?)property ?? field);
        if (Uncapturable(field.Type, 0) is { } reason)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Uncapturable, location, memberName, field.ContainingType.Name, reason,
                "Hold children in an EntitySet or EntityRef, keep an Id<T>, mark derived data [Transient], or mark the aggregate [LoadByReplay]"));
            return;
        }

        var (kind, wrap, value, read, underlying, members) = Classify(field.Type);
        state.Add(new StateField(
            field.Name, memberName, NameOf(property?.Name ?? field.Name, (ISymbol?)property ?? field, field),
            field.Type.ToDisplayString(TypeFormat), kind, wrap, value, read, underlying,
            new EquatableArray<string>(members), Describe(field.Type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0), location));
    }

    /// <summary>The JSON name of a member: <c>[StateName]</c>, else the name without leading <c>_</c> in camelCase.</summary>
    public static string NameOf(string name, ISymbol member, IFieldSymbol? backing = null)
    {
        if ((Pinned(member) ?? (backing is null ? null : Pinned(backing))) is { } pinned)
            return pinned;
        return CamelCase(name.TrimStart('_'));
    }

    /// <summary>TPRM025 (two members, one name; a reserved name) and TPRM026 (<c>[StateName]</c> on a member that is not stored).</summary>
    public static void CheckNames(TypeKind kind, INamedTypeSymbol type, List<StateField> state, List<ContainerMember> members, List<DiagnosticInfo> diagnostics)
    {
        var reserved = kind == TypeKind.Root ? new[] { "id", "version", "schema" } : new[] { "id" };
        var seen = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var (name, member, location) in state.Select(static f => (f.Name, f.Member, f.Location)).Concat(members.Select(static m => (Name: m.StateName, Member: m.Name, m.Location))))
        {
            if (reserved.Contains(name))
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.StateNameCollision, location, member, type.Name, name, "a name the state reserves"));
            else if (seen.TryGetValue(name, out var first))
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.StateNameCollision, location, member, type.Name, name, $"the same name as '{first}'"));
            else
                seen[name] = member;
        }

        CheckUnusedNames(type, diagnostics, state.Select(static f => f.Member).Concat(members.Select(static m => m.Name)).ToImmutableHashSet());
    }

    /// <summary>TPRM026 for <c>[StateName]</c> on members that are not stored (all of them, for a <c>[LoadByReplay]</c> aggregate).</summary>
    public static void CheckUnusedNames(INamedTypeSymbol type, List<DiagnosticInfo> diagnostics, ImmutableHashSet<string>? stored = null)
    {
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || member is not (IFieldSymbol or IPropertySymbol) || Pinned(member) is null)
                continue;
            if (stored is not null && stored.Contains(member.Name))
                continue;
            var why = stored is null
                ? "the aggregate is [LoadByReplay] and has no stored state"
                : member.IsStatic
                    ? "static members are not state"
                    : Symbols.HasAttribute(member, "TransientAttribute")
                        ? "the member is [Transient]"
                        : "only fields, auto-properties and child collections are stored (this property has no backing field)";
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.StateAttributeIgnored, Discovery.LocationOf(member), "[StateName]", member.Name, why));
        }
    }

    /// <summary>System.Text.Json's camelCase policy (<c>JsonNamingPolicy.CamelCase</c>), reimplemented so names are fixed at compile time.</summary>
    public static string CamelCase(string name)
    {
        if (name.Length == 0 || !char.IsUpper(name[0]))
            return name;
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
                break;
            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
            {
                if (chars[i + 1] == ' ')
                    chars[i] = char.ToLowerInvariant(chars[i]);
                break;
            }

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }

    private static string? Pinned(ISymbol member)
        => member.GetAttributes().FirstOrDefault(static a => Symbols.Is(a.AttributeClass, "StateNameAttribute")) is { ConstructorArguments.Length: 1 } a
           && a.ConstructorArguments[0].Value is string { Length: > 0 } name
            ? name
            : null;

    private static bool IsFieldLike(IEventSymbol ev)
        => ev.DeclaringSyntaxReferences.Any(static r => r.GetSyntax() is VariableDeclaratorSyntax);

    /// <summary>Why a field type cannot be stored, or <see langword="null"/>.</summary>
    private static string? Uncapturable(ITypeSymbol type, int depth)
    {
        if (depth > 8)
            return null;
        switch (type)
        {
            case { TypeKind: Microsoft.CodeAnalysis.TypeKind.Delegate }:
                return "a delegate";
            case { SpecialType: SpecialType.System_Object } or IDynamicTypeSymbol:
                return "an untyped object";
            case IPointerTypeSymbol or IFunctionPointerTypeSymbol:
                return "a pointer";
            case IArrayTypeSymbol array:
                return Uncapturable(array.ElementType, depth + 1) is { } element ? Collection(element) : null;
            case INamedTypeSymbol named:
                if (IsDomainType(named, out var what))
                    return what;
                if (IsTarol(named, "StronglyTypedId", "Id"))
                    return null; // Id<Entity> is the way to refer to an entity
                foreach (var argument in named.TypeArguments)
                    if (Uncapturable(argument, depth + 1) is { } held)
                        return Collection(held);
                return null;
            default:
                return null;
        }

        static string Collection(string inner) => inner.StartsWith("a collection", System.StringComparison.Ordinal) ? inner : $"a collection of {Plural(inner)}";

        static string Plural(string what) => what switch
        {
            "an entity" => "entities",
            "an aggregate root" => "aggregate roots",
            "a delegate" => "delegates",
            "an untyped object" => "untyped objects",
            _ => what,
        };
    }

    private static bool IsDomainType(INamedTypeSymbol type, out string what)
    {
        for (var b = type; b is not null; b = b.BaseType)
        {
            if (Symbols.Is(b.OriginalDefinition, "AggregateRoot`1"))
            {
                what = "an aggregate root";
                return true;
            }

            if (Symbols.Is(b.OriginalDefinition, "Entity`2"))
            {
                what = "an entity";
                return true;
            }
        }

        if (Symbols.Is(type.OriginalDefinition, "EntitySet`1") || Symbols.Is(type.OriginalDefinition, "EntityRef`1"))
        {
            what = "a collection of entities";
            return true;
        }

        what = "";
        return false;
    }

    private static bool IsTarol(INamedTypeSymbol type, string ns, string name)
        => type.Name == name && type.ContainingNamespace is { } n && n.Name == ns && n.ContainingNamespace is { Name: "Tarol", ContainingNamespace.IsGlobalNamespace: true };

    /// <summary>How a type is written: directly (with an optional wrapper) or through the options.</summary>
    private static (StateKind Kind, StateWrap Wrap, string Value, string Read, string Underlying, ImmutableArray<string> Members) Classify(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && Direct(nullable.TypeArguments[0]) is { Kind: not StateKind.Json } inner)
            return (inner.Kind, StateWrap.Nullable, inner.Value, inner.Read, inner.Underlying, inner.Members);
        if (type is INamedTypeSymbol { IsGenericType: true } option && IsTarol(option, "Options", "Option")
            && Direct(option.TypeArguments[0]) is { Kind: not StateKind.Json } some)
            return (some.Kind, StateWrap.Option, some.Value, some.Read, some.Underlying, some.Members);
        var direct = Direct(type);
        return (direct.Kind, StateWrap.None, direct.Value, direct.Read, direct.Underlying, direct.Members);
    }

    private static (StateKind Kind, string Value, string Read, string Underlying, ImmutableArray<string> Members) Direct(ITypeSymbol type)
    {
        var name = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var none = ImmutableArray<string>.Empty;
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return (StateKind.String, name, "GetString", "", none);
            case SpecialType.System_Boolean:
                return (StateKind.Bool, name, "GetBoolean", "", none);
            case SpecialType.System_Byte:
                return (StateKind.Number, name, "GetByte", "", none);
            case SpecialType.System_SByte:
                return (StateKind.Number, name, "GetSByte", "", none);
            case SpecialType.System_Int16:
                return (StateKind.Number, name, "GetInt16", "", none);
            case SpecialType.System_UInt16:
                return (StateKind.Number, name, "GetUInt16", "", none);
            case SpecialType.System_Int32:
                return (StateKind.Number, name, "GetInt32", "", none);
            case SpecialType.System_UInt32:
                return (StateKind.Number, name, "GetUInt32", "", none);
            case SpecialType.System_Int64:
                return (StateKind.Number, name, "GetInt64", "", none);
            case SpecialType.System_UInt64:
                return (StateKind.Number, name, "GetUInt64", "", none);
            case SpecialType.System_Decimal:
                return (StateKind.Number, name, "GetDecimal", "", none);
            case SpecialType.System_Double:
                return (StateKind.Number, name, "GetDouble", "", none);
            case SpecialType.System_Single:
                return (StateKind.Number, name, "GetSingle", "", none);
            case SpecialType.System_DateTime:
                return (StateKind.DateTime, name, "GetDateTime", "", none);
        }

        if (name == "global::System.Guid")
            return (StateKind.Guid, name, "GetGuid", "", none);
        if (name == "global::System.DateTimeOffset")
            return (StateKind.DateTimeOffset, name, "GetDateTimeOffset", "", none);
        if (type is INamedTypeSymbol { IsGenericType: true } id && IsTarol(id, "StronglyTypedId", "Id"))
            return (StateKind.Id, name, "GetGuid", "", none);
        if (type is INamedTypeSymbol { TypeKind: Microsoft.CodeAnalysis.TypeKind.Enum, EnumUnderlyingType: { } underlying } e)
            return (StateKind.Enum, name, "", underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                // one name per value (the first declared), so the generated switch has no duplicate case
                e.GetMembers().OfType<IFieldSymbol>().Where(static f => f.HasConstantValue)
                    .GroupBy(static f => f.ConstantValue).Select(static g => g.First().Name).ToImmutableArray());
        return (StateKind.Json, name, "", "", none);
    }

    /// <summary>
    /// A canonical description of a member type's JSON shape for the schema hash: framework types by name, enums with their
    /// member names, other types with their public members (names, pinned JSON names and types, recursively).
    /// </summary>
    public static string Describe(ITypeSymbol type, HashSet<ITypeSymbol> path, int depth)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return Describe(array.ElementType, path, depth + 1) + "[]";
            case INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable:
                return Describe(nullable.TypeArguments[0], path, depth + 1) + "?";
            case INamedTypeSymbol { IsGenericType: true } option when IsTarol(option, "Options", "Option"):
                return Describe(option.TypeArguments[0], path, depth + 1) + "?";
            case INamedTypeSymbol { IsGenericType: true } id when IsTarol(id, "StronglyTypedId", "Id"):
                return "id";
            case INamedTypeSymbol { TypeKind: Microsoft.CodeAnalysis.TypeKind.Enum } e:
                return Named(e) + "{" + string.Join(",", e.GetMembers().OfType<IFieldSymbol>().Where(static f => f.HasConstantValue).Select(static f => f.Name)) + "}";
            case INamedTypeSymbol named:
                var sb = new StringBuilder(Named(named));
                if (named.IsGenericType)
                    sb.Append('<').Append(string.Join(",", named.TypeArguments.Select(a => Describe(a, path, depth + 1)))).Append('>');
                if (IsFramework(named) || depth > 6 || !path.Add(named))
                    return sb.ToString();
                var members = named.GetMembers()
                    .Where(static m => !m.IsStatic && !m.IsImplicitlyDeclared && m.DeclaredAccessibility == Accessibility.Public)
                    .Select(m => m switch
                    {
                        IPropertySymbol { IsIndexer: false, GetMethod: not null } p => (Name: JsonName(p), Type: (ITypeSymbol?)p.Type),
                        IFieldSymbol f => (Name: JsonName(f), Type: f.Type),
                        _ => (Name: "", Type: null),
                    })
                    .Where(static m => m.Type is not null)
                    .OrderBy(static m => m.Name, System.StringComparer.Ordinal)
                    .Select(m => m.Name + ":" + Describe(m.Type!, path, depth + 1))
                    .ToList();
                path.Remove(named);
                return sb.Append('{').Append(string.Join(",", members)).Append('}').ToString();
            default:
                return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        static string Named(INamedTypeSymbol t) => t.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None));

        static bool IsFramework(INamedTypeSymbol t)
        {
            var ns = t.ContainingNamespace;
            while (ns is { ContainingNamespace.IsGlobalNamespace: false })
                ns = ns.ContainingNamespace;
            return t.SpecialType != SpecialType.None || ns?.Name is "System" or "Microsoft";
        }

        static string JsonName(ISymbol member)
            => member.GetAttributes().FirstOrDefault(static a => a.AttributeClass?.Name == "JsonPropertyNameAttribute") is { ConstructorArguments.Length: 1 } a
               && a.ConstructorArguments[0].Value is string pinned
                ? member.Name + "=" + pinned
                : member.Name;
    }
}
