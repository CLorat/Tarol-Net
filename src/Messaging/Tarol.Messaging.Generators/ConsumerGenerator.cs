using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Messaging.Generators;

/// <summary>
///     Generates the consumers of an assembly (docs/architecture/messaging.md §3): for every <c>[Consumes]</c> factory a
///     case of the switch in <c>GeneratedConsumers.Set</c>. Diagnostics TMSG001–TMSG003, TMSG006, TMSG007. Models are
///     symbol-free; a body edit or an unrelated file regenerates nothing.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ConsumerGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var factories = context.SyntaxProvider
            .ForAttributeWithMetadataName(Discovery.ConsumesAttribute, static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => Discovery.Consumers(ctx, ct))
            .SelectMany(static (results, _) => results)
            .WithTrackingName("Consumers");
        context.RegisterSourceOutput(factories.Where(static r => r.Diagnostic is not null), static (spc, r) => spc.ReportDiagnostic(r.Diagnostic!.ToDiagnostic()));
        var models = factories.Where(static r => r.Model is not null).Select(static (r, _) => r.Model!).Collect();

        var json = context.SyntaxProvider
            .ForAttributeWithMetadataName(Discovery.JsonSerializableAttribute, static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => Discovery.JsonTypes(ctx, ct))
            .SelectMany(static (types, _) => types)
            .Collect()
            .WithTrackingName("JsonTypes");

        var assembly = context.CompilationProvider.Select(static (c, _) => c.AssemblyName);

        context.RegisterSourceOutput(models.Combine(json).Combine(assembly), static (spc, all) =>
        {
            var ((consumers, jsonTypes), name) = all;
            if (consumers.IsEmpty)
                return;
            if (!jsonTypes.IsEmpty)
            {
                var listed = jsonTypes.ToImmutableHashSet();
                foreach (var model in consumers.Where(c => !listed.Contains(c.Payload)))
                    spc.ReportDiagnostic(DiagnosticInfo.Create(Diagnostics.NoJson, model.Location, model.Payload.Replace("global::", ""), model.Contract,
                        model.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToDiagnostic());
            }
            var collisions = Discovery.Collisions(consumers).ToArray();
            foreach (var diagnostic in collisions)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            if (collisions.Length > 0)
                return;
            spc.AddSource("GeneratedConsumers.g.cs", Emitter.Consumers(consumers, name));
        });
    }
}
