using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Tarol.Changes.Generators;

/// <summary>
/// Reaction and job names across assemblies (Tatalu FB-INT3-1): each assembly records its names in
/// <c>[assembly: DeclaredNames(kind, …)]</c>; an assembly that sees a name in two of them (its own included) reports TCHG012.
/// Names are stored in outbox and job rows, so <c>ReactionSet.Combine</c> refused at start; now the build says so.
/// </summary>
internal static class DeclaredNames
{
    public const string AttributeName = "Tarol.Changes.DeclaredNamesAttribute";

    /// <summary>The attribute arguments after the kind: <c>, "a", "b"</c>, sorted and distinct.</summary>
    public static IEnumerable<string> Of(IEnumerable<string> names)
        => names.Distinct(System.StringComparer.Ordinal).OrderBy(n => n, System.StringComparer.Ordinal)
            .Select(n => ", \"" + n.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");

    /// <summary>(kind, name, assembly) of every referenced assembly that declares names.</summary>
    public static EquatableArray<Declared> Referenced(Compilation compilation, System.Threading.CancellationToken cancellationToken)
    {
        var found = ImmutableArray.CreateBuilder<Declared>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var attribute in assembly.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() != AttributeName || attribute.ConstructorArguments.Length != 2
                    || attribute.ConstructorArguments[0].Value is not string kind)
                    continue;
                foreach (var name in attribute.ConstructorArguments[1].Values)
                    if (name.Value is string n)
                        found.Add(new Declared(kind, n, assembly.Name));
            }
        }
        return new EquatableArray<Declared>(found.ToImmutable());
    }

    /// <summary>One TCHG012 per name declared in more than one assembly.</summary>
    public static IEnumerable<DiagnosticInfo> Collisions(IEnumerable<Declared> all)
        => all.GroupBy(d => (d.Kind, d.Name))
            .Select(g => (g.Key, Assemblies: g.Select(d => d.Assembly).Distinct(System.StringComparer.Ordinal).OrderBy(a => a, System.StringComparer.Ordinal).ToList()))
            .Where(g => g.Assemblies.Count > 1)
            .OrderBy(g => g.Key.Kind, System.StringComparer.Ordinal).ThenBy(g => g.Key.Name, System.StringComparer.Ordinal)
            .Select(g => DiagnosticInfo.Create(Diagnostics.NameAcrossAssemblies, null, g.Key.Kind, g.Key.Name,
                string.Join("', '", g.Assemblies)));
}

/// <summary>A declared reaction or job name and its assembly.</summary>
internal sealed record Declared(string Kind, string Name, string Assembly);
