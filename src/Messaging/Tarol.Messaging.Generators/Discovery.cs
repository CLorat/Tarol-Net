using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Tarol.Messaging.Generators;

internal static class Discovery
{
    public const string ConsumesAttribute = "Tarol.Messaging.ConsumesAttribute";
    public const string JsonSerializableAttribute = "System.Text.Json.Serialization.JsonSerializableAttribute";

    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    public static ImmutableArray<ConsumerResult> Consumers(GeneratorAttributeSyntaxContext context, CancellationToken ct)
    {
        var method = (IMethodSymbol)context.TargetSymbol;
        var location = LocationInfo.From(method.Locations.FirstOrDefault() ?? Location.None);
        var change = method.ContainingType;
        var results = ImmutableArray.CreateBuilder<ConsumerResult>();
        foreach (var attribute in context.Attributes)
        {
            ct.ThrowIfCancellationRequested();
            var contract = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "";
            var version = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is int v ? v : 0;
            var pinned = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Name").Value.Value as string;
            var name = pinned ?? change.Name;
            var versionText = version.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (ContractProblem(contract, version) is { } badContract)
            {
                results.Add(new ConsumerResult(null, DiagnosticInfo.Create(Diagnostics.BadContract, location, contract, versionText, method.Name, badContract)));
                continue;
            }
            string? why = null;
            if (!method.IsStatic || method.DeclaredAccessibility != Accessibility.Public || method.Parameters.Length is < 1 or > 2)
                why = "the factory is a public static method taking the contract record, or the record and the Message";
            else if (method.Parameters.Length == 2 && method.Parameters[1].Type.ToDisplayString() != "Tarol.Messaging.Message")
                why = "the second parameter is the Message (Tarol.Messaging.Message)";
            else if (method.Parameters[0].Type is not { IsReferenceType: true })
                why = "the contract record is a class (a record)";
            else if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, change))
                why = $"the factory returns its change, {change.Name}";
            else if (!change.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() is "Tarol.Changes.IChange<T>" or "Tarol.Changes.ICreation<T>"))
                why = $"{change.Name} is not a change (IChange<T> or ICreation<T>)";
            else if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-')))
                why = $"the consumer's name '{name}' is letters, digits, '-' and '_' (it names a subscription)";
            results.Add(why is not null
                ? new ConsumerResult(null, DiagnosticInfo.Create(Diagnostics.BadFactory, location, contract, versionText, method.Name, why))
                : new ConsumerResult(new ConsumerModel(name, contract, version, method.Parameters[0].Type.ToDisplayString(Fq), change.ToDisplayString(Fq),
                    method.Name, method.Parameters.Length == 2, location), null));
        }
        return results.ToImmutable();
    }

    /// <summary>The types a JsonSerializerContext of this compilation lists.</summary>
    public static ImmutableArray<string> JsonTypes(GeneratorAttributeSyntaxContext context, CancellationToken ct)
    {
        var type = (INamedTypeSymbol)context.TargetSymbol;
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (b.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext")
                return [.. context.Attributes
                    .Select(a => a.ConstructorArguments.Length > 0 ? a.ConstructorArguments[0].Value as ITypeSymbol : null)
                    .Where(t => t is not null)
                    .Select(t => t!.ToDisplayString(Fq))];
        return [];
    }

    public static IEnumerable<DiagnosticInfo> Collisions(ImmutableArray<ConsumerModel> consumers)
    {
        foreach (var group in consumers.GroupBy(c => (c.Name, c.Contract, c.Version)).Where(g => g.Count() > 1))
        {
            var all = group.OrderBy(c => c.Change, StringComparer.Ordinal).ThenBy(c => c.Method, StringComparer.Ordinal).ToArray();
            yield return DiagnosticInfo.Create(Diagnostics.Duplicate, all[1].Location, group.Key.Name, group.Key.Contract,
                group.Key.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), Short(all[0]), Short(all[1]));
        }
        foreach (var group in consumers.GroupBy(c => c.Change).Where(g => g.Select(c => c.Name).Distinct().Count() > 1))
        {
            var names = group.Select(c => c.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            yield return DiagnosticInfo.Create(Diagnostics.Name, group.First().Location,
                $"The factories of {Simple(group.Key)} name the consumer differently ({string.Join(", ", names)}); one change is one consumer");
        }
        foreach (var group in consumers.GroupBy(c => c.Name).Where(g => g.Select(c => c.Change).Distinct().Count() > 1))
        {
            var changes = group.Select(c => Simple(c.Change)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            yield return DiagnosticInfo.Create(Diagnostics.Name, group.First().Location,
                $"The consumer name '{group.Key}' is used by {string.Join(" and ", changes)}; pin a unique Name");
        }
    }

    private static string? ContractProblem(string contract, int version)
    {
        if (version < 1)
            return "the version is 1 or more";
        var dot = contract.IndexOf('.');
        if (dot <= 0 || dot == contract.Length - 1)
            return "the contract is <system>.<name>, e.g. \"billing.invoice-paid\"";
        if (!char.IsLower(contract[0]) || contract.Substring(0, dot).Any(c => !(char.IsLower(c) || char.IsDigit(c) || c == '-')))
            return "the system prefix is lowercase letters, digits and '-', starting with a letter";
        if (contract.Substring(dot + 1).Any(c => !(char.IsLower(c) || char.IsDigit(c) || c == '-' || c == '.')) || contract.EndsWith(".", StringComparison.Ordinal))
            return "the name after the system is lowercase letters, digits, '-' and '.'";
        return null;
    }

    private static string Short(ConsumerModel model) => Simple(model.Change) + "." + model.Method;

    private static string Simple(string fq) => fq.Substring(fq.LastIndexOf('.') + 1);
}
