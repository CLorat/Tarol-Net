using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Validations.Generators;

/// <summary>Symbol checks shared by the generator and the analyzer. Everything is matched by full metadata name (finding 3).</summary>
internal static class Symbols
{
    public const string Namespace = "Tarol.Validations";

    public static bool IsRule(AttributeData attribute) => RuleName(attribute) is not null;

    /// <summary>The short rule name (<c>Range</c>, <c>Length</c>, …) of a Tarol rule attribute, otherwise <see langword="null" />.</summary>
    public static string? RuleName(AttributeData attribute)
    {
        var type = attribute.AttributeClass;
        if (type is null || type.ContainingNamespace?.ToDisplayString() != Namespace)
            return null;

        return type.MetadataName switch
        {
            "RangeAttribute" => "Range",
            "LengthAttribute" => "Length",
            "PatternAttribute" => "Pattern",
            "EmailAttribute" => "Email",
            "NotEmptyAttribute" => "NotEmpty",
            "CountAttribute" => "Count",
            _ => null
        };
    }

    public static bool IsValidatableInterface(ITypeSymbol type)
        => type is INamedTypeSymbol { MetadataName: "IValidatable", TypeKind: TypeKind.Interface } named
           && named.ContainingNamespace?.ToDisplayString() == Namespace;

    public static bool IsValidationErrors(ITypeSymbol type)
        => type is INamedTypeSymbol { MetadataName: "ValidationErrors" } named
           && named.ContainingNamespace?.ToDisplayString() == Namespace;

    public static bool ImplementsValidatable(ITypeSymbol type)
        => IsValidatableInterface(type) || type.AllInterfaces.Any(IsValidatableInterface);

    public static bool IsId(ITypeSymbol type)
        => type is INamedTypeSymbol { Arity: 1 } named
           && named.OriginalDefinition.MetadataName == "Id`1"
           && named.ContainingNamespace?.ToDisplayString() == "Tarol.StronglyTypedId";

    public static bool IsOption(ITypeSymbol type, out ITypeSymbol inner)
    {
        if (type is INamedTypeSymbol { Arity: 1 } named
            && named.OriginalDefinition.MetadataName == "Option`1"
            && named.ContainingNamespace?.ToDisplayString() == "Tarol.Options")
        {
            inner = named.TypeArguments[0];
            return true;
        }

        inner = type;
        return false;
    }

    /// <summary>The primary constructor of a record or record struct declared in source, otherwise <see langword="null" />.</summary>
    public static IMethodSymbol? PrimaryConstructor(INamedTypeSymbol type)
    {
        if (!type.IsRecord)
            return null;

        foreach (var constructor in type.InstanceConstructors)
        {
            foreach (var reference in constructor.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is RecordDeclarationSyntax)
                    return constructor;
            }
        }

        return null;
    }

    public static bool IsRecordPrimaryConstructor(IMethodSymbol method)
        => method.MethodKind == MethodKind.Constructor
           && method.ContainingType is { IsRecord: true }
           && method.DeclaringSyntaxReferences.Any(static r => r.GetSyntax() is RecordDeclarationSyntax);

    /// <summary>
    ///     The user's custom rule: <c>static void Validate(X value, ref ValidationErrors errors)</c> or, for a value object with
    ///     one field, <c>static void Validate(TField value, ref ValidationErrors errors)</c>.
    /// </summary>
    public static IMethodSymbol? CustomRule(INamedTypeSymbol type, ITypeSymbol? singleField)
    {
        foreach (var member in type.GetMembers("Validate"))
        {
            if (member is IMethodSymbol { IsStatic: true, ReturnsVoid: true, Parameters.Length: 2 } method
                && method.Parameters[1].RefKind == RefKind.Ref
                && IsValidationErrors(method.Parameters[1].Type)
                && (SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type)
                    || (singleField is not null && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, singleField))))
                return method;
        }

        return null;
    }

    /// <summary>A hand-written or generated <c>static void Check(X value, ref ValidationErrors errors)</c> (also from metadata).</summary>
    public static bool HasCheck(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers("Check"))
        {
            if (member is IMethodSymbol { IsStatic: true, ReturnsVoid: true, Parameters.Length: 2 } method
                && method.Parameters[1].RefKind == RefKind.Ref
                && IsValidationErrors(method.Parameters[1].Type)
                && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     A value object: a type with a <c>Check</c> method (metadata or hand-written), or a record in source whose primary
    ///     constructor has a rule attribute or that declares a custom rule, and that is not an <c>IValidatable</c> input.
    /// </summary>
    public static bool IsValueObject(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error || ImplementsValidatable(named))
            return false;

        if (HasCheck(named))
            return true;

        if (PrimaryConstructor(named) is not { } constructor)
            return false;

        if (constructor.Parameters.Any(static p => p.GetAttributes().Any(IsRule)))
            return true;

        var single = constructor.Parameters.Length == 1 ? constructor.Parameters[0].Type : null;
        return CustomRule(named, single) is not null;
    }

    /// <summary>
    ///     A hand-written value object (a <c>Check</c> method, but no rule attribute and no custom rule): its constructor may be
    ///     the user's own validating constructor, so TVAL006 does not apply.
    /// </summary>
    public static bool HasCheckOnly(INamedTypeSymbol type)
    {
        if (!HasCheck(type))
            return false;

        if (PrimaryConstructor(type) is { } primary && primary.Parameters.Any(static p => p.GetAttributes().Any(IsRule)))
            return false;

        // A type from metadata: the generator marks what it generated with the ValueJsonConverter or Create; rule
        // attributes survive in metadata on the constructor parameters.
        foreach (var constructor in type.InstanceConstructors)
        {
            if (constructor.Parameters.Any(static p => p.GetAttributes().Any(IsRule)))
                return false;
        }

        var single = type.InstanceConstructors.FirstOrDefault(static c => c.Parameters.Length == 1)?.Parameters[0].Type;
        return CustomRule(type, single) is null;
    }

    public static bool IsDataAnnotation(AttributeData attribute)
    {
        for (var type = attribute.AttributeClass; type is not null; type = type.BaseType)
        {
            if (type.MetadataName == "ValidationAttribute"
                && type.ContainingNamespace?.ToDisplayString() == "System.ComponentModel.DataAnnotations")
                return true;
        }

        return false;
    }
}
