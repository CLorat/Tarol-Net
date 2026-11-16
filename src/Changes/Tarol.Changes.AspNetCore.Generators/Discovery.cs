using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tarol.Changes.AspNetCore.Generators;

/// <summary>Reads only what the user wrote ([Endpoint], [Webhook], IChange/ICreation/IView, the value objects' rule attributes);
/// never another generator's output (generators do not see each other).</summary>
internal static class Discovery
{
    public const string EndpointAttribute = "Tarol.Changes.AspNetCore.EndpointAttribute";
    public const string WebhookAttribute = "Tarol.Changes.AspNetCore.WebhookAttribute";
    private static readonly string[] Verbs = ["GET", "POST", "PUT", "PATCH", "DELETE"];
    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    public static EntryResult Entry(GeneratorAttributeSyntaxContext context, CancellationToken ct)
    {
        var type = (INamedTypeSymbol)context.TargetSymbol;
        var attribute = context.Attributes[0];
        var location = LocationInfo.From(type.Locations.FirstOrDefault() ?? Location.None);
        var route = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "";
        EntryResult Fail(DiagnosticDescriptor d, params string[] args)
            => new(null, default, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(DiagnosticInfo.Create(d, location, args))));

        var space = route.IndexOf(' ');
        if (space <= 0 || space == route.Length - 1 || route[space + 1] != '/')
            return Fail(Diagnostics.BadRoute, route, type.Name, "write the verb, one space and a pattern starting with '/'");
        var verb = route.Substring(0, space).ToUpperInvariant();
        if (Array.IndexOf(Verbs, verb) < 0)
            return Fail(Diagnostics.BadRoute, route, type.Name, $"'{verb}' is not GET, POST, PUT, PATCH or DELETE");
        var pattern = route.Substring(space + 1);
        var placeholders = Placeholders(pattern);

