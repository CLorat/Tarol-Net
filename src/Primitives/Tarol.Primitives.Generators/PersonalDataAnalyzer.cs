using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tarol.Primitives.Generators;

/// <summary>
/// Checks the data-protection marks (step 16, DP-2, DP-7):
/// <list type="bullet">
///     <item>TPRM030: a <c>[PersonalData]</c> member is nullable (an erased value reads as <c>null</c>);</item>
///     <item>TPRM031: its <c>Subject</c> names a non-nullable <c>Guid</c>/<c>Id&lt;T&gt;</c> member of the same type;</item>
///     <item>TPRM032: it sits on an event, a stored member of an aggregate or entity, or a projection row (<c>[Projection]</c>);</item>
///     <item>TPRM033: <c>[Retention]</c> sits on an aggregate root with <c>Days &gt;= 1</c>.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PersonalDataAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.PersonalNotNullable, Diagnostics.PersonalSubject, Diagnostics.PersonalPlacement, Diagnostics.RetentionPlacement);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Tarol.Primitives.PersonalDataAttribute") is null)
                return;
            start.RegisterSymbolAction(static ctx => Check(ctx, (ISymbol)ctx.Symbol), SymbolKind.Property, SymbolKind.Field);
            start.RegisterSymbolAction(static ctx => CheckParameter(ctx), SymbolKind.Method);
            start.RegisterSymbolAction(static ctx => CheckRetention(ctx, (INamedTypeSymbol)ctx.Symbol), SymbolKind.NamedType);
        });
    }

    private static void CheckParameter(SymbolAnalysisContext ctx)
    {
        // A plain [PersonalData] on a positional record parameter: checked on the property it declares, unless there is none.
        if (ctx.Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: var type } constructor)
            return;
        foreach (var parameter in constructor.Parameters)
        {
            if (Own(parameter) is not { } mark)
                continue;
            if (type.IsRecord && type.GetMembers(parameter.Name).OfType<IPropertySymbol>().Any())
                continue;
            Report(ctx, Diagnostics.PersonalPlacement, mark, parameter.Name, type.Name, "only members are sealed; this parameter declares no property (mark the property it initializes)");
        }
    }

    private static void Check(SymbolAnalysisContext ctx, ISymbol member)
    {
        if (member.IsImplicitlyDeclared && member is IFieldSymbol)
            return;
        var mark = Own(member);
        if (mark is null && member is IPropertySymbol { ContainingType.IsRecord: true } property)
        {
            mark = property.ContainingType.InstanceConstructors.SelectMany(static c => c.Parameters)
                .Where(p => p.Name == property.Name).Select(Own).FirstOrDefault(static a => a is not null);
        }

        if (mark is null)
            return;
        var type = member.ContainingType;
        var memberType = PersonalDiscovery.TypeOf(member)!;

        if (Placement(member, type) is { } why)
        {
            Report(ctx, Diagnostics.PersonalPlacement, mark, member.Name, type.Name, why);
            return;
        }

        if (!PersonalDiscovery.IsNullable(memberType))
            Report(ctx, Diagnostics.PersonalNotNullable, mark, member.Name, type.Name, memberType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        if (PersonalDiscovery.SubjectOf(mark) is { } subject)
        {
            var subjectMember = PersonalDiscovery.FindMember(type, subject);
            var problem = subjectMember is null
                ? $"'{type.Name}' has no member '{subject}'"
                : !PersonalDiscovery.IsSubjectType(PersonalDiscovery.TypeOf(subjectMember))
                    ? $"'{subject}' has type '{PersonalDiscovery.TypeOf(subjectMember)?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}', not Guid or Id<T>"
                    : subject == member.Name
                        ? "a member cannot be its own subject"
                        : null;
            if (problem is not null)
                Report(ctx, Diagnostics.PersonalSubject, mark, subject, member.Name, type.Name, problem);
        }
    }

    /// <summary>Why <paramref name="member"/> cannot be sealed, or <see langword="null"/>.</summary>
    private static string? Placement(ISymbol member, INamedTypeSymbol type)
    {
        if (member.IsStatic)
            return "static members are not stored";
        if (Symbols.IsDomainEvent(type))
            return null;
        if (type.GetAttributes().Any(static a => a.AttributeClass is { Name: "ProjectionAttribute", ContainingNamespace: { Name: "Queries", ContainingNamespace: { Name: "Tarol", ContainingNamespace.IsGlobalNamespace: true } } }))
            return null;
        if (Symbols.RootOf(type) is { } root || (Symbols.EntityRootOf(type) is { } entityRoot && (root = entityRoot) is not null))
        {
            if (Symbols.HasAttribute(root, "LoadByReplayAttribute"))
                return "the aggregate is [LoadByReplay] and has no stored state (mark the members of its events)";
            if (Symbols.HasAttribute(member, "TransientAttribute"))
                return "the member is [Transient] and not stored";
            if (member is IPropertySymbol { IsAbstract: false } property && !HasBackingField(property))
                return "only fields and auto-properties are stored (this property has no backing field)";
            return null;
        }

        return "it belongs on a member of an event, of an aggregate's or entity's stored state, or of a projection row; a value object's member is sealed only as part of the member that holds it (mark that member)";
    }

    private static bool HasBackingField(IPropertySymbol property)
        => property.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property));

    private static void CheckRetention(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        if (type.GetAttributes().FirstOrDefault(static a => Symbols.Is(a.AttributeClass, "RetentionAttribute")) is not { } attribute)
            return;
        var location = attribute.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken).GetLocation() ?? type.Locations.FirstOrDefault();
        if (Symbols.RootOf(type) is null)
            ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.RetentionPlacement, location, type.Name, "has no effect: it belongs on an aggregate root"));
        else if (attribute.NamedArguments.FirstOrDefault(static n => n.Key == "Days").Value.Value is not int days || days < 1)
            ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.RetentionPlacement, location, type.Name, "needs Days of at least 1"));
    }

    private static AttributeData? Own(ISymbol symbol)
        => symbol.GetAttributes().FirstOrDefault(static a => Symbols.Is(a.AttributeClass, PersonalDiscovery.Attribute));

    private static void Report(SymbolAnalysisContext ctx, DiagnosticDescriptor descriptor, AttributeData mark, params object[] args)
        => ctx.ReportDiagnostic(Diagnostic.Create(descriptor,
            mark.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken).GetLocation() ?? ctx.Symbol.Locations.FirstOrDefault(), args));
}
