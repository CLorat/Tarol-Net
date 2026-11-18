using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Search.Generators;

/// <summary>
///     Turns a <c>[Searchable]</c> aggregate into a <see cref="SearchModel"/>: its searchable members and those of the entities it
///     reaches through <c>EntitySet&lt;T&gt;</c> and <c>EntityRef&lt;T&gt;</c> properties, names and kinds only.
/// </summary>
internal static class Discovery
{
    public const string AttributeName = "Tarol.Search.SearchableAttribute";
    private const string TitleName = "Tarol.Search.SearchTitleAttribute";
    private const string AudienceName = "Tarol.Search.SearchAudienceAttribute";
    private const string LanguageName = "Tarol.Search.SearchLanguageAttribute";
    private const string PersonalName = "Tarol.Primitives.PersonalDataAttribute";
    private const int MaxDepth = 4;

    private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;
    private static readonly Discovered None = new(null, new EquatableArray<DiagnosticInfo>(ImmutableArray<DiagnosticInfo>.Empty));

    public static Discovered Transform(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type)
            return None;
        var attribute = context.Attributes[0];
        var location = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation()
                                         ?? type.Locations.FirstOrDefault() ?? Location.None);
        if (Declaration(type, cancellationToken) is { } why)
            return Fail(DiagnosticInfo.Create(Diagnostics.Declaration, location, type.Name, why));

        var label = attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string { Length: > 0 } l ? l : type.Name;
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var fingerprint = new StringBuilder();
        var members = Members(type, root: true, diagnostics, fingerprint, out var audience, out var audienceKind, out var language);
        var entities = Entities(type, 1, diagnostics, fingerprint, cancellationToken);
        if (diagnostics.Count > 0)
            return new Discovered(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
        if (members.Count == 0 && entities.Count == 0)
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Empty, location, type.Name));

        var model = new SearchModel(
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            new EquatableArray<string>(Containing(type).ToImmutableArray()),
            type.Name,
            type.ToDisplayString(Qualified),
            label,
            new EquatableArray<MemberModel>(members.ToImmutableArray()),
            audience,
            audienceKind,
            language,
            new EquatableArray<EntityModel>(entities.ToImmutableArray()),
            Fnv(fingerprint.ToString()));
        return new Discovered(model, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
    }

    private static List<MemberModel> Members(INamedTypeSymbol type, bool root, ImmutableArray<DiagnosticInfo>.Builder diagnostics, StringBuilder fingerprint,
        out string? audience, out ValueKind audienceKind, out string? language)
    {
        audience = null;
        audienceKind = ValueKind.String;
        language = null;
        var members = new List<MemberModel>();
        var titled = false;
        fingerprint.Append('[').Append(type.Name).Append(']');
        foreach (var property in Properties(type))
        {
            var at = LocationInfo.From(property.Locations.FirstOrDefault() ?? Location.None);
            var searchable = Attribute(property, AttributeName);
            var title = Attribute(property, TitleName) is not null;
            var personal = Attribute(property, PersonalName) is not null;
            if (Attribute(property, AudienceName) is not null)
            {
                if (!root || audience is not null)
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Placement, at, type.Name, property.Name, root
                        ? "a second [SearchAudience]; keep one" : "[SearchAudience] belongs on the aggregate; its entities inherit it"));
                else if (AudienceKind(property.Type) is { } kind)
                {
                    audience = property.Name;
                    audienceKind = kind;
                    fingerprint.Append("audience:").Append(property.Name).Append(';');
                }
                else
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.MemberType, at, type.Name, property.Name,
                        $"[SearchAudience] needs a string, a Guid or a collection of them, not {property.Type.ToDisplayString()}"));
            }
            if (Attribute(property, LanguageName) is not null)
            {
                if (!root || language is not null)
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Placement, at, type.Name, property.Name, root
                        ? "a second [SearchLanguage]; keep one" : "[SearchLanguage] belongs on the aggregate; its entities inherit it"));
                else if (property.Type.SpecialType == SpecialType.System_String)
                {
                    language = property.Name;
                    fingerprint.Append("language:").Append(property.Name).Append(';');
                }
                else
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.MemberType, at, type.Name, property.Name, "[SearchLanguage] needs a string (an ISO 639-1 code)"));
            }
            if (searchable is null && !title)
                continue;
            var exact = searchable?.NamedArguments.Any(static a => a.Key == "Exact" && a.Value.Value is true) == true;
            var weight = searchable is { ConstructorArguments.Length: 1 } s && s.ConstructorArguments[0].Value is int w ? w : 1;
            if (personal && (title || (searchable is not null && !exact)))
            {
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Personal, at, type.Name, property.Name));
                continue;
            }
            if (title && titled)
            {
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Placement, at, type.Name, property.Name, "a second [SearchTitle]; keep one"));
                continue;
            }
            if (TextKind(property.Type) is not { } textKind || (exact && textKind == ValueKind.Strings) || (title && textKind == ValueKind.Strings))
            {
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.MemberType, at, type.Name, property.Name,
                    $"{property.Type.ToDisplayString()} has no text for {(title ? "a title" : exact ? "an exact value" : "search")} (a string, an enum, a value with ToString{(title || exact ? "" : ", or a collection of strings")})"));
                continue;
            }
            titled |= title;
            members.Add(new MemberModel(property.Name, textKind, searchable is null ? -1 : weight, exact, title));
            fingerprint.Append(property.Name).Append('=').Append(searchable is null ? "-" : exact ? "exact" : ((char)('A' + weight)).ToString())
                .Append(title ? ",title" : "").Append(';');
        }
        return members;
    }

    private static List<EntityModel> Entities(INamedTypeSymbol owner, int depth, ImmutableArray<DiagnosticInfo>.Builder diagnostics, StringBuilder fingerprint,
        CancellationToken cancellationToken)
    {
        var entities = new List<EntityModel>();
        if (depth > MaxDepth)
            return entities;
        foreach (var property in Properties(owner))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (property.Type is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } container
                || container.ContainingNamespace?.ToDisplayString() != "Tarol.Primitives"
                || (container.Name != "EntitySet" && container.Name != "EntityRef")
                || container.TypeArguments[0] is not INamedTypeSymbol entity)
                continue;
            var members = Members(entity, root: false, diagnostics, fingerprint, out _, out _, out _);
            var children = Entities(entity, depth + 1, diagnostics, fingerprint, cancellationToken);
            if (members.Count == 0 && children.Count == 0)
                continue;
            entities.Add(new EntityModel(Kebab(entity.Name), entity.Name, property.Name, container.Name == "EntitySet",
                new EquatableArray<MemberModel>(members.ToImmutableArray()), new EquatableArray<EntityModel>(children.ToImmutableArray())));
        }
        return entities;
    }

    /// <summary>Public instance properties with a getter, of the type and its bases outside Tarol.Primitives, by name.</summary>
    private static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        for (var current = type; current is not null && current.ContainingNamespace?.ToDisplayString() != "Tarol.Primitives"; current = current.BaseType)
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>().OrderBy(static p => p.Name, System.StringComparer.Ordinal))
                if (!property.IsStatic && !property.IsIndexer && property.GetMethod is not null && property.DeclaredAccessibility == Accessibility.Public
                    && seen.Add(property.Name))
                    yield return property;
    }

    private static AttributeData? Attribute(ISymbol symbol, string name) => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == name);

    private static ValueKind? TextKind(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
            return ValueKind.String;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            return ValueKind.NullableFormatted;
        if (StringsOf(type))
            return ValueKind.Strings;
        if (type.TypeKind is TypeKind.Enum or TypeKind.Struct)
            return ValueKind.Formatted;
        if (type.TypeKind == TypeKind.Class && type.IsRecord)
            return ValueKind.NullableFormatted;
        return null;
    }

    private static ValueKind? AudienceKind(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
            return ValueKind.String;
        if (type.ToDisplayString() == "System.Guid")
            return ValueKind.Guid;
        if (StringsOf(type))
            return ValueKind.Strings;
        if (type.AllInterfaces.Concat(type is INamedTypeSymbol n && n.SpecialType == SpecialType.None && type.TypeKind == TypeKind.Interface ? new[] { n } : [])
            .Any(static i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T && i.TypeArguments[0].ToDisplayString() == "System.Guid"))
            return ValueKind.Guids;
        return null;
    }

    private static bool StringsOf(ITypeSymbol type)
        => type.SpecialType != SpecialType.System_String
           && type.AllInterfaces.Concat(type is INamedTypeSymbol { TypeKind: TypeKind.Interface } self ? new[] { self } : [])
               .Any(static i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T && i.TypeArguments[0].SpecialType == SpecialType.System_String);

    private static string? Declaration(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        if (type.TypeKind != TypeKind.Class || type.IsRecord)
            return "a searchable aggregate is a class";
        if (type.IsGenericType || type.IsFileLocal)
            return "a searchable aggregate cannot be generic or file-local";
        var aggregate = false;
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (b is { Name: "AggregateRoot", IsGenericType: true } && b.ContainingNamespace?.ToDisplayString() == "Tarol.Primitives")
                aggregate = true;
        if (!aggregate)
            return "only an aggregate (AggregateRoot<T>) is searchable; mark its entities' members and the aggregate";
        for (var current = type; current is not null; current = current.ContainingType)
        {
            var partial = current.DeclaringSyntaxReferences.Select(r => r.GetSyntax(cancellationToken)).OfType<TypeDeclarationSyntax>()
                .Any(d => d.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (!partial)
                return current.Equals(type, SymbolEqualityComparer.Default)
                    ? "it must be declared partial so that ISearchable can be generated"
                    : $"its containing type '{current.Name}' must be declared partial";
            if (!current.Equals(type, SymbolEqualityComparer.Default) && current.IsGenericType)
                return $"its containing type '{current.Name}' cannot be generic";
        }
        return null;
    }

    private static Discovered Fail(DiagnosticInfo diagnostic) => new(null, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(diagnostic)));

    private static List<string> Containing(INamedTypeSymbol type)
    {
        var chain = new List<string>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            chain.Insert(0, (outer.IsRecord ? (outer.TypeKind == TypeKind.Struct ? "partial record struct" : "partial record")
                : outer.TypeKind == TypeKind.Struct ? "partial struct" : outer.TypeKind == TypeKind.Interface ? "partial interface" : "partial class") + " " + outer.Name);
        return chain;
    }

    /// <summary>"ShippingAddress" → "shipping-address".</summary>
    private static string Kebab(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                sb.Append('-');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>FNV-1a 64 of the fingerprint text, 16 hex digits: stable across compilers and machines.</summary>
    private static string Fnv(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}
