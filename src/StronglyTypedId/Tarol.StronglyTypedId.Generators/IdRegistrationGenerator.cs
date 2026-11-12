using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Tarol.StronglyTypedId.Generators;

/// <summary>
///     Emits one <c>[ModuleInitializer]</c> per assembly that calls
///     <c>IdConverterRegistry.Register&lt;X&gt;()</c> for every closed <c>Id&lt;X&gt;</c> the assembly uses, so that
///     <c>TypeDescriptor.GetConverter(typeof(Id&lt;X&gt;))</c> (configuration binding, MVC, designers) works
///     without reflection or configuration.
///     <para>
///         An id is "used" when it is spelled in source (<c>Id&lt;User&gt;</c>, also inside other types such as
///         <c>List&lt;Id&lt;User&gt;&gt;</c>), or when a type declared in this assembly inherits a property or
///         field of a closed id type from a generic base, e.g. <c>class Order : AggregateRoot&lt;Order&gt;</c>
///         inheriting <c>Id&lt;Order&gt; Id</c>.
///     </para>
///     <para>
///         Each assembly registers its own ids ("per-assembly ownership"), so there is no cross-assembly scanning
///         and the pipeline stays cacheable: models are strings only.
///     </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class IdRegistrationGenerator : IIncrementalGenerator
{
    private const string CoreNamespace = "Tarol.StronglyTypedId";
    private const string IdMetadataName = "Id`1";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Ids spelled in source: cheap syntax predicate, semantic transform to equatable candidates.
        var spelled = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is GenericNameSyntax
                {
                    Identifier.ValueText: "Id",
                    TypeArgumentList.Arguments.Count: 1
                },
                static (ctx, ct) => FromSpelledId(ctx, ct))
            .SelectMany(static (candidates, _) => candidates);

        // Ids closed through inheritance: `class Order : AggregateRoot<Order>` inherits `Id<Order> Id`.
        var inherited = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => FromInheritedMembers(ctx, ct))
            .SelectMany(static (candidates, _) => candidates);

        var registrations = spelled.Collect()
            .Combine(inherited.Collect())
            .Select(static (pair, _) => new EquatableArray<string>(
                pair.Left.Concat(pair.Right)
                    .Where(static c => c.Skipped is null)
                    .Select(static c => c.MarkerName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static n => n, StringComparer.Ordinal) // deterministic output
                    .ToImmutableArray()));

        // Select the assembly NAME (a string) out of the compilation: combining the Compilation object
        // itself would re-run the output step on every keystroke.
        var assemblyName = context.CompilationProvider
            .Select(static (compilation, _) => compilation.AssemblyName ?? "Assembly");

        context.RegisterSourceOutput(
            registrations.Combine(assemblyName),
            static (spc, pair) => Emit(spc, pair.Left, pair.Right));

        // Diagnostics only for ids spelled in source (inherited ids are not the user's spelling).
        context.RegisterSourceOutput(
            spelled.Where(static c => c.Skipped is not null),
            static (spc, candidate) => spc.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.MarkerNotRegistered,
                candidate.Location?.ToLocation(),
                candidate.MarkerName,
                candidate.Skipped)));
    }

    private static ImmutableArray<Candidate> FromSpelledId(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node, ct).Symbol is not INamedTypeSymbol named || !IsTarolId(named))
            return ImmutableArray<Candidate>.Empty;

        var candidate = ToCandidate(named.TypeArguments[0], LocationInfo.From(ctx.Node.GetLocation()));
        return candidate is null ? ImmutableArray<Candidate>.Empty : ImmutableArray.Create(candidate.Value);
    }

    private static ImmutableArray<Candidate> FromInheritedMembers(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol declared)
            return ImmutableArray<Candidate>.Empty;

        var builder = ImmutableArray.CreateBuilder<Candidate>();

        // Only generic bases and interfaces matter: a non-generic base spells its id types in its own source,
        // and that source's assembly registers them.
        var generic = new List<INamedTypeSymbol>();
        for (var baseType = declared.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.IsGenericType)
                generic.Add(baseType);
        }

        generic.AddRange(declared.AllInterfaces.Where(static i => i.IsGenericType));

        foreach (var type in generic)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var member in type.GetMembers())
            {
                var memberType = member switch
                {
                    IPropertySymbol p => p.Type,
                    IFieldSymbol f => f.Type,
                    _ => null
                };
                if (memberType is null)
                    continue;

                foreach (var id in FindIds(memberType))
                {
                    if (ToCandidate(id.TypeArguments[0], location: null) is { Skipped: null } candidate)
                        builder.Add(candidate);
                }
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>Every closed <c>Id&lt;X&gt;</c> inside <paramref name="type" /> (itself, nullable, type arguments, arrays).</summary>
    private static IEnumerable<INamedTypeSymbol> FindIds(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var id in FindIds(array.ElementType))
                    yield return id;
                break;
            case INamedTypeSymbol named:
                if (IsTarolId(named))
                    yield return named;
                foreach (var argument in named.TypeArguments)
                    foreach (var id in FindIds(argument))
                        yield return id;
                break;
        }
    }

    private static bool IsTarolId(INamedTypeSymbol named)
    {
        var definition = named.OriginalDefinition;
        return named.Arity == 1
               && definition.MetadataName == IdMetadataName
               && definition.ContainingNamespace?.ToDisplayString() == CoreNamespace;
    }

    private static Candidate? ToCandidate(ITypeSymbol marker, LocationInfo? location)
    {
        // Open generics (Id<T> inside a generic type, Id<Wrapper<T>>) are closed elsewhere; error types are
        // the compiler's business.
        if (ContainsTypeParameter(marker) || marker.TypeKind == TypeKind.Error)
            return null;

        var name = marker.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var skipped = WhyInaccessible(marker);
        return new Candidate(name, skipped, location);
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
        _ => false
    };

    /// <summary>
    ///     Generated code lives in its own file at namespace level: it can reference public and internal types,
    ///     but not private/protected nested types or file-local types.
    /// </summary>
    private static string? WhyInaccessible(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal)
                return $"'{current.Name}' is a file-local type";
            if (current.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected
                or Accessibility.ProtectedAndInternal)
                return $"'{current.Name}' is {current.DeclaredAccessibility.ToString().ToLowerInvariant()}";
        }

        return null;
    }

    private static void Emit(SourceProductionContext context, EquatableArray<string> markers, string assemblyName)
    {
        if (markers.IsEmpty)
            return;

        // The class is internal, but InternalsVisibleTo between two consuming assemblies would make a shared
        // name ambiguous: suffix it with the assembly name.
        var className = "StronglyTypedIdRegistrations_" + SanitizeIdentifier(assemblyName);

        var sb = new StringBuilder(1024);
        sb.Append("// <auto-generated/>\n");
        sb.Append("#nullable enable\n\n");
        sb.Append("namespace Tarol.StronglyTypedId.Generated\n{\n");
        sb.Append("    /// <summary>Registers the TypeConverters of the strongly-typed ids this assembly uses.</summary>\n");
        sb.Append("    internal static class ").Append(className).Append("\n    {\n");
        sb.Append("        [global::System.Runtime.CompilerServices.ModuleInitializer]\n");
        sb.Append("        internal static void Register()\n        {\n");
        foreach (var marker in markers)
            sb.Append("            global::Tarol.StronglyTypedId.IdConverterRegistry.Register<").Append(marker).Append(">();\n");
        sb.Append("        }\n    }\n}\n");

        context.AddSource("StronglyTypedIdRegistrations.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string SanitizeIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length + 1);
        if (name.Length == 0 || char.IsDigit(name[0]))
            sb.Append('_');
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.ToString();
    }

    /// <summary>One closed id found in source. Strings and value types only, so the pipeline caches.</summary>
    private readonly record struct Candidate(string MarkerName, string? Skipped, LocationInfo? Location);
}
