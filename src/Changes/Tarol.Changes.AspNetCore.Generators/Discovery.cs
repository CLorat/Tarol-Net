using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
        if (attribute.NamedArguments.Any(n => n.Key == "RequiresScope" && n.Value.Value is true))
            return Fail(Diagnostics.NotAnEntry, type.Name, "RequiresScope applies to list queries (IQuery<TRow>) only");

        string? aggregate = null;
        var kind = EntryKind.Change;
        var perTenant = false;
        foreach (var i in type.AllInterfaces)
        {
            var definition = i.OriginalDefinition.ToDisplayString();
            if (definition is not ("Tarol.Changes.IChange<T>" or "Tarol.Changes.ICreation<T>" or "Tarol.Changes.AspNetCore.IView<TAggregate, TSelf>"))
                continue;
            (aggregate, kind) = (i.TypeArguments[0].ToDisplayString(Fq),
                definition.Contains("IView") ? EntryKind.View : definition.Contains("ICreation") ? EntryKind.Creation : EntryKind.Change);
            perTenant = i.TypeArguments[0].GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Tarol.Primitives.PerTenantAttribute");
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
        var warnings = new List<DiagnosticInfo>();
        var memberRules = new List<MemberRulesModel>();
        var reached = new HashSet<string>(StringComparer.Ordinal);
        if (kind == EntryKind.View)
        {
            if (perTenant && placeholders.Count == 0)
            {
                // TN-2: the tenant's one aggregate; no member, the delegate loads it from the caller's tenant
            }
            else if (placeholders.Count != 1)
                return Fail(Diagnostics.RouteValue, type.Name, perTenant
                    ? "the view of a [PerTenant] aggregate has no route value (the tenant's one), or exactly one, the aggregate's id"
                    : "a view's route has exactly one value, the aggregate's id");
            else
                members.Add(new Member("Id", $"global::Tarol.StronglyTypedId.Id<{aggregate}>", null, placeholders[0], "id", false, null));
            if (type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).OrderByDescending(c => c.Parameters.Length).FirstOrDefault() is { } shape)
                foreach (var p in shape.Parameters)
                    if (IsOptionWithDefault(p))
                        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.OptionDefault, location, type.Name, p.Name));
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
                members.Add(new Member(name, memberType, jsonName, placeholder, routeKind, optional, Default(p))
                {
                    Nested = placeholder is null ? NestedOf(p.Type, 0) : default,
                    NestedList = IsList(p.Type),
                });
                if (placeholder is null)
                    Reach(type, p, name, jsonName, valueObjects, memberRules, reached, 0);
                if (IsOptionWithDefault(p))
                    warnings.Add(DiagnosticInfo.Create(Diagnostics.OptionDefault, location, type.Name, name));
            }
            foreach (var h in placeholders.Where(h => !ctor.Parameters.Any(p => string.Equals(h, p.Name, StringComparison.OrdinalIgnoreCase))))
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RouteValue, location, type.Name, $"the route value '{{{h}}}' is not a member of the input"));
            if (diagnostics.Count > 0)
                return new(null, default, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutableArray()));
        }
        var policy = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Policy").Value.Value as string;
        var anonymous = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Anonymous").Value.Value is true;
        var model = new EntryModel(type.Name, type.ToDisplayString(Fq), kind, aggregate, verb, pattern, policy, anonymous,
            new EquatableArray<Member>(members.ToImmutableArray()), location)
        {
            Errors = Codes(kind, aggregate, placeholders.Contains("tenant"), type, context.SemanticModel.Compilation, ct, perTenant),
        };
        warnings.AddRange(diagnostics);                                         // a view's warnings (an entry with errors returned earlier)
        return new(model, new EquatableArray<ValueObjectModel>(valueObjects.ToImmutableArray()), new EquatableArray<DiagnosticInfo>(warnings.ToImmutableArray()))
        {
            MemberRules = new EquatableArray<MemberRulesModel>(memberRules.ToImmutableArray()),
        };
    }

    /// <summary>
    /// BC-4: the rules a member carries (its own attributes, as the validation generator reads them) and the types it reaches:
    /// a single-field value object becomes a <see cref="ValueObjectModel"/> (rules on its schema), a multi-field or class value
    /// object and an <c>IValidatable</c> record give the rules of their own members (rules on their property schemas), to a
    /// depth of four. <c>Option&lt;T&gt;</c>, <c>T?</c> and the element of a list or array are looked through.
    /// </summary>
    private static void Reach(INamedTypeSymbol owner, IParameterSymbol p, string member, string? jsonName, List<ValueObjectModel> valueObjects,
        List<MemberRulesModel> memberRules, HashSet<string> reached, int depth)
    {
        var type = Unwrap(p.Type);
        var rules = RulesOf(p.GetAttributes());
        if (rules.Count > 0)
            memberRules.Add(new MemberRulesModel(owner.ToDisplayString(Fq), member, jsonName, new EquatableArray<RuleModel>(rules.ToImmutableArray())));
        if (depth >= 4 || !reached.Add(type.ToDisplayString(Fq)))
            return;
        if (ValueObject(type) is { } vo)
        {
            valueObjects.Add(vo);
            return;
        }
        if (type is not INamedTypeSymbol { IsRecord: true } record || record.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true)
            return;
        var ctor = record.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public
                && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, record)))
            .OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        if (ctor is null)
            return;
        foreach (var inner in ctor.Parameters)
        {
            var name = char.ToUpperInvariant(inner.Name[0]) + inner.Name.Substring(1);
            var property = record.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
            Reach(record, inner, name, JsonName(inner.GetAttributes()) ?? (property is null ? null : JsonName(property.GetAttributes())), valueObjects, memberRules,
                reached, depth + 1);
        }
    }

    /// <summary>
    /// TN-23 (Tatalu FB-F02.1-3, FB-F03.9-1): the constructor members of the record <paramref name="type"/> holds (through
    /// <c>Option&lt;T&gt;</c>, <c>T?</c> or a list), to a depth of four, so the binder reports a missing one as <c>Required</c>
    /// instead of reading a <c>DateOnly</c> as 0001-01-01 or an enum as its first value. Value objects and BCL types end it.
    /// </summary>
    private static EquatableArray<RequiredModel> NestedOf(ITypeSymbol type, int depth)
    {
        if (depth >= 4 || ValueObject(Unwrap(type)) is not null)
            return default;
        if (Unwrap(type) is not INamedTypeSymbol { IsRecord: true, TypeKind: TypeKind.Class or TypeKind.Struct } record
            || record.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true)
            return default;
        var ctor = record.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public
                && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, record)))
            .OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        if (ctor is null || ctor.Parameters.Length == 0)
            return default;
        var nested = new List<RequiredModel>();
        foreach (var inner in ctor.Parameters)
        {
            var name = char.ToUpperInvariant(inner.Name[0]) + inner.Name.Substring(1);
            var property = record.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
            var optional = inner.HasExplicitDefaultValue || inner.NullableAnnotation == NullableAnnotation.Annotated
                           || inner.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            nested.Add(new RequiredModel(name, JsonName(inner.GetAttributes()) ?? (property is null ? null : JsonName(property.GetAttributes())), optional,
                IsList(inner.Type), NestedOf(inner.Type, depth + 1)));
        }
        return new EquatableArray<RequiredModel>(nested.ToImmutableArray());
    }

    private static bool IsList(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { TypeArguments.Length: 1 } generic && generic.OriginalDefinition.ToDisplayString() == "Tarol.Options.Option<T>")
            type = generic.TypeArguments[0];
        return type is IArrayTypeSymbol || (type is INamedTypeSymbol { TypeArguments.Length: 1 } list && list.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.List<T>" or "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.IReadOnlyCollection<T>" or "System.Collections.Generic.IEnumerable<T>");
    }

    /// <summary>The value type behind <c>Option&lt;T&gt;</c>, <c>T?</c>, <c>IReadOnlyList&lt;T&gt;</c> and <c>T[]</c>.</summary>
    private static ITypeSymbol Unwrap(ITypeSymbol type)
    {
        for (var i = 0; i < 3; i++)
        {
            if (type is IArrayTypeSymbol array)
                type = array.ElementType;
            else if (type is INamedTypeSymbol { TypeArguments.Length: 1 } generic
                     && (generic.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                         || generic.OriginalDefinition.ToDisplayString() == "Tarol.Options.Option<T>"))
                type = generic.TypeArguments[0];
            else if (type is INamedTypeSymbol { TypeArguments.Length: 1 } list && list.OriginalDefinition.ToDisplayString() is
                     "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.List<T>" or "System.Collections.Generic.IList<T>"
                     or "System.Collections.Generic.IReadOnlyCollection<T>" or "System.Collections.Generic.IEnumerable<T>")
                type = list.TypeArguments[0];
            else
                break;
        }
        return type;
    }

    /// <summary>The <c>Tarol.Validations</c> rules of one parameter, as the client checks them (BC-4): rule, code, bounds, pattern.</summary>
    internal static List<RuleModel> RulesOf(ImmutableArray<AttributeData> attributes)
    {
        var rules = new List<RuleModel>();
        foreach (var a in attributes)
        {
            if (a.AttributeClass?.ContainingNamespace?.ToDisplayString() != "Tarol.Validations")
                continue;
            var named = a.NamedArguments.ToDictionary(n => n.Key, n => n.Value.Value);
            object? Arg(int index, string name) => a.ConstructorArguments.Length == 2 ? a.ConstructorArguments[index].Value : named.TryGetValue(name, out var v) ? v : null;
            string? Bound(object? value) => value is int.MaxValue ? null : Number(value);
            switch (a.AttributeClass.Name)
            {
                case "LengthAttribute":
                    rules.Add(new RuleModel("Length", "Length", Bound(Arg(0, "Min")) ?? "0", Bound(Arg(1, "Max")), null, null));
                    break;
                case "CountAttribute":
                    rules.Add(new RuleModel("Count", "Count", Bound(Arg(0, "Min")) ?? "0", Bound(Arg(1, "Max")), null, null));
                    break;
                case "RangeAttribute":
                    rules.Add(new RuleModel("Range", "Range", Number(Arg(0, "Min")), Number(Arg(1, "Max")), null, null));
                    break;
                case "ScaleAttribute" when a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is int digits and >= 0 and <= 28:
                    // TN-53: multipleOf 10^-digits; the step as text (0.01), exact as a decimal
                    rules.Add(new RuleModel("Scale", "Scale", null, null, null, null,
                        digits == 0 ? "1" : "0." + new string('0', digits - 1) + "1"));
                    break;
                case "NotEmptyAttribute":
                    rules.Add(new RuleModel("NotEmpty", "Required", null, null, null, null));
                    break;
                case "EmailAttribute":
                    rules.Add(new RuleModel("Email", "Email", null, null, null, null));
                    break;
                case "PatternAttribute":
                    var pattern = a.ConstructorArguments.Length == 1 ? a.ConstructorArguments[0].Value as string : null;
                    // JavaScript flags: i for IgnoreCase, u for Unicode categories (\p{…}, which RegExp reads only with u)
                    var flags = (named.TryGetValue("IgnoreCase", out var ignore) && ignore is true ? "i" : "")
                                + (pattern is not null && (pattern.Contains("\\p{") || pattern.Contains("\\P{")) ? "u" : "");
                    rules.Add(new RuleModel("Pattern", "Pattern", null, null, pattern, flags.Length == 0 ? null : flags));
                    break;
            }
        }
        return rules;
    }

    private static bool IsOptionWithDefault(IParameterSymbol p)
        => p.HasExplicitDefaultValue && p.Type is INamedTypeSymbol { Name: "Option", TypeArguments.Length: 1 } option
           && option.ContainingNamespace.ToDisplayString() == "Tarol.Options";

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
                new EquatableArray<SortModel>(sorts.ToImmutableArray()), defaultSort,
                attribute.NamedArguments.FirstOrDefault(n => n.Key == "RequiresScope").Value.Value is true))
        {
            Errors = Codes(EntryKind.List, aggregate, placeholders.Contains("tenant"), type, null, ct),
        };
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
           || type.ToDisplayString() is "System.Guid" or "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly";

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
                 "System.DateOnly" => ("date", "date"),
                 "System.TimeOnly" => ("time", "time"),
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

    /// <summary>
    /// The problem codes an entry can answer that are known at compile time (BC-3): Tarol's own for the kind, and the constant
    /// first argument of every <c>Tarol.Results.Error</c> factory called in the input's methods and in the methods of the input
    /// or the aggregate that those call (two levels; source only). A code built at run time is not found.
    /// </summary>
    internal static EquatableArray<string> Codes(EntryKind kind, string aggregate, bool tenantRoute, INamedTypeSymbol input, Compilation? compilation,
        CancellationToken ct, bool perTenant = false)
    {
        var name = Short(aggregate);
        var codes = new SortedSet<string>(StringComparer.Ordinal) { "Auth.Forbidden" };
        if (tenantRoute)
            codes.Add("Tenant.NotFound");
        var loads = input.AllInterfaces.Any(static i => i.OriginalDefinition.ToDisplayString() == "Tarol.Changes.ILoads<T>");
        switch (kind)
        {
            case EntryKind.Change:
                // TN-30 (Tatalu FB-F04.1-2): an ILoads change loads itself; its NotFound is only what Load returns (scanned below)
                if (!loads)
                    codes.Add(name + ".NotFound");
                codes.UnionWith(["Stream.VersionConflict", "Idempotency.KeyReused", "Idempotency.DuplicateKey"]);
                break;
            case EntryKind.Creation:
                // TN-30 (Tatalu FB-F17.6-1): AlreadyExists only when the id can repeat: it comes from the input, or is the tenant's
                if (perTenant || IdMembers(input).Any(t => t.ToDisplayString(Fq) == aggregate))
                    codes.Add(name + ".AlreadyExists");
                codes.UnionWith(["Stream.VersionConflict", "Idempotency.KeyReused", "Idempotency.DuplicateKey"]);
                break;
            case EntryKind.View:
                codes.Add(name + ".NotFound");
                break;
            case EntryKind.List:
                codes.Add("Projection.Building");
                break;
        }
        if (kind is EntryKind.Change or EntryKind.Creation)
        {
            // TN-30 (Tatalu FB-F04.1-2): a missing [Read] aggregate is 422 <X>.NotFound
            foreach (var member in input.GetMembers().Where(static m => m is IPropertySymbol or IParameterSymbol))
                if (member.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "Tarol.Changes.ReadAttribute") && IdTarget(member) is { } read)
                    codes.Add(read.Name + ".NotFound");
            foreach (var ctor in input.InstanceConstructors)
                foreach (var p in ctor.Parameters)
                    if (p.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "Tarol.Changes.ReadAttribute") && IdTarget(p) is { } read)
                        codes.Add(read.Name + ".NotFound");
        }
        if (kind is EntryKind.Change or EntryKind.Creation && compilation is not null)
        {
            var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            foreach (var method in input.GetMembers().OfType<IMethodSymbol>())
                if (!method.IsImplicitlyDeclared)
                    Scan(method, 0);

            void Scan(IMethodSymbol method, int depth)
            {
                if (!visited.Add(method.OriginalDefinition))
                    return;
                foreach (var reference in method.OriginalDefinition.DeclaringSyntaxReferences)
                {
                    ct.ThrowIfCancellationRequested();
                    var node = reference.GetSyntax(ct);
                    if (!compilation!.ContainsSyntaxTree(node.SyntaxTree))
                        continue;
                    var model = compilation.GetSemanticModel(node.SyntaxTree);
                    foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol called)
                            continue;
                        if (called.IsStatic && called.ContainingType?.ToDisplayString() is "Tarol.Results.Error" or "Tarol.Results.Invalid")
                        {
                            // TN-38 (Tatalu FB-F01.1-9, FB-F15.1-8): also Invalid.Field codes of the change and its aggregate
                            if (Code(invocation, called, model, ct) is { } code)
                                codes.Add(code);
                        }
                        else if (depth < 2 && called.DeclaringSyntaxReferences.Length > 0
                                 && (SymbolEqualityComparer.Default.Equals(called.ContainingType, input)
                                     || called.ContainingType?.ToDisplayString(Fq) == aggregate))
                            Scan(called, depth + 1);
                    }
                }
            }
        }
        return new EquatableArray<string>(codes.ToImmutableArray());
    }

    private static IEnumerable<ITypeSymbol> IdMembers(INamedTypeSymbol input)
        => input.InstanceConstructors.SelectMany(static c => c.Parameters).Select(static p => IdTarget(p)).Where(static t => t is not null).Select(static t => t!);

    /// <summary>The <c>T</c> of an <c>Id&lt;T&gt;</c> (or <c>Option&lt;Id&lt;T&gt;&gt;</c>) member.</summary>
    private static ITypeSymbol? IdTarget(ISymbol member)
    {
        var type = member switch { IPropertySymbol p => p.Type, IParameterSymbol p => p.Type, _ => null };
        if (type is INamedTypeSymbol { TypeArguments.Length: 1 } option && option.OriginalDefinition.ToDisplayString() == "Tarol.Options.Option<T>")
            type = option.TypeArguments[0];
        return type is INamedTypeSymbol { TypeArguments.Length: 1 } id && id.OriginalDefinition.ToDisplayString() == "Tarol.StronglyTypedId.Id<T>" ? id.TypeArguments[0] : null;
    }

    /// <summary>The code of an <c>Error</c> factory call: a constant <c>code</c> argument, or the parameter's default.</summary>
    private static string? Code(InvocationExpressionSyntax invocation, IMethodSymbol called, SemanticModel model, CancellationToken ct)
    {
        var parameter = called.Parameters.FirstOrDefault(p => p.Name == "code");
        if (parameter is null || parameter.Type.SpecialType != SpecialType.System_String)
            return null;
        var index = called.Parameters.IndexOf(parameter);
        foreach (var (argument, i) in invocation.ArgumentList.Arguments.Select((a, i) => (a, i)))
            if (argument.NameColon?.Name.Identifier.Text == "code" || (argument.NameColon is null && i == index))
                return model.GetConstantValue(argument.Expression, ct).Value as string;
        return parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue as string : null;
    }

    private static string Short(string fullName)
    {
        var name = fullName.StartsWith("global::", StringComparison.Ordinal) ? fullName.Substring(8) : fullName;
        var generic = name.IndexOf('<');
        if (generic >= 0)
            name = name.Substring(0, generic);
        return name.Substring(name.LastIndexOf('.') + 1);
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

    /// <summary>
    /// A value object: a record struct with one positional member of a JSON primitive, and its rules. A record struct of a
    /// referenced assembly does not say it is a record in metadata; there the <c>Check</c> method the validation generator
    /// writes marks it (Tatalu FB-F01.1-3, FB-F02.1-4: a value object of a contract assembly without endpoints was <c>{}</c>).
    /// </summary>
    private static ValueObjectModel? ValueObject(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsValueType: true } record || !(record.IsRecord || HasCheck(record)))
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
                "System.TimeOnly" => ("String", "time"),
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
        return new ValueObjectModel(record.ToDisplayString(Fq), jsonType, format, minLength, maxLength, minimum, maximum, pattern)
        {
            Rules = new EquatableArray<RuleModel>(RulesOf(p.GetAttributes()).ToImmutableArray()),
        };
    }

    /// <summary>The generated <c>static void Check(T value, ref ValidationErrors errors)</c> of a value object (Tarol.Validations).</summary>
    private static bool HasCheck(INamedTypeSymbol type)
        => type.GetMembers("Check").OfType<IMethodSymbol>().Any(m => m is { IsStatic: true, ReturnsVoid: true, Parameters.Length: 2 }
            && m.Parameters[1].RefKind == RefKind.Ref && m.Parameters[1].Type.ToDisplayString() == "Tarol.Validations.ValidationErrors"
            && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, type));

    private static string? Number(object? value) => value switch
    {
        null => null,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => null,
    };
}
