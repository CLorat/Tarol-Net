using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Changes.Generators;

/// <summary>What the assembly references and how its module is named (from <c>[assembly: GenerateTarolModule]</c>).</summary>
internal sealed record ModuleSettings(string? Name, string? Assembly, bool Queries, bool Endpoints, bool OpenApi, bool Jobs);

/// <summary>
///     TN-8: with <c>[assembly: GenerateTarolModule]</c>, one public <c>GeneratedModule.Module</c> per assembly: the assembly's
///     JSON contexts, aggregates and reactions, and as parts its jobs, projections, HTTP entries and OpenAPI transformers. Every
///     input is a name (fully qualified), so the output is cached on unrelated edits.
/// </summary>
internal static class ModuleGenerator
{
    private const string AttributeName = "Tarol.Changes.GenerateTarolModuleAttribute";
    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    public static void Initialize(IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<bool> hasReactions, IncrementalValueProvider<bool> hasJobs)
    {
        var settings = context.CompilationProvider.Select(static (c, _) =>
        {
            var attribute = c.Assembly.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AttributeName);
            if (attribute is null)
                return new ModuleSettings(null, c.AssemblyName, false, false, false, false);
            var name = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
            return new ModuleSettings(string.IsNullOrWhiteSpace(name) ? c.AssemblyName ?? "Module" : name, c.AssemblyName,
                c.GetTypeByMetadataName("Tarol.Queries.ProjectionModule") is not null,
                c.GetTypeByMetadataName("Tarol.Changes.AspNetCore.EndpointModule") is not null,
                c.GetTypeByMetadataName("Microsoft.AspNetCore.OpenApi.OpenApiOptions") is not null,
                c.GetTypeByMetadataName("Tarol.Changes.JobSet") is not null);
        });

