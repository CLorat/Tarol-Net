using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Tarol.Primitives.Generators;

/// <summary>
/// The data-protection part of discovery (step 16, DP-2, DP-7): an event's <c>[PersonalData]</c> members with their JSON
/// names and subjects, and a root's <c>[Retention(Days)]</c>. Invalid uses are reported by <see cref="PersonalDataAnalyzer"/>
/// (TPRM030–TPRM033); discovery skips them, so a broken mark never emits code that does not compile.
/// </summary>
internal static class PersonalDiscovery
{
    public const string Attribute = "PersonalDataAttribute";

    /// <summary>The <c>[PersonalData]</c> members of an event record and its non-library bases, ordered by name.</summary>
    public static EquatableArray<PersonalModel> OfEvent(INamedTypeSymbol type)
    {
        var found = new List<PersonalModel>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        for (var current = type; current is not null && Symbols.LibraryEventBase(current).Kind == EventKind.None && !Symbols.Is(current, "DomainEvent"); current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.GetMethod is null || !seen.Add(property.Name))
                    continue;
                if (Mark(property) is not { } mark || !IsNullable(property.Type))
                    continue;
                var subject = SubjectOf(mark);
                string? subjectJson = null;
                if (subject is not null)
                {
                    if (FindMember(type, subject) is not { } subjectMember || !IsSubjectType(TypeOf(subjectMember)))
                        continue;
                    subjectJson = JsonPropertyName(subjectMember);
                }

                found.Add(new PersonalModel(property.Name, JsonPropertyName(property), subject, subjectJson));
            }
        }

        return new EquatableArray<PersonalModel>(found.OrderBy(static p => p.Name, System.StringComparer.Ordinal).ToImmutableArray());
    }

    /// <summary>The <c>[PersonalData]</c> on a property (or, for a positional record, on its primary-constructor parameter).</summary>
    public static AttributeData? Mark(ISymbol member)
    {
        if (Find(member) is { } direct)
            return direct;
        if (member is IPropertySymbol { ContainingType: { IsRecord: true } record } property)
        {
            foreach (var constructor in record.InstanceConstructors)
            {
                if (constructor.Parameters.FirstOrDefault(p => p.Name == property.Name) is { } parameter && Find(parameter) is { } onParameter)
                    return onParameter;
            }
        }

        return null;

        static AttributeData? Find(ISymbol symbol) => symbol.GetAttributes().FirstOrDefault(static a => Symbols.Is(a.AttributeClass, Attribute));
    }

    /// <summary>The <c>Subject</c> named argument, or <see langword="null"/>.</summary>
    public static string? SubjectOf(AttributeData mark)
        => mark.NamedArguments.FirstOrDefault(static a => a.Key == "Subject").Value.Value as string;

    /// <summary>A nullable reference type (or nullable-oblivious), <c>T?</c> or <c>Option&lt;T&gt;</c>: an erased value reads as <c>null</c>.</summary>
    public static bool IsNullable(ITypeSymbol type)
        => type switch
        {
            { IsReferenceType: true } => type.NullableAnnotation != NullableAnnotation.NotAnnotated,
            INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } => true,
            INamedTypeSymbol { Name: "Option", IsGenericType: true, ContainingNamespace: { Name: "Options", ContainingNamespace: { Name: "Tarol", ContainingNamespace.IsGlobalNamespace: true } } } => true,
            _ => false,
        };

    /// <summary>A <see cref="System.Guid"/> or <c>Id&lt;T&gt;</c> (not nullable): the type a subject member must have.</summary>
    public static bool IsSubjectType(ITypeSymbol? type)
        => type is not null
           && (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Guid"
               || type is INamedTypeSymbol { Name: "Id", IsGenericType: true, ContainingNamespace: { Name: "StronglyTypedId", ContainingNamespace: { Name: "Tarol", ContainingNamespace.IsGlobalNamespace: true } } });

    /// <summary>A property or field named <paramref name="name"/> on <paramref name="type"/> or its bases.</summary>
    public static ISymbol? FindMember(INamedTypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetMembers(name).FirstOrDefault(static m => m is IPropertySymbol or IFieldSymbol && !m.IsStatic) is { } member)
                return member;
        }

        return null;
    }

    public static ITypeSymbol? TypeOf(ISymbol member) => member switch
    {
        IPropertySymbol p => p.Type,
        IFieldSymbol f => f.Type,
        _ => null,
    };

    /// <summary><c>[RetentionAttribute(Days = n)]</c> with n ≥ 1, else 0.</summary>
    public static int RetentionDays(INamedTypeSymbol root)
        => root.GetAttributes().FirstOrDefault(static a => Symbols.Is(a.AttributeClass, "RetentionAttribute")) is { } a
           && a.NamedArguments.FirstOrDefault(static n => n.Key == "Days").Value.Value is int days && days >= 1
            ? days
            : 0;

    private static string? JsonPropertyName(ISymbol member)
        => member.GetAttributes().FirstOrDefault(static a => a.AttributeClass is { Name: "JsonPropertyNameAttribute" }) is { ConstructorArguments.Length: 1 } a
           && a.ConstructorArguments[0].Value is string { Length: > 0 } name
            ? name
            : null;
}