        if (type.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == "Tarol.Queries.IQuery<TRow>") is { } query)
            return List(type, attribute, verb, pattern, placeholders, (INamedTypeSymbol)query.TypeArguments[0], location, ct);

        string? aggregate = null;
        var kind = EntryKind.Change;
        foreach (var i in type.AllInterfaces)
        {
            var definition = i.OriginalDefinition.ToDisplayString();
            if (definition is "Tarol.Changes.IChange<T>" or "Tarol.Changes.ICreation<T>")
                (aggregate, kind) = (i.TypeArguments[0].ToDisplayString(Fq), definition.Contains("ICreation") ? EntryKind.Creation : EntryKind.Change);
            else if (definition == "Tarol.Changes.AspNetCore.IView<TAggregate, TSelf>")
                (aggregate, kind) = (i.TypeArguments[0].ToDisplayString(Fq), EntryKind.View);
        }
        if (aggregate is null)
            return Fail(Diagnostics.NotAnEntry, type.Name, IsAggregate(type)
                ? "an aggregate's stored state is not an API; declare a view (IView<TAggregate, TSelf>) with [Endpoint(\"GET …\")]"
                : "it implements neither IChange<T>, ICreation<T> nor IView<TAggregate, TSelf>");
        if (type.IsGenericType || type.DeclaredAccessibility != Accessibility.Public)
            return Fail(Diagnostics.NotAnEntry, type.Name, "an entry is a public, non-generic type");
        if ((kind == EntryKind.View) != (verb == "GET"))
            return Fail(Diagnostics.NotAnEntry, type.Name, kind == EntryKind.View ? "a view is read with GET" : "a change is not read with GET; use POST, PUT, PATCH or DELETE");

        var members = new List<Member>();
        var valueObjects = new List<ValueObjectModel>();
        var diagnostics = new List<DiagnosticInfo>();
        if (kind == EntryKind.View)
        {
            if (placeholders.Count != 1)
                return Fail(Diagnostics.RouteValue, type.Name, "a view's route has exactly one value, the aggregate's id");
            members.Add(new Member("Id", $"global::Tarol.StronglyTypedId.Id<{aggregate}>", null, placeholders[0], "id", false, null));
        }
        else
        {
            var ctor = type.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type)))
                .OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
            if (ctor is null || (ctor.Parameters.Length == 0 && type.GetMembers().OfType<IPropertySymbol>().Any(p => !p.IsStatic && p.SetMethod is not null && !p.IsImplicitlyDeclared && p.Name != "EqualityContract")))
                return new(null, default, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(DiagnosticInfo.Create(Diagnostics.NoConstructor, location, type.Name))));
            foreach (var p in ctor.Parameters)
            {
                ct.ThrowIfCancellationRequested();
                var name = char.ToUpperInvariant(p.Name[0]) + p.Name.Substring(1);
                var placeholder = placeholders.FirstOrDefault(h => string.Equals(h, p.Name, StringComparison.OrdinalIgnoreCase));
                var memberType = p.Type.ToDisplayString(Fq);
                string? routeKind = null;
                if (placeholder is not null)
                {
                    routeKind = RouteKind(p.Type);
                    if (routeKind is null)
                    {
                        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RouteValue, location, type.Name,
                            $"route member '{name}' is a {p.Type.ToDisplayString()}; route members are Id<T>, Guid, string, int or long"));
                        continue;
                    }
                }
                var property = type.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
                var jsonName = JsonName(p.GetAttributes()) ?? (property is null ? null : JsonName(property.GetAttributes()));
                var optional = p.HasExplicitDefaultValue || p.NullableAnnotation == NullableAnnotation.Annotated
                               || p.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
                members.Add(new Member(name, memberType, jsonName, placeholder, routeKind, optional, Default(p)));
                if (ValueObject(p.Type) is { } vo)
                    valueObjects.Add(vo);
            }
            foreach (var h in placeholders.Where(h => !ctor.Parameters.Any(p => string.Equals(h, p.Name, StringComparison.OrdinalIgnoreCase))))
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RouteValue, location, type.Name, $"the route value '{{{h}}}' is not a member of the input"));
            if (diagnostics.Count > 0)
                return new(null, default, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutableArray()));
        }
        var policy = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Policy").Value.Value as string;
        var anonymous = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Anonymous").Value.Value is true;
        var model = new EntryModel(type.Name, type.ToDisplayString(Fq), kind, aggregate, verb, pattern, policy, anonymous,
            new EquatableArray<Member>(members.ToImmutableArray()), location);
        return new(model, new EquatableArray<ValueObjectModel>(valueObjects.ToImmutableArray()), default);
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "tenant", "id", "version", "doc" };

    /// <summary>A list entry: an IQuery&lt;TRow&gt; record read with GET (step 12C, QR-4, QR-6).</summary>
    private static EntryResult List(INamedTypeSymbol type, AttributeData attribute, string verb, string pattern, List<string> placeholders,
        INamedTypeSymbol row, LocationInfo? location, CancellationToken ct)
    {
        var diagnostics = new List<DiagnosticInfo>();
        EntryResult Fail(DiagnosticDescriptor d, params string[] args)
            => new(null, default, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(DiagnosticInfo.Create(d, location, args))));
        if (verb != "GET")
            return Fail(Diagnostics.NotAnEntry, type.Name, "a list query is read with GET");
        if (type.IsGenericType || type.DeclaredAccessibility != Accessibility.Public)
            return Fail(Diagnostics.NotAnEntry, type.Name, "an entry is a public, non-generic type");
        var aggregate = RowAggregate(row);
        if (aggregate is null)
            return Fail(Diagnostics.NotAnEntry, type.Name, $"{row.Name} is not a [Projection] row record with When methods over an aggregate's events");

        // the row's typed columns: scalar public members except the key, in snake case
        var ctorParameters = row.InstanceConstructors.SelectMany(c => c.Parameters).ToList();
        var columns = new Dictionary<string, (ITypeSymbol Type, string Column, bool Sortable)>(StringComparer.Ordinal);
        foreach (var property in row.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public || property.Name == "EqualityContract")
                continue;
            var column = Snake(property.Name);
            if (Reserved.Contains(column) || !IsScalar(Underlying(property.Type)))
                continue;
            var sortable = HasAttribute(property, "Tarol.Queries.SortAttribute")
                           || ctorParameters.Any(p => p.Name == property.Name && HasAttribute(p, "Tarol.Queries.SortAttribute"));
            columns[property.Name] = (property.Type, column, sortable && !IsNullable(property.Type));
        }

        var ctor = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type)))
            .OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        if (ctor is null)
            return new(null, default, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(DiagnosticInfo.Create(Diagnostics.NoConstructor, location, type.Name))));

        var members = new List<Member>();
        var filters = new List<FilterModel>();
        var sorts = new List<SortModel>();
        string? sortMember = null, defaultSort = null;
        foreach (var p in ctor.Parameters)
        {
            ct.ThrowIfCancellationRequested();
            var name = char.ToUpperInvariant(p.Name[0]) + p.Name.Substring(1);
            var property = type.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
            var claim = ClaimOf(p) ?? (property is null ? null : ClaimOf(property));
            var placeholder = placeholders.FirstOrDefault(h => string.Equals(h, p.Name, StringComparison.OrdinalIgnoreCase));
            var underlying = Underlying(p.Type);
            void Bad(string why) => diagnostics.Add(DiagnosticInfo.Create(Diagnostics.QueryMember, location, type.Name, name, why));

            if (claim is null && string.Equals(name, "Sort", StringComparison.Ordinal) && underlying.TypeKind == TypeKind.Enum)
            {
                sortMember = name;
                foreach (var field in underlying.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                {
                    var descending = HasAttribute(field, "Tarol.Queries.DescendingAttribute");
                    if (field.Name == "Id")
                        sorts.Add(new SortModel(field.Name, "id", descending));
                    else if (columns.TryGetValue(field.Name, out var c) && c.Sortable)
                        sorts.Add(new SortModel(field.Name, c.Column, descending));
                    else
                        Bad($"the sort '{underlying.Name}.{field.Name}' names no [Sort] member of {row.Name} (nor Id)");
                    if (p.HasExplicitDefaultValue && Equals(p.ExplicitDefaultValue, field.ConstantValue))
                        defaultSort = field.Name;
                }
                defaultSort ??= sorts.FirstOrDefault()?.EnumMember;
                continue;
            }
            if (!columns.TryGetValue(name, out var target))
            {
                Bad($"{row.Name} has no typed column '{name}' (filters compare equal to a scalar member of the row)");
                continue;
            }
            if (!SymbolEqualityComparer.Default.Equals(underlying, Underlying(target.Type)))
            {
                Bad($"the column '{target.Column}' holds {Underlying(target.Type).ToDisplayString()}, not {underlying.ToDisplayString()}");
                continue;
            }
            var (parse, openApi) = ParseKind(underlying);
            var values = underlying.TypeKind == TypeKind.Enum
                ? underlying.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => f.Name).ToImmutableArray()
                : ImmutableArray<string>.Empty;
            var typeName = underlying.ToDisplayString(Fq);
            if (claim is not null)
            {
                if (underlying.SpecialType != SpecialType.System_String && underlying.ToDisplayString() != "System.Guid")
                {
                    Bad("a [FromClaim] member is a string or a Guid");
                    continue;
                }
                if (placeholder is not null)
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.ClaimAlsoRouted, location, type.Name, name, claim, placeholder));
                filters.Add(new FilterModel(name, typeName, target.Column, parse ?? "string", "claim", claim, openApi, new EquatableArray<string>(values)));
                continue;
            }
            if (placeholder is not null)
            {
                var routeKind = RouteKind(p.Type);
                if (routeKind is null or "id")
                {
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RouteValue, location, type.Name,
                        $"route member '{name}' of a list is a Guid, string, int or long"));
                    continue;
                }
                members.Add(new Member(name, p.Type.ToDisplayString(Fq), null, placeholder, routeKind, false, null));
                filters.Add(new FilterModel(name, typeName, target.Column, parse ?? "string", "route", null, openApi, new EquatableArray<string>(values)));
                continue;
            }
            if (!IsNullable(p.Type))
            {
                Bad("a filter member is nullable: absent means no filter");
                continue;
            }
            if (parse is null)
            {
                Bad($"{underlying.ToDisplayString()} cannot be read from the query string");
                continue;
            }
            filters.Add(new FilterModel(name, typeName, target.Column, parse!, "query", null, openApi, new EquatableArray<string>(values)));
        }
        foreach (var h in placeholders.Where(h => !ctor.Parameters.Any(p => string.Equals(h, p.Name, StringComparison.OrdinalIgnoreCase))))
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RouteValue, location, type.Name, $"the route value '{{{h}}}' is not a member of the input"));
        if (diagnostics.Any(d => d.Id != Diagnostics.ClaimAlsoRouted.Id))
            return new(null, default, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutableArray()));

        var policy = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Policy").Value.Value as string;
        var anonymous = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Anonymous").Value.Value is true;
        var model = new EntryModel(type.Name, type.ToDisplayString(Fq), EntryKind.List, aggregate, verb, pattern, policy, anonymous,
            new EquatableArray<Member>(members.ToImmutableArray()), location,
            new QueryModel(row.ToDisplayString(Fq), new EquatableArray<FilterModel>(filters.ToImmutableArray()), sortMember,
                new EquatableArray<SortModel>(sorts.ToImmutableArray()), defaultSort));
        return new(model, default, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutableArray()));
    }

    private static string? ClaimOf(ISymbol symbol)
        => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Tarol.Changes.AspNetCore.FromClaimAttribute")
            is { ConstructorArguments.Length: 1 } a ? a.ConstructorArguments[0].Value as string : null;

    private static bool HasAttribute(ISymbol symbol, string name) => symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == name);

    private static bool IsNullable(ITypeSymbol type)
        => type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T || (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated);

    private static ITypeSymbol Underlying(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);

    private static bool IsScalar(ITypeSymbol type)
        => type.TypeKind == TypeKind.Enum ? ((INamedTypeSymbol)type).EnumUnderlyingType?.SpecialType != SpecialType.System_UInt64
         : type.SpecialType is SpecialType.System_String or SpecialType.System_Boolean or SpecialType.System_Int16 or SpecialType.System_Int32
             or SpecialType.System_Int64 or SpecialType.System_Decimal or SpecialType.System_Double
           || type.ToDisplayString() is "System.Guid" or "System.DateTimeOffset";

    /// <summary>How the binder parses a filter ("string", "enum", "parsable") and its OpenAPI kind.</summary>
    private static (string? Parse, string OpenApi) ParseKind(ITypeSymbol type)
        => type.TypeKind == TypeKind.Enum ? ("enum", "enum")
         : type.SpecialType switch
         {
             SpecialType.System_String => ("string", "string"),
             SpecialType.System_Boolean => ("parsable", "boolean"),
             SpecialType.System_Int16 or SpecialType.System_Int32 => ("parsable", "int32"),
             SpecialType.System_Int64 => ("parsable", "int64"),
             SpecialType.System_Decimal or SpecialType.System_Double => ("parsable", "number"),
             _ => type.ToDisplayString() switch
             {
                 "System.Guid" => ("parsable", "uuid"),
                 "System.DateTimeOffset" => ("parsable", "date-time"),
                 _ => (null, "string"),
             },
         };

    /// <summary>The aggregate of a projection row: the root of its When methods' first event (AggregateRoot&lt;T&gt; or Entity&lt;TRoot, _&gt;).</summary>
    private static string? RowAggregate(INamedTypeSymbol row)
    {
        foreach (var method in row.GetMembers("When").OfType<IMethodSymbol>())
        {
            if (method.Parameters.Length == 0 || method.Parameters[0].Type is not INamedTypeSymbol domainEvent)
                continue;
            for (var current = domainEvent.BaseType; current is not null; current = current.BaseType)
            {
                if (current.ContainingType is not { } owner || owner.ContainingNamespace?.ToDisplayString() != "Tarol.Primitives")
                    continue;
                if (owner.MetadataName is "AggregateRoot`1" or "Entity`2")
                    return owner.TypeArguments[0].ToDisplayString(Fq);
            }
        }
        return null;
    }

    /// <summary>The column name of a member (the Queries generator's rule): EndsAt → ends_at.</summary>
    internal static string Snake(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previousLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (i > 0 && name[i - 1] != '_' && (previousLower || (nextLower && char.IsUpper(name[i - 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static ImmutableArray<HookResult> Hooks(GeneratorAttributeSyntaxContext context, CancellationToken ct)
    {
        var method = (IMethodSymbol)context.TargetSymbol;
        var location = LocationInfo.From(method.Locations.FirstOrDefault() ?? Location.None);
        var results = ImmutableArray.CreateBuilder<HookResult>();
        foreach (var attribute in context.Attributes)
        {
            ct.ThrowIfCancellationRequested();
            var sender = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "";
            var type = attribute.ConstructorArguments.Length > 1 ? attribute.ConstructorArguments[1].Value as string ?? "" : "";
            var version = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Version").Value.Value as string;
            var change = method.ContainingType;
            string? why = null;
            if (sender.Length == 0 || type.Length == 0 || sender.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
                why = "the sender is a name of letters, digits, '-' and '_', and the type is not empty";
            else if (!method.IsStatic || method.DeclaredAccessibility != Accessibility.Public || method.Parameters.Length != 1)
                why = "the factory is a public static method with one parameter, the payload";
            else if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, change))
                why = $"the factory returns its change, {change.Name}";
            else if (!change.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() is "Tarol.Changes.IChange<T>" or "Tarol.Changes.ICreation<T>"))
                why = $"{change.Name} is not a change (IChange<T> or ICreation<T>)";
            results.Add(why is not null
                ? new HookResult(null, DiagnosticInfo.Create(Diagnostics.BadWebhook, location, sender, type, method.Name, why))
                : new HookResult(new HookModel(sender, type, version, method.Parameters[0].Type.ToDisplayString(Fq), change.ToDisplayString(Fq), method.Name), null));
        }
        return results.ToImmutable();
    }

    public static IEnumerable<DiagnosticInfo> Duplicates(ImmutableArray<EntryModel> entries, ImmutableArray<HookModel> hooks)
    {
        foreach (var group in entries.GroupBy(e => e.Verb + " " + Normalized(e.Pattern), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var all = group.OrderBy(e => e.FullName, StringComparer.Ordinal).ToArray();
            yield return DiagnosticInfo.Create(Diagnostics.Duplicate, all[1].Location, all[0].Name, all[1].Name, group.Key);
        }
        foreach (var group in hooks.GroupBy(h => (h.Sender, h.Type, h.Version)).Where(g => g.Count() > 1))
            yield return DiagnosticInfo.Create(Diagnostics.BadWebhook, null, group.Key.Sender, group.Key.Type, group.First().Method,
                $"another factory reads the same (sender, type, version {group.Key.Version ?? "none"})");
    }

    private static string Normalized(string pattern)
    {
        var chars = new System.Text.StringBuilder();
        var inside = false;
        foreach (var c in pattern)
        {
            if (c == '{')
            { inside = true; chars.Append("{}"); }
            else if (c == '}')
                inside = false;
            else if (!inside)
                chars.Append(c);
        }
        return chars.ToString().TrimEnd('/');
    }

    private static bool IsAggregate(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (b.OriginalDefinition.ToDisplayString() == "Tarol.Primitives.AggregateRoot<TSelf>")
                return true;
        return false;
    }

    private static List<string> Placeholders(string pattern)
    {
        var list = new List<string>();
        for (var i = pattern.IndexOf('{'); i >= 0; i = pattern.IndexOf('{', i + 1))
        {
            var end = pattern.IndexOf('}', i);
            if (end < 0)
                break;
            var inner = pattern.Substring(i + 1, end - i - 1).TrimStart('*');
            var cut = inner.IndexOfAny([':', '=', '?']);
            list.Add(cut < 0 ? inner : inner.Substring(0, cut));
        }
        return list;
    }

    private static string? RouteKind(ITypeSymbol type)
        => type is INamedTypeSymbol { Name: "Id", TypeArguments.Length: 1 } id && id.ContainingNamespace.ToDisplayString() == "Tarol.StronglyTypedId" ? "id"
         : type.SpecialType switch
         {
             SpecialType.System_String => "string",
             SpecialType.System_Int32 => "int",
             SpecialType.System_Int64 => "long",
             _ => type.ToDisplayString() == "System.Guid" ? "guid" : null,
         };

    private static string? JsonName(ImmutableArray<AttributeData> attributes)
        => attributes.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute")
            is { ConstructorArguments.Length: 1 } a ? a.ConstructorArguments[0].Value as string : null;

    private static string? Default(IParameterSymbol p)
    {
        if (!p.HasExplicitDefaultValue)
            return null;
        var value = p.ExplicitDefaultValue;
        if (value is null)
            return "default!";
        var literal = value switch
        {
            string s => SymbolDisplay.FormatLiteral(s, quote: true),
            bool b => b ? "true" : "false",
            char c => SymbolDisplay.FormatLiteral(c, quote: true),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) + (value is decimal ? "m" : value is float ? "f" : value is double ? "d" : ""),
            _ => null,
        };
        return literal is null ? "default!" : $"({p.Type.ToDisplayString(Fq)})({literal})";
    }

    /// <summary>A value object: a record struct with one positional member of a JSON primitive, and its rules.</summary>
    private static ValueObjectModel? ValueObject(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsRecord: true, IsValueType: true } record)
            return null;
        var ctor = record.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 1 && !SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, record));
        if (ctor is null)
            return null;
        var p = ctor.Parameters[0];
        var (jsonType, format) = p.Type.SpecialType switch
        {
            SpecialType.System_String => ("String", (string?)null),
            SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte => ("Integer", "int32"),
            SpecialType.System_Int64 => ("Integer", "int64"),
            SpecialType.System_Decimal or SpecialType.System_Double or SpecialType.System_Single => ("Number", (string?)null),
            SpecialType.System_Boolean => ("Boolean", (string?)null),
            _ => p.Type.ToDisplayString() switch
            {
                "System.Guid" => ("String", "uuid"),
                "System.DateTimeOffset" or "System.DateTime" => ("String", "date-time"),
                "System.DateOnly" => ("String", "date"),
                _ => (null!, null),
            },
        };
        if (jsonType is null)
            return null;
        int? minLength = null, maxLength = null;
        string? minimum = null, maximum = null, pattern = null;
        foreach (var a in p.GetAttributes())
        {
            var named = a.NamedArguments.ToDictionary(n => n.Key, n => n.Value.Value);
            switch (a.AttributeClass?.ToDisplayString())
            {
                case "Tarol.Validations.LengthAttribute":
                    minLength = a.ConstructorArguments.Length == 2 ? (int?)a.ConstructorArguments[0].Value : named.TryGetValue("Min", out var mn) ? (int?)mn : null;
                    maxLength = a.ConstructorArguments.Length == 2 ? (int?)a.ConstructorArguments[1].Value : named.TryGetValue("Max", out var mx) ? (int?)mx : null;
                    if (maxLength == int.MaxValue)
                        maxLength = null;
                    break;
                case "Tarol.Validations.NotEmptyAttribute":
                    minLength ??= 1;
                    break;
                case "Tarol.Validations.RangeAttribute":
                    minimum = Number(a.ConstructorArguments.Length == 2 ? a.ConstructorArguments[0].Value : named.TryGetValue("Min", out var rmin) ? rmin : null);
                    maximum = Number(a.ConstructorArguments.Length == 2 ? a.ConstructorArguments[1].Value : named.TryGetValue("Max", out var rmax) ? rmax : null);
                    break;
                case "Tarol.Validations.PatternAttribute":
                    pattern = a.ConstructorArguments.Length == 1 ? a.ConstructorArguments[0].Value as string : null;
                    break;
                case "Tarol.Validations.EmailAttribute":
                    format = "email";
                    break;
            }
        }
        return new ValueObjectModel(record.ToDisplayString(Fq), jsonType, format, minLength, maxLength, minimum, maximum, pattern);
    }

    private static string? Number(object? value) => value switch
    {
        null => null,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => null,
    };
}