        var aggregates = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => Aggregate(ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol))
            .Where(static a => a is not null).Select(static (a, _) => a!).Collect();
        var json = context.SyntaxProvider
            .ForAttributeWithMetadataName("System.Text.Json.Serialization.JsonSerializableAttribute", static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => ctx.TargetSymbol is INamedTypeSymbol { IsAbstract: false } t && DerivesFrom(t, "System.Text.Json.Serialization.JsonSerializerContext")
                    ? t.ToDisplayString(Fq) : null)
            .Where(static j => j is not null).Select(static (j, _) => j!).Collect();
        var projections = context.SyntaxProvider
            .ForAttributeWithMetadataName("Tarol.Queries.ProjectionAttribute", static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => ctx.TargetSymbol is INamedTypeSymbol t ? t.ToDisplayString(Fq) : null)
            .Where(static p => p is not null).Select(static (p, _) => p!).Collect();
        var entries = context.SyntaxProvider
            .ForAttributeWithMetadataName("Tarol.Changes.AspNetCore.EndpointAttribute", static (node, _) => node is TypeDeclarationSyntax, static (_, _) => true)
            .Collect().Select(static (all, _) => !all.IsEmpty);
        var hooks = context.SyntaxProvider
            .ForAttributeWithMetadataName("Tarol.Changes.AspNetCore.WebhookAttribute", static (node, _) => node is MethodDeclarationSyntax, static (_, _) => true)
            .Collect().Select(static (all, _) => !all.IsEmpty);

        var all = settings.Combine(aggregates).Combine(json).Combine(projections).Combine(entries).Combine(hooks).Combine(hasReactions).Combine(hasJobs);
        context.RegisterSourceOutput(all, static (spc, input) =>
        {
            var (((((((s, aggregateNames), contexts), rows), anyEntries), anyHooks), reactions), jobs) = input;
            if (s.Name is null)
                return;
            spc.AddSource("GeneratedModule.g.cs", Emit(s, Distinct(aggregateNames), Distinct(contexts), Distinct(rows), anyEntries, anyHooks, reactions, jobs));
        });
    }

    private static string[] Distinct(ImmutableArray<string> names) => names.Distinct().OrderBy(n => n, System.StringComparer.Ordinal).ToArray();

    /// <summary>A concrete <c>AggregateRoot&lt;T&gt;</c> with T itself, non-generic: its qualified name.</summary>
    private static string? Aggregate(INamedTypeSymbol? type)
    {
        if (type is null || type.IsAbstract || type.IsGenericType || type.IsFileLocal)
            return null;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.OriginalDefinition is { MetadataName: "AggregateRoot`1", ContainingNamespace: var ns } && ns.ToDisplayString() == "Tarol.Primitives")
                return SymbolEqualityComparer.Default.Equals(current.TypeArguments[0], type) ? type.ToDisplayString(Fq) : null;
        return null;
    }

    private static bool DerivesFrom(INamedTypeSymbol type, string name)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == name)
                return true;
        return false;
    }

    private static string Emit(ModuleSettings s, string[] aggregates, string[] contexts, string[] rows, bool entries, bool hooks, bool reactions, bool jobs)
    {
        var ns = s.Assembly ?? "Tarol.Generated";
        var sb = new StringBuilder();
        sb.Append("// <auto-generated/>\n#nullable enable\n\nnamespace ").Append(ns).Append(";\n\n");
        sb.Append("/// <summary>This assembly as a Tarol module (TN-8): <c>services.AddTarol(GeneratedModule.Module, …)</c>.</summary>\n");
        sb.Append("[global::System.CodeDom.Compiler.GeneratedCode(\"Tarol.Changes.Generators\", \"1.0\")]\n");
        sb.Append("public static class GeneratedModule\n{\n");
        sb.Append("    /// <summary>The module: JSON contexts, aggregates, reactions, jobs, projections, HTTP entries, OpenAPI.</summary>\n");
        sb.Append("    public static global::Tarol.Persistence.TarolModule Module { get; } = Create();\n\n");
        sb.Append("    private static global::Tarol.Persistence.TarolModule Create()\n    {\n");
        sb.Append("        var module = new global::Tarol.Persistence.TarolModule(").Append(Literal(s.Name!)).Append(")\n        {\n");
        if (contexts.Length == 1)
            sb.Append("            Json = ").Append(contexts[0]).Append(".Default,\n");
        else if (contexts.Length > 1)
            sb.Append("            Json = global::System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(")
              .Append(string.Join(", ", contexts.Select(c => c + ".Default"))).Append("),\n");
        if (aggregates.Length > 0)
            sb.Append("            Aggregates = static codec => codec").Append(string.Concat(aggregates.Select(a => ".Add<" + a + ">()"))).Append(",\n");
        if (reactions)
            sb.Append("            Reactions = global::").Append(ns).Append(".GeneratedReactions.Set,\n");
        sb.Append("        };\n");
        if (jobs && s.Jobs)
            sb.Append("        module = module.With(global::").Append(ns).Append(".GeneratedJobs.Set);\n");
        if (rows.Length > 0 && s.Queries)
            sb.Append("        module = module.With(new global::Tarol.Queries.ProjectionModule(static p => p")
              .Append(string.Concat(rows.Select(r => ".Add<" + r + ">()"))).Append("));\n");
        if ((entries || hooks) && s.Endpoints)
            sb.Append("        module = module.With(new global::Tarol.Changes.AspNetCore.EndpointModule(static (app, names) => global::")
              .Append(ns).Append(".GeneratedEndpoints.MapChanges(app, names)));\n");
        if (entries && s.Endpoints && s.OpenApi)
            sb.Append("        module = module.With(new global::Tarol.Changes.AspNetCore.OpenApiModule(static options => global::").Append(ns)
              .Append(".GeneratedOpenApi.AddTarolEndpoints((global::Microsoft.AspNetCore.OpenApi.OpenApiOptions)options)));\n");
        sb.Append("        return module;\n    }\n}\n");
        return sb.ToString();
    }

    private static string Literal(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
