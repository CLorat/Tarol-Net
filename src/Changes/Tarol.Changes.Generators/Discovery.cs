using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Changes.Generators;

internal static class Discovery
{
    private const string ChangeName = "Tarol.Changes.IChange`1";
    private const string CreationName = "Tarol.Changes.ICreation`1";
    private const string LoadsName = "Tarol.Changes.ILoads`1";
    private const string ReadName = "Tarol.Changes.ReadAttribute";
    private const string ValidatableName = "Tarol.Validations.IValidatable";

    private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>Cheap syntax filter: a type whose own base list names IChange/ICreation/ILoads.</summary>
    public static bool IsCandidate(SyntaxNode node)
        => node is TypeDeclarationSyntax { BaseList: { } list } && list.Types.Any(static t => IsChangeName(t.Type));

    private static bool IsChangeName(TypeSyntax type) => type switch
    {
        GenericNameSyntax g => g.Identifier.ValueText is "IChange" or "ICreation" or "ILoads",
        QualifiedNameSyntax q => IsChangeName(q.Right),
        AliasQualifiedNameSyntax a => IsChangeName(a.Name),
        _ => false,
    };

    public static Discovered Transform(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node, cancellationToken) is not INamedTypeSymbol type)
            return Discovered.None;
        // one model per type, from its first declaration that names the interface
        var first = type.DeclaringSyntaxReferences.Select(r => r.GetSyntax(cancellationToken))
            .OfType<TypeDeclarationSyntax>().FirstOrDefault(d => d.BaseList is { } b && b.Types.Any(t => IsChangeName(t.Type)));
        if (first != context.Node)
            return Discovered.None;

        var compilation = context.SemanticModel.Compilation;
        var change = compilation.GetTypeByMetadataName(ChangeName);
        var creation = compilation.GetTypeByMetadataName(CreationName);
        var loads = compilation.GetTypeByMetadataName(LoadsName);
        if (change is null || creation is null || loads is null)
            return Discovered.None;
        var location = LocationInfo.From(((TypeDeclarationSyntax)context.Node).Identifier.GetLocation());
        var diagnostics = new List<DiagnosticInfo>();

        var implemented = type.AllInterfaces.Where(i =>
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, change) ||
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, creation)).ToArray();
        if (implemented.Length == 0)
            return Discovered.None;                                         // e.g. a user type named IChange
        if (type.IsGenericType || type.IsFileLocal || implemented.Length > 1)
        {
            var why = type.IsGenericType ? "a change cannot be generic"
                : type.IsFileLocal ? "a change cannot be file-local"
                : "it implements more than one of IChange<T> and ICreation<T>; one change targets one aggregate";
            return Report(DiagnosticInfo.Create(Diagnostics.Unsupported, location, type.Name, why));
        }

        var iface = implemented[0];
        var aggregate = (INamedTypeSymbol)iface.TypeArguments[0];
        var kind = SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, creation) ? ChangeKind.Creation
            : type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, loads)) ? ChangeKind.Loads
            : ChangeKind.Change;

        var members = Members(type).ToArray();
        var readAttribute = compilation.GetTypeByMetadataName(ReadName);
        var reads = new List<ReadModel>();
        var idOfAggregate = new List<IPropertySymbol>();
        var readsOfTarget = new List<IPropertySymbol>();
        foreach (var member in members)
        {
            var isRead = readAttribute is not null && HasAttribute(member, type, readAttribute);
            var id = IdArgument(member.Type);
            if (isRead)
            {
                var reason = id is null ? "it is not an Id<X> of an aggregate"
                    : !IsAggregate(id) ? $"{id.Name} is not an aggregate root"
                    : null;
                if (reason is not null)
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.ReadMisuse, LocationInfo.From(member.Locations.FirstOrDefault() ?? Location.None) ?? location, member.Name, type.Name, reason));
                else
                {
                    if (kind != ChangeKind.Creation && SymbolEqualityComparer.Default.Equals(id, aggregate))
                        readsOfTarget.Add(member);                              // TN-40: allowed when another member is the target
                    reads.Add(new ReadModel(member.Name, id!.ToDisplayString(Qualified), id.Name, "this." + member.Name, ReadOptional(member, type, readAttribute!)));
                }
                continue;
            }
            if (id is not null && SymbolEqualityComparer.Default.Equals(id, aggregate))
                idOfAggregate.Add(member);
        }
        // TCHG004 only for the target (TN-40, Tatalu FB-F02.1-7): a [Read] Id<Child> Sibling next to the target Id<Child> Child
        if (readsOfTarget.Count > 0 && idOfAggregate.Count == 0 && kind == ChangeKind.Change)
            foreach (var member in readsOfTarget)
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.ReadMisuse, LocationInfo.From(member.Locations.FirstOrDefault() ?? Location.None) ?? location,
                    member.Name, type.Name, $"{aggregate.Name} is the change's own target, which the pipeline loads anyway (a read of another {aggregate.Name} needs a target member next to it)"));
        // [Reads<T>] on the change type (TN-40, owner decision CR-1)
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is not { IsGenericType: true } readsClass || readsClass.OriginalDefinition.ToDisplayString() != "Tarol.Changes.ReadsAttribute<T>")
                continue;
            var read = (INamedTypeSymbol)readsClass.TypeArguments[0];
            var at = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None) ?? location;
            var sameIdAs = attribute.NamedArguments.FirstOrDefault(static n => n.Key == "SameIdAs").Value.Value as string;
            var optional = attribute.NamedArguments.FirstOrDefault(static n => n.Key == "Optional").Value.Value is true;
            string? why = !IsAggregate(read) ? $"{read.Name} is not an aggregate root" : null;
            string? expression = null;
            if (why is null && sameIdAs is null)
            {
                if (!read.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "Tarol.Primitives.PerTenantAttribute"))
                    why = $"{read.Name} is not [PerTenant]: name the member with its Guid (SameIdAs = nameof(…))";
            }
            else if (why is null)
            {
                var source = members.FirstOrDefault(m => m.Name == sameIdAs);
                if (source is null)
                    why = $"'{sameIdAs}' is not a member of {type.Name}";
                else if (IdArgument(source.Type) is not null)
                    expression = "(global::Tarol.StronglyTypedId.Id<" + read.ToDisplayString(Qualified) + ">)this." + source.Name + ".Value";
                else if (source.Type.ToDisplayString() == "System.Guid")
                    expression = "(global::Tarol.StronglyTypedId.Id<" + read.ToDisplayString(Qualified) + ">)this." + source.Name;
                else
                    why = $"'{sameIdAs}' is neither an Id<X> nor a Guid";
            }
            if (why is null && reads.Any(r => r.Key == read.Name))
                why = $"{read.Name} is read twice";
            if (why is null && kind != ChangeKind.Creation && SymbolEqualityComparer.Default.Equals(read, aggregate))
                why = $"{read.Name} is the change's own target, which the pipeline loads anyway";
            if (why is not null)
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.ReadsMisuse, at, read.Name, type.Name, why));
            else
                reads.Add(new ReadModel(read.Name, read.ToDisplayString(Qualified), read.Name, expression, optional));
        }
        // FB-F05.7-1: CS8646 "explicitly implemented more than once" did not say why
        if (reads.Count > 0 && type.GetMembers().OfType<IMethodSymbol>().Any(static m => m.ExplicitInterfaceImplementations.Any(static i => i.ContainingType.ToDisplayString() == "Tarol.Changes.IHasReads")))
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.ReadsMisuse, location, "…", type.Name,
                "it implements IHasReads by hand next to [Read]/[Reads<T>], which the generator implements; move the hand-written loads into [Reads<T>] declarations"));

        string? target = null;
        if (kind == ChangeKind.Change)
        {
            var targetProperty = change.Construct(aggregate).GetMembers("Target").OfType<IPropertySymbol>().FirstOrDefault();
            var userTarget = targetProperty is not null && type.FindImplementationForInterfaceMember(targetProperty) is not null;
            if (!userTarget)
            {
                if (idOfAggregate.Count == 1)
                    target = idOfAggregate[0].Name;
                else if (idOfAggregate.Count == 0 && aggregate.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Tarol.Primitives.PerTenantAttribute"))
                    target = "";                                // TN-2: the pipeline loads the tenant's one aggregate
                else
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.NoTarget, location, type.Name, aggregate.Name, idOfAggregate.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        var validatable = compilation.GetTypeByMetadataName(ValidatableName);
        if (validatable is not null && !type.AllInterfaces.Contains(validatable, SymbolEqualityComparer.Default))
        {
            var ruled = members.Where(m => HasRules(m.Type)).Select(m => m.Name).ToArray();
            if (ruled.Length > 0)
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.NotValidated, location, type.Name, string.Join(", ", ruled)));
        }

        var model = new ChangeModel(
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            new EquatableArray<string>(Containing(type).ToImmutableArray()),
            Declaration(type),
            type.Name,
            type.ToDisplayString(Qualified),
            aggregate.ToDisplayString(Qualified),
            kind,
            target,
            new EquatableArray<ReadModel>(reads.ToImmutableArray()),
            Depth(type));

        if (model.NeedsPartial)
        {
            var notPartial = NotPartial(type, cancellationToken);
            if (notPartial is not null)
            {
                var what = target is not null ? $"IChange<{aggregate.Name}>.Target" : "the [Read] loads";
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.NotPartial, location, notPartial, what));
                model = model with { TargetMember = null, Reads = new EquatableArray<ReadModel>(ImmutableArray<ReadModel>.Empty) };
            }
        }

        return new Discovered(model, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutableArray()));

        static Discovered Report(DiagnosticInfo diagnostic)
            => new(null, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(diagnostic)));
    }

    /// <summary>Public instance properties, including inherited ones (records: the primary-constructor parameters).</summary>
    private static IEnumerable<IPropertySymbol> Members(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                if (!property.IsStatic && !property.IsIndexer && property.DeclaredAccessibility == Accessibility.Public
                    && property.ExplicitInterfaceImplementations.IsEmpty && property.Name != "EqualityContract" && seen.Add(property.Name))
                    yield return property;
    }

    private static bool ReadOptional(IPropertySymbol member, INamedTypeSymbol type, INamedTypeSymbol readAttribute)
    {
        var attributes = member.GetAttributes().AsEnumerable();
        foreach (var constructor in type.InstanceConstructors)
            foreach (var parameter in constructor.Parameters)
                if (parameter.Name == member.Name)
                    attributes = attributes.Concat(parameter.GetAttributes());
        return attributes.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, readAttribute)
                                   && a.NamedArguments.Any(static n => n.Key == "Optional" && n.Value.Value is true));
    }

    private static bool HasAttribute(IPropertySymbol property, INamedTypeSymbol type, INamedTypeSymbol attribute)
    {
        if (property.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute)))
            return true;
        foreach (var constructor in property.ContainingType.InstanceConstructors)
            foreach (var parameter in constructor.Parameters)
                if (parameter.Name == property.Name && parameter.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute)))
                    return true;
        return false;
    }

    private static INamedTypeSymbol? IdArgument(ITypeSymbol type)
        => type is INamedTypeSymbol { IsGenericType: true } named && named.OriginalDefinition.ToDisplayString() == "Tarol.StronglyTypedId.Id<T>"
            && named.TypeArguments[0] is INamedTypeSymbol argument
            ? argument
            : null;

    // By the base class, not by IAggregateRoot<T>: the Primitives generator adds that interface, and one generator never
    // sees another's output (found while building: the interface check rejected every aggregate).
    private static bool IsAggregate(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.OriginalDefinition.ToDisplayString() == "Tarol.Primitives.AggregateRoot<TSelf>"
                && SymbolEqualityComparer.Default.Equals(current.TypeArguments[0], type))
                return true;
        return false;
    }

    /// <summary>A value object with rules: a struct whose constructor parameters carry Tarol.Validations attributes (or an Option of one).</summary>
    private static bool HasRules(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true } option && option.OriginalDefinition.ToDisplayString() == "Tarol.Options.Option<T>")
            type = option.TypeArguments[0];
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named)
            return false;
        return named.InstanceConstructors.Any(c => c.Parameters.Any(p => p.GetAttributes().Any(a =>
            a.AttributeClass?.ContainingNamespace.ToDisplayString() == "Tarol.Validations")));
    }

    private static List<string> Containing(INamedTypeSymbol type)
    {
        var chain = new List<string>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            chain.Insert(0, Declaration(outer) + " " + outer.Name);
        return chain;
    }

    private static string Declaration(INamedTypeSymbol type) => type switch
    {
        { IsRecord: true, TypeKind: TypeKind.Struct } => "partial record struct",
        { IsRecord: true } => "partial record",
        { TypeKind: TypeKind.Struct } => "partial struct",
        { TypeKind: TypeKind.Interface } => "partial interface",
        _ => "partial class",
    };

    private static string? NotPartial(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            var partial = current.DeclaringSyntaxReferences.Select(r => r.GetSyntax(cancellationToken)).OfType<TypeDeclarationSyntax>()
                .Any(d => d.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (!partial)
                return current.Name;
        }
        return null;
    }

    private static int Depth(INamedTypeSymbol type)
    {
        var depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            depth++;
        return depth;
    }
}
