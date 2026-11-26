using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tarol.Primitives.Generators;

/// <summary>
/// TPRM035 and TPRM036 (Tatalu: a context of 60 hand-kept entries): the types Tarol reads and writes through the application's
/// JSON options (events, the stored state's JSON members, endpoint inputs, views, list and projection rows, webhook payloads)
/// against what the assembly's <c>JsonSerializerContext</c>s generate: their entries and everything the System.Text.Json
/// generator reaches through them (members, elements, derived types). A source generator cannot add the entries itself: the
/// System.Text.Json generator never sees another generator's output. The code fixes (Tarol.Primitives.CodeFixes) add and remove
/// them.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JsonContextAnalyzer : DiagnosticAnalyzer
{
    // events and the stored state travel through the options only for aggregates registered with a codec (codec.Add<T>(), also in a
    // generated module); an aggregate kept in memory only (ChangeTest without a codec) serializes nothing
    private const string CodecName = "Tarol.Persistence.EventCodec";

    /// <summary>The property naming the context a missing type goes to (its metadata name).</summary>
    public const string ContextKey = "Context";

    /// <summary>The property holding the type, fully qualified.</summary>
    public const string TypeKey = "Type";

    /// <summary>The property holding the <c>TypeInfoPropertyName</c> a missing type needs, when its name would collide.</summary>
    public const string NameKey = "Name";

    private const string ContextBase = "System.Text.Json.Serialization.JsonSerializerContext";
    private const string SerializableName = "System.Text.Json.Serialization.JsonSerializableAttribute";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.JsonContextMissing, Diagnostics.JsonContextRedundant);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);             // a generated module registers the aggregates
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var contextBase = start.Compilation.GetTypeByMetadataName(ContextBase);
            if (contextBase is null || start.Compilation.GetTypeByMetadataName("Tarol.Primitives.DomainEvent") is null)
                return;
            var contexts = new ConcurrentBag<INamedTypeSymbol>();
            var required = new ConcurrentBag<Required>();
            var registered = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            start.RegisterSymbolAction(ctx => Collect((INamedTypeSymbol)ctx.Symbol, contextBase, contexts, required), SymbolKind.NamedType);
            start.RegisterSymbolAction(ctx => CollectWebhook((IMethodSymbol)ctx.Symbol, required), SymbolKind.Method);
            start.RegisterOperationAction(ctx =>
            {
                if (ctx.Operation is Microsoft.CodeAnalysis.Operations.IInvocationOperation { TargetMethod: { Name: "Add", TypeArguments.Length: 1 } method }
                    && method.ContainingType?.ToDisplayString() == CodecName && method.TypeArguments[0] is INamedTypeSymbol aggregate)
                    registered.TryAdd(aggregate, 0);
            }, OperationKind.Invocation);
            start.RegisterCompilationEndAction(end => Report(end, contexts, [.. required.Where(r => r.Root is null || registered.ContainsKey(r.Root))]));
        });
    }

    private sealed class Required(ITypeSymbol type, string reason, Location location, INamedTypeSymbol? root = null)
    {
        /// <summary>The aggregate whose codec registration makes the type required; <see langword="null"/>: always required.</summary>
        public INamedTypeSymbol? Root { get; } = root;

        public ITypeSymbol Type { get; } = type;

        public string Reason { get; } = reason;

        public Location Location { get; } = location;
    }

    private sealed class Entry(ITypeSymbol type, bool named, Location location)
    {
        public ITypeSymbol Type { get; } = type;

        public bool Named { get; } = named;

        public Location Location { get; } = location;
    }

    // ── what Tarol serializes ──────────────────────────────────────────────────────────────────────────────────────────

    private static void Collect(INamedTypeSymbol type, INamedTypeSymbol contextBase, ConcurrentBag<INamedTypeSymbol> contexts, ConcurrentBag<Required> required)
    {
        if (type.TypeKind != Microsoft.CodeAnalysis.TypeKind.Class && type.TypeKind != Microsoft.CodeAnalysis.TypeKind.Struct)
            return;
        if (Derives(type, contextBase))
        {
            if (!type.IsAbstract)
                contexts.Add(type);
            return;
        }
        var location = type.Locations.FirstOrDefault(static l => l.IsInSource) ?? Location.None;
        if (!location.IsInSource)
            return;                                                                         // generated code: only its registrations count
        if (!type.IsAbstract && !type.IsGenericType && EventRoot(type) is { } eventRoot)
            required.Add(new Required(type, "an event", location, eventRoot));

        // the stored state: members written through the options (StateJson.Info<T>)
        var root = Symbols.RootOf(type) ?? Symbols.EntityRootOf(type);
        if (root is not null && !Symbols.HasAttribute(root, "LoadByReplayAttribute"))
            foreach (var member in type.GetMembers())
                if (StateDiscovery.JsonStateType(member) is var (memberType, symbol))
                    required.Add(new Required(memberType, $"the stored state member {type.Name}.{symbol.Name}",
                        symbol.Locations.FirstOrDefault(static l => l.IsInSource) ?? location, root));

        foreach (var attribute in type.GetAttributes())
        {
            var name = attribute.AttributeClass?.ToDisplayString();
            // a projection row alone is not reported: the in-memory read store keeps objects, the Npgsql one names a missing row at startup
            if (name == "Tarol.Changes.AspNetCore.EndpointAttribute")
                Endpoint(type, attribute, location, required);
        }
    }

    // the aggregate of an event: the first library event base on its base chain (AggregateRoot<R>.Event, Entity<R, X>.Event, …)
    private static INamedTypeSymbol? EventRoot(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (Symbols.LibraryEventBase(current).Root is { } root)
                return root;
        return null;
    }

    private static void Endpoint(INamedTypeSymbol type, AttributeData attribute, Location location, ConcurrentBag<Required> required)
    {
        foreach (var i in type.AllInterfaces)
        {
            var definition = i.OriginalDefinition.ToDisplayString();
            if (definition is "Tarol.Queries.IQuery<TRow>" or "Tarol.Queries.IRowQuery<TRow>")
            {
                required.Add(new Required(i.TypeArguments[0], $"the rows of the query {type.Name}", location));
                return;
            }
            if (definition == "Tarol.Changes.AspNetCore.IView<TAggregate, TSelf>")
            {
                required.Add(new Required(type, "a view", location));
                return;
            }
        }
        // a change or creation input: its body members are read through the options (only when it has a body)
        var route = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "";
        var placeholders = Placeholders(route);
        var body = type.GetMembers().OfType<IPropertySymbol>().Any(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public
                                                                        && p.Name != "EqualityContract" && !p.IsImplicitlyDeclared
                                                                        && !placeholders.Contains(p.Name.ToUpperInvariant()));
        if (body)
            required.Add(new Required(type, "an endpoint input", location));
    }

    private static HashSet<string> Placeholders(string route)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);
        for (var open = route.IndexOf('{'); open >= 0; open = route.IndexOf('{', open + 1))
        {
            var close = route.IndexOf('}', open);
            if (close < 0)
                break;
            var name = route.Substring(open + 1, close - open - 1).TrimStart('*');
            var colon = name.IndexOfAny([':', '=', '?']);
            names.Add((colon < 0 ? name : name.Substring(0, colon)).ToUpperInvariant());
        }
        return names;
    }

    private static void CollectWebhook(IMethodSymbol method, ConcurrentBag<Required> required)
    {
        if (method.Parameters.Length == 0
            || !method.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "Tarol.Changes.AspNetCore.WebhookAttribute"))
            return;
        required.Add(new Required(method.Parameters[0].Type, $"the webhook payload of {method.ContainingType.Name}.{method.Name}",
            method.Locations.FirstOrDefault(static l => l.IsInSource) ?? Location.None));
    }

    // ── what the contexts generate ─────────────────────────────────────────────────────────────────────────────────────

    private static void Report(CompilationAnalysisContext end, ConcurrentBag<INamedTypeSymbol> found, List<Required> needed)
    {
        var contexts = found.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).OrderBy(static c => c.ToDisplayString(), System.StringComparer.Ordinal).ToList();
        if (contexts.Count == 0)
            return;
        var entries = contexts.ToDictionary(static c => c, Entries, SymbolEqualityComparer.Default);
        var generated = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var names = new Dictionary<INamedTypeSymbol, Dictionary<string, ITypeSymbol>>(SymbolEqualityComparer.Default);
        foreach (var context in contexts)
        {
            var fields = IncludeFields(context);
            var all = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var entry in entries[context])
                Walk(entry.Type, all, fields);
            generated.UnionWith(all);
            var byName = new Dictionary<string, ITypeSymbol>(System.StringComparer.Ordinal);
            foreach (var type in all)
                byName[GeneratedName(type)] = type;
            names[context] = byName;

            // TPRM036: an entry that an entry which stays reaches (one no other entry reaches, or one that pins a name), so removing
            // every reported entry at once keeps them all; entries that only reach each other are kept
            var listed = entries[context];
            var reach = listed.ToDictionary(static e => e, e => Reach(e.Type, fields));
            bool Reached(Entry entry) => listed.Any(other => !ReferenceEquals(other, entry) && !SymbolEqualityComparer.Default.Equals(other.Type, entry.Type)
                                                            && reach[other].Contains(entry.Type));
            var staying = listed.Where(e => e.Named || !Reached(e)).ToList();
            foreach (var entry in listed.Where(static e => !e.Named))
            {
                if (staying.Contains(entry))
                    continue;
                var through = staying.FirstOrDefault(other => !SymbolEqualityComparer.Default.Equals(other.Type, entry.Type) && reach[other].Contains(entry.Type));
                if (through is not null)
                    end.ReportDiagnostic(Diagnostic.Create(Diagnostics.JsonContextRedundant, entry.Location,
                        entry.Type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat), through.Type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
            }
        }

        // TPRM035: once per type, in declaration order
        var missing = needed.Where(r => !generated.Contains(r.Type) && r.Type.TypeKind != Microsoft.CodeAnalysis.TypeKind.Error && r.Type is not ITypeParameterSymbol)
            .GroupBy(static r => r.Type, SymbolEqualityComparer.Default)
            .Select(static g => g.OrderBy(static r => r.Location.SourceTree?.FilePath, System.StringComparer.Ordinal).ThenBy(static r => r.Location.SourceSpan.Start).First())
            .OrderBy(static r => r.Location.SourceTree?.FilePath, System.StringComparer.Ordinal).ThenBy(static r => r.Location.SourceSpan.Start)
            .ToList();
        var pending = new Dictionary<INamedTypeSymbol, List<ITypeSymbol>>(SymbolEqualityComparer.Default);
        foreach (var r in missing)
        {
            var context = Nearest(contexts, r.Type);
            if (!pending.TryGetValue(context, out var list))
                pending[context] = list = [];
            list.Add(r.Type);
        }
        foreach (var r in missing)
        {
            var context = Nearest(contexts, r.Type);
            var name = PinnedName(r.Type, names[context], pending[context]);
            var properties = ImmutableDictionary<string, string?>.Empty
                .Add(ContextKey, context.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Add(TypeKey, r.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            if (name is not null)
                properties = properties.Add(NameKey, name);
            var at = context.Locations.FirstOrDefault(static l => l.IsInSource);
            end.ReportDiagnostic(Diagnostic.Create(Diagnostics.JsonContextMissing, r.Location, at is null ? null : new[] { at }, properties,
                r.Type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat), r.Reason, context.Name));
        }
    }

    private static List<Entry> Entries(INamedTypeSymbol context)
    {
        var list = new List<Entry>();
        foreach (var attribute in context.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != SerializableName || attribute.ConstructorArguments.Length != 1
                || attribute.ConstructorArguments[0].Value is not ITypeSymbol type)
                continue;
            var named = attribute.NamedArguments.Any(static n => n.Key == "TypeInfoPropertyName" && n.Value.Value is string { Length: > 0 });
            var location = attribute.ApplicationSyntaxReference is { } syntax ? Location.Create(syntax.SyntaxTree, syntax.Span) : Location.None;
            list.Add(new Entry(type, named, location));
        }
        return list;
    }

    private static bool IncludeFields(INamedTypeSymbol context)
        => context.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute"
                                                  && a.NamedArguments.Any(static n => n.Key == "IncludeFields" && n.Value.Value is true));

    // what a type's members reach (the type itself only through a cycle)
    private static HashSet<ITypeSymbol> Reach(ITypeSymbol from, bool fields)
    {
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var child in Children(from, fields))
            Walk(child, seen, fields);
        return seen;
    }

    // the System.Text.Json generator's walk: elements, Nullable<T>, members (unless the type has its own converter), derived types
    private static void Walk(ITypeSymbol type, HashSet<ITypeSymbol> seen, bool fields)
    {
        var stack = new Stack<ITypeSymbol>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var current = stack.Pop().WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            if (!seen.Add(current))
                continue;
            foreach (var child in Children(current, fields))
                stack.Push(child);
        }
    }

    private static IEnumerable<ITypeSymbol> Children(ITypeSymbol type, bool fields)
    {
        if (type is IArrayTypeSymbol array)
        {
            yield return array.ElementType;
            yield break;
        }
        if (type is not INamedTypeSymbol named || Terminal(named))
            yield break;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            yield return named.TypeArguments[0];
            yield break;
        }
        var enumerable = Enumerable(named);
        if (enumerable is not null)
        {
            foreach (var argument in enumerable.TypeArguments)
                yield return argument;
            if (enumerable.TypeArguments[0] is INamedTypeSymbol { Name: "KeyValuePair", TypeArguments.Length: 2 } pair)
                foreach (var argument in pair.TypeArguments)
                    yield return argument;                                                  // a dictionary: keys and values
            yield break;
        }
        if (named.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonConverterAttribute"))
            yield break;
        foreach (var attribute in named.GetAttributes())
            if (attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonDerivedTypeAttribute"
                && attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is ITypeSymbol derived)
                yield return derived;
        for (INamedTypeSymbol? current = named; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic || Ignored(member))
                    continue;
                var included = member.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIncludeAttribute");
                if (member is IPropertySymbol { IsIndexer: false, GetMethod: not null } property
                    && (property.DeclaredAccessibility == Accessibility.Public || included) && property.Name != "EqualityContract")
                    yield return property.Type;
                else if (member is IFieldSymbol { IsImplicitlyDeclared: false } field && (fields && field.DeclaredAccessibility == Accessibility.Public || included))
                    yield return field.Type;
            }
    }

    private static bool Ignored(ISymbol member)
    {
        foreach (var attribute in member.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "System.Text.Json.Serialization.JsonIgnoreAttribute")
                continue;
            var condition = attribute.NamedArguments.FirstOrDefault(static n => n.Key == "Condition").Value;
            return condition.Value is null or 1;                                                // JsonIgnoreCondition.Always
        }
        return false;
    }

    // framework scalars and anything System.Text.Json writes itself: never walked into
    private static bool Terminal(INamedTypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None || type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum)
            return true;
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        return !type.IsGenericType && (ns == "System" || ns.StartsWith("System.Text.Json", System.StringComparison.Ordinal));
    }

    private static INamedTypeSymbol? Enumerable(INamedTypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
            return null;
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            return type;
        return type.AllInterfaces.FirstOrDefault(static i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
    }

    private static bool Derives(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        return false;
    }

    // ── where a missing type goes, and under which name ────────────────────────────────────────────────────────────────

    // the context whose namespace shares the longest prefix with the type's (one context per module), ties by name
    private static INamedTypeSymbol Nearest(List<INamedTypeSymbol> contexts, ITypeSymbol type)
    {
        var target = Element(type).ContainingNamespace?.ToDisplayString() ?? "";
        return contexts.OrderByDescending(c => Shared(c.ContainingNamespace?.ToDisplayString() ?? "", target)).First();
    }

    private static ITypeSymbol Element(ITypeSymbol type)
    {
        while (true)
        {
            if (type is IArrayTypeSymbol array)
                type = array.ElementType;
            else if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: > 0 } generic && generic.ContainingNamespace?.ToDisplayString().StartsWith("System", System.StringComparison.Ordinal) == true)
                type = generic.TypeArguments[generic.TypeArguments.Length - 1];
            else
                return type;
        }
    }

    private static int Shared(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        var n = 0;
        while (n < x.Length && n < y.Length && x[n] == y[n])
            n++;
        return n;
    }

    // System.Text.Json's property name for a type: the type name, generic arguments appended, "Array" for arrays
    internal static string GeneratedName(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => GeneratedName(array.ElementType) + "Array",
        INamedTypeSymbol { IsGenericType: true } generic => generic.Name + string.Concat(generic.TypeArguments.Select(GeneratedName)),
        _ => type.Name,
    };

    // a TypeInfoPropertyName when the generated name is another type's, or another missing type's: Containing + Name
    private static string? PinnedName(ITypeSymbol type, Dictionary<string, ITypeSymbol> names, List<ITypeSymbol> pending)
    {
        var name = GeneratedName(type);
        var taken = names.TryGetValue(name, out var other) && !SymbolEqualityComparer.Default.Equals(other, type);
        var twin = pending.Any(p => !SymbolEqualityComparer.Default.Equals(p, type) && GeneratedName(p) == name);
        if (!taken && !twin)
            return null;
        var prefix = type.ContainingType?.Name ?? type.ContainingNamespace?.Name ?? "";
        return prefix + name;
    }
}
