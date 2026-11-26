using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Queries.Generators;

/// <summary>
///     Turns a <c>[Projection]</c> record into a <see cref="ProjectionModel"/> of names and types only: its When methods
///     (signatures, never bodies) and its scalar members as typed columns.
/// </summary>
internal static class Discovery
{
    public const string AttributeName = "Tarol.Queries.ProjectionAttribute";
    private const string SortName = "Tarol.Queries.SortAttribute";
    private const string RecordedName = "Tarol.Primitives.RecordedEvent";
    private const string DomainEventName = "Tarol.Primitives.DomainEvent";

    private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;
    private static readonly Regex ValidName = new("^[a-z][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Reserved = new(System.StringComparer.Ordinal) { "tenant", "id", "version", "doc" };

    public static Discovered Transform(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type)
            return None;
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var attribute = context.Attributes[0];
        var location = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation()
                                         ?? type.Locations.FirstOrDefault() ?? Location.None);

        var declaration = Declaration(type, cancellationToken);
        if (declaration is not null)
            return Fail(DiagnosticInfo.Create(Diagnostics.Declaration, location, type.Name, declaration));

        var name = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
        var version = 1;
        var perEntity = false;
        var sql = false;
        var guarded = false;
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == "Version" && argument.Value.Value is int v)
                version = v;
            if (argument.Key == "PerEntity" && argument.Value.Value is bool e)
                perEntity = e;
            if (argument.Key == "Sql" && argument.Value.Value is bool q)
                sql = q;
            if (argument.Key == "Aggregate" && argument.Value.Value is not null)
                guarded = true;
        }
        if (name is null || !ValidName.IsMatch(name))
            return Fail(DiagnosticInfo.Create(Diagnostics.Declaration, location, type.Name,
                $"the name '{name}' must be lowercase letters, digits and hyphens, at most 40, starting with a letter"));
        if (version < 1)
            return Fail(DiagnosticInfo.Create(Diagnostics.Declaration, location, type.Name, $"the Version {version} must be 1 or more"));
        if (guarded && !sql)
            return Fail(DiagnosticInfo.Create(Diagnostics.Declaration, location, type.Name, "Aggregate names the read grant of an SQL projection's rows (Sql = true); a row projection's aggregate is its events'"));
        if (sql && perEntity)
            return Fail(DiagnosticInfo.Create(Diagnostics.Declaration, location, type.Name, "an SQL projection (Sql = true) keys its rows itself; it cannot be PerEntity"));

        // the When methods
        var creators = new List<(WhenModel Model, LocationInfo? Location)>();
        var updaters = new List<(WhenModel Model, LocationInfo? Location)>();
        string? aggregate = null;
        foreach (var method in type.GetMembers("When").OfType<IMethodSymbol>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method.MethodKind != MethodKind.Ordinary)
                continue;
            var at = LocationInfo.From(method.Locations.FirstOrDefault() ?? Location.None);
            var shown = string.Join(", ", method.Parameters.Select(p => p.Type.Name));
            if (sql)
            {
                // TN-54 (P-2): its ISqlProjection<TRow> writes the rows; a When would never run
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.When, at, type.Name, shown,
                    $"an SQL projection (Sql = true) has no When methods: its ISqlProjection<{type.Name}> writes the rows"));
                continue;
            }
            var (why, eventAggregate, when) = When(method, type);
            if (why is null && aggregate is not null && eventAggregate != aggregate)
                why = $"all events of a projection come from one aggregate; this one is of {Simple(eventAggregate!)}, the first of {Simple(aggregate)}";
            if (why is not null)
            {
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.When, at, type.Name, shown, why));
                continue;
            }
            aggregate ??= eventAggregate;
            (method.IsStatic ? creators : updaters).Add((when!, at));
        }
        foreach (var list in new[] { creators, updaters })
            foreach (var group in list.GroupBy(w => w.Model.Event).Where(g => g.Count() > 1))
                foreach (var duplicate in group.Skip(1))
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Creation, duplicate.Location, type.Name,
                        $"two {(list == creators ? "static" : "instance")} When methods for {Simple(group.Key)}; keep one"));
        // TN-54 (Tatalu FB-F01.2-2): a start row for a stream whose first event varies
        var hasEmpty = false;
        foreach (var empty in type.GetMembers("Empty").OfType<IMethodSymbol>().Where(static m => m.MethodKind == MethodKind.Ordinary))
        {
            if (empty.IsStatic && !empty.IsGenericMethod && SymbolEqualityComparer.Default.Equals(empty.ReturnType, type)
                && empty.Parameters.Length == 1 && empty.Parameters[0].RefKind == RefKind.None && empty.Parameters[0].Type.ToDisplayString() == RecordedName)
                hasEmpty = true;
            else
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.When, LocationInfo.From(empty.Locations.FirstOrDefault() ?? Location.None), type.Name,
                    string.Join(", ", empty.Parameters.Select(p => p.Type.Name)), $"the start row is 'public static {type.Name} Empty(RecordedEvent r)'"));
        }
        if (!sql && creators.Count == 0 && !hasEmpty && diagnostics.Count == 0)
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Creation, location, type.Name,
                $"no static When creates the row; add 'public static {type.Name} When(<event> e, RecordedEvent r)' for the stream's first event, or 'public static {type.Name} Empty(RecordedEvent r)' to start every instance When"));
        if (!sql && aggregate is null && diagnostics.Count == 0)
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Creation, location, type.Name, "no When names an event, so the stream is unknown"));

        // the columns
        var columns = new List<ColumnModel>();
        var parameters = type.InstanceConstructors.Where(c => c.Parameters.Length > 0)
            .SelectMany(c => c.Parameters).ToList();
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public || property.GetMethod is null
                || property.Name == "EqualityContract")
                continue;
            var sort = HasSort(property) || parameters.Any(p => p.Name == property.Name && HasSort(p));
            var column = Snake(property.Name);
            var mapped = Column(property.Type);
            string? problem = null;
            if (Reserved.Contains(column))
                problem = $"the column name '{column}' is reserved for the key (tenant, id, version, doc); the member stays in the row's jsonb";
            else if (mapped is null)
                problem = $"{property.Type.ToDisplayString()} has no column type (Guid, string, bool, short, int, long, decimal, double, DateTimeOffset, DateOnly, TimeOnly, enum, or an array of Guid, string, short, int, long, enum)";
            else if (mapped.Value.Array)
                problem = "an array column takes set filters only; it cannot be a sort";
            else if (mapped.Value.Nullable)
                problem = "a sort column cannot be nullable";
            if (problem is not null)
            {
                if (sort)
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.Sort,
                        LocationInfo.From(property.Locations.FirstOrDefault() ?? Location.None), type.Name, property.Name, problem));
                if (mapped is null || Reserved.Contains(column))
                    continue;
            }
            var (columnType, valueType, getter, nullable, array) = mapped!.Value;
            columns.Add(new ColumnModel(property.Name, column, columnType, valueType, getter.Replace("{0}", "r." + property.Name), nullable, sort && !nullable && !array, array));
        }

        if (diagnostics.Count > 0)
            return new Discovered(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
        var model = new ProjectionModel(
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            new EquatableArray<string>(Containing(type).ToImmutableArray()),
            type.Name,
            type.ToDisplayString(Qualified),
            name,
            version,
            aggregate ?? "",
            new EquatableArray<WhenModel>(creators.Select(c => c.Model).OrderByDescending(w => w.Depth).ThenBy(w => w.Event, System.StringComparer.Ordinal).ToImmutableArray()),
            new EquatableArray<WhenModel>(updaters.Select(c => c.Model).OrderByDescending(w => w.Depth).ThenBy(w => w.Event, System.StringComparer.Ordinal).ToImmutableArray()),
            new EquatableArray<ColumnModel>(columns.ToImmutableArray()))
        { HasEmpty = hasEmpty, PerEntity = perEntity, Sql = sql };
        return new Discovered(model, new EquatableArray<DiagnosticInfo>(ImmutableArray<DiagnosticInfo>.Empty));
    }

    private static readonly Discovered None = new(null, new EquatableArray<DiagnosticInfo>(ImmutableArray<DiagnosticInfo>.Empty));

    private static Discovered Fail(DiagnosticInfo diagnostic) => new(null, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(diagnostic)));

    private static string? Declaration(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        if (!type.IsRecord || type.TypeKind != TypeKind.Class)
            return "a projection must be a partial record class (sealed partial record)";
        if (type.IsGenericType)
            return "a projection cannot be generic";
        if (type.IsFileLocal)
            return "a projection cannot be file-local";
        for (var current = type; current is not null; current = current.ContainingType)
        {
            var partial = current.DeclaringSyntaxReferences.Select(r => r.GetSyntax(cancellationToken)).OfType<TypeDeclarationSyntax>()
                .Any(d => d.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (!partial)
                return current.Equals(type, SymbolEqualityComparer.Default)
                    ? "it must be declared partial so that IProjection can be generated"
                    : $"its containing type '{current.Name}' must be declared partial";
            if (!current.Equals(type, SymbolEqualityComparer.Default) && current.IsGenericType)
                return $"its containing type '{current.Name}' cannot be generic";
        }
        return null;
    }

    /// <summary>Checks one When; returns why it is wrong, or its aggregate and model.</summary>
    private static (string? Why, string? Aggregate, WhenModel? Model) When(IMethodSymbol method, INamedTypeSymbol row)
    {
        if (method.IsGenericMethod)
            return ("a When cannot be generic", null, null);
        if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, row))
            return ($"a When returns the row ({row.Name})", null, null);
        var parameters = method.Parameters;
        if (parameters.Length is not (1 or 2) || parameters.Any(p => p.RefKind != RefKind.None))
            return ("the parameters must be (TEvent) or (TEvent, RecordedEvent)", null, null);
        if (parameters.Length == 2 && parameters[1].Type.ToDisplayString() != RecordedName)
            return ($"the second parameter must be RecordedEvent, not {parameters[1].Type.Name}", null, null);
        if (parameters[0].Type is not INamedTypeSymbol domainEvent || domainEvent.IsAbstract || !IsDomainEvent(domainEvent))
            return ($"{parameters[0].Type.Name} is not a concrete event (a record deriving from an aggregate's or entity's Event, Created or Deleted)", null, null);
        var aggregate = AggregateOf(domainEvent);
        if (aggregate is null)
            return ($"{domainEvent.Name} is not an event of an aggregate (AggregateRoot<T>.Event or Entity<TRoot, T> events)", null, null);
        return (null, aggregate, new WhenModel(domainEvent.ToDisplayString(Qualified), parameters.Length == 2, Depth(domainEvent)));
    }

    private static bool IsDomainEvent(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == DomainEventName)
                return true;
        return false;
    }

    /// <summary>The aggregate whose stream holds the event: T of AggregateRoot&lt;T&gt;.Event, TRoot of Entity&lt;TRoot, T&gt; events.</summary>
    private static string? AggregateOf(INamedTypeSymbol domainEvent)
    {
        for (var current = domainEvent.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ContainingType is not { } owner || owner.ContainingNamespace?.ToDisplayString() != "Tarol.Primitives")
                continue;
            if (owner.MetadataName == "AggregateRoot`1" && owner.TypeArguments.Length == 1)
                return owner.TypeArguments[0].ToDisplayString(Qualified);
            if (owner.MetadataName == "Entity`2" && owner.TypeArguments.Length == 2)
                return owner.TypeArguments[0].ToDisplayString(Qualified);
        }
        return null;
    }

    private static bool HasSort(ISymbol symbol) => symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == SortName);

    /// <summary>The column of a member type: (ColumnType, stored type, getter format over {0}, nullable, array); null when none.</summary>
    private static (string Type, string ValueType, string Getter, bool Nullable, bool Array)? Column(ITypeSymbol type)
    {
        if (ArrayColumn(type) is { } array)
            return array;
        var scalar = Scalar(type);
        return scalar is { } s ? (s.Type, s.ValueType, s.Getter, s.Nullable, false) : null;
    }

    /// <summary>
    /// An array column (TN-6): <c>T[]</c> or <c>IReadOnlyList&lt;T&gt;</c> of a non-nullable Guid, string, short, int, long or
    /// enum (over int); the stored element type and a getter that yields a <c>T[]</c> (enums as their numbers).
    /// </summary>
    private static (string Type, string ValueType, string Getter, bool Nullable, bool Array)? ArrayColumn(ITypeSymbol type)
    {
        ITypeSymbol? element = null;
        var isArray = false;
        if (type is IArrayTypeSymbol { Rank: 1 } a)
            (element, isArray) = (a.ElementType, true);
        else if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } list
                 && list.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IReadOnlyList<T>")
            element = list.TypeArguments[0];
        if (element is null || element.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            || (element.IsReferenceType && element.NullableAnnotation == NullableAnnotation.Annotated))
            return null;
        var nullable = type.NullableAnnotation == NullableAnnotation.Annotated;
        string kind, stored;
        var cast = false;
        if (element.TypeKind == TypeKind.Enum && element is INamedTypeSymbol { EnumUnderlyingType.SpecialType: SpecialType.System_Int32 })
            (kind, stored, cast) = ("Int4", "int", true);
        else if (element.SpecialType == SpecialType.System_String)
            (kind, stored) = ("Text", "string");
        else if (element.SpecialType == SpecialType.System_Int16)
            (kind, stored) = ("Int2", "short");
        else if (element.SpecialType == SpecialType.System_Int32)
            (kind, stored) = ("Int4", "int");
        else if (element.SpecialType == SpecialType.System_Int64)
            (kind, stored) = ("BigInt", "long");
        else if (element.ToDisplayString() == "System.Guid")
            (kind, stored) = ("Uuid", "global::System.Guid");
        else
            return null;
        string getter;
        if (cast)
            getter = "{0} is null ? null : global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({0}, static v => (int)v))";
        else if (isArray)
            getter = "{0}";
        else
            getter = "{0} is null ? null : global::System.Linq.Enumerable.ToArray({0})";
        return (kind, stored, getter, nullable, true);
    }

    private static (string Type, string ValueType, string Getter, bool Nullable)? Scalar(ITypeSymbol type)
    {
        var nullable = false;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped)
        {
            type = wrapped.TypeArguments[0];
            nullable = true;
        }
        else if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            nullable = true;
        }
        var q = nullable && type.IsValueType ? "?" : "";
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying })
        {
            return underlying.SpecialType switch
            {
                SpecialType.System_Int64 or SpecialType.System_UInt32 => ("BigInt", "long" + q, "(long" + q + "){0}", nullable),
                SpecialType.System_UInt64 => null,
                _ => ("Int4", "int" + q, "(int" + q + "){0}", nullable),
            };
        }
        var plain = (string Type, string ValueType) => ((string, string, string, bool)?)(Type, ValueType + q, "{0}", nullable);
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return ("Text", nullable ? "string?" : "string", "{0}", nullable);
            case SpecialType.System_Boolean:
                return plain("Boolean", "bool");
            case SpecialType.System_Int16:
                return plain("Int2", "short");
            case SpecialType.System_Int32:
                return plain("Int4", "int");
            case SpecialType.System_Int64:
                return plain("BigInt", "long");
            case SpecialType.System_Decimal:
                return plain("Numeric", "decimal");
            case SpecialType.System_Double:
                return plain("Float8", "double");
        }
        var name = type.ToDisplayString();
        if (name == "System.Guid")
            return plain("Uuid", "global::System.Guid");
        if (name == "System.DateOnly")
            return plain("Date", "global::System.DateOnly");
        if (name == "System.TimeOnly")
            return ("Time", "global::System.TimeOnly" + q, "global::Tarol.Queries.ColumnValues.Time({0})", nullable);
        if (name == "System.DateTimeOffset")
            return ("TimestampTz", "global::System.DateTimeOffset" + q, "global::Tarol.Queries.ColumnValues.Timestamp({0})", nullable);
        return null;
    }

    /// <summary>EndsAt → ends_at, HighestBid → highest_bid, URLPath → url_path.</summary>
    internal static string Snake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
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

    private static List<string> Containing(INamedTypeSymbol type)
    {
        var chain = new List<string>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            chain.Insert(0, (outer.IsRecord ? (outer.TypeKind == TypeKind.Struct ? "partial record struct" : "partial record")
                : outer.TypeKind == TypeKind.Struct ? "partial struct" : outer.TypeKind == TypeKind.Interface ? "partial interface" : "partial class") + " " + outer.Name);
        return chain;
    }

    private static int Depth(INamedTypeSymbol type)
    {
        var depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            depth++;
        return depth;
    }

    private static string Simple(string qualified) => qualified.Substring(qualified.LastIndexOf('.') + 1);
}
