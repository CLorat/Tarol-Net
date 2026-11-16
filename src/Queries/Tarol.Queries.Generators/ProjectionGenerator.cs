using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Queries.Generators;

/// <summary>
///     Implements <c>IProjection&lt;TRow&gt;</c> on every <c>[Projection]</c> record: the name, version and stream type, the
///     typed column map and the switch over its <c>When</c> methods. Diagnostics TQRY001–TQRY004.
///     <para>
///         The transform yields a symbol-free, location-free <see cref="ProjectionModel"/> (signatures, never bodies), so
///         editing a <c>When</c> body or an unrelated file regenerates nothing (incrementality tests).
///     </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ProjectionGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var discovered = context.SyntaxProvider
            .ForAttributeWithMetadataName(Discovery.AttributeName, static (node, _) => node is RecordDeclarationSyntax or ClassDeclarationSyntax,
                static (ctx, ct) => Discovery.Transform(ctx, ct));

        context.RegisterSourceOutput(discovered.Where(static d => !d.Diagnostics.IsEmpty), static (spc, d) =>
        {
            foreach (var diagnostic in d.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });

        var models = discovered.Where(static d => d.Model is not null).Select(static (d, _) => d.Model!).WithTrackingName("ProjectionModels");
        context.RegisterSourceOutput(models, static (spc, model) => spc.AddSource(model.HintName, Emitter.Projection(model)));
    }
}
