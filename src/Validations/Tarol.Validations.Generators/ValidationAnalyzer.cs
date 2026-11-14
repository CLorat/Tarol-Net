using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tarol.Validations.Generators;

/// <summary>
///     TVAL003 (a rule attribute where nothing reads it), TVAL006 (a value object built without <c>Create</c>) and TVAL007
///     (a DataAnnotations attribute on a value object or an input).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ValidationAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.WrongPlace, Diagnostics.BypassesCreate, Diagnostics.DataAnnotations, Diagnostics.ContextWithoutResolver);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
        context.RegisterOperationAction(AnalyzeCreation, OperationKind.ObjectCreation, OperationKind.With);
        context.RegisterCompilationStartAction(static start =>
        {
            var reach = new System.Collections.Concurrent.ConcurrentDictionary<INamedTypeSymbol, INamedTypeSymbol?>(SymbolEqualityComparer.Default);
            start.RegisterOperationAction(op => AnalyzeContextUse(op, reach), OperationKind.PropertyReference);
        });
    }

    private static void AnalyzeContextUse(OperationAnalysisContext context,
        System.Collections.Concurrent.ConcurrentDictionary<INamedTypeSymbol, INamedTypeSymbol?> reach)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        if (reference.Property is not { IsStatic: true, Name: "Default" } property
            || property.ContainingType is not { } jsonContext
            || !SymbolEqualityComparer.Default.Equals(jsonContext.ContainingAssembly, context.Compilation.Assembly)
            || !DerivesFromJsonContext(jsonContext))
            return;

        // Only the paths that serialize with the context's own options: MyJson.Default.Event / .Options / .GetTypeInfo(…),
        // or MyJson.Default passed to JsonSerializer. Using the context as a resolver (TypeInfoResolver = MyJson.Default,
        // ValueObjectJson.Options(MyJson.Default), Combine(…)) composes options and is left alone.
        var parent = reference.Parent;
        while (parent is IConversionOperation)
            parent = parent.Parent;

        var serializesDirectly = parent switch
        {
            IPropertyReferenceOperation member => member.Instance is not null,
            IInvocationOperation call => call.Instance is not null,
            IArgumentOperation { Parent: IInvocationOperation invocation } =>
                invocation.TargetMethod.ContainingType is { Name: "JsonSerializer" } serializer
                && serializer.ContainingNamespace?.ToDisplayString() == "System.Text.Json",
            _ => false
        };
        if (!serializesDirectly)
            return;

        var valueObject = reach.GetOrAdd(jsonContext, c => FindValueObject(c, context.Compilation.Assembly));
        if (valueObject is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ContextWithoutResolver, reference.Syntax.GetLocation(), jsonContext.Name, valueObject.Name));
    }

    private static bool DerivesFromJsonContext(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
        {
            if (b.Name == "JsonSerializerContext" && b.ContainingNamespace?.ToDisplayString() == "System.Text.Json.Serialization")
                return true;
        }

        return false;
    }

    /// <summary>The first value object with a generated converter, declared in this assembly, that the context's types reach.</summary>
    private static INamedTypeSymbol? FindValueObject(INamedTypeSymbol jsonContext, IAssemblySymbol assembly)
    {
        var visited = new System.Collections.Generic.HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var attribute in jsonContext.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == "JsonSerializableAttribute"
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is ITypeSymbol root
                && Walk(root, assembly, visited, 0) is { } found)
                return found;
        }

        return null;
    }

    private static INamedTypeSymbol? Walk(ITypeSymbol type, IAssemblySymbol assembly, System.Collections.Generic.HashSet<ITypeSymbol> visited, int depth)
    {
        if (depth > 8 || !visited.Add(type))
            return null;

        if (type is IArrayTypeSymbol array)
            return Walk(array.ElementType, assembly, visited, depth + 1);

        if (type is not INamedTypeSymbol named || named.SpecialType != SpecialType.None)
            return null;

        if (SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly)
            && Symbols.IsValueObject(named)
            && Symbols.PrimaryConstructor(named) is { Parameters.Length: 1 } constructor
            && Emitter.JsonAccess(constructor.Parameters[0].Type) is not null)
            return named;

        foreach (var argument in named.TypeArguments)
        {
            if (Walk(argument, assembly, visited, depth + 1) is { } found)
                return found;
        }

        // Only this assembly's types can lead to this assembly's value objects (besides type arguments above).
        if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly))
            return null;

        foreach (var member in named.GetMembers())
        {
            if (member is IPropertySymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public } p
                && Walk(p.Type, assembly, visited, depth + 1) is { } found)
                return found;
        }

        return null;
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (Symbols.IsRecordPrimaryConstructor(method))
            return;

        foreach (var parameter in method.Parameters)
        {
            foreach (var attribute in parameter.GetAttributes())
            {
                if (Symbols.RuleName(attribute) is not { } rule)
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.WrongPlace,
                    attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? parameter.Locations.FirstOrDefault(),
                    rule, parameter.Name, method.ContainingType?.Name ?? method.Name));
            }
        }
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (Symbols.PrimaryConstructor(type) is not { } constructor)
            return;

        if (!Symbols.IsValueObject(type) && !type.Interfaces.Any(Symbols.IsValidatableInterface))
            return;

        foreach (var parameter in constructor.Parameters)
        {
            var property = type.GetMembers(parameter.Name).OfType<IPropertySymbol>().FirstOrDefault();
            var attributes = parameter.GetAttributes().AddRange(property?.GetAttributes() ?? ImmutableArray<AttributeData>.Empty);
            foreach (var attribute in attributes)
            {
                if (!Symbols.IsDataAnnotation(attribute))
                    continue;

                var name = attribute.AttributeClass!.Name;
                if (name.EndsWith("Attribute", System.StringComparison.Ordinal))
                    name = name.Substring(0, name.Length - "Attribute".Length);

                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.DataAnnotations,
                    attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? parameter.Locations.FirstOrDefault(),
                    name, parameter.Name, type.Name, Replacement(name)));
            }
        }
    }

    private static string Replacement(string name) => name switch
    {
        "MaxLength" or "MinLength" or "StringLength" or "Length" => "Tarol.Validations.Length",
        "Range" => "Tarol.Validations.Range",
        "RegularExpression" => "Tarol.Validations.Pattern",
        "EmailAddress" => "Tarol.Validations.Email",
        "Required" => "the member's type: every non-Option member is required",
        _ => "a Tarol.Validations rule or a custom Validate method"
    };

    private static void AnalyzeCreation(OperationAnalysisContext context)
    {
        var type = context.Operation switch
        {
            IObjectCreationOperation creation => creation.Type,
            IWithOperation with => with.Type,
            _ => null
        };

        if (type is not INamedTypeSymbol named || !Symbols.IsValueObject(named) || Symbols.HasCheckOnly(named))
            return;

        // Inside the value object itself (Create, the JSON converter, its own members) construction is the point.
        for (var symbol = context.ContainingSymbol; symbol is not null; symbol = symbol.ContainingSymbol)
        {
            if (SymbolEqualityComparer.Default.Equals(symbol, named))
                return;
        }

        var what = context.Operation is IWithOperation ? "a 'with' expression" : "'new " + named.Name + "(…)'";
        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.BypassesCreate, context.Operation.Syntax.GetLocation(), named.Name, what));
    }
}
