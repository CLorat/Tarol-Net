using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Tarol.Cron;

namespace Tarol.Changes.Generators;

/// <summary>
///     Finds <c>[Scheduled]</c> factories (<c>ForAttributeWithMetadataName</c>) and turns each into a <see cref="JobModel"/> of
///     names and literals only (step 14A, jobs.md §3). The cron literal is checked with the parser's own field rules (TCHG010).
/// </summary>
internal static class JobDiscovery
{
    public const string AttributeName = "Tarol.Changes.ScheduledAttribute";
    private const string ChangeName = "Tarol.Changes.IChange`1";
    private const string CreationName = "Tarol.Changes.ICreation`1";
    private const string OccurrenceName = "Tarol.Changes.JobOccurrence";

    private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;

    public static ImmutableArray<DiscoveredJob> Transform(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not IMethodSymbol method)
            return ImmutableArray<DiscoveredJob>.Empty;
        var builder = ImmutableArray.CreateBuilder<DiscoveredJob>(context.Attributes.Length);
        foreach (var attribute in context.Attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(One(method, attribute, context.SemanticModel.Compilation));
        }
        return builder.MoveToImmutable();
    }

    private static DiscoveredJob One(IMethodSymbol method, AttributeData attribute, Compilation compilation)
    {
        var location = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                                         ?? method.Locations.FirstOrDefault() ?? Location.None);
        var change = method.ContainingType;
        var factory = change.Name + "." + method.Name;
        var why = Shape(method, change, compilation);
        if (why is not null)
            return Fail(DiagnosticInfo.Create(Diagnostics.JobShape, location, factory, why));

        var cron = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
        if (CronFields.TryParse(cron, out _) is { } invalid)
            return Fail(DiagnosticInfo.Create(Diagnostics.JobCron, location, cron ?? "", factory, invalid));

        string? name = change.Name;
        var zone = "UTC";
        var missed = 0;
        var perTenant = false;
        foreach (var argument in attribute.NamedArguments)
        {
            switch (argument.Key)
            {
                case "Name":
                    name = argument.Value.Value as string;
                    break;
                case "TimeZone":
                    zone = argument.Value.Value as string ?? "";
                    break;
                case "Missed" when argument.Value.Value is int value:
                    missed = value;
                    break;
                case "PerTenant" when argument.Value.Value is bool value:
                    perTenant = value;
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(name))
            return Fail(DiagnosticInfo.Create(Diagnostics.JobShape, location, factory, "Name is empty"));
        if (string.IsNullOrWhiteSpace(zone))
            return Fail(DiagnosticInfo.Create(Diagnostics.JobShape, location, factory, "TimeZone is empty (leave it out for UTC)"));
        if (missed is < 0 or > 2)
            return Fail(DiagnosticInfo.Create(Diagnostics.JobShape, location, factory, "Missed is not a Missed value"));

        var changeName = change.ToDisplayString(Qualified);
        var model = new JobModel(name!, changeName, changeName + "." + method.Name, cron!, zone, missed, perTenant);
        return new DiscoveredJob(model, new JobSite(name!, changeName, location), new EquatableArray<DiagnosticInfo>(ImmutableArray<DiagnosticInfo>.Empty));

        static DiscoveredJob Fail(DiagnosticInfo diagnostic)
            => new(null, null, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(diagnostic)));
    }

    private static string? Shape(IMethodSymbol method, INamedTypeSymbol change, Compilation compilation)
    {
        var changeInterface = compilation.GetTypeByMetadataName(ChangeName);
        var creationInterface = compilation.GetTypeByMetadataName(CreationName);
        var isChange = change.AllInterfaces.Any(i =>
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, changeInterface) ||
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, creationInterface));
        if (!isChange)
            return $"{change.Name} is not a change (IChange<T>, ICreation<T> or ILoads<T>)";
        if (change.IsGenericType || change.IsFileLocal)
            return $"{change.Name} is generic or file-local";
        if (!method.IsStatic)
            return "the factory must be static";
        if (method.IsGenericMethod)
            return "the factory cannot be generic";
        if (!compilation.IsSymbolAccessibleWithin(method, compilation.Assembly))
            return "the factory (or a type containing it) is private; generated code must call it";
        if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, change))
            return $"the factory must return {change.Name}";
        if (method.Parameters.Length != 1 || method.Parameters[0].RefKind != RefKind.None
                                          || method.Parameters[0].Type.ToDisplayString() != OccurrenceName)
            return "the factory takes one JobOccurrence";
        return null;
    }

    /// <summary>TCHG011: a job name used by two scheduled factories.</summary>
    public static IEnumerable<DiagnosticInfo> Duplicates(ImmutableArray<JobSite> sites)
    {
        foreach (var group in sites.GroupBy(s => s.Name, System.StringComparer.Ordinal))
        {
            var list = group.ToList();
            if (list.Count < 2)
                continue;
            var where = string.Join(", ", list.Select(s => Simple(s.Change)).OrderBy(x => x, System.StringComparer.Ordinal));
            foreach (var site in list.Skip(1))
                yield return DiagnosticInfo.Create(Diagnostics.JobName, site.Location, group.Key, where);
        }
    }

    private static string Simple(string qualified) => qualified.Substring(qualified.LastIndexOf('.') + 1);
}
